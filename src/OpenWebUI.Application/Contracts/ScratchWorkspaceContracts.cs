namespace OpenWebUI.Application.Contracts;

/// <summary>
/// Payload de <c>POST /api/v1/chats/{id}/workspace/publish</c>
/// (SPEC-20261027-scratch-workspace RF-006). Formas aceitas:
/// <c>{ "repo": "owner/name" }</c> (repo existente → branch chat/{id} +
/// compare URL; inexistente → criado via API), <c>{ "repo": "name" }</c>
/// (cria sob a conta do PAT configurado), <c>{ "repo": "name", "token" }</c>
/// (PAT ad-hoc) ou <c>{ "remoteUrl": "https://x:TOKEN@host/o/r.git" }</c>.
/// </summary>
/// <param name="Repo">Slug <c>owner/name</c> ou nome de repo novo.</param>
/// <param name="RemoteUrl">URL remota com credencial embutida (opcional).</param>
/// <param name="Token">PAT ad-hoc (nunca persistido).</param>
public sealed record PublishWorkspaceRequest(
    string? Repo,
    string? RemoteUrl,
    string? Token);

/// <summary>Resultado do publish do scratch.</summary>
/// <param name="RepoUrl">URL web do repositório.</param>
/// <param name="Branch">Branch que recebeu o push.</param>
/// <param name="CommitSha">SHA do commit publicado.</param>
/// <param name="CompareUrl">Link de compare/PR (quando o repo já existia).</param>
public sealed record PublishWorkspaceResponse(
    string RepoUrl,
    string Branch,
    string CommitSha,
    string? CompareUrl);

/// <summary>Status do scratch da conversa (RF-007 — o botão Publicar da UI).</summary>
/// <param name="Exists">Pasta materializada.</param>
/// <param name="FileCount">Arquivos de trabalho (sem .git).</param>
/// <param name="HasCommits">Pelo menos um commit no scratch.</param>
public sealed record ScratchStatusResponse(
    bool Exists,
    int FileCount,
    bool HasCommits);
