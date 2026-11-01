using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Consome os endpoints SSE de chat — <c>/api/chat/completions</c> (direto,
/// legado) e o attach de runs desacopladas
/// <c>/api/v1/chats/{id}/runs/{runId}/stream</c> — produzindo deltas de
/// conteúdo. Erros do servidor viram exceção na enumeração.
/// </summary>
public class ChatStreamService(HttpClient http, AuthService auth)
{
    /// <summary>
    /// Evento tipado do stream de uma run (SPEC-20261007-chat-tool-streaming):
    /// delta de texto, tool_call/tool_result, fase de status ou pedido de
    /// aprovação. O endpoint legado só produz <see cref="Delta"/>.
    /// </summary>
    public abstract record ChatStreamEvent
    {
        /// <summary>
        /// Seq do evento no broadcaster da run (linha <c>id:</c> do SSE).
        /// Usado pelo consumidor para retomar com <c>lastSeq</c> após queda
        /// do stream — replay só devolve seq &gt; lastSeq (sem duplicar).
        /// </summary>
        public int Seq { get; init; }

        /// <summary>Pedaco de texto do assistant.</summary>
        public sealed record Delta(string Text) : ChatStreamEvent;

        /// <summary>
        /// Pedaço de raciocínio/thinking do modelo
        /// (SPEC-20261015-chat-thinking-stream): canal separado do content —
        /// a UI exibe numa seção colapsável e nada entra na mensagem final.
        /// </summary>
        public sealed record Reasoning(string Text) : ChatStreamEvent;

        /// <summary>
        /// Keepalive do servidor (linha <c>: hb</c>) — prova de que a
        /// conexão SSE segue viva; re-arma o watchdog do consumidor.
        /// </summary>
        public sealed record Heartbeat : ChatStreamEvent;

        /// <summary>Tool call iniciada (antes de executar).</summary>
        public sealed record ToolCall(RunToolCallEvent Call) : ChatStreamEvent;

        /// <summary>Resultado/negação de uma tool call.</summary>
        public sealed record ToolResult(RunToolResultEvent Result) : ChatStreamEvent;

        /// <summary>Snapshot <c>changes</c>: arquivos alterados pela run (aba Changes do painel).</summary>
        public sealed record Changes(RunChangesEvent Snapshot) : ChatStreamEvent;

        /// <summary>Checkpoint do workdir criado pela run (S6 — botão Revert).</summary>
        public sealed record Checkpoint(RunCheckpointEvent Snapshot) : ChatStreamEvent;

        /// <summary>Fase da run (generating|running_tool|awaiting_approval).</summary>
        public sealed record Phase(RunPhaseEvent Status) : ChatStreamEvent;

        /// <summary>Aprovação pedida — a run pausou esperando decisão.</summary>
        public sealed record ApprovalAsked(RunApprovalAskedEvent Asked) : ChatStreamEvent;

        /// <summary>Pergunta estruturada do ask_user aguardando resposta (RF-005).</summary>
        public sealed record QuestionAsked(RunQuestionAskedEvent Asked) : ChatStreamEvent;

        /// <summary>Decisão automática do preset autônomo (SPEC-15 RF-003).</summary>
        public sealed record DecisionAuto(RunDecisionAutoEvent Info) : ChatStreamEvent;

        /// <summary>Evento <c>mode</c>: modo do agente do chat mudou mid-run.</summary>
        public sealed record Mode(RunModeEvent Info) : ChatStreamEvent;

        /// <summary>
        /// Snapshot da lista de tarefas da run (evento <c>tasks</c> —
        /// SPEC-20261007-chat-agent-parity RF-010, emitido por todo_write).
        /// </summary>
        public sealed record Tasks(RunTasksEvent Snapshot) : ChatStreamEvent;

        /// <summary>Conselho do revisor de run (SPEC-20261013-run-advisor RF-003).</summary>
        public sealed record Advisor(RunAdvisorEvent Event) : ChatStreamEvent;
    }

    /// <summary>Resultado de arena da última requisição (payload {"arena": ...}), quando houver.</summary>
    public ArenaCompletionResult? LastArenaResult { get; private set; }

    /// <summary>
    /// Envia a requisição e produz os deltas de texto conforme chegam.
    /// Erros do servidor são produzidos como exceção na enumeração.
    /// </summary>
    /// <param name="request">Requisição de completion.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastArenaResult = null;

        using var httpRequest = auth.CreateRequest(HttpMethod.Post, "/api/chat/completions");
        httpRequest.SetBrowserResponseStreamingEnabled(true);
        httpRequest.Content = JsonContent.Create(request);

        using var response = await http.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "gerar resposta", ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var delta in ReadDeltasAsync(stream, ct))
        {
            yield return delta;
        }
    }

    /// <summary>
    /// Enfileira uma run desacoplada (SPEC-20261007-chat-detached-runs):
    /// a mensagem é persistida no servidor e a geração roda em background —
    /// sobrevive a reload/fechamento da aba. <paramref name="content"/> null
    /// regenera sobre o histórico existente.
    /// </summary>
    public sealed record EnqueueOptions(
        IReadOnlyList<string>? FileIds = null,
        IReadOnlyList<string>? ToolIds = null,
        bool? WebSearch = null,
        IReadOnlyList<string>? MentionPaths = null,
        string? ForkFromMessageId = null,
        string? Command = null,
        string? CommandArgs = null);

    public async Task<ChatRunResponse> EnqueueRunAsync(
        string chatId,
        string? content,
        string model,
        EnqueueOptions? options = null,
        CancellationToken ct = default)
    {
        var fileIds = options?.FileIds;
        var toolIds = options?.ToolIds;
        var webSearch = options?.WebSearch;
        var mentionPaths = options?.MentionPaths;
        var forkFromMessageId = options?.ForkFromMessageId;
        var command = options?.Command;
        var commandArgs = options?.CommandArgs;
        using var httpRequest = auth.CreateRequest(
            HttpMethod.Post, $"/api/v1/chats/{chatId}/messages");
        httpRequest.Content = JsonContent.Create(new EnqueueChatRunRequest(
            content, model, fileIds, toolIds, WebSearch: webSearch,
            MentionPaths: mentionPaths, ForkFromMessageId: forkFromMessageId,
            Command: command, CommandArgs: commandArgs));

        using var response = await http.SendAsync(httpRequest, ct);
        await EnsureSuccessAsync(response, "enviar mensagem", ct);
        return (await response.Content.ReadFromJsonAsync<ChatRunResponse>(ct))!;
    }

    /// <summary>Run ativa (queued/running) do chat, ou null.</summary>
    public async Task<ChatRunResponse?> GetActiveRunAsync(
        string chatId, CancellationToken ct = default)
    {
        using var httpRequest = auth.CreateRequest(
            HttpMethod.Get, $"/api/v1/chats/{chatId}/runs/active");
        using var response = await http.SendAsync(httpRequest, ct);
        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentLength is null or 0)
        {
            return null;
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<ChatRunResponse>(ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Estado atual de uma run (usado pelo loop de resume para saber se ela já finalizou).</summary>
    public async Task<ChatRunResponse?> GetRunAsync(
        string chatId, string runId, CancellationToken ct = default)
    {
        using var httpRequest = auth.CreateRequest(
            HttpMethod.Get, $"/api/v1/chats/{chatId}/runs/{runId}");
        using var response = await http.SendAsync(httpRequest, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<ChatRunResponse>(ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Runs recentes do chat (mais nova primeiro — máx. 20).</summary>
    public async Task<List<ChatRunResponse>> GetRunsAsync(
        string chatId, CancellationToken ct = default)
    {
        using var httpRequest = auth.CreateRequest(
            HttpMethod.Get, $"/api/v1/chats/{chatId}/runs");
        using var response = await http.SendAsync(httpRequest, ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        try
        {
            return await response.Content
                .ReadFromJsonAsync<List<ChatRunResponse>>(ct) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Pede a interrupção de uma run ativa.</summary>
    public async Task<bool> StopRunAsync(
        string chatId, string runId, CancellationToken ct = default)
    {
        using var httpRequest = auth.CreateRequest(
            HttpMethod.Post, $"/api/v1/chats/{chatId}/runs/{runId}/stop");
        using var response = await http.SendAsync(httpRequest, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Suspende uma run ativa — o executor bloqueia no próximo checkpoint.</summary>
    public async Task<bool> PauseRunAsync(
        string chatId, string runId, CancellationToken ct = default)
    {
        using var httpRequest = auth.CreateRequest(
            HttpMethod.Post, $"/api/v1/chats/{chatId}/runs/{runId}/pause");
        using var response = await http.SendAsync(httpRequest, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Retoma uma run pausada de onde ela parou.</summary>
    public async Task<bool> ResumeRunAsync(
        string chatId, string runId, CancellationToken ct = default)
    {
        using var httpRequest = auth.CreateRequest(
            HttpMethod.Post, $"/api/v1/chats/{chatId}/runs/{runId}/resume");
        using var response = await http.SendAsync(httpRequest, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Anexa ao stream de uma run: replay dos eventos com seq &gt;
    /// <paramref name="lastSeq"/> e depois os vivos até a run fechar.
    /// Produz os mesmos deltas de <see cref="StreamCompletionAsync"/>.
    /// </summary>
    public async IAsyncEnumerable<string> StreamRunAsync(
        string chatId,
        string runId,
        int lastSeq = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var evt in StreamRunEventsAsync(chatId, runId, lastSeq, ct))
        {
            if (evt is ChatStreamEvent.Delta delta)
            {
                yield return delta.Text;
            }
        }
    }

    /// <summary>
    /// Anexa ao stream de uma run produzindo os eventos tipados
    /// (<see cref="ChatStreamEvent"/>): deltas de texto mais tool_call,
    /// tool_result, fase de status e pedidos de aprovação
    /// (SPEC-20261007-chat-tool-streaming). Eventos desconhecidos são
    /// ignorados (forward-compat).
    /// </summary>
    public async IAsyncEnumerable<ChatStreamEvent> StreamRunEventsAsync(
        string chatId,
        string runId,
        int lastSeq = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        LastArenaResult = null;

        using var httpRequest = auth.CreateRequest(
            HttpMethod.Get, $"/api/v1/chats/{chatId}/runs/{runId}/stream?lastSeq={lastSeq}");
        httpRequest.SetBrowserResponseStreamingEnabled(true);

        using var response = await http.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "anexar à run", ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var evt in ReadEventsAsync(stream, ct))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// Envia a decisão do usuário sobre uma aprovação pendente
    /// (<c>approve</c>|<c>deny</c>; remember vale nesta conversa; message
    /// é instrução opcional numa negação — vira o resultado da tool,
    /// SPEC-20261007-chat-agent-ux; rememberPersist grava override
    /// persistente, SPEC-20261013-approval-tiers).
    /// Retorna false quando a call não está mais pendente.
    /// </summary>
    public async Task<bool> DecideApprovalAsync(
        string chatId, string runId, string callId,
        RunApprovalDecisionRequest decision, CancellationToken ct = default)
    {
        using var httpRequest = auth.CreateRequest(
            HttpMethod.Post,
            $"/api/v1/chats/{chatId}/runs/{runId}/approvals/{callId}");
        httpRequest.Content = JsonContent.Create(decision);
        using var response = await http.SendAsync(httpRequest, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Lê linhas `data:` do SSE e produz os deltas de conteúdo.</summary>
    private async IAsyncEnumerable<string> ReadDeltasAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in ReadEventsAsync(stream, ct))
        {
            if (evt is ChatStreamEvent.Delta delta)
            {
                yield return delta.Text;
            }
        }
    }

    /// <summary>
    /// Parser SSE: lê pares `event:`/`data:` e produz
    /// <see cref="ChatStreamEvent"/>s — `data:` sem `event:` segue o
    /// formato OpenAI (deltas, erro, arena); `data: [DONE]` encerra.
    /// </summary>
    private async IAsyncEnumerable<ChatStreamEvent> ReadEventsAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        string? eventName = null;
        var lastSeq = 0;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            // Keepalive do servidor (`: hb`) — não é `data:` mas prova que
            // o stream segue vivo (proxies derrubam/fecham SSE ocioso).
            if (line.StartsWith(':'))
            {
                yield return new ChatStreamEvent.Heartbeat();
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }
            if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                // Last-Event-ID do bloco — o consumidor usa como checkpoint
                // de resume (`lastSeq` no próximo attach).
                _ = int.TryParse(line["id:".Length..].Trim(), out lastSeq);
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
            {
                yield break;
            }

            var evt = eventName;
            eventName = null;
            var (produced, error, parsed) = ParseDataPayload(payload, evt);
            if (!parsed)
            {
                continue;
            }

            if (error is not null)
            {
                throw new InvalidOperationException(error);
            }
            if (produced is not null)
            {
                foreach (var item in produced)
                {
                    yield return item with { Seq = lastSeq };
                }
            }
        }
    }

    /// <summary>
    /// Parse de uma linha `data:` — retorna <c>(produced, error, parsed)</c>.
    /// Evento individual malformado/divergente não deve derrubar o stream —
    /// <c>parsed=false</c> e a próxima linha segue o consumo.
    /// </summary>
    private (List<ChatStreamEvent>?, string?, bool) ParseDataPayload(
        string payload, string? evt)
    {
        try
        {
            var node = JsonNode.Parse(payload);
            // Gateways podem emitir `data:` com JSON não-objeto (string
            // solta, número, bool — keepalive/erro de provider). Indexar
            // JsonValue por nome lança InvalidOperationException ("requires
            // an element of type 'Object'") e derrubava o stream inteiro.
            if (node is null || node is JsonValue)
            {
                return (null, null, false);
            }
            var produced = ParseEventPayload(node, evt, out var error);
            return (produced, error, true);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            return (null, null, false);
        }
    }

    /// <summary>Despacha o payload por <c>event:</c>; sem nome segue o formato OpenAI.</summary>
    private List<ChatStreamEvent>? ParseEventPayload(JsonNode node, string? evt, out string? error)
    {
        error = null;
        var single = evt switch
        {
            "tool_call" => ParseToolCall(node),
            "tool_result" => ParseToolResult(node),
            "question_asked" => ParseQuestionAsked(node),
            "decision_auto" => ParseDecisionAuto(node),
            "approval_asked" => ParseApprovalAsked(node),
            "mode" => ParseMode(node),
            "tasks" => ParseTasks(node),
            "advisor" => ParseAdvisor(node),
            "changes" => ParseChanges(node),
            "checkpoint" => ParseCheckpoint(node),
            "status" => ParseStatus(node),
            _ => null,
        };
        if (single is not null)
        {
            return [single];
        }
        return evt is null ? ParseOpenAiChunk(node, out error) : null;
    }

    private static ChatStreamEvent ParseToolCall(JsonNode node) =>
        new ChatStreamEvent.ToolCall(
            new RunToolCallEvent(
                node["id"]?.GetValue<string>() ?? string.Empty,
                node["name"]?.GetValue<string>() ?? "tool",
                node["argsPreview"]?.GetValue<string>()));

    private static ChatStreamEvent ParseToolResult(JsonNode node) =>
        new ChatStreamEvent.ToolResult(
            new RunToolResultEvent(
                node["id"]?.GetValue<string>() ?? string.Empty,
                node["name"]?.GetValue<string>() ?? "tool",
                node["ok"]?.GetValue<bool>() ?? false,
                node["preview"]?.GetValue<string>(),
                node["imagePath"]?.GetValue<string>(),
                node["denied"]?.GetValue<bool>() ?? false,
                node["result"] is { } resultNode
                    ? JsonDocument.Parse(resultNode.ToJsonString()).RootElement
                    : null,
                node["videoPath"]?.GetValue<string>()));

    private static ChatStreamEvent ParseQuestionAsked(JsonNode node)
    {
        RunQuestionItem[]? questions = null;
        if (node["questions"] is JsonArray arr && arr.Count > 0)
        {
            questions = [.. arr
                .Where(q => q is not null)
                .Select(q => new RunQuestionItem(
                    q!["question"]?.GetValue<string>() ?? "?",
                    q["header"]?.GetValue<string>(),
                    (q["options"] as JsonArray)?
                        .Select(o => o?.GetValue<string>() ?? string.Empty)
                        .ToArray() ?? [],
                    q["multiple"]?.GetValue<bool>() ?? false,
                    q["required"]?.GetValue<bool>() ?? true))];
        }
        return new ChatStreamEvent.QuestionAsked(
            new RunQuestionAskedEvent(
                node["callId"]?.GetValue<string>() ?? string.Empty,
                node["question"]?.GetValue<string>() ?? string.Empty,
                (node["options"] as JsonArray)?
                    .Select(o => o?.GetValue<string>() ?? string.Empty)
                    .ToArray() ?? [],
                node["multiple"]?.GetValue<bool>() ?? false,
                questions,
                node["tool"]?.GetValue<string>()));
    }

    private static ChatStreamEvent ParseDecisionAuto(JsonNode node) =>
        new ChatStreamEvent.DecisionAuto(
            new RunDecisionAutoEvent(
                node["callId"]?.GetValue<string>() ?? string.Empty,
                node["source"]?.GetValue<string>() ?? "ask_user",
                node["chosen"]?.GetValue<string>() ?? string.Empty,
                node["reason"]?.GetValue<string>() ?? "recommended_option"));

    private static ChatStreamEvent ParseApprovalAsked(JsonNode node) =>
        new ChatStreamEvent.ApprovalAsked(
            new RunApprovalAskedEvent(
                node["callId"]?.GetValue<string>() ?? string.Empty,
                node["toolName"]?.GetValue<string>() ?? "tool",
                node["kind"]?.GetValue<string>() ?? "http",
                node["argsPreview"]?.GetValue<string>()));

    private static ChatStreamEvent ParseMode(JsonNode node) =>
        new ChatStreamEvent.Mode(
            new RunModeEvent(
                node["mode"]?.GetValue<string>() ?? "build"));

    /// <summary>Evento <c>advisor</c>: conselho curto do revisor de run.</summary>
    private static ChatStreamEvent ParseAdvisor(JsonNode node) =>
        new ChatStreamEvent.Advisor(
            new RunAdvisorEvent(
                node["text"]?.GetValue<string>() ?? string.Empty,
                node["trigger"]?.GetValue<string>() ?? string.Empty));

    private static ChatStreamEvent ParseTasks(JsonNode node)
    {
        var arr = node as JsonArray ?? node["tasks"] as JsonArray;
        var items = (arr ?? [])
            .Select(t => new RunTaskItem(
                t?["id"]?.GetValue<string>() ?? string.Empty,
                t?["content"]?.GetValue<string>() ?? string.Empty,
                t?["status"]?.GetValue<string>() ?? "pending"))
            .ToList();
        return new ChatStreamEvent.Tasks(new RunTasksEvent(items));
    }

    private static ChatStreamEvent ParseChanges(JsonNode node)
    {
        var arr = node["changes"] as JsonArray ?? node as JsonArray ?? [];
        var items = arr
            .Select(c => new RunChangeItem(
                c?["path"]?.GetValue<string>() ?? string.Empty,
                c?["added"]?.GetValue<int>() ?? 0,
                c?["removed"]?.GetValue<int>() ?? 0,
                c?["diff"]?.GetValue<string>()))
            .ToList();
        return new ChatStreamEvent.Changes(new RunChangesEvent(items));
    }

    private static ChatStreamEvent ParseCheckpoint(JsonNode node)
    {
        var files = (node["files"] as JsonArray ?? [])
            .Select(f => f?.GetValue<string>() ?? string.Empty)
            .Where(f => f.Length > 0)
            .ToList();
        return new ChatStreamEvent.Checkpoint(new RunCheckpointEvent(
            node["hash"]?.GetValue<string>() ?? string.Empty,
            files,
            node["turn"]?.GetValue<int>() ?? 0));
    }

    private static ChatStreamEvent? ParseStatus(JsonNode node)
    {
        // Fases do executor usam "phase"; transições de estado da
        // run (dispatcher/FinishAsync) usam "status".
        if ((node["phase"] ?? node["status"])?.GetValue<string>() is { } phase)
        {
            return new ChatStreamEvent.Phase(
                new RunPhaseEvent(
                    phase, node["label"]?.GetValue<string>()));
        }
        return null;
    }

    /// <summary>Chunk do formato OpenAI (sem <c>event:</c>): delta, reasoning,
    /// erro ou arena — um chunk pode carregar reasoning E content juntos.</summary>
    private List<ChatStreamEvent>? ParseOpenAiChunk(JsonNode node, out string? error)
    {
        error = node["error"]?.GetValue<string>();
        // Gateways podem emitir chunks sem choice (só role/usage/keepalive):
        // indexar um array vazio lança ArgumentOutOfRangeException.
        var choices = node["choices"] as JsonArray;
        var delta = choices is { Count: > 0 } ? choices[0]?["delta"] : null;
        var content = delta?["content"]?.GetValue<string>();
        var arena = node["arena"];
        if (arena is not null)
        {
            LastArenaResult = new ArenaCompletionResult(
                arena["battle_id"]?.GetValue<string>() ?? string.Empty,
                (arena["responses"] as JsonArray ?? [])
                    .Select(r => new ArenaCompletionResponse(
                        r?["label"]?.GetValue<string>() ?? "?",
                        r?["content"]?.GetValue<string>() ?? string.Empty))
                    .ToList());
        }

        List<ChatStreamEvent>? events = null;
        // SPEC-20261015 RF-003: reasoning/thinking — campo normalizado
        // `reasoning` (tradutores) + campos nativos do pass-through
        // OpenAI-compatível (reasoning_content / reasoning /
        // reasoning_details[].text). Reasoning sai antes do Delta: a UI
        // auto-colapsa a seção quando o primeiro content chega.
        var reasoning = ExtractReasoning(delta);
        if (reasoning is not null)
        {
            events = [new ChatStreamEvent.Reasoning(reasoning)];
        }
        if (!string.IsNullOrEmpty(content))
        {
            (events ??= []).Add(new ChatStreamEvent.Delta(content));
        }
        return events;
    }

    /// <summary>Extrai reasoning do delta — normalizado + variantes de
    /// gateway OpenAI-compatível (SPEC-20261015 RF-003).</summary>
    private static string? ExtractReasoning(JsonNode? delta)
    {
        if (delta is null)
        {
            return null;
        }
        if (delta["reasoning"] is JsonValue normalized
            && normalized.TryGetValue<string>(out var s) && s.Length > 0)
        {
            return s;
        }
        if (delta["reasoning_content"] is JsonValue rc
            && rc.TryGetValue<string>(out s) && s.Length > 0)
        {
            return s;
        }
        if (delta["reasoning_details"] is JsonArray details)
        {
            var joined = string.Concat(details
                .Select(d => d?["text"] as JsonValue)
                .Where(t => t is not null && t.TryGetValue<string>(out _))
                .Select(t => t!.GetValue<string>()));
            return joined.Length > 0 ? joined : null;
        }
        return null;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"Erro {(int)response.StatusCode} ao {action}: {body}");
    }
}
