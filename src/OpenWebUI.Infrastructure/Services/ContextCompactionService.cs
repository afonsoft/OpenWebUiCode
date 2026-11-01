using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Resumo estruturado (SPEC-20261014-context-maintenance-v2 RF-002): o
/// summarizer tenta devolver <c>{files, decisions, pending, summary}</c>
/// em JSON; quando o modelo responde prosa pura, só <c>Text</c> fica
/// preenchido — a janela injeta o formato estruturado quando presente.
/// </summary>
public sealed record ContextSummary(
    string Text,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<string> Pending);

/// <summary>
/// Seam de sumarização para compactação de contexto (SPEC-20261010-context-compaction):
/// recebe o transcript do head e devolve um resumo. A implementação real
/// chama o provider; testes injetam um fake.
/// </summary>
public interface IContextSummarizer
{
    /// <summary>Resume o trecho de conversa (formatado "role: content").</summary>
    /// <param name="userId">Usuário dono da conversa — resolve BYOK quando habilitado.</param>
    Task<ContextSummary?> SummarizeAsync(
        string model, string transcript, string? userId, CancellationToken ct);
}

/// <summary>
/// Seam de compactação nativa do provider (SPEC-20261018-agent-polish
/// RF-004): quando <c>compaction:native</c> e o modelo anuncia a
/// capacidade <c>"compaction"</c>, tenta o endpoint nativo; null =
/// não suportado/falhou → fallback ao summarizer.
/// </summary>
public interface INativeContextCompactor
{
    /// <summary>Compacta o histórico; null quando o provider não suporta.</summary>
    Task<string?> TryCompactAsync(
        IReadOnlyList<ChatCompletionMessage> history,
        string model, string? userId, CancellationToken ct);
}

/// <summary>
/// Compactação nativa via completion com <c>context_management</c> no
/// payload (providers OpenAI-compatíveis que anunciam a capacidade o
/// reconhecem; os demais ignoram o parâmetro e devolvem prosa comum —
/// nesse caso o resultado ainda é um resumo utilizável).
/// </summary>
public sealed class ProviderNativeCompactor(ProviderService providers)
    : INativeContextCompactor
{
    /// <inheritdoc />
    public async Task<string?> TryCompactAsync(
        IReadOnlyList<ChatCompletionMessage> history,
        string model, string? userId, CancellationToken ct)
    {
        var transcript = string.Join("\n\n",
            history.Select(m => $"{m.Role}: {m.Content}"));
        var request = new ChatCompletionRequest(
            model,
            [new ChatCompletionMessage("user",
                "Compact this conversation preserving goals, files, decisions " +
                "and pending tasks. Answer with the compacted context only.\n\n"
                + transcript)],
            Stream: false,
            Params: new Dictionary<string, object>
            {
                ["context_management"] = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                },
            });
        try
        {
            var result = await providers.CompleteAsync(request, userId, ct);
            return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Sumarizador real: completion única não-streaming no modelo da própria run.</summary>
public sealed class LlmContextSummarizer(ProviderService providers) : IContextSummarizer
{
    private const string Prompt =
        "Resuma esta conversa de forma compacta e fiel. Responda SÓ com um JSON " +
        "no formato {\"summary\": \"<resumo em prosa preservando erros, correções " +
        "e o objetivo atual>\", \"files\": [\"caminhos de arquivos citados ou " +
        "modificados\"], \"decisions\": [\"decisões tomadas\"], \"pending\": " +
        "[\"tarefas pendentes\"]} — listas vazias quando não houver nada. " +
        "Sem preâmbulo, sem markdown.\n\n";

    /// <inheritdoc />
    public async Task<ContextSummary?> SummarizeAsync(
        string model, string transcript, string? userId, CancellationToken ct)
    {
        var request = new ChatCompletionRequest(model,
            [new ChatCompletionMessage("user", Prompt + transcript)], Stream: false);
        var result = await providers.CompleteAsync(request, userId, ct);
        return string.IsNullOrWhiteSpace(result) ? null : Parse(result.Trim());
    }

    /// <summary>
    /// Parse tolerante do JSON estruturado (RF-002): qualquer falha de
    /// parse degrada para prosa — o resumo nunca falha a run.
    /// </summary>
    internal static ContextSummary Parse(string raw)
    {
        try
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var doc = JsonDocument.Parse(raw[start..(end + 1)]).RootElement;
                var text = doc.TryGetProperty("summary", out var s)
                    ? s.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return new ContextSummary(
                        text.Trim(), List(doc, "files"),
                        List(doc, "decisions"), List(doc, "pending"));
                }
            }
        }
        catch (JsonException)
        {
            // Degrada para prosa abaixo.
        }
        return new ContextSummary(raw, [], [], []);
    }

    private static List<string> List(JsonElement doc, string prop) =>
        doc.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim()).ToList()
            : [];
}

/// <summary>
/// Compactação de contexto para runs longas (SPEC-20261010-context-compaction):
/// quando o histórico passa do limiar, o head é resumido via LLM e
/// <c>{ cutoff, summary }</c> fica no kv <c>chat:{id}:compaction</c>. Runs
/// seguintes enviam <c>[resumo] + tail</c> em vez do histórico inteiro — as
/// mensagens originais permanecem no DB e a UI continua exibindo tudo; só a
/// janela enviada ao modelo é compactada. o histórico
/// são ordenados por Position (o cutoff é contagem de mensagens desde o início).
/// Best-effort: falha no summarizer nunca bloqueia a run.
/// </summary>
public sealed record CompactionDeps(
    INativeContextCompactor? Native = null,
    ProviderService? Providers = null,
    ModelCapabilityResolver? Caps = null);

public sealed class ContextCompactionService(
    AppDbContext db,
    ConfigService config,
    IContextSummarizer summarizer,
    ModelRoleResolver roles,
    ILogger<ContextCompactionService> logger,
    CompactionDeps? extras = null)
{
    private readonly INativeContextCompactor? native = extras?.Native;
    private readonly ProviderService? providers = extras?.Providers;
    private readonly ModelCapabilityResolver? caps = extras?.Caps;
    /// <summary>Resultado de uma compactação manual (RF-004).</summary>
    public sealed record CompactResult(
        bool Compacted, bool Native, string? Warning);
    /// <summary>Prefixo da mensagem sintética que injeta o resumo no histórico.</summary>
    public const string SummaryPrefix = "[Contexto compactado de mensagens anteriores]\n";

    /// <summary>Prefixo do bloco de turno parcial (RF-004) injetado após o resumo.</summary>
    public const string TurnPrefixHeader = "[Turno em andamento no ponto de corte — prefixo]\n";

    // SPEC-20261020-context-snapshots RF-001: o disparo normal é por
    // tokens estimados (ceil chars/4) — o default de chars sobe para ~1M
    // (~250k tokens) para a KV de tokens mandar; compaction:threshold
    // segue como override do admin para compactar mais cedo.
    private const int DefaultThresholdChars = 1_000_000;
    private const int DefaultThresholdTokens = 224_000;
    private const int DefaultTailMessages = 30;
    private const int DefaultTailChars = 25_000;

    /// <summary>Teto do prefixo de turno textual persistido (RF-004).</summary>
    private const int MaxTurnPrefixChars = 8_000;

    /// <param name="Digest">Resumo estruturado (RF-002) quando o modelo respondeu JSON.</param>
    /// <param name="TurnPrefix">
    /// Texto do prefixo do turno corrente (RF-004): quando o corte caiu
    /// dentro de um turno, a parte já executada dele vai separada do
    /// resumo para não cortar um par tool_call/tool_result.
    /// </param>
    private sealed record CompactionState(
        int Cutoff, string Summary,
        ContextSummary? Digest = null, string? TurnPrefix = null);
    internal sealed record Thresholds(
        int TotalChars, int TailMessages, int TailChars,
        int ThresholdTokens = int.MaxValue);

    private static string KvKey(string chatId) => $"chat:{chatId}:compaction";

    /// <summary>
    /// Aplica a compactação persistida: com cutoff válido devolve
    /// <c>[ctx-summary] + [prefixo do turno?] + mensagens após o cutoff</c>;
    /// senão devolve o histórico intacto. Nos dois casos a janela passa
    /// pela poda mecânica (<see cref="ContextPruner"/>, RF-001) — tool
    /// results supersedes/ok/gigantes viram marcadores na projeção.
    /// </summary>
    public async Task<List<ChatCompletionMessage>> ApplyAsync(
        string chatId, IReadOnlyList<ChatCompletionMessage> history, CancellationToken ct)
    {
        var state = await config.GetAsync<CompactionState?>(KvKey(chatId), null, ct);
        List<ChatCompletionMessage> window;
        if (state is null || state.Cutoff <= 0 || state.Cutoff >= history.Count
            || string.IsNullOrWhiteSpace(state.Summary))
        {
            window = [.. history];
        }
        else
        {
            window = history.Skip(state.Cutoff).ToList();
            window.Insert(0, new ChatCompletionMessage(
                "user", SummaryPrefix + FormatSummary(state)));
            if (!string.IsNullOrWhiteSpace(state.TurnPrefix))
            {
                window.Insert(1, new ChatCompletionMessage(
                    "user", TurnPrefixHeader + state.TurnPrefix));
            }
        }
        return ContextPruner.Prune(window, chatId);
    }

    /// <summary>Resumo + digest estruturado (arquivos/decisões/pendências) quando presente.</summary>
    private static string FormatSummary(CompactionState state)
    {
        var d = state.Digest;
        if (d is null || (d.Files.Count == 0 && d.Decisions.Count == 0 && d.Pending.Count == 0))
        {
            return state.Summary;
        }
        var sb = new System.Text.StringBuilder(d.Text);
        AppendSection(sb, "Arquivos", d.Files);
        AppendSection(sb, "Decisões", d.Decisions);
        AppendSection(sb, "Pendências", d.Pending);
        return sb.ToString();
    }

    private static void AppendSection(
        System.Text.StringBuilder sb, string title, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }
        sb.Append("\n\n").Append(title).Append(':');
        foreach (var item in items)
        {
            sb.Append("\n- ").Append(item);
        }
    }

    /// <summary>
    /// Compacta o histórico se ultrapassar o limiar: resume o trecho ainda não
    /// compactado (incluindo o resumo anterior como contexto), persiste kv e
    /// anexa um marcador <c>Role="compaction"</c> no chat. Retorna true quando
    /// compactou. Nunca lança — falha vira log e a run segue sem compactar.
    /// </summary>
    /// <param name="force">Quando true, ignora o limiar de tamanho — usado
    /// pelo pedido de nova janela do agente (<c>new_context</c>,
    /// SPEC-20261013-agent-context-management RF-004).</param>
    /// <param name="tailScale">Escala do tail preservado (0–1]: o retry de
    /// context-overflow do provider-resilience compacta com alvos
    /// progressivos 70%→50%→35% do tail normal (RF-004).</param>
    public async Task<bool> MaybeCompactAsync(
        string chatId, IReadOnlyList<ChatCompletionMessage> history,
        string model, CancellationToken ct, string? userId = null,
        bool force = false, double tailScale = 1.0)
    {
        try
        {
            var t = await ScaledThresholdsAsync(tailScale, ct);
            var total = history.Sum(m => (m.Content?.Length ?? 0) + (m.ToolCallsJson?.Length ?? 0));
            // RF-001: dispara quando tokens OU chars estouram o limiar —
            // a KV de chars sobrevive como override para compactar antes.
            if (!force && ContextTokens.Estimate(total) < t.ThresholdTokens
                && total <= t.TotalChars)
            {
                return false;
            }

            var headEnd = SplitIndex(history, t);
            if (headEnd <= 0)
            {
                return false;
            }
            var (turnPrefix, summaryEnd) = ComputeTurnSplit(history, headEnd, chatId);

            var transcript = await BuildTranscriptAsync(chatId, history, summaryEnd, ct);

            // Papel "memory" (SPEC-20261013-model-roles): resumo roda no
            // modelo do papel quando configurado; senão no modelo da run.
            var summaryModel = userId is null
                ? model
                : await roles.ResolveAsync("memory", userId, model, ct);
            var summary = await summarizer.SummarizeAsync(summaryModel, transcript, userId, ct);
            if (summary is null)
            {
                return false;
            }

            await PersistCompactionAsync(chatId, headEnd, summary.Text,
                HasDigest(summary) ? summary : null, turnPrefix, ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Compactação de contexto falhou para chat {ChatId}.", chatId);
            return false;
        }
    }

    /// <summary>Limiares com escala do tail (retry de overflow, RF-004).</summary>
    private async Task<Thresholds> ScaledThresholdsAsync(
        double tailScale, CancellationToken ct)
    {
        var t = await ThresholdsAsync(ct);
        return tailScale >= 1.0
            ? t
            : new Thresholds(
                t.TotalChars,
                Math.Max(4, (int)(t.TailMessages * tailScale)),
                Math.Max(2000, (int)(t.TailChars * tailScale)),
                t.ThresholdTokens);
    }

    /// <summary>
    /// RF-004: quando o corte deixou mensagens tool órfãs no head
    /// (<see cref="SplitIndex"/> avançou por um bloco tool), o resumo cobre só
    /// até o início do turno; o trecho do turno já executado vira prefixo
    /// textual separado — par tool_call/tool_result nunca parte.
    /// </summary>
    private static (string? TurnPrefix, int SummaryEnd) ComputeTurnSplit(
        IReadOnlyList<ChatCompletionMessage> history, int headEnd, string chatId)
    {
        var turnStart = headEnd;
        while (turnStart > 0 && history[turnStart - 1].Role == "tool")
        {
            turnStart--;
        }
        if (turnStart > 0 && turnStart < headEnd
            && history[turnStart - 1].ToolCallsJson is not null)
        {
            turnStart--;
        }
        var turnPrefix = turnStart > 0 && turnStart < headEnd
            ? RenderTurnPrefix(history, turnStart, headEnd, chatId)
            : null;
        return (turnPrefix, turnPrefix is null ? headEnd : turnStart);
    }

    /// <summary>Transcript do head podado + resumo anterior (se houver).</summary>
    private async Task<string> BuildTranscriptAsync(
        string chatId, IReadOnlyList<ChatCompletionMessage> history,
        int summaryEnd, CancellationToken ct)
    {
        var prev = await config.GetAsync<CompactionState?>(KvKey(chatId), null, ct);
        var newHeadStart = prev is { Cutoff: > 0 } && prev.Cutoff < summaryEnd
            ? prev.Cutoff : 0;
        // Poda mecânica do head antes do resumo (RF-001): tool results
        // elidíveis viram marcadores — o summarizer não paga por lixo.
        var head = ContextPruner.Prune(
            history.Skip(newHeadStart).Take(summaryEnd - newHeadStart).ToList(), chatId);
        return (prev is not null && newHeadStart > 0
                ? $"Resumo anterior já compactado: {prev.Summary}\n\n" : string.Empty)
            + string.Join("\n\n", head.Select(m => $"{m.Role}: {m.Content}"));
    }

    /// <summary>
    /// Compactação manual (RF-004, SPEC-20261018-agent-polish): força a
    /// compactação independente do limiar. Com <c>compaction:native</c> e
    /// o modelo anunciando a capacidade <c>"compaction"</c>, tenta o
    /// endpoint nativo; sem capacidade ou falha → summarizer interno com
    /// <see cref="CompactResult.Warning"/>.
    /// </summary>
    public async Task<CompactResult> CompactNowAsync(
        string chatId, IReadOnlyList<ChatCompletionMessage> history,
        string model, string? userId, CancellationToken ct)
    {
        try
        {
            var preferNative = await config.GetAsync("compaction:native", false, ct);
            var supportsNative = preferNative
                && native is not null
                && providers is not null
                && caps is not null
                && await NativeSupportedAsync(model, ct);
            string? warning = preferNative && !supportsNative
                ? "compaction.native habilitado, mas o modelo não anuncia a " +
                  "capacidade 'compaction' — usado o summarizer interno."
                : null;

            if (supportsNative)
            {
                var text = await native!.TryCompactAsync(history, model, userId, ct);
                if (text is not null)
                {
                    var t = await ThresholdsAsync(ct);
                    var headEnd = SplitIndex(history, t);
                    if (headEnd > 0)
                    {
                        await PersistCompactionAsync(
                            chatId, headEnd, text, null, null, ct);
                        return new CompactResult(true, true, null);
                    }
                }
                warning = "Compactação nativa falhou — usado o summarizer interno.";
            }

            var compacted = await MaybeCompactAsync(
                chatId, history, model, ct, userId, force: true);
            return new CompactResult(compacted, false,
                compacted ? warning : warning ?? "Nada para compactar.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Compactação manual falhou para chat {ChatId}.", chatId);
            return new CompactResult(false, false, "Compactação falhou.");
        }
    }

    /// <summary>O modelo anuncia a capacidade <c>"compaction"</c>?</summary>
    private async Task<bool> NativeSupportedAsync(string model, CancellationToken ct)
    {
        var info = (await providers!.ListModelsAsync(ct))
            .FirstOrDefault(m => m.Id.Equals(model, StringComparison.OrdinalIgnoreCase));
        if (info is null)
        {
            return false;
        }
        var resolved = await caps!.ResolveAsync(info.Provider, info.Id, info.Capabilities, ct);
        return resolved.Any(c => c.Equals("compaction", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Persiste kv + marcador <c>compaction</c> no leaf ativo.</summary>
    private async Task PersistCompactionAsync(
        string chatId, int headEnd, string summary,
        ContextSummary? digest, string? turnPrefix, CancellationToken ct)
    {
        await config.SetAsync(KvKey(chatId),
            new CompactionState(headEnd, summary, digest, turnPrefix), ct);

        var tracked = await db.Chats.Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (tracked is not null)
        {
            var pos = tracked.Messages.Count == 0 ? 0 : tracked.Messages.Max(m => m.Position) + 1;
            // O marcador pendura no leaf do caminho compactado
            // (SPEC-20261013-chat-branching): outro ramo não o herda.
            var marker = new ChatMessage
            {
                ChatId = tracked.Id, Role = "compaction", Content = summary,
                Position = pos, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ParentId = BranchService.ResolveLeaf(tracked)?.Id,
            };
            tracked.Messages.Add(marker);
            tracked.LeafMessageId = marker.Id;
            tracked.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Índice de corte (mensagens mantidas no tail): recua do fim até cobrir
    /// <see cref="Thresholds.TailMessages"/> mensagens ou <see cref="Thresholds.TailChars"/>
    /// chars — e avança enquanto o primeiro item do tail for <c>tool</c>, para não
    /// separar respostas tool do assistant(tool_calls) que as gerou.
    /// </summary>
    internal static int SplitIndex(IReadOnlyList<ChatCompletionMessage> history, Thresholds t)
    {
        var tailCount = 0;
        var tailChars = 0;
        var idx = 0;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var m = history[i];
            tailCount++;
            tailChars += (m.Content?.Length ?? 0) + (m.ToolCallsJson?.Length ?? 0);
            idx = i;
            if (tailCount >= t.TailMessages || tailChars >= t.TailChars)
            {
                break;
            }
        }
        while (idx < history.Count && history[idx].Role == "tool")
        {
            idx++;
        }
        return idx;
    }

    /// <summary>Se o resumo carrega digest estruturado (alguma lista preenchida).</summary>
    private static bool HasDigest(ContextSummary s) =>
        s.Files.Count > 0 || s.Decisions.Count > 0 || s.Pending.Count > 0;

    /// <summary>
    /// Renderiza o prefixo do turno cortado (RF-004): texto das mensagens
    /// já executadas do turno corrente, podadas e truncadas a
    /// <see cref="MaxTurnPrefixChars"/> com ponteiro para history://.
    /// </summary>
    private static string RenderTurnPrefix(
        IReadOnlyList<ChatCompletionMessage> history, int from, int to, string chatId)
    {
        var slice = ContextPruner.Prune(
            history.Skip(from).Take(to - from).ToList(), chatId);
        var text = string.Join("\n\n", slice.Select(m => $"{m.Role}: {m.Content}"));
        if (text.Length > MaxTurnPrefixChars)
        {
            text = string.Concat(text.AsSpan(0, MaxTurnPrefixChars),
                $"\n[…prefixo truncado — histórico completo em history://{chatId}]");
        }
        return text;
    }

    private async Task<Thresholds> ThresholdsAsync(CancellationToken ct)
    {
        var total = await config.GetAsync("compaction:threshold", DefaultThresholdChars, ct);
        var tailMsgs = await config.GetAsync("compaction:tail_messages", DefaultTailMessages, ct);
        var tailChars = await config.GetAsync("compaction:tail_chars", DefaultTailChars, ct);
        var thresholdTokens = Math.Clamp(
            await config.GetAsync("compaction:threshold_tokens", DefaultThresholdTokens, ct),
            32_000, 256_000);
        return new Thresholds(total, tailMsgs, tailChars, thresholdTokens);
    }
}
