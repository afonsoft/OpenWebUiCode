using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Runner de avaliações (SPEC-20261023-agent-evals RF-002/RF-005): consome
/// <see cref="EvalRun"/>s pendentes, executa cada caso×repetição como uma
/// run real num chat efêmero via <see cref="IChatRunDispatcher"/>, aplica o
/// grader e grava <see cref="EvalResult"/>. Mesmo padrão do
/// <see cref="AutomationScheduler"/>: tick periódico com escopo próprio.
/// </summary>
public class EvalRunnerService(IServiceScopeFactory scopeFactory,
    ILogger<EvalRunnerService> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Chave de config: teto de repetições por caso.</summary>
    public const string MaxKKey = "agent.evals.maxK";

    /// <summary>Chave de config: teto de concorrência de runs filhas.</summary>
    public const string MaxConcurrencyKey = "agent.evals.maxConcurrency";

    /// <summary>Chave de config: modelo juiz do grader rubric.</summary>
    public const string JudgeModelKey = "agent.evals.judgeModel";

    /// <summary>Chave de config: timeout por caso (minutos).</summary>
    public const string CaseTimeoutKey = "agent.evals.caseTimeoutMinutes";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Orfãs de restart: EvalRuns 'running' voltam a 'pending' (execução
        // é idempotente — resultados anteriores do mesmo caso×rep são
        // substituídos antes de gravar).
        await RecoverOrphansAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickOnceAsync(stoppingToken);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                // Um tick com falha não derruba o runner.
            }
            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RecoverOrphansAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.EvalRuns
                .Where(r => r.Status == "running")
                .ExecuteUpdateAsync(
                    s => s.SetProperty(r => r.Status, "pending"), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "evals: falha recuperando EvalRuns órfãs");
        }
    }

    private async Task TickOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evalRun = await db.EvalRuns
            .Where(r => r.Status == "pending")
            .OrderBy(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (evalRun is null)
        {
            return;
        }
        evalRun.Status = "running";
        evalRun.StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        try
        {
            await ExecuteEvalRunAsync(scope.ServiceProvider, evalRun.Id, ct);
            evalRun.Status = "done";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            evalRun.Status = "failed";
            evalRun.Error = ex.Message;
            logger.LogWarning(ex, "evals: EvalRun {Id} falhou", evalRun.Id);
        }
        evalRun.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Executa um EvalRun: casos × repetições com concorrência.</summary>
    public async Task ExecuteEvalRunAsync(
        IServiceProvider services, string evalRunId, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var evalRun = await db.EvalRuns.FindAsync([evalRunId], ct)
            ?? throw new InvalidOperationException($"EvalRun {evalRunId} ausente");
        var cases = await db.EvalCases.AsNoTracking()
            .Where(c => c.DatasetId == evalRun.DatasetId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);
        var config = services.GetRequiredService<ConfigService>();
        var maxConcurrency = Math.Clamp(
            await config.GetAsync(MaxConcurrencyKey, 2, ct), 1, 8);
        var timeout = TimeSpan.FromMinutes(
            await config.GetAsync(CaseTimeoutKey, 10.0, ct));

        using var gate = new SemaphoreSlim(Math.Min(evalRun.Concurrency, maxConcurrency));
        var tasks = cases.SelectMany(c =>
            Enumerable.Range(0, evalRun.K).Select(rep => (c, rep)))
            .Select(async unit =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    await ExecuteCaseAsync(
                        services, evalRun, unit.c, unit.rep, timeout, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "evals: caso {CaseId} rep {Rep} falhou",
                        unit.c.Id, unit.rep);
                    await StoreResultAsync(db, evalRun, unit.c, unit.rep,
                        null, null, null,
                        new EvalGrade(false, null,
                            new { error = ex.Message }), ct);
                }
                finally
                {
                    gate.Release();
                    await BumpProgressAsync(db, evalRun.Id, ct);
                }
            });
        await Task.WhenAll(tasks);
    }

    private async Task ExecuteCaseAsync(
        IServiceProvider services, EvalRun evalRun, EvalCase evalCase,
        int rep, TimeSpan timeout, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var dispatcher = services.GetRequiredService<IChatRunDispatcher>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Chat efêmero — fora da sidebar e do memory sync (RF-002).
        var chat = new Chat
        {
            UserId = evalRun.UserId,
            Title = $"eval:{evalRun.Id}:{evalCase.Id}:{rep}",
            ModelsJson = JsonSerializer.Serialize(new[] { evalRun.ModelId }),
            IsEphemeral = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Chats.Add(chat);
        var message = new ChatMessage
        {
            ChatId = chat.Id,
            Role = "user",
            Content = evalCase.Prompt,
            Timestamp = now,
        };
        db.ChatMessages.Add(message);
        chat.LeafMessageId = message.Id;

        var toolset = ParseList(evalRun.ToolsetJson);
        var requestParams = evalRun.AgentName is null
            ? null
            : new Dictionary<string, object> { ["agent"] = evalRun.AgentName };
        var run = new ChatRun
        {
            ChatId = chat.Id,
            UserId = evalRun.UserId,
            Model = evalRun.ModelId,
            RequestJson = JsonSerializer.Serialize(
                new ChatCompletionRequest(
                    evalRun.ModelId,
                    [new ChatCompletionMessage("user", evalCase.Prompt)],
                    Stream: true,
                    Params: requestParams,
                    ToolIds: toolset.Count > 0 ? toolset : null),
                JsonOptions),
            CreatedAt = now,
        };
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync(ct);
        dispatcher.Enqueue(run.Id);

        var finished = await WaitForRunAsync(db, run.Id, timeout, ct);
        var grade = finished is null
            ? new EvalGrade(false, null,
                new { error = $"timeout após {timeout.TotalMinutes}min" })
            : await (await services.GetRequiredService<EvalGraderFactory>()
                    .CreateAsync(evalRun.ModelId, ct))
                .GradeAsync(evalCase, finished, finished.PartialContent,
                    evalRun.UserId, ct);
        await StoreResultAsync(db, evalRun, evalCase, rep,
            run.Id, chat.Id, finished?.PartialContent, grade, ct);
    }

    private static async Task<ChatRun?> WaitForRunAsync(
        AppDbContext db, string runId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var run = await db.ChatRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run?.Status is ChatRunStatus.Completed
                or ChatRunStatus.Failed or ChatRunStatus.Stopped)
            {
                return run;
            }
            await Task.Delay(PollInterval, ct);
        }
        return null;
    }

    private static async Task StoreResultAsync(
        AppDbContext db, EvalRun evalRun, EvalCase evalCase, int rep,
        string? chatRunId, string? chatId, string? finalAnswer,
        EvalGrade grade, CancellationToken ct)
    {
        // Idempotente: reexecução pós-restart substitui o resultado.
        var existing = await db.EvalResults
            .Where(r => r.EvalRunId == evalRun.Id
                && r.CaseId == evalCase.Id && r.Rep == rep)
            .ToListAsync(ct);
        db.EvalResults.RemoveRange(existing);
        db.EvalResults.Add(new EvalResult
        {
            EvalRunId = evalRun.Id,
            CaseId = evalCase.Id,
            Rep = rep,
            ChatRunId = chatRunId,
            ChatId = chatId,
            Pass = grade.Pass,
            Score = grade.Score,
            GraderDetailJson = JsonSerializer.Serialize(
                grade.Detail, JsonOptions),
            FinalAnswer = finalAnswer,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        await db.SaveChangesAsync(ct);
    }

    private static async Task BumpProgressAsync(
        AppDbContext db, string evalRunId, CancellationToken ct)
    {
        await db.EvalRuns.Where(r => r.Id == evalRunId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.DoneJobs, r => r.DoneJobs + 1), ct);
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <summary>Factory do grader com o modelo juiz resolvido por EvalRun.</summary>
public class EvalGraderFactory(AppDbContext db, ProviderService provider,
    ConfigService config)
{
    /// <summary>
    /// Cria o grader; juiz = <c>agent.evals.judgeModel</c> ou, sem config,
    /// o próprio modelo alvo do eval.
    /// </summary>
    public async Task<EvalGraderService> CreateAsync(
        string fallbackModel, CancellationToken ct = default)
    {
        var judgeModel = await config.GetAsync<string?>(
            EvalRunnerService.JudgeModelKey, null, ct) ?? fallbackModel;
        return new EvalGraderService(db, provider, judgeModel);
    }
}
