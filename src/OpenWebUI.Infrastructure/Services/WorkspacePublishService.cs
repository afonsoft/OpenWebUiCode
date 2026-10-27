using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Publish do workspace scratch (SPEC-20261027-scratch-workspace RF-006):
/// commita o rascunho da conversa e faz push para o destino informado —
/// <c>repo</c> (slug ou nome novo via API), <c>remoteUrl</c> com credencial
/// embutida ou PAT ad-hoc. O push aponta direto para a URL (sem
/// <c>git remote add</c>), então credencial nunca é persistida em
/// <c>.git/config</c>; a autenticação usa <c>http.extraheader</c> por
/// invocação, como no clone.
/// </summary>
public sealed class WorkspacePublishService(WorkspaceRepoService repos, GitHubService github)
{
    /// <summary>
    /// Commita e publica o scratch de <paramref name="chatId"/>.
    /// <c>ErrorStatus</c>: 0 = ok; 400 = alvo inválido/ausente;
    /// 404 = scratch não materializado; 502 = git/API recusou.
    /// </summary>
    public async Task<(PublishWorkspaceResponse? Result, int ErrorStatus, string? Error)> PublishAsync(
        string userId, string chatId, string authorName, string authorEmail,
        PublishWorkspaceRequest req, CancellationToken ct)
    {
        var dir = repos.ScratchDir(userId, chatId);
        if (!Directory.Exists(dir))
        {
            return (null, 404, "a conversa não tem pasta de trabalho");
        }
        if (string.IsNullOrWhiteSpace(req.Repo) && string.IsNullOrWhiteSpace(req.RemoteUrl))
        {
            return (null, 400, "informe repo ou remoteUrl");
        }
        if (!Directory.Exists(Path.Join(dir, ".git")))
        {
            _ = await WorkspaceRepoService.RunGitAsync(dir, ct, "init", "-q");
        }

        var pending = await WorkspaceRepoService.RunGitAsync(dir, ct, "status", "--porcelain");
        var head = await WorkspaceRepoService.RunGitAsync(dir, ct, "rev-parse", "--verify", "HEAD");
        if (string.IsNullOrWhiteSpace(pending) && head is null)
        {
            return (null, 400, "a pasta de trabalho está vazia");
        }

        if (!string.IsNullOrWhiteSpace(pending))
        {
            if (await WorkspaceRepoService.RunGitLoggedAsync(dir, ct, null, "add", "-A") is { } addErr)
            {
                return (null, 502, addErr);
            }
            var commitErr = await WorkspaceRepoService.RunGitLoggedAsync(dir, ct, null,
                "-c", $"user.name={authorName}", "-c", $"user.email={authorEmail}",
                "commit", "-q", "-m", $"Work from chat {chatId}");
            if (commitErr is not null)
            {
                return (null, 502, commitErr);
            }
        }

        var sha = (await WorkspaceRepoService.RunGitAsync(dir, ct, "rev-parse", "HEAD"))?.Trim();
        if (sha is null)
        {
            return (null, 502, "git rev-parse HEAD falhou");
        }

        var (remote, token, repoUrl, compareUrl, branch, targetErr) =
            await ResolveTargetAsync(userId, chatId, req, ct);
        if (remote is null)
        {
            return (null, 400, targetErr ?? "destino inválido");
        }

        var pushErr = await WorkspaceRepoService.RunGitLoggedAsync(
            dir, ct, token, "push", remote, $"HEAD:refs/heads/{branch}");
        return pushErr is not null
            ? (null, 502, pushErr)
            : (new PublishWorkspaceResponse(repoUrl!, branch, sha, compareUrl), 0, null);
    }

    /// <summary>Resolve URL de push, token por invocação, branch alvo e URLs web.</summary>
    private async Task<(string? Remote, string? Token, string? RepoUrl, string? CompareUrl,
        string Branch, string? Error)> ResolveTargetAsync(
        string userId, string chatId, PublishWorkspaceRequest req, CancellationToken ct)
    {
        var branch = $"chat/{WorkspaceRepoService.Sanitize(chatId)}";
        var storedToken = await github.GetTokenAsync(userId, ct);

        if (!string.IsNullOrWhiteSpace(req.RemoteUrl))
        {
            var url = req.RemoteUrl.Trim();
            var (clean, slug, hasCredentials) = ParseRemoteUrl(url);
            if (clean is null)
            {
                return (null, null, null, null, branch, "remoteUrl inválida");
            }
            // Credencial embutida dispensa header; URL limpa pode usar PAT ad-hoc/salvo.
            var token = hasCredentials ? null : req.Token ?? storedToken;
            string? compareUrl = null;
            if (slug is not null && token is not null)
            {
                var (o, r) = SplitSlug(slug);
                var branches = await github.ListBranchesAsync(userId, o, r, ct);
                compareUrl = $"{clean}/compare/{branches?.DefaultBranch ?? "main"}...{branch}";
            }
            return (url, token, clean, compareUrl, branch, null);
        }

        // Repo é não-vazio aqui — PublishAsync já rejeitou os dois vazios.
        var adhocToken = req.Token;
        if (adhocToken is null && storedToken is null)
        {
            return (null, null, null, null, branch,
                "sem token GitHub — configure o PAT ou passe token no payload");
        }

        var slugRepo = await github.ResolveRepoSlugAsync(userId, req.Repo!, adhocToken, ct);
        if (slugRepo is null)
        {
            return (null, null, null, null, branch, "nome de repo inválido");
        }
        var (ok, created) = await github.EnsureRepoExistsAsync(userId, slugRepo, adhocToken, ct);
        if (!ok)
        {
            return (null, null, null, null, branch,
                "repo inacessível e a criação falhou (verifique token/permissões)");
        }

        var repoUrlFinal = $"https://github.com/{slugRepo}";
        if (created)
        {
            return ($"{repoUrlFinal}.git", adhocToken ?? storedToken,
                repoUrlFinal, null, "main", null);
        }

        var (owner, name) = SplitSlug(slugRepo);
        var existing = await github.ListBranchesAsync(userId, owner, name, ct);
        var compare = $"{repoUrlFinal}/compare/{existing?.DefaultBranch ?? "main"}...{branch}";
        return ($"{repoUrlFinal}.git", adhocToken ?? storedToken,
            repoUrlFinal, compare, branch, null);
    }

    /// <summary>
    /// Extrai a URL limpa (sem userinfo), o slug <c>owner/repo</c> quando o
    /// host é github.com e se a URL já carrega credencial.
    /// </summary>
    private static (string? Clean, string? Slug, bool HasCredentials) ParseRemoteUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http" or "file"))
        {
            return (null, null, false);
        }
        var hasCredentials = !string.IsNullOrEmpty(uri.UserInfo);
        var clean = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString()
            .TrimEnd('/');
        string? slug = null;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var path = uri.AbsolutePath.Trim('/');
            if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^4];
            }
            if (path.Split('/').Length == 2)
            {
                slug = path;
            }
        }
        return (clean.TrimEnd('/'), slug, hasCredentials);
    }

    private static (string Owner, string Name) SplitSlug(string slug)
    {
        var idx = slug.IndexOf('/');
        return (slug[..idx], slug[(idx + 1)..]);
    }
}
