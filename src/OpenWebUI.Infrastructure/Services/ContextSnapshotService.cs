using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Snapshots versionados do contexto de trabalho por chat
/// (SPEC-20261020-context-snapshots RF-003/RF-004): único ponto com EF
/// para a tabela <c>ContextSnapshots</c> — gravação com versão monotônica
/// e retenção por contagem no mesmo SaveChanges, leituras por versão/
/// último/lista. Validação de payload (kinds, tamanhos, caracteres de
/// controle, segredos) acontece aqui: conteúdo suspeito nunca persiste
/// nem é logado.
/// </summary>
public sealed class ContextSnapshotService(
    AppDbContext db,
    ConfigService config,
    WorkspaceRepoService repos)
{
    private const string InvalidPayloadCode = "invalid_payload";
    /// <summary>Cap de caracteres do <c>content</c> de um item (RF-004).</summary>
    internal const int MaxItemContentChars = 16_000;

    /// <summary>Cap do rótulo opcional do snapshot (RF-003).</summary>
    internal const int MaxLabelChars = 200;

    private const int DefaultMaxItems = 200;
    private const int DefaultMaxPayloadBytes = 524_288; // 512 KB
    private const int DefaultMaxSnapshotsPerChat = 20;
    private const int DefaultSummaryTimeoutSeconds = 30;

    /// <summary>Kinds válidos de item (RF-004) — fechado, sem extensão no v1.</summary>
    internal static readonly HashSet<string> ItemKinds = new(StringComparer.Ordinal)
    {
        "decision", "note", "passage", "tool_output", "summary",
    };

    /// <summary>Kinds protegidos: nunca elididos nem cortados pelo budget (RF-006).</summary>
    internal static readonly HashSet<string> ProtectedKinds = new(StringComparer.Ordinal)
    {
        "decision", "note", "summary",
    };

    /// <summary>Campos conhecidos de um item — qualquer outro é invalid_payload.</summary>
    private static readonly HashSet<string> s_itemFields = new(StringComparer.Ordinal)
    {
        "kind", "title", "content", "score", "elided",
    };

    /// <summary>Item do payload persistido (RF-004).</summary>
    /// <param name="Kind">Tipo: decision | note | passage | tool_output | summary.</param>
    /// <param name="Title">Rótulo curto opcional.</param>
    /// <param name="Content">Conteúdo (≤ <see cref="MaxItemContentChars"/>).</param>
    /// <param name="Score">Relevância opcional — ordena o corte por budget.</param>
    /// <param name="Elided">Marcado quando o conteúdo virou stub de elision.</param>
    public sealed record SnapshotItem(
        string Kind, string? Title, string Content,
        double? Score = null, bool Elided = false);

    /// <summary>Erro estável devolvido pelas tools (código + detalhe sem payload).</summary>
    public sealed record SnapshotError(string Code, string Detail);

    /// <summary>Limites efetivos lidos das KVs <c>compaction:*</c> com clamp (RF-007).</summary>
    internal sealed record Limits(
        int MaxItems, int MaxPayloadBytes, int MaxSnapshotsPerChat,
        int ThresholdTokens, int SummaryTimeoutSeconds);

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Limites efetivos (clamps da RF-007 aplicados na leitura).</summary>
    internal async Task<Limits> LimitsAsync(CancellationToken ct)
    {
        var maxItems = Math.Clamp(
            await config.GetAsync("compaction:max_items_per_snapshot", DefaultMaxItems, ct),
            1, 1_000);
        var maxPayload = Math.Clamp(
            await config.GetAsync("compaction:max_payload_bytes", DefaultMaxPayloadBytes, ct),
            64_000, 2_000_000);
        var maxPerChat = Math.Clamp(
            await config.GetAsync("compaction:max_snapshots_per_chat", DefaultMaxSnapshotsPerChat, ct),
            1, 100);
        var thresholdTokens = Math.Clamp(
            await config.GetAsync("compaction:threshold_tokens", 224_000, ct),
            32_000, 256_000);
        var summaryTimeout = Math.Clamp(
            await config.GetAsync("compaction:summary_timeout_seconds", DefaultSummaryTimeoutSeconds, ct),
            1, 120);
        return new Limits(maxItems, maxPayload, maxPerChat, thresholdTokens, summaryTimeout);
    }

    /// <summary>Estimativa de tokens do conjunto de itens (ceil chars/4 — RF-001).</summary>
    public static int EstimateTokens(IEnumerable<SnapshotItem> items) =>
        ContextTokens.Estimate(items.Sum(i =>
            (i.Content?.Length ?? 0) + (i.Title?.Length ?? 0)));

    /// <summary>
    /// Parse estrito dos itens vindos dos args da tool (RF-004): campo
    /// desconhecido, kind fora do conjunto, tipo errado ou item não-objeto
    /// → <c>invalid_payload</c>. Validação de conteúdo fica em
    /// <see cref="ValidateItems"/>.
    /// </summary>
    public static (List<SnapshotItem>? Items, SnapshotError? Error) ParseItems(
        JsonElement el, int maxItems)
    {
        if (el.ValueKind != JsonValueKind.Array)
        {
            return (null, new SnapshotError(InvalidPayloadCode, "'items' deve ser um array."));
        }
        if (el.GetArrayLength() == 0)
        {
            return (null, new SnapshotError(InvalidPayloadCode, "'items' está vazio."));
        }
        if (el.GetArrayLength() > maxItems)
        {
            return (null, new SnapshotError(InvalidPayloadCode,
                $"'items' excede o máximo de {maxItems} itens."));
        }

        var items = new List<SnapshotItem>(el.GetArrayLength());
        var idx = 0;
        foreach (var e in el.EnumerateArray())
        {
            idx++;
            if (e.ValueKind != JsonValueKind.Object)
            {
                return (null, new SnapshotError(InvalidPayloadCode, $"item {idx} não é objeto."));
            }
            var unknownField = e.EnumerateObject()
                .Select(p => p.Name)
                .FirstOrDefault(n => !s_itemFields.Contains(n));
            if (unknownField is not null)
            {
                return (null, new SnapshotError(InvalidPayloadCode,
                        $"item {idx}: campo desconhecido '{unknownField}'."));
            }

            var kind = e.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString() : null;
            if (kind is null || !ItemKinds.Contains(kind))
            {
                return (null, new SnapshotError(InvalidPayloadCode,
                    $"item {idx}: kind inválido."));
            }

            if (!e.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.String)
            {
                return (null, new SnapshotError(InvalidPayloadCode,
                    $"item {idx}: 'content' é obrigatório."));
            }

            string? title = null;
            if (e.TryGetProperty("title", out var t))
            {
                if (t.ValueKind != JsonValueKind.String && t.ValueKind != JsonValueKind.Null)
                {
                    return (null, new SnapshotError(InvalidPayloadCode,
                        $"item {idx}: 'title' deve ser string."));
                }
                title = t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            }

            double? score = null;
            if (e.TryGetProperty("score", out var s) && s.ValueKind != JsonValueKind.Null)
            {
                if (s.ValueKind != JsonValueKind.Number || !s.TryGetDouble(out var d))
                {
                    return (null, new SnapshotError(InvalidPayloadCode,
                        $"item {idx}: 'score' deve ser número."));
                }
                score = d;
            }

            var elided = e.TryGetProperty("elided", out var g)
                && g.ValueKind == JsonValueKind.True;

            items.Add(new SnapshotItem(kind, title, c.GetString() ?? string.Empty, score, elided));
        }

        return (items, null);
    }

    /// <summary>
    /// Validação de conteúdo (RF-004): tamanho por item, caracteres de
    /// controle (exceto \n e \t) e padrões de segredo — o conteúdo nunca
    /// é ecoado no detalhe nem logado.
    /// </summary>
    internal static SnapshotError? ValidateItems(IReadOnlyList<SnapshotItem> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Content.Length > MaxItemContentChars)
            {
                return new SnapshotError(InvalidPayloadCode,
                    $"item {i + 1}: content excede {MaxItemContentChars} caracteres.");
            }
            if (HasControlChars(item.Content) || HasControlChars(item.Title))
            {
                return new SnapshotError(InvalidPayloadCode,
                    $"item {i + 1}: contém caracteres de controle.");
            }
            if (SecretScrubber.ContainsSecret(item.Content)
                || SecretScrubber.ContainsSecret(item.Title))
            {
                return new SnapshotError("secret_detected",
                    $"item {i + 1} contém padrão de segredo.");
            }
        }
        return null;
    }

    private static bool HasControlChars(string? s) =>
        s is not null && s.Any(c => c < 0x20 && c != '\n' && c != '\t' || c == 0x7F);

    /// <summary>Serializa os itens para <c>PayloadJson</c> (camelCase, nulls omitidos).</summary>
    public static string SerializeItems(IReadOnlyList<SnapshotItem> items) =>
        JsonSerializer.Serialize(items, s_json);

    /// <summary>Lê os itens de um <c>PayloadJson</c> persistido; formato inválido → [].</summary>
    public static List<SnapshotItem> DeserializeItems(string payloadJson)
    {
        try
        {
            var (items, _) = ParseItems(
                JsonDocument.Parse(payloadJson).RootElement, int.MaxValue);
            return items ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Grava uma nova versão do snapshot do chat (RF-003): valida o
    /// payload, resolve o <c>RepoKey</c> do binding vigente, incrementa a
    /// versão e aplica a retenção por contagem — tudo numa transação.
    /// </summary>
    public async Task<(ContextSnapshot? Snapshot, SnapshotError? Error)> SaveAsync(
        string chatId, string? userId, string? label,
        IReadOnlyList<SnapshotItem> items, string? strategy, bool compacted,
        CancellationToken ct)
    {
        var limits = await LimitsAsync(ct);
        if (label is not null && label.Length > MaxLabelChars)
        {
            return (null, new SnapshotError(InvalidPayloadCode,
                $"label excede {MaxLabelChars} caracteres."));
        }
        if (HasControlChars(label))
        {
            return (null, new SnapshotError(InvalidPayloadCode, "label contém caracteres de controle."));
        }
        if (SecretScrubber.ContainsSecret(label))
        {
            return (null, new SnapshotError("secret_detected", "label contém padrão de segredo."));
        }
        if (items.Count == 0)
        {
            return (null, new SnapshotError(InvalidPayloadCode, "'items' está vazio."));
        }
        if (items.Count > limits.MaxItems)
        {
            return (null, new SnapshotError(InvalidPayloadCode,
                $"'items' excede o máximo de {limits.MaxItems} itens."));
        }
        if (ValidateItems(items) is { } validation)
        {
            return (null, validation);
        }

        var payload = SerializeItems(items);
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > limits.MaxPayloadBytes)
        {
            return (null, new SnapshotError("payload_too_large",
                $"payload serializado excede {limits.MaxPayloadBytes} bytes."));
        }

        var binding = await repos.GetChatBindingAsync(chatId, ct);
        var repoKey = binding?.Repo?.ToLowerInvariant();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var version = (await db.ContextSnapshots
            .Where(s => s.ChatId == chatId)
            .Select(s => (int?)s.Version)
            .MaxAsync(ct) ?? 0) + 1;

        var snapshot = new ContextSnapshot
        {
            ChatId = chatId,
            RepoKey = repoKey,
            Version = version,
            Label = label,
            PayloadJson = payload,
            TokenEstimate = EstimateTokens(items),
            Compacted = compacted,
            Strategy = strategy,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CreatedByUserId = userId,
        };
        db.ContextSnapshots.Add(snapshot);

        // Retenção por contagem (RF-003): remove os mais antigos além do
        // cap, contando a linha nova — mesmo SaveChanges da inserção.
        var excess = await db.ContextSnapshots.CountAsync(s => s.ChatId == chatId, ct)
            + 1 - limits.MaxSnapshotsPerChat;
        if (excess > 0)
        {
            var stale = await db.ContextSnapshots
                .Where(s => s.ChatId == chatId)
                .OrderBy(s => s.Version)
                .Take(excess)
                .ToListAsync(ct);
            db.ContextSnapshots.RemoveRange(stale);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (snapshot, null);
    }

    /// <summary>Última versão do chat; null quando não há snapshots.</summary>
    public Task<ContextSnapshot?> LatestAsync(string chatId, CancellationToken ct) =>
        db.ContextSnapshots.AsNoTracking()
            .Where(s => s.ChatId == chatId)
            .OrderByDescending(s => s.Version)
            .FirstOrDefaultAsync(ct);

    /// <summary>Versão específica do chat; null quando inexistente.</summary>
    public Task<ContextSnapshot?> GetAsync(string chatId, int version, CancellationToken ct) =>
        db.ContextSnapshots.AsNoTracking()
            .Where(s => s.ChatId == chatId && s.Version == version)
            .FirstOrDefaultAsync(ct);

    /// <summary>Versões do chat em ordem crescente.</summary>
    public Task<List<ContextSnapshot>> ListAsync(string chatId, CancellationToken ct) =>
        db.ContextSnapshots.AsNoTracking()
            .Where(s => s.ChatId == chatId)
            .OrderBy(s => s.Version)
            .ToListAsync(ct);
}
