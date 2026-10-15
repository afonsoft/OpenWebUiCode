using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Limiar de contexto marcado pelo agente (SPEC-20261013-agent-context-
/// management RF-001): <c>checkpoint(goal)</c> abre a boundary na posição
/// seguinte à última mensagem persistida; <c>rewind(report)</c> fecha o
/// intervalo <c>[FromPos, ToPos]</c> e grava o relatório num marcador
/// persistido <c>Role="compaction"</c> (mesmo padrão do resumo automático —
/// vai ao modelo como mensagem e vira o divisor visual no transcript).
/// Mensagens persistidas nunca são deletadas — a boundary só filtra o
/// contexto reconstruído no enqueue (RF-002).
/// </summary>
public sealed record ContextBoundary(
    int FromPos, int ToPos, string Goal, bool Open, string? RunId = null)
{
    /// <summary>Fecha a boundary cobrindo até <paramref name="toPos"/>.</summary>
    public ContextBoundary Close(int toPos) => this with { ToPos = toPos, Open = false };
}

/// <summary>
/// kv por chat do gerenciamento dirigido pelo agente:
/// <c>chat:{id}:context-boundaries</c> = intervalos colapsados,
/// <c>chat:{id}:context-notes</c> = notebook persistente (RF-003) e
/// <c>run:{id}:new-context</c> = flag de compactação a pedido (RF-004).
/// </summary>
public sealed class ContextBoundaryService(AppDbContext db, ConfigService config)
{
    /// <summary>Cap do notebook por escrita (RF-003).</summary>
    internal const int MaxNoteChars = 8_000;

    private static string BoundariesKey(string chatId) => $"chat:{chatId}:context-boundaries";
    private static string NotesKey(string chatId) => $"chat:{chatId}:context-notes";
    private static string NewContextKey(string runId) => $"run:{runId}:new-context";

    /// <summary>
    /// Nomes das tools de gerenciamento de contexto (RF-005) — gate admin
    /// <c>contexttools.enabled</c> (default on; <c>false</c> remove do
    /// anúncio/picker e bloqueia a execução).
    /// </summary>
    public static bool IsContextTool(string name) =>
        name is "checkpoint" or "rewind" or "context_notes" or "new_context";

    /// <summary>Gate admin: ausente/null = ligado; false desliga as 4 tools.</summary>
    public async Task<bool> EnabledAsync(CancellationToken ct) =>
        await config.GetAsync<bool?>("contexttools.enabled", null, ct) != false;

    // ---- boundaries (RF-001/RF-002) ----

    /// <summary>Lista as boundaries do chat (abertas e fechadas).</summary>
    public async Task<List<ContextBoundary>> ListAsync(string chatId, CancellationToken ct) =>
        await config.GetAsync<List<ContextBoundary>?>(BoundariesKey(chatId), null, ct) ?? [];

    /// <summary>
    /// Abre um checkpoint no fim do histórico persistido. Retorna a posição
    /// de início da exploração, ou null quando a própria run já tem um
    /// checkpoint aberto (double-open → erro de tool). Checkpoint aberto
    /// de outra run é órfão (morreu sem rewind — edge da SPEC): descartado
    /// sem filtrar nada, e o novo checkpoint segue.
    /// </summary>
    public async Task<int?> MarkAsync(
        string chatId, string? runId, string goal, CancellationToken ct)
    {
        var boundaries = await ListAsync(chatId, ct);
        if (runId is not null && boundaries.Any(b => b.Open && b.RunId == runId))
        {
            return null;
        }
        boundaries.RemoveAll(b => b.Open && b.RunId != runId);
        var lastPos = await LastPositionAsync(chatId, ct);
        var from = lastPos + 1;
        boundaries.Add(new ContextBoundary(from, -1, goal, Open: true, RunId: runId));
        await config.SetAsync(BoundariesKey(chatId), boundaries, ct);
        return from;
    }

    /// <summary>
    /// Fecha o checkpoint aberto: o intervalo até a última mensagem
    /// persistida é coberto pela boundary e o relatório vira um marcador
    /// <c>Role="compaction"</c> logo depois dele — a filtragem do rebuild
    /// dropa o trecho e o marcador carrega o report ao modelo. Boundaries
    /// fechadas sobrepostas são absorvidas (união + reports concatenados,
    /// edge da SPEC). Retorna (report, cobertas) ou null sem checkpoint
    /// aberto; intervalo vazio fecha sem boundary e devolve cobertas=-1.
    /// </summary>
    public async Task<(string Report, int Covered)?> CommitAsync(
        string chatId, string? runId, string report, CancellationToken ct)
    {
        var boundaries = await ListAsync(chatId, ct);
        // Prefere o checkpoint da própria run; senão qualquer aberto serve
        // (runId nulo em testes/contextos sem run).
        var openIdx = boundaries.FindIndex(
            b => b.Open && (runId is null || b.RunId is null || b.RunId == runId));
        if (openIdx < 0)
        {
            return null;
        }

        var open = boundaries[openIdx];
        var lastPos = await LastPositionAsync(chatId, ct);
        boundaries.RemoveAt(openIdx);
        if (lastPos < open.FromPos)
        {
            // Intervalo vazio (run morreu/nada persistido) — erro suave,
            // checkpoint some sem deixar boundary (edge da SPEC).
            await config.SetAsync(BoundariesKey(chatId), boundaries, ct);
            return (report, -1);
        }

        // Absorve boundaries fechadas cobertas pelo novo intervalo:
        // união + reports concatenados em ordem (a mensagem-marcador delas
        // cai dentro do range e é filtrada — o texto migra para o marcador
        // novo para não perder o resumo).
        var mergedReport = report;
        var absorbed = boundaries
            .Where(b => !b.Open && b.FromPos >= open.FromPos && b.ToPos <= lastPos)
            .OrderBy(b => b.FromPos)
            .ToList();
        if (absorbed.Count > 0)
        {
            // O report de cada boundary absorvida vive na mensagem-marcador
            // em ToPos+1 — recupera o texto antes de uni-la à nova.
            var markerPos = absorbed.Select(a => a.ToPos + 1).Distinct().ToList();
            var markers = (await db.ChatMessages.AsNoTracking()
                    .Where(m => m.ChatId == chatId && markerPos.Contains(m.Position))
                    .ToListAsync(ct))
                .GroupBy(m => m.Position)
                .ToDictionary(g => g.Key, g => g.First().Content);
            var prior = string.Join("\n\n", absorbed
                .Where(a => markers.ContainsKey(a.ToPos + 1))
                .Select(a => markers[a.ToPos + 1]));
            mergedReport = string.IsNullOrEmpty(prior)
                ? report
                : prior + "\n\n" + report;
            foreach (var a in absorbed)
            {
                boundaries.Remove(a);
            }
        }

        var closed = open.Close(lastPos);
        boundaries.Add(closed);
        await config.SetAsync(BoundariesKey(chatId), boundaries, ct);

        // Marcador persistido (Role="compaction" — mesmo visual do resumo
        // automático): posição logo após o intervalo coberto, fora dele.
        // Pendura no leaf do caminho ativo (SPEC-20261013-chat-branching) —
        // o marcador pertence ao ramo onde a fronteira foi fechada.
        var tracked = await db.Chats.Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (tracked is not null)
        {
            var marker = new ChatMessage
            {
                ChatId = tracked.Id,
                Role = "compaction",
                Content = $"[contexto colapsado — {closed.Goal}]\n{mergedReport}",
                Position = lastPos + 1,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ParentId = BranchService.ResolveLeaf(tracked)?.Id,
            };
            tracked.Messages.Add(marker);
            tracked.LeafMessageId = marker.Id;
            tracked.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SaveChangesAsync(ct);
        }
        var covered = lastPos - closed.FromPos + 1;
        return (mergedReport, covered);
    }

    /// <summary>
    /// Filtra mensagens persistidas cobertas por boundaries fechadas
    /// (RF-002): mantém ordem; boundaries abertas não filtram nada (run
    /// morreu com checkpoint aberto → trecho fica intacto).
    /// </summary>
    public static List<ChatMessage> FilterMessages(
        IEnumerable<ChatMessage> ordered, IReadOnlyList<ContextBoundary> boundaries)
    {
        var closed = boundaries.Where(b => !b.Open && b.ToPos >= b.FromPos).ToList();
        if (closed.Count == 0)
        {
            return [.. ordered];
        }
        return [.. ordered.Where(m => !closed.Any(b => m.Position >= b.FromPos && m.Position <= b.ToPos))];
    }

    /// <summary>Atalho: lista boundaries e filtra a lista ordenada.</summary>
    public async Task<List<ChatMessage>> FilterAsync(
        string chatId, IEnumerable<ChatMessage> ordered, CancellationToken ct) =>
        FilterMessages(ordered, await ListAsync(chatId, ct));

    // ---- notebook persistente (RF-003) ----

    /// <summary>Notebook do chat; null/vazio quando não escrito ainda.</summary>
    public Task<string?> GetNotesAsync(string chatId, CancellationToken ct) =>
        config.GetAsync<string?>(NotesKey(chatId), null, ct);

    /// <summary>Sobrescreve o notebook (replace); false quando estoura o cap.</summary>
    public async Task<bool> SetNotesAsync(string chatId, string text, CancellationToken ct)
    {
        if (text.Length > MaxNoteChars)
        {
            return false;
        }
        if (string.IsNullOrEmpty(text))
        {
            await RemoveNotesAsync(chatId, ct);
        }
        else
        {
            await config.SetAsync(NotesKey(chatId), text, ct);
        }
        return true;
    }

    private Task RemoveNotesAsync(string chatId, CancellationToken ct) =>
        config.SetAsync<string?>(NotesKey(chatId), null, ct);

    // ---- compactação a pedido (RF-004) ----

    /// <summary>Sinaliza que a run pediu nova janela de contexto.</summary>
    public Task RequestNewContextAsync(string runId, CancellationToken ct) =>
        config.SetAsync(NewContextKey(runId), true, ct);

    /// <summary>Consome o pedido uma única vez (true quando havia flag).</summary>
    public async Task<bool> ConsumeNewContextAsync(string runId, CancellationToken ct)
    {
        var pending = await config.GetAsync<bool?>(NewContextKey(runId), null, ct) == true;
        if (pending)
        {
            await config.SetAsync<bool?>(NewContextKey(runId), null, ct);
        }
        return pending;
    }

    /// <summary>Última posição persistida do chat (-1 quando vazio).</summary>
    private async Task<int> LastPositionAsync(string chatId, CancellationToken ct) =>
        await db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatId == chatId)
            .Select(m => (int?)m.Position)
            .MaxAsync(ct) ?? -1;
}
