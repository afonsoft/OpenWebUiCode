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

/// <summary>
/// <c>builtin:gh_view</c> — detalhe de um PR do repo vinculado
/// (plano P1.17): metadados, corpo (truncado em <see cref="MaxBodyChars"/>),
/// rollup de CI e estatísticas do diff (+/-/arquivos, mergeable).
/// Read-only.
/// </summary>
public sealed class GhViewBuiltinTool(GitHubService github, WorkspaceRepoService repos)
    : IBuiltinChatTool
{
    private const int MaxBodyChars = 4000;

    /// <inheritdoc />
    public string Name => "gh_view";

    /// <inheritdoc />
    public string Description =>
        "Show one pull request of the chat's bound repository by number — "
            + "metadata, body, CI rollup, diff stats and mergeable state. Read-only.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "number": { "type": "integer", "description": "Pull request number." }
          },
          "required": ["number"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var number = args.TryGetProperty("number", out var nEl)
            && nEl.ValueKind is JsonValueKind.Number
                ? nEl.GetInt32()
                : 0;
        if (number <= 0)
        {
            return new BuiltinToolResult(
                "Erro: 'number' (inteiro > 0) é obrigatório.");
        }

        var (binding, _) = await repos.ResolveBindingAsync(
            context.UserId, context.ChatId, ct);
        if (binding?.Repo is not { } slug
            || slug.Split('/') is not [var owner, var repo])
        {
            return new BuiltinToolResult(
                "Erro: este chat não tem repo vinculado.");
        }

        var resp = await github.GetPullDetailAsync(
            context.UserId, owner, repo, number, ct);
        if (resp is null || !resp.Github)
        {
            return new BuiltinToolResult(
                resp?.NeedsToken == true
                    ? "Erro: falta (ou expirou) o PAT do GitHub — configure em Integrações."
                    : "Erro: GitHub inacessível agora.");
        }
        if (resp.NotFound || resp.Pull is not { } pr)
        {
            return new BuiltinToolResult(
                $"PR #{number} não encontrado em {slug}.");
        }

        var checks = pr.Checks is { } c
            ? $"ci:{c.State} ({c.Passing}✓ {c.Failing}✗ {c.Pending}⏳ de {c.Total})"
            : "ci:n/d";
        var body = resp.Body is { Length: > MaxBodyChars } b
            ? b[..MaxBodyChars] + "… [truncado]"
            : resp.Body ?? "(sem descrição)";
        var text = $"# {slug} PR #{pr.Number}: {pr.Title}\n"
            + $"autor: @{pr.Author ?? "?"} | head: {pr.HeadRef ?? "?"}"
            + (pr.Draft ? " (draft)" : "") + "\n"
            + $"{checks} | +{resp.Additions}/-{resp.Deletions}"
            + $" em {resp.ChangedFiles} arquivo(s)"
            + $" | mergeable:{(resp.Mergeable ? "sim" : "não/indefinido")}\n"
            + $"url: {pr.Url}\n\n{body}";
        return new BuiltinToolResult(text,
            new { number, checks = pr.Checks.State,
                  mergeable = resp.Mergeable });
    }
}
