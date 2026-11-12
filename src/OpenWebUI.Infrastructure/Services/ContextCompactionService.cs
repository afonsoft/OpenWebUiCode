using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Context;
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
    IReadOnlyList<string> Pending,
    /// <summary>Current State (summarizer-v2, SPEC-20261102 RF-005):
    /// tarefa imediata, próximos passos e bloqueios — null quando o
    /// modelo respondeu no formato legado.</summary>
    string? State = null,
    /// <summary>Âncoras de busca para recuperar detalhe do head
    /// (<c>[pos]</c> do transcript ou <c>history://{chatId}/msg:{id}</c>),
    /// cap 20 itens.</summary>
    IReadOnlyList<string>? Breadcrumbs = null,
    /// <summary>Errors and fixes (summarizer-v3, SPEC-20261103 RF-001):
    /// erros encontrados e como foram corrigidos — cap 20 itens.</summary>
    IReadOnlyList<string>? Errors = null,
    /// <summary>Next step alinhado ao pedido mais recente do usuário, com
    /// citação verbatim de onde o trabalho parou (RF-004: sem
    /// <c>quote</c> a seção é descartada no render).</summary>
    ContextNextStep? NextStep = null);

/// <summary>Próximo passo do resumo-v3: <paramref name="Text"/> descreve
/// a ação; <paramref name="Quote"/> é a citação verbatim do ponto de
/// parada ("verbatim to ensure there's no drift").</summary>
public sealed record ContextNextStep(string Text, string Quote);

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
public sealed class LlmContextSummarizer(
    ProviderService providers, ConfigService? config = null) : IContextSummarizer
{
    private const string Prompt =
        "Resuma esta conversa para retomar o trabalho sem o histórico. " +
        "Responda SÓ com um JSON no formato {\"summary\": \"<## Overview — " +
        "1-2 frases do objetivo>\", \"state\": \"<Current State — tarefa " +
        "imediata, próximos passos, bloqueios>\", \"breadcrumbs\": " +
        "[\"<âncoras de busca para recuperar detalhe: posições [n] do " +
        "transcript ou termos-chave>\"], \"files\": [\"caminhos de arquivos " +
        "citados ou modificados\"], \"decisions\": [\"decisões tomadas\"], " +
        "\"pending\": [\"tarefas pendentes\"]} — listas vazias quando não " +
        "houver nada. NÃO recite rules/instruções de sistema verbatim — " +
        "elas serão re-injetadas depois. Sem preâmbulo, sem markdown fora " +
        "do JSON.\n\n";

    /// <summary>Summarizer-v3 (SPEC-20261103 RF-001): acrescenta
    /// <c>errors[]</c> ("errors encountered and how they were fixed") e
    /// <c>next_step{text,quote}</c> — o next step pede uma citação
    /// verbatim de onde o trabalho parou ("verbatim to ensure there's
    /// no drift").</summary>
    private const string PromptV3 =
        "Resuma esta conversa para retomar o trabalho sem o histórico. " +
        "Responda SÓ com um JSON no formato {\"summary\": \"<## Overview — " +
        "1-2 frases do objetivo>\", \"state\": \"<Current State — tarefa " +
        "imediata, próximos passos, bloqueios>\", \"breadcrumbs\": " +
        "[\"<âncoras de busca para recuperar detalhe: posições [n] do " +
        "transcript ou termos-chave>\"], \"files\": [\"caminhos de arquivos " +
        "citados ou modificados\"], \"decisions\": [\"decisões tomadas\"], " +
        "\"pending\": [\"tarefas pendentes\"], \"errors\": [\"erros " +
        "encontrados e como foram corrigidos\"], \"next_step\": " +
        "{\"text\": \"<Optional Next Step — o passo mais diretamente " +
        "alinhado ao pedido mais recente do usuário>\", \"quote\": " +
        "\"<citação verbatim do transcript de onde o trabalho parou, " +
        "para garantir que não haja desvio>\"}} — listas vazias quando " +
        "não houver nada e next_step null quando não houver passo claro. " +
        "NÃO recite rules/instruções de sistema verbatim — elas serão " +
        "re-injetadas depois. Sem preâmbulo, sem markdown fora do JSON.\n\n";

    /// <inheritdoc />
    public async Task<ContextSummary?> SummarizeAsync(
        string model, string transcript, string? userId, CancellationToken ct)
    {
        var prompt = config is null
            || await config.GetAsync("compaction:summarizer_v3", true, ct)
                ? PromptV3 : Prompt;
        var request = new ChatCompletionRequest(model,
            [new ChatCompletionMessage("user", prompt + transcript)], Stream: false);
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
                        List(doc, "decisions"), List(doc, "pending"),
                        doc.TryGetProperty("state", out var st)
                            && st.ValueKind == JsonValueKind.String
                                ? st.GetString() : null,
                        List(doc, "breadcrumbs").Take(20).ToList(),
                        List(doc, "errors").Take(20).ToList(),
                        ParseNextStep(doc));
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

    /// <summary><c>next_step{text,quote}</c> do resumo-v3 — null quando
    /// ausente, não-objeto ou sem texto.</summary>
    private static ContextNextStep? ParseNextStep(JsonElement doc)
    {
        if (!doc.TryGetProperty("next_step", out var ns)
            || ns.ValueKind != JsonValueKind.Object
            || !ns.TryGetProperty("text", out var txt)
            || txt.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(txt.GetString()))
        {
            return null;
        }
        var quote = ns.TryGetProperty("quote", out var q)
            && q.ValueKind == JsonValueKind.String
                ? q.GetString() ?? string.Empty : string.Empty;
        return new ContextNextStep(txt.GetString()!.Trim(), quote.Trim());
    }
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
    ModelCapabilityResolver? Caps = null,
    /// <summary>Scope factory para a task de compactação em background
    /// (SPEC-20261102 RF-002): o trabalho sobrevive ao scope do request e
    /// precisa de DbContext próprio — compartilhar o contexto do chamador
    /// correria contra operações concorrentes da run.</summary>
    IServiceScopeFactory? Scopes = null);

public sealed class ContextCompactionService(
    AppDbContext db,
    ConfigService config,
    IContextSummarizer summarizer,
    ModelRoleResolver roles,
    ILogger<ContextCompactionService> logger,
    CompactionDeps? extras = null,
    AsyncCompactionScheduler? scheduler = null)
{
    private readonly INativeContextCompactor? native = extras?.Native;
    private readonly ProviderService? providers = extras?.Providers;
    private readonly ModelCapabilityResolver? caps = extras?.Caps;
    private readonly IServiceScopeFactory? scopes = extras?.Scopes;
    private readonly AsyncCompactionScheduler scheduler =
        scheduler ?? new AsyncCompactionScheduler();
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

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

    /// <summary>Ratio de spawn do 3-tier (SPEC-20261102 RF-001): 0.70
    /// espelha o spawn do Devin (~183k de 262k); clamps impedem spawn
    /// precoce/tardio demais.</summary>
    private const double DefaultSpawnRatio = 0.70;

    /// <summary>Teto default do gatilho por attachments (SPEC-20261103
    /// RF-002): 8 MB acumulados na janela disparam spawn de compactação.</summary>
    private const long DefaultAttachmentThresholdBytes = 8_000_000;
    private const double MinSpawnRatio = 0.30;
    private const double MaxSpawnRatio = 0.95;

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
        ContextSummary? Digest = null, string? TurnPrefix = null,
        /// <summary>Records verbatim das últimas mensagens <c>role=user</c>
        /// do trecho compactado (RF-006) — extração mecânica, não-LLM.</summary>
        IReadOnlyList<string>? UserMessages = null,
        /// <summary>JSON do plano de tasks do último tool call de plano
        /// no trecho compactado (RF-007) — sobrevive à compactação.</summary>
        string? TodosJson = null);

    /// <summary>
    /// Resumo gerado em background aguardando aplicação (RF-002): vive no
    /// kv <c>:pending</c> até a próxima fronteira mover para o estado
    /// aplicado — o cutoff do snapshot que originou o resumo garante que
    /// um resumo de branch/revert antigo nunca regride a janela.
    /// </summary>
    private sealed record PendingCompaction(
        CompactionState State, long SpawnedAt, int EstimateTokens);

    internal sealed record Thresholds(
        int TotalChars, int TailMessages, int TailChars,
        int ThresholdTokens = int.MaxValue,
        int SpawnTokens = int.MaxValue, long SpawnChars = long.MaxValue,
        /// <summary>Gatilho por acúmulo de attachments (SPEC-20261103
        /// RF-002): bytes de imagens/arquivos na janela acima disso
        /// disparam spawn mesmo abaixo do spawn-ratio — default 8 MB.</summary>
        long AttachmentThresholdBytes = long.MaxValue);

    private static string KvKey(string chatId) => $"chat:{chatId}:compaction";
    private static string KvKeyPending(string chatId) =>
        $"chat:{chatId}:compaction:pending";

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
                "user", SummaryPrefix + FormatSummary(state, chatId,
                    await config.GetAsync(
                        "compaction:summarizer_v3", true, ct))));
            if (!string.IsNullOrWhiteSpace(state.TurnPrefix))
            {
                window.Insert(1, new ChatCompletionMessage(
                    "user", TurnPrefixHeader + state.TurnPrefix));
            }
        }
        return ContextPruner.Prune(window, chatId);
    }

    /// <summary>Resumo + digest + seções v2 (estado, breadcrumbs, records,
    /// tarefas) + ponteiro <c>history://</c> para recuperar o transcript.</summary>
    private string FormatSummary(
        CompactionState state, string chatId, bool renderV3 = true)
    {
        var d = state.Digest;
        var hasDigest = HasDigest(d);
        var hasExtras = state.UserMessages is { Count: > 0 }
            || !string.IsNullOrWhiteSpace(state.TodosJson);
        if (!hasDigest && !hasExtras)
        {
            return $"{state.Summary}\n\n[histórico completo em history://{chatId}]";
        }
        var sb = new System.Text.StringBuilder(d?.Text ?? state.Summary);
        if (!string.IsNullOrWhiteSpace(d?.State))
        {
            sb.Append("\n\nEstado atual: ").Append(d.State.Trim());
        }
        if (d is not null)
        {
            AppendSection(sb, "Arquivos", d.Files);
            AppendSection(sb, "Decisões", d.Decisions);
            AppendSection(sb, "Pendências", d.Pending);
            AppendSection(sb, "Breadcrumbs", d.Breadcrumbs ?? []);
            if (renderV3)
            {
                AppendSection(sb, "## Errors and fixes", d.Errors ?? []);
                // RF-004: next_step sem quote é descartado — a citação
                // verbatim é a garantia contra drift ("sem drift").
                if (d.NextStep is { } ns && !string.IsNullOrWhiteSpace(ns.Quote))
                {
                    sb.Append("\n\n## Optional Next Step\n")
                        .Append(ns.Text.Trim())
                        .Append("\n> \"").Append(ns.Quote.Trim()).Append('"');
                }
                else if (d.NextStep is { } nsNoQuote
                    && !string.IsNullOrWhiteSpace(nsNoQuote.Text))
                {
                    logger.LogWarning(
                        "compaction_next_step_no_quote: seção descartada na compactação do chat {ChatId}.",
                        chatId);
                }
            }
        }
        AppendSection(sb, "Pedidos do usuário (verbatim)", state.UserMessages ?? []);
        if (!string.IsNullOrWhiteSpace(state.TodosJson))
        {
            sb.Append("\n\nTarefas:\n").Append(state.TodosJson);
        }
        sb.Append("\n\n[histórico completo em history://").Append(chatId).Append(']');
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
            var t = await ScaledThresholdsAsync(tailScale, model, ct);
            var total = history.Sum(m => (m.Content?.Length ?? 0) + (m.ToolCallsJson?.Length ?? 0));
            // RF-001: dispara quando tokens OU chars estouram o limiar —
            // a KV de chars sobrevive como override para compactar antes.
            if (!force && ContextTokens.Estimate(total) < t.ThresholdTokens
                && total <= t.TotalChars)
            {
                return false;
            }

            var head = await ComputeHeadSummaryAsync(
                new HeadSummaryArgs(config, roles, summarizer,
                    chatId, history, model, userId), t, ct);
            if (head.HeadEnd <= 0 || head.Summary is null)
            {
                return false;
            }

            await PersistCompactionAsync(chatId,
                new CompactionState(head.HeadEnd, head.Summary.Text,
                    HasDigest(head.Summary) ? head.Summary : null,
                    head.TurnPrefix, head.UserRecords, head.TodosJson), ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Compactação de contexto falhou para chat {ChatId}.", chatId);
            return false;
        }
    }

    /// <summary>Entrada do corte+resumo do head: serviços resolvidos no
    /// scope do caller (instância ou scope próprio da task de spawn) e o
    /// pedido (chat, janela, modelo, usuário).</summary>
    private sealed record HeadSummaryArgs(
        ConfigService Cfg, ModelRoleResolver Roles, IContextSummarizer Sum,
        string ChatId, IReadOnlyList<ChatCompletionMessage> History,
        string Model, string? UserId);

    /// <summary>
    /// Corte do head + transcript podado + chamada do summarizer — trecho
    /// compartilhado entre a compactação síncrona e a task de spawn
    /// (SPEC-20261102 RF-002). Devolve o cutoff efetivo, o prefixo do
    /// turno partido e o resumo (null = summarizer indisponível).
    /// </summary>
    private static async Task<HeadSummary>
        ComputeHeadSummaryAsync(HeadSummaryArgs a, Thresholds t, CancellationToken ct)
    {
        var headEnd = SplitIndex(a.History, t);
        if (headEnd <= 0)
        {
            return new HeadSummary(0, null, null, [], null);
        }
        var (turnPrefix, summaryEnd) = ComputeTurnSplit(a.History, headEnd, a.ChatId);
        var (transcript, headStart) = await BuildTranscriptAsync(
            a.Cfg, a.ChatId, a.History, summaryEnd, ct);
        // Extrações mecânicas (RF-006/007): verbatim determinístico do
        // head pré-poda — independente do que o summarizer devolver.
        var records = ExtractUserRecords(
            a.History.Take(summaryEnd).ToList(),
            await a.Cfg.GetAsync("compaction:user_records", 20, ct),
            await a.Cfg.GetAsync("compaction:user_records_chars", 4_000, ct));
        var todos = ExtractTodosJson(a.History.Take(summaryEnd).ToList());
        // Papel "memory" (SPEC-20261013-model-roles): resumo roda no
        // modelo do papel quando configurado; senão no modelo da run.
        var summaryModel = a.UserId is null
            ? a.Model
            : await a.Roles.ResolveAsync("memory", a.UserId, a.Model, ct);
        var summary = await a.Sum.SummarizeAsync(summaryModel, transcript, a.UserId, ct);
        // Sanity (RF-005): breadcrumbs "[pos]" fora do range do transcript
        // são âncoras alucinadas — descarta best-effort.
        if (summary?.Breadcrumbs is { Count: > 0 })
        {
            summary = summary with
            {
                Breadcrumbs = SanitizeBreadcrumbs(
                    summary.Breadcrumbs, headStart, summaryEnd),
            };
        }
        return new HeadSummary(headEnd, turnPrefix, summary, records, todos);
    }

    /// <summary>Descarta breadcrumbs <c>[pos]</c> fora do range
    /// <c>[lo, hi)</c> das posições marcadas no transcript; outros
    /// formatos (termos, <c>history://</c>) passam intactos.</summary>
    private static IReadOnlyList<string> SanitizeBreadcrumbs(
        IReadOnlyList<string> items, int lo, int hi)
    {
        var kept = new List<string>(items.Count);
        foreach (var raw in items)
        {
            var item = raw?.Trim();
            if (string.IsNullOrEmpty(item))
            {
                continue;
            }
            if (item.StartsWith('['))
            {
                var close = item.IndexOf(']');
                if (close > 1 && int.TryParse(item[1..close], out var pos)
                    && (pos < lo || pos >= hi))
                {
                    continue;
                }
            }
            kept.Add(item);
        }
        return kept;
    }

    /// <summary>Pacote do head: cutoff, prefixo de turno, resumo e as
    /// extrações mecânicas (records verbatim + JSON do plano).</summary>
    private sealed record HeadSummary(
        int HeadEnd, string? TurnPrefix, ContextSummary? Summary,
        IReadOnlyList<string> UserRecords, string? TodosJson);

    /// <summary>
    /// RF-006: últimas <paramref name="maxRecords"/> mensagens
    /// <c>role=user</c> do head, verbatim e em ordem cronológica — cap
    /// <paramref name="maxChars"/> corta do início com marcador de
    /// omissão. Sintéticas (resumo anterior, prefixo de turno, notas de
    /// orçamento/controle) nunca viram record.
    /// </summary>
    private static IReadOnlyList<string> ExtractUserRecords(
        IReadOnlyList<ChatCompletionMessage> head, int maxRecords, int maxChars)
    {
        var real = head
            .Where(m => m.Role == "user"
                && !string.IsNullOrWhiteSpace(m.Content)
                && !m.Content.StartsWith(SummaryPrefix, StringComparison.Ordinal)
                && !m.Content.StartsWith(TurnPrefixHeader, StringComparison.Ordinal)
                && !m.Content.StartsWith("<context_usage>", StringComparison.Ordinal)
                && !m.Content.StartsWith("[contexto]", StringComparison.Ordinal))
            .Select(m => m.Content)
            .ToList();
        if (real.Count == 0)
        {
            return [];
        }
        var skipped = Math.Max(0, real.Count - maxRecords);
        var kept = real.Skip(skipped).ToList();
        var keptChars = kept.Sum(s => s.Length);
        while (kept.Count > 1 && keptChars > maxChars)
        {
            keptChars -= kept[0].Length;
            kept.RemoveAt(0);
            skipped++;
        }
        if (skipped > 0)
        {
            kept.Insert(0, $"[…{skipped} anteriores omitidas]");
        }
        return kept;
    }

    /// <summary>
    /// RF-007: snapshot JSON do plano persistido pelo último call da
    /// tool de tasks no head — sobrevive à compactação. Parse falho →
    /// omite a seção (nunca falha a run). Cap de 4k chars. O nome
    /// persistido pode chegar com prefixo <c>builtin:</c> (tool_call do
    /// provider) ou <c>builtin_</c> (formas internas) — casa os dois.
    /// </summary>
    private static string? ExtractTodosJson(IReadOnlyList<ChatCompletionMessage> head)
    {
        const string toolName = "todo_write";
        for (var i = head.Count - 1; i >= 0; i--)
        {
            var calls = head[i].ToolCallsJson;
            if (calls is not null && calls.Contains(toolName, StringComparison.Ordinal)
                && TryExtractTodosJson(calls) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>Varre as tool_calls de uma mensagem procurando a do
    /// plano de tasks; devolve o JSON <c>todos</c> (cap 4k) ou null.</summary>
    private static string? TryExtractTodosJson(string calls)
    {
        const int maxChars = 4_000;
        try
        {
            using var doc = JsonDocument.Parse(calls);
            foreach (var call in doc.RootElement.EnumerateArray())
            {
                var fnName = call.TryGetProperty("function", out var fn)
                    && fn.TryGetProperty("name", out var name)
                        ? name.GetString() : null;
                var isTodo = fnName is "builtin:todo_write" or "builtin_todo_write"
                    or "todo_write";
                if (!isTodo || !fn.TryGetProperty("arguments", out var argsEl))
                {
                    continue;
                }
                var argsJson = argsEl.ValueKind == JsonValueKind.String
                    ? argsEl.GetString() : argsEl.GetRawText();
                if (argsJson is null)
                {
                    continue;
                }
                using var args = JsonDocument.Parse(argsJson);
                if (args.RootElement.TryGetProperty("todos", out var todos)
                    && todos.ValueKind == JsonValueKind.Array)
                {
                    var raw = todos.GetRawText();
                    return raw.Length > maxChars ? raw[..maxChars] : raw;
                }
            }
        }
        catch (JsonException)
        {
            // Call malformada não bloqueia: segue varrendo para trás.
        }
        return null;
    }

    /// <summary>
    /// Resultado do spawn-check (RF-002/RF-010): se a task disparou e a
    /// estimativa que a motivou — alimenta o evento
    /// <c>compaction.spawned {tokens}</c>. <paramref name="FailedStage"/>
    /// sinaliza exceção no disparo para <c>compaction.failed</c>.
    /// </summary>
    public sealed record SpawnResult(
        bool Spawned, int EstimateTokens, string? FailedStage = null,
        /// <summary>Causa do disparo (SPEC-20261103 RF-006):
        /// <c>"tokens"</c> ou <c>"attachments"</c> — attachments vencem
        /// quando ambos os gatilhos batem (maior severidade).</summary>
        string? Trigger = null);

    /// <summary>
    /// Spawn 3-tier (SPEC-20261102 RF-002): estimativa ≥ spawn e &lt; hard
    /// dispara o resumo em background — a task grava o resultado no kv
    /// <c>:pending</c> para a próxima fronteira aplicar sem bloquear o
    /// turno. Single-flight por chat; pendente pronto inibe re-spawn.
    /// </summary>
    public async Task<SpawnResult> MaybeSpawnAsync(
        string chatId, IReadOnlyList<ChatCompletionMessage> history,
        string model, string? userId, CancellationToken ct)
    {
        try
        {
            var t = await ThresholdsAsync(model, ct);
            var total = history.Sum(m =>
                (m.Content?.Length ?? 0) + (m.ToolCallsJson?.Length ?? 0));
            var estimate = ContextTokens.Estimate(total);
            // RF-002: overflow por attachments dispara spawn mesmo
            // abaixo do spawn-ratio (Claude: "accumulated images and
            // attachments pushed the request over the limit").
            var attachBytes = await AttachmentBytesAsync(history, ct);
            var byTokens = estimate >= t.SpawnTokens || total >= t.SpawnChars;
            var byAttachments = attachBytes > t.AttachmentThresholdBytes;
            // Abaixo do spawn → cedo; ≥ hard → a compactação síncrona do
            // executor cuida (o spawn perdeu a janela).
            if (!byTokens && !byAttachments)
            {
                return new SpawnResult(false, estimate);
            }
            if (estimate >= t.ThresholdTokens || total >= t.TotalChars)
            {
                return new SpawnResult(false, estimate);
            }
            // Pendente já gravado → a fronteira aplica; não re-resume.
            if (await ReadPendingAsync(chatId, ct) is not null)
            {
                return new SpawnResult(false, estimate);
            }
            // Snapshot: a lista do chamador muta entre rounds — a task
            // trabalha sobre cópia estável.
            var snapshot = history.ToList();
            var estimateTokens = estimate;
            var spawned = scheduler.TryStart(chatId, token =>
                CompactToPendingAsync(
                    chatId, snapshot, model, userId, t, estimateTokens, token));
            if (spawned)
            {
                logger.LogInformation(
                    "Compactação em background disparada para chat {ChatId} ({Tokens} tokens estimados).",
                    chatId, estimateTokens);
            }
            return new SpawnResult(spawned, estimateTokens,
                Trigger: byAttachments ? "attachments" : "tokens");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Spawn de compactação falhou para chat {ChatId}.", chatId);
            return new SpawnResult(false, 0, "spawn");
        }
    }

    /// <summary>
    /// Bytes acumulados de attachments na janela (RF-002):
    /// <c>Images</c> contam pelo tamanho base64×0.75; <c>FileIds</c>
    /// resolvem <see cref="FileEntry.Size"/> numa única query — arquivo
    /// sem registro cai na estimativa chars×0.75 do content da mensagem
    /// (edge case "metadado ausente").
    /// </summary>
    private async Task<long> AttachmentBytesAsync(
        IReadOnlyList<ChatCompletionMessage> history, CancellationToken ct)
    {
        var (bytes, fileIds) = CollectAttachmentInfo(history);
        if (fileIds is null || fileIds.Count == 0)
        {
            return bytes;
        }
        var found = await db.Files.AsNoTracking()
            .Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.Size })
            .ToListAsync(ct);
        var foundIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in found)
        {
            bytes += Math.Max(0L, f.Size);
            foundIds.Add(f.Id);
        }
        // Arquivo sem registro cai na estimativa chars×0.75 da mensagem.
        bytes += history
            .Where(m => m.FileIds is { Count: > 0 })
            .Sum(m => m.FileIds!.Count(id => !foundIds.Contains(id))
                * (long)((m.Content?.Length ?? 0) * 0.75));
        return bytes;
    }

    /// <summary>Bytes das imagens inline + conjunto de FileIds da janela.</summary>
    private static (long Bytes, HashSet<string>? FileIds) CollectAttachmentInfo(
        IReadOnlyList<ChatCompletionMessage> history)
    {
        long bytes = 0;
        HashSet<string>? fileIds = null;
        foreach (var m in history)
        {
            if (m.Images is { Count: > 0 } images)
            {
                foreach (var img in images)
                {
                    bytes += (img.Base64Data?.Length ?? 0) * 3 / 4;
                }
            }
            if (m.FileIds is { Count: > 0 } ids)
            {
                fileIds ??= new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in ids)
                {
                    fileIds.Add(id);
                }
            }
        }
        return (bytes, fileIds);
    }

    /// <summary>Task de background: resume o head do snapshot e grava
    /// no kv <c>:pending</c> — nunca lança (fire-and-forget). Com
    /// <see cref="IServiceScopeFactory"/> disponível roda num scope
    /// próprio (DbContext/ConfigService/Summarizer frescos) — o scope do
    /// chamador pode ser disposto antes do fim da task.</summary>
    private async Task CompactToPendingAsync(
        string chatId, IReadOnlyList<ChatCompletionMessage> snapshot,
        string model, string? userId, Thresholds t, int estimateTokens,
        CancellationToken ct)
    {
        try
        {
            if (scopes is not null)
            {
                await using var scope = scopes.CreateAsyncScope();
                var sp = scope.ServiceProvider;
                await CompactToPendingCoreAsync(
                    new HeadSummaryArgs(
                        sp.GetRequiredService<ConfigService>(),
                        sp.GetRequiredService<ModelRoleResolver>(),
                        sp.GetRequiredService<IContextSummarizer>(),
                        chatId, snapshot, model, userId),
                    t, estimateTokens, ct);
            }
            else
            {
                await CompactToPendingCoreAsync(
                    new HeadSummaryArgs(config, roles, summarizer,
                        chatId, snapshot, model, userId),
                    t, estimateTokens, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Compactação em background falhou para chat {ChatId}.", chatId);
        }
    }

    private static async Task CompactToPendingCoreAsync(
        HeadSummaryArgs a, Thresholds t, int estimateTokens,
        CancellationToken ct)
    {
        var head = await ComputeHeadSummaryAsync(a, t, ct);
        if (head.HeadEnd <= 0 || head.Summary is null)
        {
            return;
        }
        var state = new CompactionState(head.HeadEnd, head.Summary.Text,
            HasDigest(head.Summary) ? head.Summary : null, head.TurnPrefix,
            head.UserRecords, head.TodosJson);
        await a.Cfg.SetAsync(KvKeyPending(a.ChatId),
            new PendingCompaction(state,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                estimateTokens), ct);
    }

    /// <summary>Resultado do apply (RF-003/RF-010): se a janela mudou e
    /// metadados do resumo aplicado para o evento
    /// <c>compaction.applied {cutoff, spawned_at, applied_at}</c>.</summary>
    public sealed record PendingApplyResult(
        bool Applied, int? Cutoff, long? SpawnedAt);

    /// <summary>
    /// Fronteira de aplicação (SPEC-20261102 RF-003): se há pendente válido
    /// para o chat — cutoff positivo, dentro do range da janela atual e
    /// mais novo que o aplicado — move para o estado aplicado (kv +
    /// marker no chat); senão descarta/devolve não-aplicado.
    /// </summary>
    public async Task<PendingApplyResult> ApplyPendingAsync(
        string chatId, int currentCount, CancellationToken ct)
    {
        var pending = await ReadPendingAsync(chatId, ct);
        if (pending is null)
        {
            return new PendingApplyResult(false, null, null);
        }
        var applied = await config.GetAsync<CompactionState?>(
            KvKey(chatId), null, ct);
        if (pending.State.Cutoff <= 0
            || pending.State.Cutoff >= currentCount
            || (applied?.Cutoff ?? 0) >= pending.State.Cutoff)
        {
            await config.SetAsync<PendingCompaction?>(
                KvKeyPending(chatId), null, ct);
            return new PendingApplyResult(false, null, null);
        }
        await PersistCompactionAsync(chatId, pending.State, ct);
        await config.SetAsync<PendingCompaction?>(
            KvKeyPending(chatId), null, ct);
        return new PendingApplyResult(
            true, pending.State.Cutoff, pending.SpawnedAt);
    }

    /// <summary>
    /// Hard-wait (SPEC-20261102 RF-004): espera a task em voo até
    /// <paramref name="timeout"/> e aplica o resultado; devolve aplicado
    /// só quando a janela realmente mudou — o chamador cai no caminho
    /// síncrono quando não aplicado (sem task, timeout ou pendente
    /// inválido).
    /// </summary>
    public async Task<PendingApplyResult> WaitPendingAsync(
        string chatId, TimeSpan timeout, CancellationToken ct,
        int currentCount = int.MaxValue)
    {
        await scheduler.WaitAsync(chatId, timeout, ct);
        // Com ou sem task em voo, pendente pronto ainda pode aplicar.
        return await ApplyPendingAsync(chatId, currentCount, ct);
    }

    /// <summary>
    /// RF-008 (SPEC-20261102): injeta a nota de orçamento
    /// <c>&lt;context_usage&gt;</c> como última mensagem da janela —
    /// estimativa pós-poda em k-tokens, espelhando o aviso por-rodada do
    /// Devin CLI. Flag <c>compaction:context_usage</c> (default true);
    /// playground (sem chatId) nunca injeta.
    /// </summary>
    public async Task<IReadOnlyList<ChatCompletionMessage>> WithContextUsageAsync(
        string? chatId, IReadOnlyList<ChatCompletionMessage> messages,
        string model, CancellationToken ct)
    {
        if (chatId is null
            || !await config.GetAsync("compaction:context_usage", true, ct))
        {
            return messages;
        }
        var t = await ThresholdsAsync(model, ct);
        var total = messages.Sum(m =>
            (m.Content?.Length ?? 0) + (m.ToolCallsJson?.Length ?? 0));
        var used = ContextTokens.Estimate(total) / 1_000;
        var limit = t.ThresholdTokens / 1_000;
        return
        [
            .. messages,
            new ChatCompletionMessage("system",
                "<context_usage>\n" + used + "k of " + limit
                + "k tokens used. Ignore this: at the limit your context is "
                + "automatically summarized and you keep working with your "
                + "memory of the task intact; only the full outputs of earlier "
                + "tool calls are hidden. Never tell the user."
                + "\n</context_usage>"),
        ];
    }

    /// <summary>Resultado do hard-wait (RF-004/RF-010): se a espera foi
    /// acionada (≥ hard), se aplicou, quanto esperou e metadados do
    /// resumo aplicado para os eventos <c>compaction.hard_wait</c> /
    /// <c>compaction.applied</c>. <paramref name="FailedStage"/> sinaliza
    /// exceção no caminho para <c>compaction.failed</c>.</summary>
    public sealed record HardWaitResult(
        bool Waited, bool Applied, int WaitedMs,
        int? Cutoff = null, long? SpawnedAt = null,
        string? FailedStage = null);

    /// <summary>
    /// Hard cap (SPEC-20261102 RF-004): estimativa ≥ hard → aguarda até
    /// <c>compaction:hard_wait_ms</c> (default 45_000, clamp 5_000–120_000)
    /// pela task em voo e aplica o resumo; expirada/ausente devolve
    /// <c>Applied=false</c> — o chamador cai no caminho síncrono.
    /// Abaixo do hard é no-op (a banda spawn não bloqueia o turno).
    /// </summary>
    public async Task<HardWaitResult> HardWaitAsync(
        string chatId, IReadOnlyList<ChatCompletionMessage> history,
        string model, CancellationToken ct)
    {
        try
        {
            var t = await ThresholdsAsync(model, ct);
            var total = history.Sum(m =>
                (m.Content?.Length ?? 0) + (m.ToolCallsJson?.Length ?? 0));
            var estimate = ContextTokens.Estimate(total);
            if (estimate < t.ThresholdTokens && total < t.TotalChars)
            {
                return new HardWaitResult(false, false, 0);
            }
            var waitMs = Math.Clamp(
                await config.GetAsync(
                    "compaction:hard_wait_ms", 45_000, ct),
                5_000, 120_000);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var applied = await WaitPendingAsync(
                chatId, TimeSpan.FromMilliseconds(waitMs), ct, history.Count);
            sw.Stop();
            return new HardWaitResult(
                true, applied.Applied, (int)sw.ElapsedMilliseconds,
                applied.Cutoff, applied.SpawnedAt);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Hard-wait de compactação falhou para chat {ChatId}.", chatId);
            return new HardWaitResult(false, false, 0, FailedStage: "hard_wait");
        }
    }

    /// <summary>
    /// Leitura sempre fresca do kv <c>:pending</c> — o valor é gravado por
    /// outro <see cref="ConfigService"/> (scope da task), então o L1 do
    /// HybridCache desta instância seguraria um null obsoleto lido no
    /// spawn-check. O kv é interno deste serviço: consulta direta na base.
    /// </summary>
    private async Task<PendingCompaction?> ReadPendingAsync(
        string chatId, CancellationToken ct)
    {
        var entry = await db.ConfigEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == KvKeyPending(chatId), ct);
        if (entry is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<PendingCompaction>(
                entry.ValueJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Limiares com escala do tail (retry de overflow, RF-004).</summary>
    private async Task<Thresholds> ScaledThresholdsAsync(
        double tailScale, string model, CancellationToken ct)
    {
        var t = await ThresholdsAsync(model, ct);
        return tailScale >= 1.0
            ? t
            : new Thresholds(
                t.TotalChars,
                Math.Max(4, (int)(t.TailMessages * tailScale)),
                Math.Max(2000, (int)(t.TailChars * tailScale)),
                t.ThresholdTokens, t.SpawnTokens, t.SpawnChars);
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

    /// <summary>Transcript do head podado + resumo anterior (se houver) —
    /// devolve também o índice absoluto de início do head para validar
    /// breadcrumbs <c>[pos]</c>.</summary>
    private static async Task<(string Transcript, int HeadStart)> BuildTranscriptAsync(
        ConfigService cfg,
        string chatId, IReadOnlyList<ChatCompletionMessage> history,
        int summaryEnd, CancellationToken ct)
    {
        var prev = await cfg.GetAsync<CompactionState?>(KvKey(chatId), null, ct);
        var newHeadStart = prev is { Cutoff: > 0 } && prev.Cutoff < summaryEnd
            ? prev.Cutoff : 0;
        // Poda mecânica do head antes do resumo (RF-001): tool results
        // elidíveis viram marcadores — o summarizer não paga por lixo.
        var head = ContextPruner.Prune(
            history.Skip(newHeadStart).Take(summaryEnd - newHeadStart).ToList(), chatId);
        // Marcadores [pos] (RF-005): posição absoluta no histórico — os
        // breadcrumbs do resumo podem citá-los como âncoras de busca.
        var transcript = (prev is not null && newHeadStart > 0
                ? $"Resumo anterior já compactado: {prev.Summary}\n\n" : string.Empty)
            + string.Join("\n\n", head.Select(
                (m, i) => $"[{newHeadStart + i}] {m.Role}: {m.Content}"));
        return (transcript, newHeadStart);
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
                    var t = await ThresholdsAsync(model, ct);
                    var headEnd = SplitIndex(history, t);
                    if (headEnd > 0)
                    {
                        await PersistCompactionAsync(
                            chatId, new CompactionState(headEnd, text), ct);
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
        string chatId, CompactionState state, CancellationToken ct)
    {
        await config.SetAsync(KvKey(chatId), state, ct);

        var tracked = await db.Chats.Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (tracked is not null)
        {
            var pos = tracked.Messages.Count == 0 ? 0 : tracked.Messages.Max(m => m.Position) + 1;
            // O marcador pendura no leaf do caminho compactado
            // (SPEC-20261013-chat-branching): outro ramo não o herda.
            var marker = new ChatMessage
            {
                ChatId = tracked.Id, Role = "compaction", Content = state.Summary,
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

    /// <summary>Se o resumo carrega digest estruturado — qualquer seção
    /// v2/v3 preenchida (alinhado com o que <see cref="FormatSummary"/>
    /// renderiza, SPEC-20261103 RF-003).</summary>
    private static bool HasDigest(ContextSummary? s) =>
        s is not null
        && (s.Files.Count > 0 || s.Decisions.Count > 0 || s.Pending.Count > 0
            || !string.IsNullOrWhiteSpace(s.State)
            || s.Breadcrumbs is { Count: > 0 }
            || s.Errors is { Count: > 0 }
            || s.NextStep is not null);

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

    /// <summary>
    /// Limiares efetivos da run (SPEC-20261102 RF-001): hard =
    /// <c>compaction:threshold_tokens</c>; spawn =
    /// <c>hard × compaction:spawn_ratio</c> (default 0.70, clamp
    /// 0.30–0.95, inválido → default). Overrides por modelo em
    /// <c>compaction:{model}:threshold_tokens|spawn_ratio</c> caem no
    /// global quando ausentes/inválidos.
    /// </summary>
    private async Task<Thresholds> ThresholdsAsync(
        string model, CancellationToken ct)
    {
        var total = await config.GetAsync("compaction:threshold", DefaultThresholdChars, ct);
        var tailMsgs = await config.GetAsync("compaction:tail_messages", DefaultTailMessages, ct);
        var tailChars = await config.GetAsync("compaction:tail_chars", DefaultTailChars, ct);
        var thresholdTokens = Math.Clamp(
            await ModelConfigAsync("threshold_tokens", model,
                DefaultThresholdTokens, ct),
            32_000, 256_000);
        var ratio = Math.Clamp(
            await ModelConfigAsync("spawn_ratio", model, DefaultSpawnRatio, ct),
            MinSpawnRatio, MaxSpawnRatio);
        // spawn < hard sempre: piso em min(32k, hard-1), teto em hard-1.
        var spawnTokens = Math.Clamp(
            (int)(thresholdTokens * ratio),
            Math.Min(32_000, thresholdTokens - 1),
            thresholdTokens - 1);
        var spawnChars = Math.Clamp(
            (long)(total * ratio), 1L, Math.Max(1L, (long)total - 1));
        var attachBytes = Math.Max(1L,
            await config.GetAsync(
                "compaction:attachment_threshold_bytes",
                DefaultAttachmentThresholdBytes, ct));
        return new Thresholds(
            total, tailMsgs, tailChars,
            thresholdTokens, spawnTokens, spawnChars, attachBytes);
    }

    /// <summary>Lê <c>compaction:{model}:{suffix}</c> com fallback à chave
    /// global <c>compaction:{suffix}</c>; valor ausente/inválido cai no
    /// global. A leitura por-modelo vai direto na base: o slot nullable
    /// de <see cref="ConfigService.GetAsync{T}"/> devolve
    /// <c>default(T?)</c>=0 para value types em miss — não dá para
    /// distinguir "não configurado" de "configurado como 0" pelo cache.</summary>
    private async Task<T> ModelConfigAsync<T>(
        string suffix, string model, T defaultValue, CancellationToken ct)
        where T : struct
    {
        var perModel = await ModelValueAsync<T>(
            $"compaction:{model}:{suffix}", ct);
        return perModel is not null
            ? perModel.Value
            : await config.GetAsync($"compaction:{suffix}", defaultValue, ct);
    }

    /// <summary>Leitura exata da kv por-modelo — null em ausente/inválida.</summary>
    private async Task<T?> ModelValueAsync<T>(string key, CancellationToken ct)
        where T : struct
    {
        var entry = await db.ConfigEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == key, ct);
        if (entry is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(entry.ValueJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
