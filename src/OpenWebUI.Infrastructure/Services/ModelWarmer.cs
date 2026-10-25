using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Warming opt-in de prompt-cache (SPEC-20261018-agent-polish RF-001):
/// modelos listados no kv <c>warming.models</c>
/// (<c>{modelId: {intervalMinutes, billable}}</c>) recebem um request
/// mínimo (<c>max_tokens:1</c>, prompt "ping") a cada intervalo para
/// manter o prompt-cache do provider quente.
/// Regras de guarda: só pinga quando houve uso real recente (run na
/// janela de 3× o intervalo — para quando o modelo fica ocioso);
/// provider com custo exige <c>billable:true</c> explícito na entrada;
/// modelos servidos por OmniRoute (que gerencia cache próprio) são
/// ignorados. Off por default — kv ausente/vazio desliga tudo.
/// </summary>
public sealed class ModelWarmer(
    AppDbContext db,
    ConfigService config,
    ProviderService providers,
    ILogger<ModelWarmer>? logger = null)
{
    /// <summary>Kv do mapa de modelos aquecidos.</summary>
    public const string ConfigKey = "warming.models";

    /// <summary>Intervalo default quando a entrada não declara um.</summary>
    public const int DefaultIntervalMinutes = 5;

    /// <summary>Janela de ociosidade em múltiplos do intervalo (SPEC: &gt;3).</summary>
    internal const int IdleWindows = 3;

    /// <summary>Entrada do mapa <c>warming.models</c>.</summary>
    /// <param name="IntervalMinutes">Cadência do keep-alive (default 5).</param>
    /// <param name="Billable">
    /// Opt-in explícito para provider com custo — sem a flag o modelo só é
    /// aquecido quando o provider é local (ollama).
    /// </param>
    public sealed record WarmingEntry(int? IntervalMinutes = null, bool Billable = false);

    // Último ping por modelo — estado de processo, suficiente para a
    // cadência (reboot apenas atrasa um ciclo, nunca acelera).
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastPing = new();

    /// <summary>Seam de teste: injeta o relógio.</summary>
    internal static Func<DateTimeOffset> Now { get; set; } =
        () => DateTimeOffset.UtcNow;

    /// <summary>Seam de teste: limpa o estado de cadência entre testes.</summary>
    internal static void ResetState() => LastPing.Clear();

    /// <summary>
    /// Executa uma varredura: modelos devidos e em uso recente recebem o
    /// ping mínimo. Falhas individuais viram log — nunca lançam.
    /// </summary>
    /// <param name="ct">Cancelamento.</param>
    /// <returns>Número de pings enviados.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var map = await config.GetAsync<Dictionary<string, WarmingEntry>?>(
            ConfigKey, null, ct);
        if (map is null || map.Count == 0)
        {
            return 0;
        }

        List<ModelInfo> models;
        try
        {
            models = await providers.ListModelsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogDebug(ex, "Warming: catálogo de modelos indisponível.");
            return 0;
        }
        var byId = models.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        var omnirouteOnly = await IsOpenAiOnlyOmnirouteAsync(ct);
        var now = Now();
        var sent = 0;
        foreach (var (modelId, entry) in map)
        {
            var interval = TimeSpan.FromMinutes(
                Math.Max(1, entry.IntervalMinutes ?? DefaultIntervalMinutes));
            if (LastPing.TryGetValue(modelId, out var last)
                && now - last < interval)
            {
                continue;
            }

            if (!byId.TryGetValue(modelId, out var model))
            {
                continue;
            }

            // Provider com custo exige billable:true explícito na entrada.
            if (model.Provider != "ollama" && !entry.Billable)
            {
                continue;
            }

            // OmniRoute gerencia o próprio prompt-cache — pular.
            if (model.Provider == "openai" && omnirouteOnly)
            {
                continue;
            }

            // Para quando não usado &gt;3×intervalo: a janela de uso real é
            // a última run do modelo — pings nunca se auto-sustentam.
            var lastUse = await db.ChatRuns.AsNoTracking()
                .Where(r => r.Model == modelId)
                .OrderByDescending(r => r.StartedAt ?? r.CreatedAt)
                .Select(r => (long?)(r.StartedAt ?? r.CreatedAt))
                .FirstOrDefaultAsync(ct);
            if (lastUse is null
                || now - DateTimeOffset.FromUnixTimeSeconds(lastUse.Value)
                    > interval * IdleWindows)
            {
                continue;
            }

            try
            {
                await providers.CompleteAsync(
                    new ChatCompletionRequest(
                        modelId,
                        [new ChatCompletionMessage("user", "ping")],
                        Stream: false,
                        Params: new Dictionary<string, object>
                        {
                            ["max_tokens"] = 1,
                        }),
                    userId: null, ct);
                LastPing[modelId] = now;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogDebug(ex, "Warming: ping em {Model} falhou.", modelId);
            }
        }
        return sent;
    }

    /// <summary>
    /// Heurística OmniRoute: true quando TODA base URL OpenAI configurada
    /// aponta para OmniRoute (ele mantém cache próprio — warming externo
    /// é desperdício).
    /// </summary>
    private async Task<bool> IsOpenAiOnlyOmnirouteAsync(CancellationToken ct)
    {
        var connections = await config.GetConnectionsAsync(ct);
        var urls = connections.OpenAiBaseUrls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        return urls.Count > 0
            && urls.All(u => u.Contains("omniroute", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// BackgroundService do warming (SPEC-20261018-agent-polish RF-001):
/// acorda a cada <see cref="Tick"/> e delega ao
/// <see cref="ModelWarmer.RunOnceAsync"/> — a cadência por modelo é o
/// <c>intervalMinutes</c> da entrada (default 5min).
/// </summary>
public class ModelWarmerScheduler(IServiceScopeFactory scopeFactory, ILogger<ModelWarmerScheduler> logger)
    : BackgroundService
{
    // Cadências como estáticos mutáveis — seam de teste (mesmo padrão de
    // ModelWarmer.Now): a suíte encurta para exercitar o loop real.
    internal static TimeSpan Tick = TimeSpan.FromSeconds(60);
    internal static TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ModelWarmer>();
                await service.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Tick do model warmer falhou.");
            }

            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
