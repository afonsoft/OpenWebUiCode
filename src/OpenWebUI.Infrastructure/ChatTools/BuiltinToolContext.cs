namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Contexto de execução das tools built-in
/// (SPEC-20261007-chat-agent-tools): quem chamou, em qual chat/run e o
/// diretório de trabalho confinado (<c>data/workspaces/{userId}</c>).
/// Construído no endpoint/executor — nunca por a tool.
/// </summary>
/// <param name="UserId">Dono da conversa (isolamento).</param>
/// <param name="ChatId">Chat da run, quando conhecido.</param>
/// <param name="RunId">Run ativa, quando dentro do dispatcher.</param>
/// <param name="WorkspacePath">Diretório de trabalho do usuário.</param>
/// <param name="UploadDir">Diretório de uploads do usuário (imagens geradas).</param>
/// <param name="Model">
/// Modelo da run — usado por tools que chamam provider (ex.:
/// context_compact sumariza com ele; SPEC-20261020-context-snapshots RF-008).
/// </param>
/// <param name="AttachedRefs">
/// Aliases de referências anexadas via <c>@alias</c> no composer
/// (SPEC-20261018-references RF-003) — tools <c>ref://</c> só resolvem
/// aliases presentes aqui; subagent herda o conjunto do chat pai.
/// </param>
public sealed record BuiltinToolContext(
    string UserId,
    string? ChatId,
    string? RunId,
    string WorkspacePath,
    string UploadDir,
    string? Model = null,
    IReadOnlyList<string>? AttachedRefs = null);
