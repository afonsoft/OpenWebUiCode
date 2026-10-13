using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using OpenWebUI.Api.Completions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura da integração GitHub + repo vinculado ao workspace
/// (SPEC-20261008-github-repo-workspace): token por usuário no kv (nunca
/// retornado), listagens via API do GitHub (handler stub), clone/switch de
/// branch do <see cref="WorkspaceRepoService"/> com remote local, e os
/// endpoints <c>/api/v1/github/*</c> + <c>/api/v1/workspace/repo</c>.
/// </summary>
[TestFixture, IsolateEnvironment]
public class GitHubWorkspaceTests
{
    private string _root = null!;
    private AppDbContext _db = null!;
    private HybridCache _cache = null!;
    private ConfigService _config = null!;
    private GitHubStub _github = null!;
    private GitHubService _svc = null!;
    private WorkspaceRepoService _repos = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-gh-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Join(_root, "t.db")}").Options);
        DatabaseMigrator.MigrateAsync(_db).GetAwaiter().GetResult();
        _cache = TestCache.Create();
        _config = new ConfigService(_db, _cache);
        _github = new GitHubStub();
        _svc = new GitHubService(new StubFactory(_github), _config);
        _repos = new WorkspaceRepoService(_config, new StubEnv(_root));
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _github.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    // ---------------- GitHubService ----------------

    [Test]
    public async Task Status_SemToken_NaoConfigurado()
    {
        var status = await _svc.GetStatusAsync("u1", default);
        Assert.Multiple(() =>
        {
            Assert.That(status.Configured, Is.False);
            Assert.That(status.Login, Is.Null);
        });
        Assert.That(await _svc.ListReposAsync("u1", default), Is.Empty);
    }

    [Test]
    public async Task SetToken_Valido_PersisteLoginENuncaRetornaToken()
    {
        var login = await _svc.SetTokenAsync("u1", "pat-1", default);
        Assert.That(login, Is.EqualTo("afonso"));
        // O handler registra o Bearer enviado à API.
        Assert.That(_github.LastAuth, Is.EqualTo("Bearer pat-1"));

        var status = await _svc.GetStatusAsync("u1", default);
        Assert.Multiple(() =>
        {
            Assert.That(status.Configured, Is.True);
            Assert.That(status.Login, Is.EqualTo("afonso"));
        });
        // O token fica acessível só via GetTokenAsync (uso interno).
        Assert.That(await _svc.GetTokenAsync("u1", default), Is.EqualTo("pat-1"));

        await _svc.ClearTokenAsync("u1", default);
        Assert.That((await _svc.GetStatusAsync("u1", default)).Configured, Is.False);
    }

    [Test]
    public async Task SetToken_Invalido_NaoPersiste()
    {
        _github.Status = HttpStatusCode.Unauthorized;
        Assert.That(await _svc.SetTokenAsync("u1", "bad", default), Is.Null);
        Assert.That(await _svc.GetTokenAsync("u1", default), Is.Null);
    }

    [Test]
    public async Task ListRepos_MapeiaCamposDoGitHub()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        var repos = await _svc.ListReposAsync("u1", default);
        Assert.That(repos, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(repos[0].FullName, Is.EqualTo("afonsoft/open-webui"));
            Assert.That(repos[0].Private, Is.True);
            Assert.That(repos[0].DefaultBranch, Is.EqualTo("main"));
            Assert.That(repos[0].HtmlUrl, Does.Contain("github.com/afonsoft/open-webui"));
            Assert.That(repos[1].Description, Is.EqualTo("repo 2"));
        });
    }

    [Test]
    public async Task ListBranches_RetornaDefaultELista()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        var branches = await _svc.ListBranchesAsync("u1", "afonsoft", "open-webui", default);
        Assert.That(branches, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(branches!.DefaultBranch, Is.EqualTo("main"));
            Assert.That(branches.Branches, Does.Contain("main").And.Contain("dev"));
        });
    }

    // ---------------- WorkspaceRepoService ----------------

    [Test]
    public async Task Repo_Open_CloneLocal_VinculaEResolveWorkdir()
    {
        RequireGit();
        var origin = CriarOrigem("main");
        try
        {
            var (binding, error) = await _repos.OpenAsync(
                "u1", "afonsoft/demo", "main", origin, null, default);
            Assert.That(error, Is.Null);
            Assert.That(binding, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(binding!.Repo, Is.EqualTo("afonsoft/demo"));
                Assert.That(binding.Branch, Is.EqualTo("main"));
                Assert.That(binding.Dir, Is.EqualTo("repos/afonsoft__demo"));
            });

            var workdir = await _repos.ResolveWorkdirAsync("u1", default);
            Assert.That(workdir, Is.EqualTo(
                Path.Join(_root, "data", "workspaces", "u1", "repos", "afonsoft__demo")));
            Assert.That(File.Exists(Path.Join(workdir, "readme.md")), Is.True);

            // O remote gravado não carrega credencial.
            var remote = Git(workdir, "remote", "get-url", "origin");
            Assert.That(remote, Is.Not.Null);
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
        }
    }

    [Test]
    public async Task Repo_Open_SlugOuBranchInvalidos_Erro()
    {
        var (binding, error) = await _repos.OpenAsync(
            "u1", "sem-barra", "main", "unused", null, default);
        Assert.That(binding, Is.Null);
        Assert.That(error, Does.Contain("owner/repo"));

        (binding, error) = await _repos.OpenAsync(
            "u1", "a/b", "branch ruim", "unused", null, default);
        Assert.That(binding, Is.Null);
        Assert.That(error, Does.Contain("Branch"));
    }

    [Test]
    public async Task Repo_Open_TrocaDeBranch_NoCheckoutExistente()
    {
        RequireGit();
        var origin = CriarOrigem("main", extraBranch: "dev");
        try
        {
            var (binding, _) = await _repos.OpenAsync(
                "u1", "a/b", "main", origin, null, default);
            Assert.That(binding?.Branch, Is.EqualTo("main"));

            var dir = Path.Join(_root, "data", "workspaces", "u1", "repos", "a__b");
            File.WriteAllText(Path.Join(dir, "local.txt"), "x\n");

            (binding, var error) = await _repos.OpenAsync(
                "u1", "a/b", "dev", origin, null, default);
            Assert.That(error, Is.Null);
            Assert.That(binding?.Branch, Is.EqualTo("dev"));
            Assert.That(Git(dir, "branch", "--show-current"), Does.Contain("dev"));
            // Arquivo não-rastreado do checkout sobrevive à troca.
            Assert.That(File.Exists(Path.Join(dir, "local.txt")), Is.True);
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
        }
    }

    [Test]
    public async Task Repo_Open_CheckoutDivergido_ResetaComBackup()
    {
        RequireGit();
        var origin = CriarOrigem("main");
        try
        {
            var (binding, _) = await _repos.OpenAsync(
                "u1", "a/b", "main", origin, null, default);
            Assert.That(binding, Is.Not.Null);
            var dir = Path.Join(_root, "data", "workspaces", "u1", "repos", "a__b");

            // Diverge o checkout base: commit local-only + commit novo no
            // origin → pull --ff-only não reconcilia.
            Git(dir, "-c", "user.email=t@t", "-c", "user.name=t",
                "commit", "--allow-empty", "-m", "drift local");
            Git(origin, "-c", "user.email=t@t", "-c", "user.name=t",
                "commit", "--allow-empty", "-m", "origin novo");

            (binding, var error) = await _repos.OpenAsync(
                "u1", "a/b", "main", origin, null, default);
            Assert.That(error, Is.Null);
            Assert.That(binding, Is.Not.Null);

            Assert.Multiple(() =>
            {
                // Base realinhado ao remoto.
                Assert.That(Git(dir, "rev-parse", "HEAD"),
                    Is.EqualTo(Git(origin, "rev-parse", "HEAD")));
                // HEAD divergido preservado em branch de backup.
                Assert.That(Git(dir, "branch", "--list", "backup/*"),
                    Does.Contain("backup/main-"));
            });
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
        }
    }

    [Test]
    public async Task Repo_Unbind_VoltaParaRaizDoWorkspace()
    {
        RequireGit();
        var origin = CriarOrigem("main");
        try
        {
            await _repos.OpenAsync("u1", "a/b", "main", origin, null, default);
            await _repos.UnbindAsync("u1", default);
            Assert.That(await _repos.GetBindingAsync("u1", default), Is.Null);
            Assert.That(await _repos.ResolveWorkdirAsync("u1", default),
                Is.EqualTo(Path.Join(_root, "data", "workspaces", "u1")));
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
        }
    }

    // ---------------- EnrichRequestAsync (repo binding) ----------------

    [Test]
    public async Task Enrich_ComRepoVinculado_InjetaContextoNoSistema()
    {
        RequireGit();
        var origin = CriarOrigem("main");
        try
        {
            await _repos.OpenAsync("u1", "a/b", "main", origin, null, default);
            var request = new ChatCompletionRequest(
                "fake:1", [new ChatCompletionMessage("user", "liste os arquivos")]);
            var effective = await ChatPipeline.EnrichRequestAsync(
                request,
                new User { Id = "u1", Name = "U", Email = "u@x" },
                _db, _config, rag: null!, webSearch: null!, _repos, default);
            var system = effective.Messages.FirstOrDefault(m => m.Role == "system");
            Assert.That(system?.Content, Does.Contain("Repositório vinculado ao workspace: a/b")
                .And.Contain("branch 'main'"));
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
        }
    }

    [Test]
    public async Task Enrich_SemRepo_NaoInjetaContexto()
    {
        var request = new ChatCompletionRequest(
            "fake:1", [new ChatCompletionMessage("user", "oi")]);
        var effective = await ChatPipeline.EnrichRequestAsync(
            request,
            new User { Id = "u2", Name = "U", Email = "u2@x" },
            _db, _config, rag: null!, webSearch: null!, _repos, default);
        Assert.That(
            effective.Messages.Any(m => m.Role == "system" && m.Content.Contains("Repositório vinculado")),
            Is.False);
    }

    [Test]
    public async Task Enrich_SemModeloCustom_InjetaPromptBaseDeQualidade()
    {
        var request = new ChatCompletionRequest(
            "fake:1", [new ChatCompletionMessage("user", "oi")]);
        var effective = await ChatPipeline.EnrichRequestAsync(
            request,
            new User { Id = "u3", Name = "U3", Email = "u3@x" },
            _db, _config, rag: null!, webSearch: null!, _repos, default);
        var system = effective.Messages.FirstOrDefault(m => m.Role == "system");
        Assert.That(system?.Content, Does.Contain("Open WebUI agent")
            .And.Contain("autonomous").And.Contain("user's language")
            // Disciplina de tools obrigatória no prompt base (todo + memória).
            .And.Contain("builtin_todo_write")
            .And.Contain("builtin_memory_save")
            .And.Contain("builtin_memory_search"));
    }

    // ---------------- GitHubService: caminhos de erro ----------------

    [Test]
    public async Task ListRepos_RespostaNaoArray_RetornaVazio()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        _github.Body = "{}";
        Assert.That(await _svc.ListReposAsync("u1", default), Is.Empty);
    }

    [Test]
    public async Task ListRepos_ErroRede_RetornaVazio()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        _github.Throw = new HttpRequestException("boom");
        Assert.That(await _svc.ListReposAsync("u1", default), Is.Empty);
    }

    [Test]
    public async Task ListBranches_SemBranches_UsaDefault()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        _github.Body = "{\"default_branch\":\"trunk\"}";
        _github.BranchesBody = "[]";
        var branches = await _svc.ListBranchesAsync("u1", "a", "b", default);
        Assert.Multiple(() =>
        {
            Assert.That(branches!.DefaultBranch, Is.EqualTo("trunk"));
            Assert.That(branches.Branches, Is.EqualTo(["trunk"]));
        });
    }

    [Test]
    public async Task ListBranches_ErroRede_RetornaNull()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        _github.Throw = new HttpRequestException("boom");
        Assert.That(await _svc.ListBranchesAsync("u1", "a", "b", default), Is.Null);
    }

    [Test]
    public async Task ValidateToken_ErroRede_RetornaNull()
    {
        _github.Throw = new HttpRequestException("boom");
        Assert.That(await _svc.ValidateTokenAsync("pat", default), Is.Null);
    }

    // ---------------- WorkspaceRepoService: caminhos de erro ----------------

    [Test]
    public async Task Repo_Open_DiretorioComOutroConteudo_Erro()
    {
        var dir = Path.Join(_root, "data", "workspaces", "u1", "repos", "a__b");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "outro.txt"), "x");

        var (binding, error) = await _repos.OpenAsync(
            "u1", "a/b", "main", "unused", null, default);
        Assert.Multiple(() =>
        {
            Assert.That(binding, Is.Null);
            Assert.That(error, Does.Contain("outro conteúdo"));
        });
    }

    [Test]
    public async Task Repo_Open_CheckoutExistente_BranchInexistente_Erro()
    {
        RequireGit();
        var origin = CriarOrigem("main");
        try
        {
            var (_, err1) = await _repos.OpenAsync(
                "u1", "a/b", "main", origin, null, default);
            Assert.That(err1, Is.Null);

            var (binding, error) = await _repos.OpenAsync(
                "u1", "a/b", "branch-fantasma", origin, null, default);
            Assert.Multiple(() =>
            {
                Assert.That(binding, Is.Null);
                Assert.That(error, Does.Contain("git fetch"));
            });
        }
        finally
        {
            Directory.Delete(origin, recursive: true);
        }
    }

    [Test]
    public async Task Repo_Open_CloneFalha_Erro()
    {
        var (binding, error) = await _repos.OpenAsync(
            "u1", "a/b", "main",
            Path.Join(_root, "origem-inexistente"), null, default);
        Assert.Multiple(() =>
        {
            Assert.That(binding, Is.Null);
            Assert.That(error, Does.StartWith("git clone:"));
        });
    }

    [Test]
    public async Task Repo_Open_Cancelado_PropagaOperacaoCancelada()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _repos.OpenAsync("u1", "a/b", "main", "unused", null, cts.Token));
    }

    // ---------------- Pulls (SPEC-20261009-pr-ci-panel) ----------------

    [Test]
    public async Task Pulls_SemToken_NeedsToken()
    {
        var result = await _svc.ListPullRequestsAsync("u1", "a", "b", default);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Github, Is.False);
            Assert.That(result.NeedsToken, Is.True);
            Assert.That(result.Pulls, Is.Empty);
        });
        Assert.That(_github.RequestCount, Is.Zero, "não deve chamar a API sem token");
    }

    [Test]
    public async Task Pulls_TokenInvalido401_NeedsToken()
    {
        await _config.SetAsync("u:u1:github.token", "bad-token", default);
        _github.Status = HttpStatusCode.Unauthorized;
        var result = await _svc.ListPullRequestsAsync("u1", "a", "b", default);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Github, Is.False);
            Assert.That(result.NeedsToken, Is.True);
            Assert.That(result.Pulls, Is.Empty);
        });
    }

    [Test]
    public async Task Pulls_ErroRede_GithubFalse()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        _github.Throw = new HttpRequestException("boom");
        Assert.That(await _svc.ListPullRequestsAsync("u1", "a", "b", default), Is.Null);
    }

    [Test]
    public async Task Pulls_HappyPath_MapeiaERollupChecks()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        var result = await _svc.ListPullRequestsAsync("u1", "afonsoft", "demo", default);
        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Github, Is.True);
            Assert.That(result.NeedsToken, Is.False);
            Assert.That(result.Pulls, Has.Count.EqualTo(2));
        });

        var first = result.Pulls[0];
        Assert.Multiple(() =>
        {
            Assert.That(first.Number, Is.EqualTo(7));
            Assert.That(first.Title, Is.EqualTo("feat: painel"));
            Assert.That(first.Author, Is.EqualTo("afonso"));
            Assert.That(first.HeadRef, Is.EqualTo("devin/x"));
            Assert.That(first.Url, Does.Contain("/pull/7"));
            Assert.That(first.Draft, Is.False);
            // stub: status success+pending, check-runs success+failure+in_progress
            Assert.That(first.Checks.Total, Is.EqualTo(5));
            Assert.That(first.Checks.Passing, Is.EqualTo(2));
            Assert.That(first.Checks.Failing, Is.EqualTo(1));
            Assert.That(first.Checks.Pending, Is.EqualTo(2));
            Assert.That(first.Checks.State, Is.EqualTo("failure"));
        });
        Assert.That(result.Pulls[1].Draft, Is.True);
    }

    [Test]
    public async Task Pulls_RollupSucessoEPendente()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        _github.StatusBody = "{\"state\":\"success\",\"statuses\":[{\"state\":\"success\"}]}";
        _github.CheckRunsBody = "{\"check_runs\":[{\"status\":\"completed\",\"conclusion\":\"success\"}]}";
        var ok = await _svc.ListPullRequestsAsync("u1", "a", "b", default);
        Assert.That(ok!.Pulls[0].Checks.State, Is.EqualTo("success"));
        Assert.That(ok.Pulls[0].Checks.Passing, Is.EqualTo(2));

        var svc2 = new GitHubService(new StubFactory(_github), _config);
        _github.StatusBody = "{\"state\":\"pending\",\"statuses\":[{\"state\":\"pending\"}]}";
        _github.CheckRunsBody = "{\"check_runs\":[]}";
        var pending = await svc2.ListPullRequestsAsync("u1", "a", "b", default);
        Assert.That(pending!.Pulls[0].Checks.State, Is.EqualTo("pending"));
    }

    [Test]
    public async Task Pulls_Cache60s_NaoRepeteChamada()
    {
        await _svc.SetTokenAsync("u1", "pat-1", default);
        var svc = new GitHubService(new StubFactory(_github), _config, _cache);
        var before = _github.RequestCount;
        var first = await svc.ListPullRequestsAsync("u1", "a", "b", default);
        var afterFirst = _github.RequestCount;
        var second = await svc.ListPullRequestsAsync("u1", "a", "b", default);
        Assert.Multiple(() =>
        {
            Assert.That(first!.Pulls, Has.Count.EqualTo(2));
            Assert.That(second!.Pulls, Is.EqualTo(first!.Pulls), "60s cache devolve a mesma resposta");
            Assert.That(_github.RequestCount, Is.EqualTo(afterFirst));
            Assert.That(afterFirst, Is.GreaterThan(before));
        });
    }

    // ---------------- Endpoints ----------------

    [Test]
    public async Task Endpoints_SemAuth_401()
    {
        using var dbScope = TestInfra.UseDb();
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        foreach (var uri in new[]
        {
            "/api/v1/github/config", "/api/v1/github/repos",
            "/api/v1/workspace/repo/", "/api/v1/workspace/repo/pulls",
        })
        {
            var response = await client.GetAsync(uri);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), uri);
        }
    }

    [Test]
    public async Task Endpoints_Pulls_SemBinding_404()
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"openwebui-pulls-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={dbPath}");
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        try
        {
            var (auth, _) = await SignUpAsync(client, "pulls0");
            var response = await client.GetAsync("/api/v1/workspace/repo/pulls");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", null);
            if (File.Exists(dbPath))
            {
                TestInfra.DeleteDb(dbPath);
            }
        }
    }

    [Test]
    public async Task Endpoints_Pulls_BindingSemToken_NeedsTokenENunca500()
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"openwebui-pulls-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={dbPath}");
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        try
        {
            var (auth, userId) = await SignUpAsync(client, "pulls1");
            // Semeia o binding direto no kv (o endpoint de open clonaria de verdade).
            await SeedKvAsync(dbPath, $"u:{userId}:workspace.repo",
                new WorkspaceRepoBinding("a/b", "main", "repos/a__b"));

            var pulls = await client.GetFromJsonAsync<WorkspacePullsResponse>(
                "/api/v1/workspace/repo/pulls");
            Assert.Multiple(() =>
            {
                Assert.That(pulls!.Github, Is.False);
                Assert.That(pulls.NeedsToken, Is.True);
                Assert.That(pulls.Pulls, Is.Empty);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", null);
            if (File.Exists(dbPath))
            {
                TestInfra.DeleteDb(dbPath);
            }
        }
    }

    [Test]
    public async Task Endpoints_Pulls_TokenBogus_GithubFalse()
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"openwebui-pulls-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={dbPath}");
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        try
        {
            var (auth, userId) = await SignUpAsync(client, "pulls2");
            await SeedKvAsync(dbPath, $"u:{userId}:workspace.repo",
                new WorkspaceRepoBinding("a/b", "main", "repos/a__b"));
            await SeedKvAsync(dbPath, $"u:{userId}:github.token", "token-bogus-x");

            var response = await client.GetAsync("/api/v1/workspace/repo/pulls");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var pulls = await response.Content.ReadFromJsonAsync<WorkspacePullsResponse>();
            // api.github.com inalcançável ou 401 → sempre github:false, nunca 500.
            Assert.That(pulls!.Github, Is.False);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", null);
            if (File.Exists(dbPath))
            {
                TestInfra.DeleteDb(dbPath);
            }
        }
    }

    [Test]
    public async Task Endpoints_ConfigEBinding_FluxoBasico()
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"openwebui-gh-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={dbPath}");
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        try
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/auths/signup", new SignUpRequest("GH", "gh@gh.local", "senha123"));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", auth.Token);

            // Sem token → configured=false; binding vazio.
            var cfg = await client.GetFromJsonAsync<GitHubConfigResponse>("/api/v1/github/config");
            Assert.That(cfg!.Configured, Is.False);
            var bound = await client.GetFromJsonAsync<WorkspaceRepoResponse>("/api/v1/workspace/repo/");
            Assert.That(bound!.Repo, Is.Null);

            // Token inválido → 400 (GitHub recusa ou indisponível).
            var put = await client.PutAsJsonAsync(
                "/api/v1/github/config", new GitHubTokenRequest("token-bogus-x"));
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

            // Slug inválido → 400; unbind → ok e continua null.
            var open = await client.PostAsJsonAsync("/api/v1/workspace/repo/open",
                new WorkspaceRepoOpenRequest("sem-barra", "main"));
            Assert.That(open.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            var del = await client.DeleteAsync("/api/v1/workspace/repo/");
            Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", null);
            if (File.Exists(dbPath))
            {
                TestInfra.DeleteDb(dbPath);
            }
        }
    }

    // ---------------- helpers ----------------

    private static void RequireGit()
    {
        try
        {
            Git(null, "--version");
        }
        catch (Win32Exception) { Assert.Ignore("git indisponível neste ambiente."); }
        catch (InvalidOperationException) { Assert.Ignore("git indisponível neste ambiente."); }
    }

    /// <summary>Cria um repo local (remote <c>file://</c>) com 1 commit.</summary>
    private static string CriarOrigem(string branch, string? extraBranch = null)
    {
        var origin = Path.Join(Path.GetTempPath(), $"owui-origin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-b", branch);
        File.WriteAllText(Path.Join(origin, "readme.md"), "oi\n");
        Git(origin, "-c", "user.email=t@t", "-c", "user.name=t", "add", ".");
        Git(origin, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "base");
        if (extraBranch is not null)
        {
            Git(origin, "branch", extraBranch);
        }
        return origin;
    }

    private static string? Git(string? workdir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (workdir is not null)
        {
            psi.WorkingDirectory = workdir;
        }
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(psi);
        process!.WaitForExit(15000);
        return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
    }

    /// <summary>Cadastra um usuário via signup e autentica o client.</summary>
    private static async Task<(AuthResponse Auth, string UserId)> SignUpAsync(
        HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, $"{name}@t.local", "senha123"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.Token);
        return (auth, auth.User.Id);
    }

    /// <summary>Grava uma entrada no kv da base da factory (seed de binding/token).</summary>
    private static async Task SeedKvAsync<T>(string dbPath, string key, T value)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options);
        var cache = TestCache.Create();
        await new ConfigService(db, cache).SetAsync(key, value, default);
    }

    /// <summary>Stub da API do GitHub: /user, /user/repos, /repos/{o}/{r}*.</summary>
    private sealed class GitHubStub : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? LastAuth { get; private set; }
        public string? Body { get; set; }
        public string? BranchesBody { get; set; }
        public string? PullsBody { get; set; }
        public string? StatusBody { get; set; }
        public string? CheckRunsBody { get; set; }
        public Exception? Throw { get; set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuth = request.Headers.Authorization?.ToString();
            RequestCount++;
            if (Throw is not null)
            {
                throw Throw;
            }
            var path = request.RequestUri!.AbsolutePath;
            var body = Body
                ?? (Status != HttpStatusCode.OK ? "{}"
                : path == "/user" ? "{\"login\":\"afonso\"}"
                : path == "/user/repos" ? """
                    [{"full_name":"afonsoft/open-webui","name":"open-webui","private":true,
                      "default_branch":"main","html_url":"https://github.com/afonsoft/open-webui",
                      "description":"fork dotnet","updated_at":"2026-10-08T00:00:00Z"},
                     {"full_name":"afonsoft/outro","name":"outro","private":false,
                      "default_branch":"dev","html_url":"https://github.com/afonsoft/outro",
                      "description":"repo 2","updated_at":null}]
                    """
                : path.EndsWith("/pulls") ? (PullsBody ?? """
                    [{"number":7,"title":"feat: painel","draft":false,
                      "user":{"login":"afonso"},"head":{"ref":"devin/x","sha":"abc1"},
                      "updated_at":"2026-10-09T10:00:00Z","html_url":"https://github.com/a/b/pull/7"},
                     {"number":8,"title":"fix: bug","draft":true,
                      "user":{"login":"bia"},"head":{"ref":"devin/y","sha":"def2"},
                      "updated_at":"2026-10-08T09:00:00Z","html_url":"https://github.com/a/b/pull/8"}]
                    """)
                : path.EndsWith("/check-runs") ? (CheckRunsBody ?? """
                    {"check_runs":[{"status":"completed","conclusion":"success"},
                                   {"status":"completed","conclusion":"failure"},
                                   {"status":"in_progress","conclusion":null}]}
                    """)
                : path.EndsWith("/status") ? (StatusBody ?? """
                    {"state":"success","statuses":[{"state":"success"},{"state":"pending"}]}
                    """)
                : path.EndsWith("/branches") ? (BranchesBody ?? "[{\"name\":\"main\"},{\"name\":\"dev\"}]")
                : "{\"default_branch\":\"main\"}");
            var response = new HttpResponseMessage(
                Status == HttpStatusCode.OK ? HttpStatusCode.OK : Status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            _pending.Add(response);
            return Task.FromResult(response);
        }

        private readonly List<HttpResponseMessage> _pending = [];

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var pending in _pending)
                {
                    pending.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    // ---------------- Binding por chat (SPEC-20261010-chat-repo-binding) ----------------

    [Test]
    public async Task ChatBinding_Resolve_PriorizaChat_SobreGlobal()
    {
        var originA = CriarOrigem("main");
        var originB = CriarOrigem("main");
        var (global, _) = await _repos.OpenAsync("u1", "a/ra", "main", originA, null, default);
        var (chat, err) = await _repos.OpenChatAsync("u1", "c1", "b/rb", "main", originB, null, default);

        Assert.That(err, Is.Null);
        var (resolved, source) = await _repos.ResolveBindingAsync("u1", "c1", default);
        Assert.Multiple(() =>
        {
            Assert.That(source, Is.EqualTo("chat"));
            Assert.That(resolved!.Repo, Is.EqualTo("b/rb"));
            Assert.That(resolved.Dir, Is.Not.EqualTo(global!.Dir));
            Assert.That(_repos.ResolveWorkdirAsync("u1", "c1", default).Result,
                Does.EndWith(resolved.Dir));
        });
    }

    [Test]
    public async Task ChatBinding_SemChatBinding_NaoHerdaGlobal_EClearFicaNone()
    {
        var origin = CriarOrigem("main");
        await _repos.OpenAsync("u1", "a/ra", "main", origin, null, default);

        // SPEC-15: binding por chat é estrito — sem binding próprio o
        // resolve devolve "none"; o global NÃO vaza para dentro do chat.
        var (fallback, source) = await _repos.ResolveBindingAsync("u1", "c9", default);
        Assert.Multiple(() =>
        {
            Assert.That(source, Is.EqualTo("none"));
            Assert.That(fallback, Is.Null);
        });

        // Binding por chat → depois de limpar segue "none" (sem global).
        var originB = CriarOrigem("main");
        await _repos.OpenChatAsync("u1", "c9", "b/rb", "main", originB, null, default);
        await _repos.SetChatBindingAsync("c9", null, default);
        var (back, backSource) = await _repos.ResolveBindingAsync("u1", "c9", default);
        Assert.Multiple(() =>
        {
            Assert.That(backSource, Is.EqualTo("none"));
            Assert.That(back, Is.Null);
        });
    }

    [Test]
    public async Task ChatBinding_SemNenhum_SourceNone()
    {
        var (binding, source) = await _repos.ResolveBindingAsync("u9", "c9", default);
        Assert.Multiple(() =>
        {
            Assert.That(source, Is.EqualTo("none"));
            Assert.That(binding, Is.Null);
            Assert.That(_repos.ResolveWorkdirAsync("u9", "c9", default).Result,
                Does.EndWith("workspaces/u9".Replace('/', Path.DirectorySeparatorChar)));
        });
    }

    [Test]
    public async Task ChatBinding_DoisChats_WorkdirsDistintos()
    {
        var originA = CriarOrigem("main");
        var originB = CriarOrigem("main");
        await _repos.OpenChatAsync("u1", "cA", "a/ra", "main", originA, null, default);
        await _repos.OpenChatAsync("u1", "cB", "b/rb", "main", originB, null, default);

        var wdA = await _repos.ResolveWorkdirAsync("u1", "cA", default);
        var wdB = await _repos.ResolveWorkdirAsync("u1", "cB", default);
        Assert.That(wdA, Is.Not.EqualTo(wdB));
        // Worktree por conversa (SPEC-15): .git é arquivo, não pasta.
        Assert.That(File.Exists(Path.Join(wdA, ".git")), Is.True);
        Assert.That(File.Exists(Path.Join(wdB, ".git")), Is.True);
    }

    [Test]
    public async Task ChatBinding_OpenChat_RemotoInvalido_DevolveErro()
    {
        var (binding, error) = await _repos.OpenChatAsync(
            "u1", "c1", "a/ra", "main",
            Path.Join(_root, "origem-inexistente"), null, default);
        Assert.Multiple(() =>
        {
            Assert.That(binding, Is.Null);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public async Task ChatBinding_GetESet_GravaELimpa()
    {
        var origin = CriarOrigem("main");
        var (binding, _) = await _repos.OpenChatAsync(
            "u1", "c1", "a/ra", "main", origin, null, default);

        var lido = await _repos.GetChatBindingAsync("c1", default);
        Assert.That(lido!.Repo, Is.EqualTo("a/ra"));
        Assert.That(lido.Dir, Is.EqualTo(binding!.Dir));

        await _repos.SetChatBindingAsync("c1", null, default);
        Assert.That(await _repos.GetChatBindingAsync("c1", default), Is.Null);
    }

    [Test]
    public async Task ChatBinding_HerdaComandosDoGlobal()
    {
        var origin = CriarOrigem("main");
        await _repos.OpenAsync("u1", "a/ra", "main", origin, null, default);
        await _repos.SetTestCommandAsync("u1", "dotnet test", default);
        await _repos.SetFormatCommandAsync("u1", "dotnet format", default);
        var originB = CriarOrigem("main");
        var (chat, _) = await _repos.OpenChatAsync(
            "u1", "c1", "b/rb", "main", originB, null, default);
        Assert.Multiple(() =>
        {
            Assert.That(chat!.TestCommand, Is.EqualTo("dotnet test"));
            Assert.That(chat.FormatCommand, Is.EqualTo("dotnet format"));
        });
    }

    [Test]
    public async Task ChatBinding_OpenChat_SlugOuBranchInvalidos_DevolveErro()
    {
        var (b1, e1) = await _repos.OpenChatAsync(
            "u1", "c1", "sem-barra", "main", "x", null, default);
        var (b2, e2) = await _repos.OpenChatAsync(
            "u1", "c1", "a/b", "-ruim", "x", null, default);
        Assert.Multiple(() =>
        {
            Assert.That(b1, Is.Null);
            Assert.That(e1, Is.Not.Null.And.Not.Empty);
            Assert.That(b2, Is.Null);
            Assert.That(e2, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public async Task ChatBinding_Reopen_MesmaCheckout_TrocaBranch()
    {
        // Mesmo slug → checkout compartilhado: o segundo open cai no
        // caminho fetch+switch+pull do EnsureCheckoutAsync.
        var origin = CriarOrigem("main", "dev");
        await _repos.OpenChatAsync("u1", "c1", "a/ra", "main", origin, null, default);
        var (b2, err) = await _repos.OpenChatAsync(
            "u1", "c2", "a/ra", "dev", origin, null, default);
        Assert.Multiple(() =>
        {
            Assert.That(err, Is.Null);
            Assert.That(b2!.Branch, Is.EqualTo("dev"));
        });
        // SPEC-15: worktree por conversa — c1 e c2 têm dirs próprios sob
        // o mesmo slug (checkout base compartilhado).
        var (b1, _) = await _repos.ResolveBindingAsync("u1", "c1", default);
        Assert.Multiple(() =>
        {
            Assert.That(b1!.Dir, Is.Not.EqualTo(b2!.Dir));
            Assert.That(b1.Dir, Does.StartWith("worktrees/"));
            Assert.That(b2.Dir, Does.StartWith("worktrees/"));
        });
    }

    [Test]
    public async Task ChatBinding_Worktree_ReleaseRemove_EResolveRecria()
    {
        var origin = CriarOrigem("main");
        var (binding, err) = await _repos.OpenChatAsync(
            "u1", "c1", "a/ra", "main", origin, null, default);
        Assert.That(err, Is.Null);
        var root = _repos.WorkspaceRoot("u1");
        var wtAbs = Path.Join(root, binding!.Dir);
        var baseAbs = Path.Join(root, "repos", "a__ra");

        // Worktree por conversa existe em disco (.git é arquivo).
        Assert.Multiple(() =>
        {
            Assert.That(binding.Dir, Does.StartWith("worktrees/"));
            Assert.That(File.Exists(Path.Join(wtAbs, ".git")), Is.True);
        });

        // Archive (keepBinding): worktree sai de disco; binding e checkout
        // base sobrevivem (o chat ainda referencia o repo).
        await _repos.ReleaseChatWorkspaceAsync("u1", "c1", keepBinding: true, default);
        var kept = await _repos.GetChatBindingAsync("c1", default);
        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(wtAbs), Is.False);
            Assert.That(kept, Is.Not.Null);
            Assert.That(Directory.Exists(baseAbs), Is.True);
        });

        // Unarchive: a primeira resolução recria o worktree sob demanda.
        var wd = await _repos.ResolveWorkdirAsync("u1", "c1", default);
        Assert.Multiple(() =>
        {
            Assert.That(wd, Is.EqualTo(wtAbs));
            Assert.That(File.Exists(Path.Join(wtAbs, ".git")), Is.True);
        });

        // Unbind (sem keepBinding): worktree + binding saem; sem mais
        // referências o checkout base sai de disco junto.
        await _repos.ReleaseChatWorkspaceAsync("u1", "c1", keepBinding: false, default);
        var cleared = await _repos.GetChatBindingAsync("c1", default);
        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(wtAbs), Is.False);
            Assert.That(cleared, Is.Null);
            Assert.That(Directory.Exists(baseAbs), Is.False);
        });
    }

    [Test]
    public async Task Workdir_ChatIdNull_SemBinding_CaiNoDefault()
    {
        var wd = await _repos.ResolveWorkdirAsync("u7", null, default);
        Assert.That(wd, Does.EndWith("workspaces/u7"));
    }

    private sealed class StubEnv(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
