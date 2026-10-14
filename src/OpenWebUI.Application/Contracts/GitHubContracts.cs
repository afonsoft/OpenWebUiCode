namespace OpenWebUI.Application.Contracts;

/// <summary>Status da integração GitHub do usuário (token nunca retorna).</summary>
/// <param name="Configured">Se há um token salvo.</param>
/// <param name="Login">Login do GitHub validado, quando configurado.</param>
/// <param name="Source">
/// Camada que forneceu o token (SPEC-20261014-config-layers RF-003):
/// <c>user</c> (PAT próprio) ou <c>global</c> (default do admin).
/// </param>
public sealed record GitHubConfigResponse(
    bool Configured, string? Login, string? Source = null);

/// <summary>Salva o token GitHub do usuário.</summary>
/// <param name="Token">Personal access token (repo scope).</param>
public sealed record GitHubTokenRequest(string? Token);

/// <summary>Repositório do usuário no GitHub.</summary>
/// <param name="FullName">Slug owner/repo.</param>
/// <param name="Name">Nome curto do repositório.</param>
/// <param name="Private">Visibilidade privada.</param>
/// <param name="DefaultBranch">Branch padrão.</param>
/// <param name="HtmlUrl">URL web do repositório.</param>
/// <param name="Description">Descrição opcional.</param>
/// <param name="UpdatedAt">Última atualização (ISO 8601).</param>
public sealed record GitHubRepoResponse(
    string FullName, string Name, bool Private, string DefaultBranch,
    string HtmlUrl, string? Description, string? UpdatedAt);

/// <summary>Branches de um repositório.</summary>
/// <param name="DefaultBranch">Branch padrão do repositório.</param>
/// <param name="Branches">Nomes das branches.</param>
public sealed record GitHubBranchesResponse(string DefaultBranch, IReadOnlyList<string> Branches);

/// <summary>Repositório vinculado ao workspace do usuário.</summary>
/// <param name="Repo">Slug owner/repo (null quando não vinculado).</param>
/// <param name="Branch">Branch aberta.</param>
/// <param name="Dir">Diretório do checkout dentro do workspace.</param>
public sealed record WorkspaceRepoResponse(
    string? Repo, string? Branch, string? Dir, string? TestCommand = null,
    string? FormatCommand = null);

/// <summary>Vincula/abre um repositório + branch no workspace.</summary>
/// <param name="Repo">Slug owner/repo.</param>
/// <param name="Branch">Branch a abrir.</param>
public sealed record WorkspaceRepoOpenRequest(
    string? Repo, string? Branch, string? TestCommand = null, string? FormatCommand = null);

/// <summary>Binding persistido do repositório do workspace (kv por usuário).</summary>
/// <param name="Repo">Slug owner/repo.</param>
/// <param name="Branch">Branch selecionada.</param>
/// <param name="Dir">Subdiretório do checkout dentro do workspace.</param>
/// <param name="TestCommand">Comando de teste customizado (override da detecção por manifesto — SPEC-20261009-ide-mentions-tests).</param>
/// <param name="FormatCommand">Format hook por-repo (SPEC-20261009-worktree-format-hooks) — override do global <c>Format:Command</c>.</param>
public sealed record WorkspaceRepoBinding(
    string Repo, string Branch, string Dir, string? TestCommand = null,
    string? FormatCommand = null);

/// <summary>Aplica o diff do worktree de uma run no workdir compartilhado (RF-002).</summary>
/// <param name="RunId">Id da run cujo worktree deve ser mergeado.</param>
public sealed record MergeWorktreeRequest(string? RunId);

/// <summary>Resultado do merge worktree → workdir.</summary>
/// <param name="Merged">True quando tudo aplicou e o worktree foi removido.</param>
/// <param name="Applied">Arquivos aplicados com sucesso.</param>
/// <param name="Conflicts">Arquivos com conflito (nunca aplicados à força).</param>
/// <param name="Error">Erro inesperado.</param>
public sealed record MergeWorktreeResponse(
    bool Merged, IReadOnlyList<string> Applied, IReadOnlyList<string> Conflicts,
    string? Error = null);

/// <summary>Rollup dos checks de CI de um pull request (SPEC-20261009-pr-ci-panel).</summary>
/// <param name="Total">Total de checks (status contexts + check runs).</param>
/// <param name="Passing">Checks verdes (success/neutral/skipped).</param>
/// <param name="Failing">Checks falhos (failure/error/timed_out/cancelled/action_required).</param>
/// <param name="Pending">Checks em andamento (pending/queued/in_progress/stale).</param>
/// <param name="State">Estado agregado: <c>success</c> | <c>failure</c> | <c>pending</c>.</param>
public sealed record WorkspacePrChecksResponse(
    int Total, int Passing, int Failing, int Pending, string State);

/// <summary>Pull request aberto do repositório vinculado.</summary>
/// <param name="Number">Número do PR.</param>
/// <param name="Title">Título.</param>
/// <param name="Author">Login do autor.</param>
/// <param name="HeadRef">Branch de origem.</param>
/// <param name="UpdatedAt">Última atualização (ISO 8601).</param>
/// <param name="Url">URL web do PR.</param>
/// <param name="Draft">Se é rascunho.</param>
/// <param name="Checks">Rollup de CI do head.</param>
public sealed record WorkspacePullResponse(
    int Number, string Title, string? Author, string? HeadRef,
    string? UpdatedAt, string Url, bool Draft, WorkspacePrChecksResponse Checks);

/// <summary>Resposta de <c>GET /api/v1/workspace/repo/pulls</c>.</summary>
/// <param name="Github">false quando o GitHub está inacessível/sem auth.</param>
/// <param name="NeedsToken">true quando falta (ou expirou) o PAT do usuário.</param>
/// <param name="Pulls">PRs abertos do repo vinculado.</param>
public sealed record WorkspacePullsResponse(
    bool Github, bool NeedsToken, IReadOnlyList<WorkspacePullResponse> Pulls);
