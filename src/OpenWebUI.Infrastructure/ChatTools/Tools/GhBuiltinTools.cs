using System.Text;
using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:gh_pulls</c> — PRs abertos do repo vinculado ao chat
/// (plano P1.17, omp <c>gh_*</c>): número, título, autor, branch e rollup
/// de CI — para o agente revisar estado dos PRs sem sair do loop.
/// Reusa <see cref="GitHubService"/> (PAT do usuário, cache 60s).
/// Read-only.
/// </summary>
public sealed class GhPullsBuiltinTool(GitHubService github, WorkspaceRepoService repos)
    : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "gh_pulls";

    /// <inheritdoc />
    public string Description =>
        "List open pull requests of the chat's bound repository — number, "
            + "title, author, head branch and CI rollup. Read-only.";

    /// <inheritdoc />
    public string ParametersJson => """{ "type": "object", "properties": {} }""";

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var (binding, _) = await repos.ResolveBindingAsync(
            context.UserId, context.ChatId, ct);
        if (binding?.Repo is not { } slug
            || slug.Split('/') is not [var owner, var repo])
        {
            return new BuiltinToolResult(
                "Erro: este chat não tem repo vinculado.");
        }

        var resp = await github.ListPullRequestsAsync(
            context.UserId, owner, repo, ct);
        if (resp is null || !resp.Github)
        {
            return new BuiltinToolResult(
                resp?.NeedsToken == true
                    ? "Erro: falta (ou expirou) o PAT do GitHub — configure em Integrações."
                    : "Erro: GitHub inacessível agora.");
        }
        if (resp.Pulls.Count == 0)
        {
            return new BuiltinToolResult(
                $"Nenhum PR aberto em {slug}.",
                new { count = 0 });
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# {slug} — {resp.Pulls.Count} PR(s) aberto(s)");
        foreach (var pr in resp.Pulls)
        {
            var checks = pr.Checks is { } c
                ? $" ci:{c.State}"
                : string.Empty;
            sb.AppendLine(
                $"- #{pr.Number} {pr.Title}"
                    + (pr.Author is not null ? $" @{pr.Author}" : "")
                    + (pr.HeadRef is not null ? $" ({pr.HeadRef})" : "")
                    + checks);
        }
        return new BuiltinToolResult(
            sb.ToString(), new { count = resp.Pulls.Count });
    }
}
