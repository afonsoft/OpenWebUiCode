using System.Text.Json;

namespace OpenWebUI.Application.Contracts;

/// <summary>Pedido de envio de mensagem que dispara uma run desacoplada.</summary>
/// <param name="Content">Texto da mensagem do usuário; null para regenerar a resposta sobre o histórico existente (última mensagem deve ser do usuário).</param>
/// <param name="Model">Modelo de geração pedido.</param>
/// <param name="FileIds">Ids de arquivos cujo conteúdo entra como contexto (opcional).</param>
/// <param name="ToolIds">Ids de tools habilitadas para a run (opcional).</param>
/// <param name="Params">Parâmetros de geração (temperature, top_p, max_tokens), opcional.</param>
/// <param name="WebSearch">Quando true, injeta resultados de busca web como contexto.</param>
/// <param name="MentionPaths">Caminhos do workdir mencionados via chips @path (RF-002 — expandidos em blocos <file> no prompt).</param>
public sealed record EnqueueChatRunRequest(
    string? Content,
    string Model,
    IReadOnlyList<string>? FileIds = null,
    IReadOnlyList<string>? ToolIds = null,
    IReadOnlyDictionary<string, object>? Params = null,
    bool? WebSearch = null,
    IReadOnlyList<string>? MentionPaths = null);

/// <summary>Payload do POST de steer/queue numa run viva (SPEC-20261010-steer-queue).</summary>
/// <param name="Message">Texto injetado como mensagem do usuário na run.</param>
/// <param name="Mode"><c>steer</c> (default — próxima fronteira de rodada) ou <c>queue</c> (só quando a run ficaria ociosa).</param>
public sealed record SteerRunRequest(string? Message, string? Mode = null);

/// <summary>Estado serializável de uma run desacoplada de chat.</summary>
/// <param name="Id">Identificador da run.</param>
/// <param name="ChatId">Chat ao qual a run pertence.</param>
/// <param name="Status">queued | running | completed | failed | stopped | interrupted.</param>
/// <param name="Model">Modelo pedido no envio.</param>
/// <param name="PartialContent">Conteúdo parcial do assistant (checkpoint por iteração).</param>
/// <param name="Error">Erro final quando failed.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="StartedAt">Início da execução; null enquanto queued.</param>
/// <param name="CompletedAt">Finalização; null enquanto ativa.</param>
public sealed record ChatRunResponse(
    string Id,
    string ChatId,
    string Status,
    string Model,
    string? PartialContent,
    string? Error,
    long CreatedAt,
    long? StartedAt,
    long? CompletedAt,
    string? ParentRunId = null);

/// <summary>
/// Item da lista consolidada de runs do usuário
/// (<c>GET /api/v1/chats/runs</c>, SPEC-20261010-parallel-runs-console):
/// a run + título do chat + vínculos de hierarquia pra árvore de sub-agents.
/// </summary>
public sealed record ParallelRunResponse(
    string Id,
    string ChatId,
    string ChatTitle,
    string Status,
    string Model,
    long CreatedAt,
    long? StartedAt,
    long? CompletedAt,
    string? ParentRunId = null,
    string? ParentChatId = null);

/// <summary>
/// Evento SSE <c>tool_call</c> (SPEC-20261007-chat-tool-streaming RF-001):
/// emitido antes de executar a tool; <paramref name="ArgsPreview"/> é o JSON
/// de argumentos truncado e higienizado (sem secrets).
/// </summary>
public sealed record RunToolCallEvent(string Id, string Name, string? ArgsPreview);

/// <summary>
/// Evento SSE <c>tool_result</c>: <paramref name="Ok"/> false em erro ou
/// negação (<paramref name="Denied"/>); <paramref name="Preview"/> truncado;
/// <paramref name="ImagePath"/> quando a tool gerou imagem renderizável;
/// <paramref name="VideoPath"/> idem para vídeo (<c>&lt;video&gt;</c> inline);
/// <paramref name="Result"/> é o payload estruturado da tool (links,
/// diffs, ids) para renderização rica no cliente.
/// </summary>
public sealed record RunToolResultEvent(
    string Id, string Name, bool Ok, string? Preview,
    string? ImagePath = null, bool Denied = false, JsonElement? Result = null,
    string? VideoPath = null);

/// <summary>
/// Evento SSE <c>status</c> de fase (RF-002): <paramref name="Phase"/> ∈
/// <c>generating</c> | <c>running_tool</c> | <c>awaiting_approval</c>;
/// <paramref name="Label"/> é o texto exibido no chip de status.
/// </summary>
public sealed record RunPhaseEvent(string Phase, string? Label);

/// <summary>
/// Evento SSE <c>approval_asked</c> (RF-003): a run pausou aguardando
/// decisão do dono sobre a tool mutável <paramref name="ToolName"/>.
/// <paramref name="Kind"/> descreve o tipo de execução (python|mcp|http).
/// </summary>
public sealed record RunApprovalAskedEvent(
    string CallId, string ToolName, string Kind, string? ArgsPreview);

/// <summary>
/// Evento SSE <c>question_asked</c> (SPEC-20261007-chat-agent-ux RF-005):
/// a run pausou aguardando resposta do dono para a pergunta estruturada
/// emitida pela builtin <c>ask_user</c> — resolve pelo mesmo endpoint de
/// aprovação (<c>decision=approve</c> + <c>message</c> = resposta).
/// </summary>
/// <param name="Options">Opções clicáveis; vazio = só resposta livre.</param>
/// <param name="Multiple">Mais de uma opção pode ser escolhida.</param>
public sealed record RunQuestionAskedEvent(
    string CallId, string Question, string[] Options, bool Multiple);

/// <summary>
/// Evento SSE <c>decision_auto</c> (SPEC-15 RF-003): nos presets autônomos
/// (<c>smart</c>/<c>auto</c>/<c>always-allow</c>) o executor respondeu sozinho
/// uma pergunta com opções — <paramref name="Chosen"/> é a opção recomendada
/// (marcada) ou a primeira; <paramref name="Source"/> indica a origem
/// (<c>ask_user</c> | <c>plan_exit</c>). Auditável no feed de atividade.
/// </summary>
public sealed record RunDecisionAutoEvent(
    string CallId, string Source, string Chosen, string Reason);

/// <summary>
/// Item da lista de tarefas da run (SPEC-20261007-chat-agent-parity
/// RF-010): snapshot emitido pela builtin <c>todo_write</c>.
/// </summary>
/// <param name="Id">Id estável (t1, t2, ...).</param>
/// <param name="Content">Descrição da tarefa.</param>
/// <param name="Status">pending | in_progress | completed.</param>
public sealed record RunTaskItem(string Id, string Content, string Status);

/// <summary>
/// Evento SSE <c>tasks</c> (RF-010): snapshot da lista de tarefas da run
/// — o executor republica o array <c>tasks</c> do resultado estruturado
/// de <c>todo_write</c> para o painel/checklist do cliente.
/// </summary>
public sealed record RunTasksEvent(IReadOnlyList<RunTaskItem> Tasks);

/// <summary>
/// Evento SSE <c>mode</c> (SPEC-20261009-agent-modes-plan-build RF-003):
/// emitido quando o modo do agente do chat muda mid-run — o
/// <c>plan_exit</c> aprovado promove <c>plan</c> → <c>build</c> e o chip
/// do cliente atualiza sem esperar o reload pós-run.
/// </summary>
public sealed record RunModeEvent(string Mode);

/// <summary>
/// Arquivo alterado por file_write/file_edit numa run (SPEC-20261007-
/// chat-agent-parity RF-015): <paramref name="Diff"/> é o unificado da
/// última alteração naquele path.
/// </summary>
public sealed record RunChangeItem(
    string Path, int Added, int Removed, string? Diff);

/// <summary>
/// Evento SSE <c>changes</c> (RF-015): snapshot dos arquivos alterados
/// pela run — o executor acumula por path e republica a cada file_*,
/// para a aba Changes do painel de workspace.
/// </summary>
public sealed record RunChangesEvent(IReadOnlyList<RunChangeItem> Changes);

/// <summary>
/// Evento SSE <c>checkpoint</c> (SPEC-20261009-checkpoints-revert RF-001):
/// snapshot imutável do workdir criado antes da run (turn 0) e após cada
/// tool mutável — <paramref name="Hash"/> identifica o commit na ref
/// oculta do snapshot e alimenta o botão Revert da aba Changes.
/// </summary>
public sealed record RunCheckpointEvent(
    string Hash, IReadOnlyList<string> Files, int Turn);

/// <summary>
/// Projeção de um <c>ChatJob</c> para o cliente (lista da aba Jobs do
/// painel, <c>GET /api/v1/jobs</c>).
/// </summary>
public sealed record ChatJobResponse(
    string Id,
    string? ChatId,
    string? RunId,
    string Command,
    string Status,
    int? Pid,
    int? ExitCode,
    string? Error,
    long StartedAt,
    long? FinishedAt);

/// <summary>Decisão do dono sobre uma aprovação pendente (RF-003).</summary>
/// <param name="Decision"><c>approve</c> | <c>deny</c>.</param>
/// <param name="Remember">Quando true, aprova a tool pelo resto da conversa.</param>
/// <param name="Message">
/// Instrução opcional numa negação (SPEC-20261007-chat-agent-ux RF-002):
/// vai como resultado da tool para o modelo corrigir a rota.
/// </param>
/// <param name="RememberPersist">
/// Quando true numa aprovação, grava override persistente
/// <c>allow</c> da tool para o usuário (SPEC-20261013-approval-tiers),
/// além do remember da sessão.
/// </param>
public sealed record RunApprovalDecisionRequest(
    string Decision, bool Remember = false, string? Message = null,
    bool RememberPersist = false);

/// <summary>
/// Decisão do gate de tools (SPEC-20261007-chat-agent-ux): aprovado, ou
/// negado com mensagem opcional de instrução para o modelo.
/// <paramref name="Output"/> quando não nulo substitui a execução da tool
/// — usado pelo <c>ask_user</c> para devolver a resposta do dono como
/// resultado direto (RF-005).
/// </summary>
public sealed record ToolGateDecision(
    bool Approved, string? DenyMessage = null, string? Output = null)
{
    /// <summary>Aprovado.</summary>
    public static readonly ToolGateDecision Allow = new(true);

    /// <summary>Negado sem instrução.</summary>
    public static readonly ToolGateDecision Deny = new(false);
}

/// <summary>Atualização parcial do chat (preset de aprovação de tools e/ou título).</summary>
/// <param name="ApprovalPreset">
/// <c>allow-readonly</c> | <c>approve-mutations</c> | <c>smart</c> |
/// <c>always-allow</c> | <c>auto</c> (RF-004). Null não altera.
/// </param>
/// <param name="Title">Novo título do chat. Null não altera; vazio é ignorado.</param>
/// <param name="Mode">Modo do agente (<c>chat|plan|build</c>). Null não altera.</param>
/// <param name="AdvisorEnabled">
/// Revisor de run (SPEC-20261013-run-advisor RF-005). Null não altera.
/// </param>
public sealed record ChatPatchRequest(
    string? ApprovalPreset, string? Title = null, string? Mode = null,
    bool? AdvisorEnabled = null);

/// <summary>
/// Conselho do revisor de run (SPEC-20261013-run-advisor RF-003): evento
/// SSE <c>advisor</c> com o texto curto e o gatilho que o produziu.
/// </summary>
/// <param name="Text">Conselho (capado a ~500 chars).</param>
/// <param name="Trigger"><c>user_message</c> | <c>tool_burst</c> | <c>pause</c>.</param>
public sealed record RunAdvisorEvent(string Text, string Trigger);

/// <summary>Arquivo alterado num workdir git (numstat + status M/A).</summary>
public sealed record WorkspaceGitFileResponse(
    string Path, int Added, int Removed, string Status);

/// <summary>
/// Snapshot git do workspace do chat (SPEC-20261007-chat-agent-parity
/// RF-018): branch, totais +a/-d, por-arquivo e o unified diff truncado.
/// <c>Git=false</c> quando o workdir não é repo (cliente cai pro
/// agregado de <c>file_*</c> da run).
/// </summary>
public sealed record WorkspaceGitResponse(
    bool Git,
    string? Branch,
    int Added,
    int Removed,
    IReadOnlyList<WorkspaceGitFileResponse> Files,
    string? Diff,
    bool DiffTruncated,
    bool Worktree = false,
    bool GitWrite = false);
