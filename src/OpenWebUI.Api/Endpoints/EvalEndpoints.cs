using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Harness de avaliação de agentes (SPEC-20261023-agent-evals):
/// datasets + casos (CRUD/import JSONL), execução de EvalRuns via
/// dispatcher real, resultados com pass@k, baselines e compare com
/// relatório markdown.
/// </summary>
public static class EvalEndpoints
{
    private const int MaxCasesPerDataset = 500;
    private static readonly HashSet<string> Graders =
        ["exact", "contains", "regex", "rubric", "trajectory"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Mapeia o grupo /api/v1/evals (autenticado, por usuário).</summary>
    public static IEndpointRouteBuilder MapEvalEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/evals").RequireAuthorization();

        g.MapPost("/datasets", CreateDatasetAsync);
        g.MapGet("/datasets", ListDatasetsAsync);
        g.MapGet("/datasets/{id}", GetDatasetAsync);
        g.MapDelete("/datasets/{id}", DeleteDatasetAsync);
        g.MapPost("/datasets/{id}/cases", CreateCaseAsync);
        g.MapDelete("/datasets/{id}/cases/{caseId}", DeleteCaseAsync);
        g.MapPost("/datasets/{id}/cases:import", ImportCasesAsync);

        g.MapPost("/runs", CreateRunAsync);
        g.MapGet("/runs", ListRunsAsync);
        g.MapGet("/runs/{id}", GetRunAsync);
        g.MapGet("/runs/{id}/results", ListResultsAsync);
        g.MapGet("/runs/{id}/compare", CompareRunAsync);

        g.MapPost("/baselines", CreateBaselineAsync);
        g.MapGet("/baselines", ListBaselinesAsync);
        g.MapDelete("/baselines/{id}", DeleteBaselineAsync);

        return app;
    }

    // ---------- datasets ----------

    private static async Task<IResult> CreateDatasetAsync(
        HttpContext http, AppDbContext db, EvalDatasetInput input,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null || string.IsNullOrWhiteSpace(input.Name))
        {
            return user is null
                ? Results.Unauthorized()
                : Results.BadRequest(new { error = "name obrigatório" });
        }
        var ds = new EvalDataset
        {
            UserId = user.Id,
            Name = input.Name.Trim(),
            Description = input.Description,
            TagsJson = JsonSerializer.Serialize(input.Tags ?? [], JsonOptions),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        db.EvalDatasets.Add(ds);
        await db.SaveChangesAsync(ct);
        return Results.Ok(DatasetView(ds, 0));
    }

    private static async Task<IResult> ListDatasetsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var rows = await db.EvalDatasets.AsNoTracking()
            .Where(d => d.UserId == user.Id)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
        var counts = await db.EvalCases.AsNoTracking()
            .Where(c => rows.Select(r => r.Id).Contains(c.DatasetId))
            .GroupBy(c => c.DatasetId)
            .Select(g => new { g.Key, N = g.Count() })
            .ToListAsync(ct);
        return Results.Ok(rows.Select(d =>
            DatasetView(d, counts.FirstOrDefault(c => c.Key == d.Id)?.N ?? 0)));
    }

    private static async Task<IResult> GetDatasetAsync(
        HttpContext http, AppDbContext db, string id, CancellationToken ct)
    {
        var (user, ds) = await OwnDatasetAsync(http, db, id, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (ds is null)
        {
            return Results.NotFound();
        }
        var cases = await db.EvalCases.AsNoTracking()
            .Where(c => c.DatasetId == id)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(new
        {
            dataset = DatasetView(ds, cases.Count),
            cases = cases.Select(CaseView),
        });
    }

    private static async Task<IResult> DeleteDatasetAsync(
        HttpContext http, AppDbContext db, string id, CancellationToken ct)
    {
        var (user, ds) = await OwnDatasetAsync(http, db, id, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (ds is null)
        {
            return Results.NotFound();
        }
        db.EvalCases.RemoveRange(db.EvalCases.Where(c => c.DatasetId == id));
        db.EvalDatasets.Remove(ds);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ---------- cases ----------

    private static async Task<IResult> CreateCaseAsync(
        HttpContext http, AppDbContext db, string id,
        EvalCaseInput input, CancellationToken ct)
    {
        var (user, ds) = await OwnDatasetAsync(http, db, id, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (ds is null)
        {
            return Results.NotFound();
        }
        var err = ValidateCase(input);
        if (err is not null)
        {
            return Results.BadRequest(new { error = err });
        }
        var count = await db.EvalCases.CountAsync(c => c.DatasetId == id, ct);
        if (count >= MaxCasesPerDataset)
        {
            return Results.BadRequest(new
            {
                error = $"limite de {MaxCasesPerDataset} casos por dataset",
            });
        }
        var evalCase = ToCase(id, input);
        db.EvalCases.Add(evalCase);
        await db.SaveChangesAsync(ct);
        return Results.Ok(CaseView(evalCase));
    }

    private static async Task<IResult> DeleteCaseAsync(
        HttpContext http, AppDbContext db, string id, string caseId,
        CancellationToken ct)
    {
        var (user, ds) = await OwnDatasetAsync(http, db, id, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (ds is null)
        {
            return Results.NotFound();
        }
        var evalCase = await db.EvalCases
            .FirstOrDefaultAsync(c => c.Id == caseId && c.DatasetId == id, ct);
        if (evalCase is null)
        {
            return Results.NotFound();
        }
        db.EvalCases.Remove(evalCase);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Import JSONL: um caso por linha; erros reportados por linha.</summary>
    private static async Task<IResult> ImportCasesAsync(
        HttpContext http, AppDbContext db, string id, CancellationToken ct)
    {
        var (user, ds) = await OwnDatasetAsync(http, db, id, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (ds is null)
        {
            return Results.NotFound();
        }
        using var reader = new StreamReader(http.Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        var errors = new List<object>();
        var imported = 0;
        var existing = await db.EvalCases.CountAsync(
            c => c.DatasetId == id, ct);
        var lineNo = 0;
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            lineNo++;
            EvalCaseInput? input;
            try
            {
                input = JsonSerializer.Deserialize<EvalCaseInput>(
                    line, JsonOptions);
            }
            catch (JsonException ex)
            {
                errors.Add(new { line = lineNo, error = ex.Message });
                continue;
            }
            var err = input is null ? "json inválido" : ValidateCase(input);
            if (err is not null)
            {
                errors.Add(new { line = lineNo, error = err });
                continue;
            }
            if (existing + imported >= MaxCasesPerDataset)
            {
                errors.Add(new
                {
                    line = lineNo,
                    error = $"limite de {MaxCasesPerDataset} casos",
                });
                continue;
            }
            db.EvalCases.Add(ToCase(id, input!));
            imported++;
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { imported, errors });
    }

    // ---------- runs ----------

    private static async Task<IResult> CreateRunAsync(
        HttpContext http, AppDbContext db, ConfigService config,
        EvalRunInput input, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var ds = await db.EvalDatasets.AsNoTracking()
            .FirstOrDefaultAsync(
                d => d.Id == input.DatasetId && d.UserId == user.Id, ct);
        if (ds is null)
        {
            return Results.NotFound(new { error = "dataset não encontrado" });
        }
        if (string.IsNullOrWhiteSpace(input.ModelId))
        {
            return Results.BadRequest(new { error = "modelId obrigatório" });
        }
        var maxK = await config.GetAsync(EvalRunnerService.MaxKKey, 5, ct);
        var k = Math.Clamp(input.K ?? 1, 1, maxK);
        if (input.K > maxK)
        {
            return Results.BadRequest(new { error = $"k > MaxK ({maxK})" });
        }
        var caseCount = await db.EvalCases.CountAsync(
            c => c.DatasetId == ds.Id, ct);
        if (caseCount == 0)
        {
            return Results.BadRequest(new { error = "dataset sem casos" });
        }
        var maxConc = await config.GetAsync(
            EvalRunnerService.MaxConcurrencyKey, 2, ct);
        var run = new EvalRun
        {
            DatasetId = ds.Id,
            UserId = user.Id,
            ModelId = input.ModelId,
            AgentName = input.Agent,
            ToolsetJson = input.Toolset is { Count: > 0 }
                ? JsonSerializer.Serialize(input.Toolset, JsonOptions)
                : null,
            K = k,
            Concurrency = Math.Clamp(input.Concurrency ?? 2, 1, maxConc),
            TotalJobs = caseCount * k,
        };
        db.EvalRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new
        {
            id = run.Id,
            status = run.Status,
            totalJobs = run.TotalJobs,
        });
    }

    private static async Task<IResult> ListRunsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var runs = await db.EvalRuns.AsNoTracking()
            .Where(r => r.UserId == user.Id)
            .OrderByDescending(r => r.StartedAt ?? 0)
            .Take(50)
            .ToListAsync(ct);
        return Results.Ok(runs.Select(r => new
        {
            r.Id, r.DatasetId, r.ModelId, r.K, r.Status,
            r.TotalJobs, r.DoneJobs, r.StartedAt, r.FinishedAt,
        }));
    }

    private static async Task<IResult> GetRunAsync(
        HttpContext http, AppDbContext db, string id, CancellationToken ct)
    {
        var (user, run) = await OwnRunAsync(http, db, id, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (run is null)
        {
            return Results.NotFound();
        }
        return Results.Ok(await RunViewAsync(db, run, ct));
    }

    private static async Task<IResult> ListResultsAsync(
        HttpContext http, AppDbContext db, string id, CancellationToken ct)
    {
        var (user, run) = await OwnRunAsync(http, db, id, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (run is null)
        {
            return Results.NotFound();
        }
        var results = await db.EvalResults.AsNoTracking()
            .Where(r => r.EvalRunId == id)
            .OrderBy(r => r.CaseId).ThenBy(r => r.Rep)
            .ToListAsync(ct);
        return Results.Ok(results.Select(r => new
        {
            r.Id, r.CaseId, r.Rep, r.ChatRunId, r.ChatId,
            r.Pass, r.Score, r.GraderDetailJson,
        }));
    }

    private static async Task<IResult> CompareRunAsync(
        HttpContext http, AppDbContext db, string id,
        string? baseline, string? format, CancellationToken ct)
    {
        var (user, run) = await OwnRunAsync(http, db, id, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (run is null)
        {
            return Results.NotFound();
        }
        var baseRun = await ResolveBaselineAsync(
            db, user.Id, baseline, ct);
        if (baseRun is null)
        {
            return Results.NotFound(new { error = "baseline não encontrada" });
        }
        var diff = await BuildDiffAsync(db, run, baseRun, ct);
        if (string.Equals(format, "md", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Text(RenderMarkdown(run, baseRun, diff),
                "text/markdown");
        }
        return Results.Ok(new
        {
            run = run.Id,
            baseline = baseRun.Id,
            diff,
        });
    }

    // ---------- baselines ----------

    private static async Task<IResult> CreateBaselineAsync(
        HttpContext http, AppDbContext db, EvalBaselineInput input,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null || string.IsNullOrWhiteSpace(input.Name))
        {
            return user is null
                ? Results.Unauthorized()
                : Results.BadRequest(new { error = "name obrigatório" });
        }
        var run = await db.EvalRuns.AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Id == input.EvalRunId && r.UserId == user.Id, ct);
        if (run is null)
        {
            return Results.NotFound(new { error = "eval run não encontrado" });
        }
        var existing = await db.EvalBaselines
            .FirstOrDefaultAsync(
                b => b.UserId == user.Id && b.Name == input.Name, ct);
        if (existing is not null)
        {
            existing.EvalRunId = run.Id;
        }
        else
        {
            db.EvalBaselines.Add(new EvalBaseline
            {
                UserId = user.Id,
                Name = input.Name.Trim(),
                EvalRunId = run.Id,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { name = input.Name, evalRunId = run.Id });
    }

    private static async Task<IResult> ListBaselinesAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var rows = await db.EvalBaselines.AsNoTracking()
            .Where(b => b.UserId == user.Id)
            .ToListAsync(ct);
        return Results.Ok(rows.Select(b => new
        {
            b.Id, b.Name, b.EvalRunId, b.CreatedAt,
        }));
    }

    private static async Task<IResult> DeleteBaselineAsync(
        HttpContext http, AppDbContext db, string id, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var row = await db.EvalBaselines
            .FirstOrDefaultAsync(b => b.Id == id && b.UserId == user.Id, ct);
        if (row is null)
        {
            return Results.NotFound();
        }
        db.EvalBaselines.Remove(row);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ---------- helpers ----------

    private static async Task<object> RunViewAsync(
        AppDbContext db, EvalRun run, CancellationToken ct)
    {
        var results = await db.EvalResults.AsNoTracking()
            .Where(r => r.EvalRunId == run.Id)
            .ToListAsync(ct);
        var passAtK = results
            .GroupBy(r => r.CaseId)
            .Select(g => g.Any(r => r.Pass) ? 1d : 0d)
            .DefaultIfEmpty(0d)
            .Average();
        var meanScore = results.Where(r => r.Score is not null)
            .Select(r => r.Score!.Value)
            .DefaultIfEmpty(0d)
            .Average();
        // Custo agregado das runs filhas via ledger (SPEC budgets RF-001).
        var chatRunIds = results.Where(r => r.ChatRunId != null)
            .Select(r => r.ChatRunId!).ToList();
        var costUsd = await db.RunUsageRecords.AsNoTracking()
            .Where(u => chatRunIds.Contains(u.RunId) && u.CostUsd != null)
            .SumAsync(u => (double?)u.CostUsd, ct) ?? 0d;
        return new
        {
            run.Id, run.DatasetId, run.ModelId, run.AgentName, run.K,
            run.Concurrency, run.Status, run.TotalJobs, run.DoneJobs,
            run.Error, run.StartedAt, run.FinishedAt,
            passAtK = Math.Round(passAtK, 4),
            meanScore = Math.Round(meanScore, 4),
            costUsd = Math.Round(costUsd, 6),
            cases = results.Count,
        };
    }

    private static async Task<List<EvalCaseDiff>> BuildDiffAsync(
        AppDbContext db, EvalRun run, EvalRun baseRun, CancellationToken ct)
    {
        var cur = await db.EvalResults.AsNoTracking()
            .Where(r => r.EvalRunId == run.Id).ToListAsync(ct);
        var prev = await db.EvalResults.AsNoTracking()
            .Where(r => r.EvalRunId == baseRun.Id).ToListAsync(ct);
        var curByCase = cur.GroupBy(r => r.CaseId)
            .ToDictionary(g => g.Key, g => g.Any(r => r.Pass));
        var prevByCase = prev.GroupBy(r => r.CaseId)
            .ToDictionary(g => g.Key, g => g.Any(r => r.Pass));
        return curByCase.Select(kv =>
        {
            string status;
            if (!prevByCase.TryGetValue(kv.Key, out var was))
            {
                status = "new";
            }
            else if (kv.Value == was)
            {
                status = "unchanged";
            }
            else
            {
                status = kv.Value ? "improved" : "regressed";
            }
            return new EvalCaseDiff(
                kv.Key, status, kv.Value,
                prevByCase.GetValueOrDefault(kv.Key),
                cur.First(r => r.CaseId == kv.Key).ChatId);
        }).OrderBy(d => d.Status == "regressed" ? 0 : 1)
            .ToList();
    }

    private static string RenderMarkdown(
        EvalRun run, EvalRun baseRun, List<EvalCaseDiff> diff)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Eval compare: {run.Id} vs {baseRun.Id}");
        sb.AppendLine();
        sb.AppendLine("| case | status | pass@k | baseline | chat |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var d in diff)
        {
            var chat = d.ChatId is null ? "" : $"[transcript](/c/{d.ChatId})";
            sb.AppendLine(
                $"| {d.CaseId} | {d.Status} | {d.PassAtK} | {d.BaselinePassAtK} | {chat} |");
        }
        return sb.ToString();
    }

    private static async Task<EvalRun?> ResolveBaselineAsync(
        AppDbContext db, string userId, string? baseline, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(baseline))
        {
            return null;
        }
        var pointer = await db.EvalBaselines.AsNoTracking()
            .FirstOrDefaultAsync(
                b => b.UserId == userId
                    && (b.Id == baseline || b.Name == baseline), ct);
        var runId = pointer?.EvalRunId ?? baseline;
        return await db.EvalRuns.AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Id == runId && r.UserId == userId, ct);
    }

    private static async Task<(User? user, EvalDataset? ds)> OwnDatasetAsync(
        HttpContext http, AppDbContext db, string id, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return (null, null);
        }
        var ds = await db.EvalDatasets
            .FirstOrDefaultAsync(
                d => d.Id == id && d.UserId == user.Id, ct);
        return (user, ds);
    }

    private static async Task<(User? user, EvalRun? run)> OwnRunAsync(
        HttpContext http, AppDbContext db, string id, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return (null, null);
        }
        var run = await db.EvalRuns
            .FirstOrDefaultAsync(
                r => r.Id == id && r.UserId == user.Id, ct);
        return (user, run);
    }

    private static string? ValidateCase(EvalCaseInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Prompt))
        {
            return "prompt obrigatório";
        }
        var grader = input.Grader ?? "exact";
        if (!Graders.Contains(grader))
        {
            return $"grader inválido: {grader}";
        }
        return null;
    }

    private static EvalCase ToCase(string datasetId, EvalCaseInput input) => new()
    {
        DatasetId = datasetId,
        Prompt = input.Prompt!,
        Expected = input.Expected,
        Grader = input.Grader ?? "exact",
        Rubric = input.Rubric,
        ExpectToolsJson = input.ExpectTools is { Count: > 0 }
            ? JsonSerializer.Serialize(input.ExpectTools, JsonOptions)
            : null,
        ForbidToolsJson = input.ForbidTools is { Count: > 0 }
            ? JsonSerializer.Serialize(input.ForbidTools, JsonOptions)
            : null,
        MaxSteps = input.MaxSteps,
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    private static object DatasetView(EvalDataset ds, int cases) => new
    {
        ds.Id, ds.Name, ds.Description, ds.CreatedAt,
        tags = JsonSerializer.Deserialize<List<string>>(ds.TagsJson),
        cases,
    };

    private static object CaseView(EvalCase c) => new
    {
        c.Id, c.DatasetId, c.Prompt, c.Expected, c.Grader, c.Rubric,
        c.MaxSteps, c.CreatedAt,
        expectTools = c.ExpectToolsJson is null
            ? null
            : JsonSerializer.Deserialize<List<string>>(c.ExpectToolsJson),
        forbidTools = c.ForbidToolsJson is null
            ? null
            : JsonSerializer.Deserialize<List<string>>(c.ForbidToolsJson),
    };
}

/// <summary>Entrada de dataset (POST /evals/datasets).</summary>
public record EvalDatasetInput(
    string? Name, string? Description, List<string>? Tags);

/// <summary>Entrada de caso (POST cases / linha do JSONL import).</summary>
public class EvalCaseInput
{
    /// <summary>Prompt enviado ao agente.</summary>
    public string? Prompt { get; set; }

    /// <summary>Valor esperado.</summary>
    public string? Expected { get; set; }

    /// <summary>Grader: exact|contains|regex|rubric|trajectory.</summary>
    public string? Grader { get; set; }

    /// <summary>Rubrica para o juiz.</summary>
    public string? Rubric { get; set; }

    /// <summary>Tools esperadas na trajetória.</summary>
    public List<string>? ExpectTools { get; set; }

    /// <summary>Tools proibidas na trajetória.</summary>
    public List<string>? ForbidTools { get; set; }

    /// <summary>Máximo de steps.</summary>
    public int? MaxSteps { get; set; }
}

/// <summary>Entrada de EvalRun (POST /evals/runs).</summary>
public record EvalRunInput(
    string? DatasetId, string? ModelId, string? Agent,
    int? K, int? Concurrency, List<string>? Toolset);

/// <summary>Entrada de baseline (POST /evals/baselines).</summary>
public record EvalBaselineInput(string? Name, string? EvalRunId);

/// <summary>Linha do diff de compare por caso.</summary>
/// <param name="CaseId">Caso avaliado.</param>
/// <param name="Status">improved|regressed|unchanged|new.</param>
/// <param name="PassAtK">Passou ao menos uma vez (pass@k) no run atual.</param>
/// <param name="BaselinePassAtK">Pass@k na baseline.</param>
/// <param name="ChatId">Chat efêmero do transcript.</param>
public record EvalCaseDiff(string CaseId, string Status,
    bool PassAtK, bool BaselinePassAtK, string? ChatId);
