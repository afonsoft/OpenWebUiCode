using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Graders do harness de avaliação (SPEC-20261023-agent-evals RF-003/004):
/// exact/contains/regex sobre a resposta final, rubric via LLM-as-judge e
/// trajectory sobre os eventos persistidos da run.
/// </summary>
public class EvalGraderService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AppDbContext _db;
    private readonly Func<ChatCompletionRequest, string?, CancellationToken, Task<string>> _judge;
    private readonly string? _judgeModel;

    /// <summary>Cria o grader com juiz LLM customizável (testes).</summary>
    public EvalGraderService(
        AppDbContext db,
        Func<ChatCompletionRequest, string?, CancellationToken, Task<string>> judge,
        string? judgeModel = null)
    {
        _db = db;
        _judge = judge;
        _judgeModel = judgeModel;
    }

    /// <summary>Cria o grader chamando o provider real como juiz.</summary>
    public EvalGraderService(AppDbContext db, ProviderService provider, string? judgeModel = null)
        : this(db,
            (req, userId, ct) => provider.CompleteAsync(req, userId, ct),
            judgeModel)
    {
    }

    /// <summary>Aplica o grader do caso à resposta final da run.</summary>
    /// <param name="evalCase">Caso com grader e expectativas.</param>
    /// <param name="run">ChatRun filha executada.</param>
    /// <param name="finalAnswer">Resposta final do assistente.</param>
    /// <param name="userId">Dono do eval (BYOK do juiz).</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<EvalGrade> GradeAsync(
        EvalCase evalCase, ChatRun run, string? finalAnswer,
        string userId, CancellationToken ct)
    {
        return evalCase.Grader switch
        {
            "contains" => GradeContains(evalCase, finalAnswer),
            "regex" => GradeRegex(evalCase, finalAnswer),
            "rubric" => await GradeRubricAsync(evalCase, finalAnswer, userId, ct),
            "trajectory" => await GradeTrajectoryAsync(evalCase, run, ct),
            _ => GradeExact(evalCase, finalAnswer),
        };
    }

    private static EvalGrade GradeExact(EvalCase evalCase, string? answer)
    {
        var pass = string.Equals(
            Normalize(evalCase.Expected), Normalize(answer),
            StringComparison.Ordinal);
        return new EvalGrade(pass, pass ? 1 : 0,
            new { expected = evalCase.Expected, answer });
    }

    private static EvalGrade GradeContains(EvalCase evalCase, string? answer)
    {
        var pass = answer is not null && evalCase.Expected is not null
            && answer.Contains(evalCase.Expected, StringComparison.OrdinalIgnoreCase);
        return new EvalGrade(pass, pass ? 1 : 0,
            new { expected = evalCase.Expected, answer });
    }

    private static EvalGrade GradeRegex(EvalCase evalCase, string? answer)
    {
        if (string.IsNullOrEmpty(evalCase.Expected))
        {
            return new EvalGrade(false, null,
                new { error = "grader regex sem expected" });
        }
        try
        {
            var pass = answer is not null
                && Regex.IsMatch(answer, evalCase.Expected,
                    RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
            return new EvalGrade(pass, pass ? 1 : 0,
                new { pattern = evalCase.Expected, answer });
        }
        catch (RegexParseException ex)
        {
            return new EvalGrade(false, null,
                new { error = $"regex inválida: {ex.Message}" });
        }
    }

    private async Task<EvalGrade> GradeRubricAsync(
        EvalCase evalCase, string? answer, string userId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_judgeModel))
        {
            return new EvalGrade(false, null,
                new { error = "sem modelo juiz configurado" });
        }
        var prompt = $$"""
            You are an impartial grader. Score the assistant's answer against
            the rubric below. Reply with ONLY a JSON object:
            {"score": <0.0-1.0>, "rationale": "<one sentence>"}

            <rubric>
            {{evalCase.Rubric ?? ""}}
            </rubric>

            <question>
            {{evalCase.Prompt}}
            </question>

            <untrusted_input>
            {{answer ?? ""}}
            </untrusted_input>
            """;
        try
        {
            var raw = await _judge(
                new ChatCompletionRequest(
                    _judgeModel,
                    [new ChatCompletionMessage("user", prompt)],
                    Stream: false),
                userId, ct);
            var json = ExtractJson(raw);
            using var doc = JsonDocument.Parse(json);
            var score = doc.RootElement.GetProperty("score").GetDouble();
            var rationale = doc.RootElement.TryGetProperty("rationale", out var r)
                ? r.GetString()
                : null;
            return new EvalGrade(score >= 0.5, Math.Clamp(score, 0, 1),
                new { score, rationale });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Juiz inválido/indisponível → falha do caso, nunca do EvalRun.
            return new EvalGrade(false, null,
                new { error = $"judge: {ex.Message}" });
        }
    }

    private async Task<EvalGrade> GradeTrajectoryAsync(
        EvalCase evalCase, ChatRun run, CancellationToken ct)
    {
        var events = await _db.ChatRunEvents.AsNoTracking()
            .Where(e => e.RunId == run.Id)
            .OrderBy(e => e.Seq)
            .ToListAsync(ct);
        var toolsCalled = events
            .Where(e => e.Type == RunEventWriter.Types.ToolCall)
            .Select(e => ToolName(e.PayloadJson))
            .Where(n => n is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var steps = events.Count(e =>
            e.Type == RunEventWriter.Types.StepFinished);

        var expect = ParseList(evalCase.ExpectToolsJson);
        var forbid = ParseList(evalCase.ForbidToolsJson);
        var asserts = new List<object>();
        var pass = run.Status == ChatRunStatus.Completed;
        asserts.Add(new
        {
            assert = "run_completed", pass,
            detail = run.Status,
        });
        foreach (var tool in expect)
        {
            var ok = toolsCalled.Contains(tool);
            pass &= ok;
            asserts.Add(new { assert = $"expect_tool:{tool}", pass = ok });
        }
        foreach (var tool in forbid)
        {
            var ok = !toolsCalled.Contains(tool);
            pass &= ok;
            asserts.Add(new { assert = $"forbid_tool:{tool}", pass = ok });
        }
        if (evalCase.MaxSteps is { } max)
        {
            var ok = steps <= max;
            pass &= ok;
            asserts.Add(new
            {
                assert = "max_steps", pass = ok, steps, max,
            });
        }
        return new EvalGrade(pass, pass ? 1 : 0, new { asserts });
    }

    private static string? Normalize(string? s)
    {
        if (s is null)
        {
            return null;
        }
        var t = s.Trim().TrimEnd('.', '!', '?', ';', ':').Trim();
        return t.ToLowerInvariant();
    }

    private static string? ToolName(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.TryGetProperty("name", out var n)
                ? n.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string> ParseList(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ExtractJson(string raw)
    {
        var s = raw.IndexOf('{');
        var e = raw.LastIndexOf('}');
        return s >= 0 && e > s ? raw[s..(e + 1)] : raw;
    }
}

/// <summary>Resultado de um grader: pass, score e detalhe estruturado.</summary>
/// <param name="Pass">Caso passou.</param>
/// <param name="Score">Score 0-1 ou null.</param>
/// <param name="Detail">Detalhe serializado em <c>GraderDetailJson</c>.</param>
public record EvalGrade(bool Pass, double? Score, object Detail);
