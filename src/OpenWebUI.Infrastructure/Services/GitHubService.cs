using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Integração GitHub por usuário (SPEC-20261008-github-repo-workspace):
/// o PAT fica no kv <see cref="ConfigService"/> sob
/// <c>u:{userId}:github.token</c> — nunca retorna pela API; a tela só vê
/// <c>configured</c> + <c>login</c>. Token validado em <c>GET /user</c>
/// antes de persistir.
/// </summary>
public sealed class GitHubService(
    IHttpClientFactory httpFactory, ConfigService config, HybridCache? cache = null)
{
    /// <summary>Nome do <see cref="HttpClient"/> registrado no Program.cs.</summary>
    public const string HttpClientName = "github";

    private const string ApiBase = "https://api.github.com";

    private static readonly TimeSpan PullsCacheTtl = TimeSpan.FromSeconds(60);

    private static string TokenKey(string userId) => $"u:{userId}:github.token";
    private static string LoginKey(string userId) => $"u:{userId}:github.login";

    /// <summary>Chaves do default global do admin (SPEC-20261014-config-layers RF-003).</summary>
    public const string GlobalTokenKey = "github.token";
    private const string GlobalLoginKey = "github.login";

    /// <summary>Status da integração do usuário (token mascarado, com provenance).</summary>
    public async Task<GitHubConfigResponse> GetStatusAsync(string userId, CancellationToken ct)
    {
        if (await config.GetAsync<string?>(TokenKey(userId), null, ct) is { Length: > 0 })
        {
            var login = await config.GetAsync<string?>(LoginKey(userId), null, ct);
            return new GitHubConfigResponse(true, login, "user");
        }
        if (await config.GetAsync<string?>(GlobalTokenKey, null, ct) is { Length: > 0 })
        {
            var login = await config.GetAsync<string?>(GlobalLoginKey, null, ct);
            return new GitHubConfigResponse(true, login, "global");
        }
        return new GitHubConfigResponse(false, null);
    }

    /// <summary>
    /// Token efetivo (uso interno — nunca exposto em resposta): PAT do
    /// usuário → default global do admin (RF-003).
    /// </summary>
    public async Task<string?> GetTokenAsync(string userId, CancellationToken ct) =>
        await config.GetAsync<string?>(TokenKey(userId), null, ct)
        ?? await config.GetAsync<string?>(GlobalTokenKey, null, ct);

    /// <summary>
    /// Só o PAT global do admin (sem fallback de usuário) — para processos
    /// compartilhados como o terminal local (SPEC-20261017-agent-secrets-env).
    /// </summary>
    public async Task<string?> GetGlobalTokenAsync(CancellationToken ct) =>
        await config.GetAsync<string?>(GlobalTokenKey, null, ct);

    /// <summary>
    /// Grava o token default global (admin). Devolve o login validado ou
    /// null quando o GitHub recusa. O valor nunca volta pela API.
    /// </summary>
    public async Task<string?> SetGlobalTokenAsync(string token, CancellationToken ct)
    {
        var login = await ValidateTokenAsync(token, ct);
        if (login is null)
        {
            return null;
        }
        await config.SetAsync(GlobalTokenKey, token, ct);
        await config.SetAsync(GlobalLoginKey, login, ct);
        return login;
    }

    /// <summary>Remove o token default global.</summary>
    public async Task ClearGlobalTokenAsync(CancellationToken ct)
    {
        await config.SetAsync<string?>(GlobalTokenKey, null, ct);
        await config.SetAsync<string?>(GlobalLoginKey, null, ct);
    }

    /// <summary>
    /// Valida o token em <c>GET /user</c> e persiste; devolve o login ou
    /// null quando o GitHub recusa (401/403) ou a API está indisponível.
    /// </summary>
    public async Task<string?> SetTokenAsync(string userId, string token, CancellationToken ct)
    {
        var login = await ValidateTokenAsync(token, ct);
        if (login is null)
        {
            return null;
        }

        await config.SetAsync(TokenKey(userId), token, ct);
        await config.SetAsync(LoginKey(userId), login, ct);
        return login;
    }

    /// <summary>Remove token + login do usuário.</summary>
    public async Task ClearTokenAsync(string userId, CancellationToken ct)
    {
        await config.SetAsync<string?>(TokenKey(userId), null, ct);
        await config.SetAsync<string?>(LoginKey(userId), null, ct);
    }

    /// <summary>Valida um token em <c>GET /user</c>; devolve o login ou null.</summary>
    public async Task<string?> ValidateTokenAsync(string token, CancellationToken ct)
    {
        try
        {
            using var doc = await SendAsync(token, HttpMethod.Get, $"{ApiBase}/user", ct);
            if (doc is null)
            {
                return null;
            }
            return doc.RootElement.TryGetProperty("login", out var l)
                ? l.GetString()
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Lista repositórios do usuário (mais recentes primeiro, até 100).</summary>
    public async Task<IReadOnlyList<GitHubRepoResponse>> ListReposAsync(
        string userId, CancellationToken ct)
    {
        var token = await GetTokenAsync(userId, ct);
        if (token is null)
        {
            return [];
        }

        try
        {
            using var doc = await SendAsync(token, HttpMethod.Get,
                $"{ApiBase}/user/repos?per_page=100&sort=updated&affiliation=owner,collaborator,organization_member", ct);
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var repos = new List<GitHubRepoResponse>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (ParseRepo(el) is { } repo)
                {
                    repos.Add(repo);
                }
            }
            return repos;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    private static GitHubRepoResponse? ParseRepo(JsonElement el)
    {
        var fullName = el.TryGetProperty("full_name", out var fn) ? fn.GetString() : null;
        if (string.IsNullOrEmpty(fullName))
        {
            return null;
        }
        return new GitHubRepoResponse(
            fullName,
            el.TryGetProperty("name", out var n) ? n.GetString() ?? fullName : fullName,
            el.TryGetProperty("private", out var p) && p.GetBoolean(),
            el.TryGetProperty("default_branch", out var db) ? db.GetString() ?? "main" : "main",
            el.TryGetProperty("html_url", out var h) ? h.GetString() ?? $"https://github.com/{fullName}" : $"https://github.com/{fullName}",
            el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            el.TryGetProperty("updated_at", out var u) ? u.GetString() : null);
    }

    /// <summary>
    /// PRs abertos do repo vinculado com rollup de checks (SPEC-20261009-pr-ci-panel).
    /// Sem token ou token recusado (401/403) → <c>NeedsToken</c>; falha de rede →
    /// null (o endpoint responde <c>github:false</c>). Resultados OK ficam 60s em cache.
    /// </summary>
    public async Task<WorkspacePullsResponse?> ListPullRequestsAsync(
        string userId, string owner, string repo, CancellationToken ct)
    {
        var token = await GetTokenAsync(userId, ct);
        if (token is null)
        {
            return new WorkspacePullsResponse(false, true, []);
        }

        var cacheKey = $"github:pulls:{userId}:{owner}/{repo}";
        var pulls = cache is null
            ? await FetchPullsAsync(ct)
            : await cache.GetOrCreateAsync(
                cacheKey,
                async cancel => await FetchPullsAsync(cancel),
                new HybridCacheEntryOptions
                {
                    Expiration = PullsCacheTtl,
                    LocalCacheExpiration = PullsCacheTtl,
                },
                tags: ["github"],
                cancellationToken: ct);
        return pulls;

        async Task<WorkspacePullsResponse?> FetchPullsAsync(CancellationToken cancel)
        {
        try
        {
            var (status, doc) = await SendStatusAsync(token, HttpMethod.Get,
                $"{ApiBase}/repos/{owner}/{repo}/pulls?state=open&per_page=30", cancel);
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new WorkspacePullsResponse(false, true, []);
            }
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var tasks = doc.RootElement.EnumerateArray()
                .Select(pr => MapPullAsync(token, owner, repo, pr, cancel))
                .ToList();
            var mapped = await Task.WhenAll(tasks);
            // Auth recusada no caminho dos checks (token revogado) → needsToken.
            if (mapped.Any(p => p is null))
            {
                return new WorkspacePullsResponse(false, true, []);
            }

            return new WorkspacePullsResponse(
                true, false, mapped.Select(p => p!).ToList());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
        }
    }

    /// <summary>Mapeia um PR da API; null quando a auth falha no caminho.</summary>
    private async Task<WorkspacePullResponse?> MapPullAsync(
        string token, string owner, string repo, JsonElement pr, CancellationToken ct)
    {
        var headObj = pr.TryGetProperty("head", out var h0) && h0.ValueKind == JsonValueKind.Object
            ? h0 : (JsonElement?)null;
        var checks = await ResolveChecksAsync(token, owner, repo, headObj, ct);
        if (checks is null)
        {
            return null; // 401/403 no caminho dos checks
        }

        return new WorkspacePullResponse(
            GetInt(pr, "number"),
            GetString(pr, "title") ?? string.Empty,
            GetNestedString(pr, "user", "login"),
            headObj is { } hb ? GetString(hb, "ref") : null,
            GetString(pr, "updated_at"),
            GetString(pr, "html_url") ?? string.Empty,
            pr.TryGetProperty("draft", out var d) && d.GetBoolean(),
            checks);
    }

    /// <summary>Rollup de checks do head.sha; <see cref="EmptyChecks"/> sem sha.</summary>
    private async Task<WorkspacePrChecksResponse?> ResolveChecksAsync(
        string token, string owner, string repo, JsonElement? headObj, CancellationToken ct)
    {
        var sha = headObj is { } hh && hh.TryGetProperty("sha", out var s) ? s.GetString() : null;
        return sha is null
            ? EmptyChecks
            : await FetchChecksAsync(token, owner, repo, sha, ct);
    }

    private static int GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) ? v.GetInt32() : 0;

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static string? GetNestedString(JsonElement el, string name, string nested) =>
        el.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object
            && o.TryGetProperty(nested, out var v) ? v.GetString() : null;

    private static readonly WorkspacePrChecksResponse EmptyChecks =
        new(0, 0, 0, 0, "pending");

    /// <summary>
    /// Rollup de CI do commit: combined status + check runs (RF-002).
    /// null quando a auth é recusada; checks vazios quando o endpoint falha.
    /// </summary>
    private async Task<WorkspacePrChecksResponse?> FetchChecksAsync(
        string token, string owner, string repo, string sha, CancellationToken ct)
    {
        var tally = new CheckTally();

        var (statusCode, statusDoc) = await SendStatusAsync(token, HttpMethod.Get,
            $"{ApiBase}/repos/{owner}/{repo}/commits/{sha}/status", ct);
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }
        CountStatuses(statusDoc, tally);

        var (checksCode, checksDoc) = await SendStatusAsync(token, HttpMethod.Get,
            $"{ApiBase}/repos/{owner}/{repo}/commits/{sha}/check-runs?per_page=100", ct);
        if (checksCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }
        CountCheckRuns(checksDoc, tally);

        var state = tally switch
        {
            { Failing: > 0 } => "failure",
            { Pending: > 0 } => "pending",
            { Total: 0 } => "pending",
            _ => "success",
        };
        return new WorkspacePrChecksResponse(
            tally.Total, tally.Passing, tally.Failing, tally.Pending, state);
    }

    /// <summary>Soma o array <c>statuses</c> do combined status no tally.</summary>
    private static void CountStatuses(JsonDocument? statusDoc, CheckTally tally)
    {
        if (statusDoc is null
            || !statusDoc.RootElement.TryGetProperty("statuses", out var statuses)
            || statuses.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var st in statuses.EnumerateArray())
        {
            tally.AddStatus(st.TryGetProperty("state", out var s) ? s.GetString() : null);
        }
    }

    /// <summary>Soma o array <c>check_runs</c> no tally (só runs completados contam).</summary>
    private static void CountCheckRuns(JsonDocument? checksDoc, CheckTally tally)
    {
        if (checksDoc is null
            || !checksDoc.RootElement.TryGetProperty("check_runs", out var runs)
            || runs.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var run in runs.EnumerateArray())
        {
            var runStatus = run.TryGetProperty("status", out var rs) ? rs.GetString() : null;
            var conclusion = run.TryGetProperty("conclusion", out var c)
                && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            tally.AddRun(runStatus, conclusion);
        }
    }

    /// <summary>Contadores do rollup de CI (passing/failing/pending).</summary>
    private sealed class CheckTally
    {
        public int Passing { get; private set; }
        public int Failing { get; private set; }
        public int Pending { get; private set; }
        public int Total => Passing + Failing + Pending;

        public void AddStatus(string? state)
        {
            switch (state)
            {
                case "success": Passing++; break;
                case "failure" or "error": Failing++; break;
                default: Pending++; break;
            }
        }

        public void AddRun(string? runStatus, string? conclusion)
        {
            switch (runStatus == "completed" ? conclusion : null)
            {
                case "success" or "neutral" or "skipped": Passing++; break;
                case "failure" or "cancelled" or "timed_out"
                    or "action_required" or "startup_failure": Failing++; break;
                default: Pending++; break;
            }
        }
    }

    /// <summary>Branches do repositório (até 100) + a branch padrão.</summary>
    public async Task<GitHubBranchesResponse?> ListBranchesAsync(
        string userId, string owner, string repo, CancellationToken ct)
    {
        var token = await GetTokenAsync(userId, ct);
        if (token is null)
        {
            return null;
        }

        try
        {
            var defaultBranch = "main";
            using var repoDoc = await SendAsync(
                token, HttpMethod.Get, $"{ApiBase}/repos/{owner}/{repo}", ct);
            if (repoDoc is not null
                && repoDoc.RootElement.TryGetProperty("default_branch", out var db)
                && db.GetString() is { Length: > 0 } def)
            {
                defaultBranch = def;
            }

            var branches = new List<string>();
            using var doc = await SendAsync(token, HttpMethod.Get,
                $"{ApiBase}/repos/{owner}/{repo}/branches?per_page=100", ct);
            if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in doc.RootElement.EnumerateArray()
                             .Select(el => el.TryGetProperty("name", out var n) ? n.GetString() : null)
                             .Where(name => !string.IsNullOrEmpty(name)))
                {
                    branches.Add(name!);
                }
            }
            if (branches.Count == 0)
            {
                branches.Add(defaultBranch);
            }

            return new GitHubBranchesResponse(defaultBranch, branches);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolve o slug efetivo do repo (SPEC-20261027-scratch-workspace RF-006):
    /// <paramref name="repo"/> com <c>owner/name</c> é usado como está;
    /// sem owner, assume a conta do token (login persistido ou
    /// <c>GET /user</c> para PAT ad-hoc).
    /// </summary>
    public async Task<string?> ResolveRepoSlugAsync(
        string userId, string repo, string? adhocToken, CancellationToken ct)
    {
        var slug = SanitizeRepoName(repo);
        if (slug is null)
        {
            return null;
        }
        if (slug.Contains('/'))
        {
            return slug;
        }

        string? login;
        if (adhocToken is not null)
        {
            login = await ValidateTokenAsync(adhocToken, ct);
        }
        else
        {
            login = await config.GetAsync<string?>(LoginKey(userId), null, ct)
                ?? await config.GetAsync<string?>(GlobalLoginKey, null, ct);
        }
        return login is null ? null : $"{login}/{slug}";
    }

    /// <summary>
    /// Garante que <c>owner/name</c> exista: cria na conta do token quando
    /// a API responde 404 (com owner ≠ login atual o GitHub recusa e a
    /// criação falha — devolvemos <c>Ok=false</c>).
    /// </summary>
    /// <returns><c>Ok</c> = repo acessível; <c>Created</c> = criado agora (vazio).</returns>
    public async Task<(bool Ok, bool Created)> EnsureRepoExistsAsync(
        string userId, string slug, string? adhocToken, CancellationToken ct)
    {
        var token = adhocToken ?? await GetTokenAsync(userId, ct);
        if (token is null)
        {
            return (false, false);
        }

        try
        {
            var (status, _) = await SendStatusAsync(
                token, HttpMethod.Get, $"{ApiBase}/repos/{slug}", ct);
            if (status == HttpStatusCode.OK)
            {
                return (true, false);
            }
            if (status != HttpStatusCode.NotFound)
            {
                return (false, false);
            }

            var client = httpFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/user/repos");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("OpenWebUI-NET");
            request.Content = JsonContent.Create(
                new { name = slug[(slug.IndexOf('/') + 1)..], @private = true });
            using var response = await client.SendAsync(request, ct);
            return (response.IsSuccessStatusCode
                || response.StatusCode == HttpStatusCode.UnprocessableEntity, // já existia
                response.IsSuccessStatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, false);
        }
    }

    /// <summary>
    /// <c>owner/repo</c> com os caracteres do GitHub; devolve null para
    /// qualquer outra forma (vazio, caracteres inválidos).
    /// </summary>
    private static string? SanitizeRepoName(string? raw)
    {
        var s = (raw ?? string.Empty).Trim().Trim('/');
        if (s.Length == 0 || s.Length > 200)
        {
            return null;
        }
        return s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/')
            && !s.Contains("//") && !s.StartsWith('.') && !s.EndsWith('.')
            ? s
            : null;
    }

    /// <summary>Chamada autenticada à API do GitHub; null em não-2xx/timeout.</summary>
    private async Task<JsonDocument?> SendAsync(
        string token, HttpMethod method, string uri, CancellationToken ct)
    {
        var (_, doc) = await SendStatusAsync(token, method, uri, ct);
        return doc;
    }

    /// <summary><see cref="SendAsync"/> que também devolve o status HTTP (401/403 importam).</summary>
    private async Task<(HttpStatusCode Status, JsonDocument? Doc)> SendStatusAsync(
        string token, HttpMethod method, string uri, CancellationToken ct)
    {
        var client = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("OpenWebUI-NET");
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, null);
        }

        return (response.StatusCode,
            await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct));
    }
}
