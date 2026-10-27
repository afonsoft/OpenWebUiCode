using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261027-scratch-workspace: todo chat sem repo vinculado recebe pasta
/// scratch exclusiva <c>data/workspaces/{u}/scratch/{chatId}</c> (lazy, com
/// git init); filhos de delegate_task caem em <c>agents/{childId}</c> dentro
/// do scratch do pai; delete e archive removem a pasta do disco.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ScratchWorkspaceTests
{
    private string _root = null!;
    private AppDbContext _db = null!;
    private ConfigService _config = null!;
    private WorkspaceRepoService _repos = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-scratch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Join(_root, "t.db")}").Options);
        DatabaseMigrator.MigrateAsync(_db).GetAwaiter().GetResult();
        _config = new ConfigService(_db, TestCache.Create());
        _repos = new WorkspaceRepoService(_config, new StubEnv(_root));
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    // DataPaths.Root(contentRoot) = {contentRoot}/data.
    private string ScratchPath(string chatId) =>
        Path.Join(_root, "data", "workspaces", "u1", "scratch", chatId);

    // ---------------- RF-001/RF-002: resolução + materialização lazy ----------------

    [Test]
    public async Task Workdir_SemBinding_ComChatId_DevolveScratchComGitInit()
    {
        var workdir = await _repos.ResolveWorkdirAsync("u1", "chat-abc", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(workdir, Is.EqualTo(ScratchPath("chat-abc")));
            Assert.That(Directory.Exists(workdir), Is.True);
            Assert.That(Directory.Exists(Path.Join(workdir, ".git")), Is.True,
                "scratch materializa com git init — publish é só remote+push");
        });
    }

    [Test]
    public async Task Workdir_SemChatId_SemBinding_DevolveRaizDoUsuario()
    {
        // Uso global (sem conversa) segue na raiz do usuário — inalterado.
        var workdir = await _repos.ResolveWorkdirAsync("u1", CancellationToken.None);

        Assert.That(workdir, Is.EqualTo(Path.Join(_root, "data", "workspaces", "u1")));
    }

    [Test]
    public void Scratch_NaoExisteAntesDoPrimeiroResolve()
    {
        // Lazy: chat que nunca usa tool de workspace não gera pasta.
        Assert.That(Directory.Exists(ScratchPath("chat-nunca-usou")), Is.False);
    }

    [Test]
    public async Task Scratch_Idempotente_ConteudoSobreviveAResolve()
    {
        var workdir = await _repos.ResolveWorkdirAsync("u1", "chat-1", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Join(workdir, "f.txt"), "x");

        var again = await _repos.ResolveWorkdirAsync("u1", "chat-1", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(again, Is.EqualTo(workdir));
            Assert.That(File.ReadAllText(Path.Join(again, "f.txt")), Is.EqualTo("x"));
        });
    }

    [Test]
    public async Task Scratch_Reinit_QuandoGitSome()
    {
        var workdir = await _repos.ResolveWorkdirAsync("u1", "chat-2", CancellationToken.None);
        Directory.Delete(Path.Join(workdir, ".git"), recursive: true);

        var again = await _repos.ResolveWorkdirAsync("u1", "chat-2", CancellationToken.None);

        Assert.That(Directory.Exists(Path.Join(again, ".git")), Is.True,
            "scratch sem .git é re-inicializado no próximo resolve");
    }

    [Test]
    public async Task Scratch_ChatIdHostil_SanitizadoDentroDoJail()
    {
        var scratchRoot = Path.Join(_root, "data", "workspaces", "u1", "scratch");

        // ".." puro sobreviveria ao Sanitize (pontos preservados) — o dir
        // precisa ficar neutro dentro do jail; "../x" vira nome literal inerte.
        var dotdot = await _repos.ResolveWorkdirAsync("u1", "..", CancellationToken.None);
        var dots = await _repos.ResolveWorkdirAsync("u1", "../../fora", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(dotdot, Is.EqualTo(Path.Join(scratchRoot, "_")),
                "chatId \"..\" não pode resolver para fora de scratch/");
            Assert.That(dots.StartsWith(scratchRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal), Is.True);
            Assert.That(Directory.Exists(dots), Is.True);
        });
    }

    // ---------------- RF-004: filho de delegate_task cai em agents/ do pai ----------------

    [Test]
    public async Task Workdir_ChatFilho_VaiParaAgentsDoPai()
    {
        var parentWorkdir = await _repos.ResolveWorkdirAsync("u1", "pai-1", CancellationToken.None);
        await _repos.SetScratchParentAsync("filho-1", "pai-1", CancellationToken.None);

        var childWorkdir = await _repos.ResolveWorkdirAsync("u1", "filho-1", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(childWorkdir, Is.EqualTo(
                Path.Join(parentWorkdir, "agents", "filho-1")));
            Assert.That(Directory.Exists(childWorkdir), Is.True);
            // Sem .git próprio — o trabalho do filho integra o repo do pai
            // e sai junto no publish.
            Assert.That(Directory.Exists(Path.Join(childWorkdir, ".git")), Is.False);
        });
    }

    // ---------------- RF-005: delete/archive removem o scratch ----------------

    [Test]
    public async Task Release_Delete_RemoveScratch()
    {
        var workdir = await _repos.ResolveWorkdirAsync("u1", "chat-del", CancellationToken.None);

        await _repos.ReleaseChatWorkspaceAsync(
            "u1", "chat-del", keepBinding: false, CancellationToken.None);

        Assert.That(Directory.Exists(workdir), Is.False);
    }

    [Test]
    public async Task Release_Archive_RemoveScratch()
    {
        // Decisão da SPEC: scratch é rascunho — archive também descarta
        // (diferente do worktree de repo, que o unarchive recria).
        var workdir = await _repos.ResolveWorkdirAsync("u1", "chat-arch", CancellationToken.None);

        await _repos.ReleaseChatWorkspaceAsync(
            "u1", "chat-arch", keepBinding: true, CancellationToken.None);

        Assert.That(Directory.Exists(workdir), Is.False);
    }

    [Test]
    public async Task Release_SemScratch_NaoFalha()
    {
        await _repos.ReleaseChatWorkspaceAsync(
            "u1", "chat-inexistente", keepBinding: false, CancellationToken.None);
        Assert.Pass("release idempotente — sem scratch, sem binding: no-op");
    }

    [Test]
    public async Task Release_DoPai_RemoveAgentsDosFilhos()
    {
        var parentWorkdir = await _repos.ResolveWorkdirAsync("u1", "pai-2", CancellationToken.None);
        await _repos.SetScratchParentAsync("filho-2", "pai-2", CancellationToken.None);
        var childWorkdir = await _repos.ResolveWorkdirAsync("u1", "filho-2", CancellationToken.None);
        Assert.That(childWorkdir.StartsWith(parentWorkdir, StringComparison.Ordinal), Is.True);

        await _repos.ReleaseChatWorkspaceAsync(
            "u1", "pai-2", keepBinding: false, CancellationToken.None);

        Assert.That(Directory.Exists(childWorkdir), Is.False,
            "scratch do pai leva os agents/* dos filhos junto");
    }

    [Test]
    public async Task Release_Unbind_PreservaScratch()
    {
        // Desvincular o repo (PUT workspace-repo vazio) usa dropScratch:false —
        // o rascunho da conversa não pode sumir numa troca de binding.
        var workdir = await _repos.ResolveWorkdirAsync("u1", "chat-keep", CancellationToken.None);
        File.WriteAllText(Path.Join(workdir, "wip.txt"), "x");

        await _repos.ReleaseChatWorkspaceAsync(
            "u1", "chat-keep", keepBinding: false, CancellationToken.None,
            dropScratch: false);

        Assert.That(File.Exists(Path.Join(workdir, "wip.txt")), Is.True);
    }

    [Test]
    public async Task Release_DoFilho_RemoveAgentsNoPai()
    {
        var parentWorkdir = await _repos.ResolveWorkdirAsync("u1", "pai-3", CancellationToken.None);
        await _repos.SetScratchParentAsync("filho-3", "pai-3", CancellationToken.None);
        var childWorkdir = await _repos.ResolveWorkdirAsync("u1", "filho-3", CancellationToken.None);
        File.WriteAllText(Path.Join(childWorkdir, "out.txt"), "x");

        await _repos.ReleaseChatWorkspaceAsync(
            "u1", "filho-3", keepBinding: false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(childWorkdir), Is.False,
                "agents/{filho} sai do scratch do pai");
            Assert.That(Directory.Exists(parentWorkdir), Is.True,
                "o scratch do pai sobrevive à remoção do filho");
        });
    }

    // ---------------- Regressão: com binding segue worktree ----------------

    [Test]
    public async Task Workdir_ComBinding_NaoUsaScratch()
    {
        // Repo remoto local via file:// — mesmo padrão de GitHubWorkspaceTests.
        var remote = Path.Join(_root, "remote");
        Directory.CreateDirectory(remote);
        Run(remote, "init", "--bare");
        var seed = Path.Join(_root, "seed");
        Run(seed, "init");
        Run(seed, "config", "user.email", "t@t.local");
        Run(seed, "config", "user.name", "t");
        await File.WriteAllTextAsync(Path.Join(seed, "a.txt"), "x");
        Run(seed, "add", ".");
        Run(seed, "commit", "-m", "init");
        Run(seed, "remote", "add", "origin", remote);
        Run(seed, "push", "origin", "HEAD:main");

        var (binding, error) = await _repos.OpenChatAsync(
            "u1", "chat-repo", "owner/repo", "main", remote, null, CancellationToken.None);

        Assert.That(error, Is.Null);
        var workdir = await _repos.ResolveWorkdirAsync("u1", "chat-repo", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(workdir, Does.Contain($"{Path.DirectorySeparatorChar}worktrees{Path.DirectorySeparatorChar}"),
                "com binding o workdir é o worktree por conversa — scratch não se aplica");
            Assert.That(workdir, Does.Not.Contain(
                $"{Path.DirectorySeparatorChar}scratch{Path.DirectorySeparatorChar}"));
        });
    }

    // ---------------- RF-006/RF-007: publish + status ----------------

    [Test]
    public async Task ScratchStatus_SemPasta_ExistsFalse()
    {
        var status = await _repos.GetScratchStatusAsync("u1", "chat-nada", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(status.Exists, Is.False);
            Assert.That(status.FileCount, Is.EqualTo(0));
            Assert.That(status.HasCommits, Is.False);
        });
    }

    [Test]
    public async Task ScratchStatus_AposMaterializar_ContaArquivosSemDotGit()
    {
        var dir = await _repos.ResolveWorkdirAsync("u1", "chat-st", CancellationToken.None);
        File.WriteAllText(Path.Join(dir, "a.txt"), "oi");
        Directory.CreateDirectory(Path.Join(dir, "sub"));
        File.WriteAllText(Path.Join(dir, "sub", "b.txt"), "oi");

        var status = await _repos.GetScratchStatusAsync("u1", "chat-st", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(status.Exists, Is.True);
            Assert.That(status.FileCount, Is.EqualTo(2), ".git não entra na contagem");
            Assert.That(status.HasCommits, Is.False);
        });
    }

    [Test]
    public async Task Publish_SemPasta_404()
    {
        var publish = NewPublish();
        var (result, status, _) = await publish.PublishAsync(
            "u1", "chat-x", "Tester", "t@t",
            new PublishWorkspaceRequest("o/r", null, null), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null);
            Assert.That(status, Is.EqualTo(404));
        });
    }

    [Test]
    public async Task Publish_SemDestino_400()
    {
        await _repos.ResolveWorkdirAsync("u1", "chat-p", CancellationToken.None);
        var publish = NewPublish();

        var (result, status, error) = await publish.PublishAsync(
            "u1", "chat-p", "Tester", "t@t",
            new PublishWorkspaceRequest(null, null, null), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null);
            Assert.That(status, Is.EqualTo(400));
            Assert.That(error, Does.Contain("repo ou remoteUrl"));
        });
    }

    [Test]
    public async Task Publish_PastaVazia_400()
    {
        // Scratch materializado mas sem nenhum arquivo (só .git) → 400.
        await _repos.ResolveWorkdirAsync("u1", "chat-vazio", CancellationToken.None);
        var publish = NewPublish();

        var (result, status, error) = await publish.PublishAsync(
            "u1", "chat-vazio", "Tester", "t@t",
            new PublishWorkspaceRequest("o/r", null, null), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null);
            Assert.That(status, Is.EqualTo(400));
            Assert.That(error, Does.Contain("vazia"));
        });
    }

    [Test]
    public async Task Publish_RepoSemNenhumToken_400()
    {
        var dir = await _repos.ResolveWorkdirAsync("u1", "chat-t", CancellationToken.None);
        File.WriteAllText(Path.Join(dir, "a.txt"), "oi");
        var publish = NewPublish();

        var (result, status, error) = await publish.PublishAsync(
            "u1", "chat-t", "Tester", "t@t",
            new PublishWorkspaceRequest("o/r", null, null), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null);
            Assert.That(status, Is.EqualTo(400));
            Assert.That(error, Does.Contain("token"));
        });
    }

    [Test]
    public async Task Publish_RemoteUrlFile_CommitaEFazPush_SemGravarRemote()
    {
        RequireGit();
        var bare = Path.Join(_root, "destino.git");
        Run(_root, "init", "--bare", "-q", bare);

        var dir = await _repos.ResolveWorkdirAsync("u1", "chat-push", CancellationToken.None);
        File.WriteAllText(Path.Join(dir, "trabalho.txt"), "conteúdo do rascunho\n");

        var publish = NewPublish();
        var (result, status, error) = await publish.PublishAsync(
            "u1", "chat-push", "Tester", "t@t.local",
            new PublishWorkspaceRequest(null, new Uri(bare).AbsoluteUri, null),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(error, Is.Null);
            Assert.That(status, Is.EqualTo(0));
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Branch, Is.EqualTo("chat/chat-push"));
        });

        // O commit chegou ao remote.
        var log = WorkspaceRepoService.RunGitAsync(
            bare, CancellationToken.None, "log", "--format=%s", "chat/chat-push")
            .GetAwaiter().GetResult();
        Assert.That(log, Does.Contain("Work from chat chat-push"));

        // Remote nunca é gravado — nada de credencial ou URL no .git/config.
        var remotes = WorkspaceRepoService.RunGitAsync(
            dir, CancellationToken.None, "remote").GetAwaiter().GetResult();
        Assert.That(remotes?.Trim(), Is.Null.Or.Empty);
        var gitConfig = File.ReadAllText(Path.Join(dir, ".git", "config"));
        Assert.That(gitConfig, Does.Not.Contain("destino.git"));
    }

    // ---------------- RF-006: resolução de destino (slug/repo/remoto) ----------------

    [Test]
    public async Task Publish_RemoteUrlInvalida_400()
    {
        var dir = await _repos.ResolveWorkdirAsync("u1", "chat-badurl", CancellationToken.None);
        File.WriteAllText(Path.Join(dir, "a.txt"), "x");

        var (_, status, error) = await NewPublish().PublishAsync(
            "u1", "chat-badurl", "T", "t@t",
            new PublishWorkspaceRequest(null, "isso-nao-e-url", null),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(400));
            Assert.That(error, Does.Contain("remoteUrl"));
        });
    }

    [Test]
    public async Task Publish_RemoteGithub_ComTokenSalvo_MontaCompare_EPush502()
    {
        RequireGit();
        // PAT salvo → extraheader + consulta de branches p/ compare URL.
        await _config.SetAsync("u:u1:github.token", "tok", CancellationToken.None);
        var github = new GitHubService(new StubHttpFactory(new StubHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/branches")
                ? Json(HttpStatusCode.OK, "[{\"name\":\"main\"}]")
                : Json(HttpStatusCode.OK, "{\"default_branch\":\"main\"}"))), _config);
        var dir = await _repos.ResolveWorkdirAsync("u1", "chat-gh", CancellationToken.None);
        File.WriteAllText(Path.Join(dir, "a.txt"), "x");

        var (_, status, error) = await new WorkspacePublishService(_repos, github)
            .PublishAsync("u1", "chat-gh", "T", "t@t",
                new PublishWorkspaceRequest(null, "https://github.com/a/b.git", null),
                CancellationToken.None);

        // Destino resolvido até o push — que falha sem credencial real.
        Assert.That(status, Is.EqualTo(502), error);
    }

    [Test]
    public async Task Publish_RepoNomeInvalido_400()
    {
        var dir = await _repos.ResolveWorkdirAsync("u1", "chat-badname", CancellationToken.None);
        File.WriteAllText(Path.Join(dir, "a.txt"), "x");
        await _config.SetAsync("u:u1:github.token", "tok", CancellationToken.None);

        var (_, status, error) = await NewPublish().PublishAsync(
            "u1", "chat-badname", "T", "t@t",
            new PublishWorkspaceRequest("nome com espaço!", null, null),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(400));
            Assert.That(error, Does.Contain("inválido"));
        });
    }

    [Test]
    public async Task Publish_RepoInacessivel_400()
    {
        var dir = await _repos.ResolveWorkdirAsync("u1", "chat-down", CancellationToken.None);
        File.WriteAllText(Path.Join(dir, "a.txt"), "x");
        await _config.SetAsync("u:u1:github.token", "tok", CancellationToken.None);
        var github = new GitHubService(new StubHttpFactory(
            new StubHandler(_ => Json(HttpStatusCode.InternalServerError, "{}"))), _config);

        var (_, status, error) = await new WorkspacePublishService(_repos, github)
            .PublishAsync("u1", "chat-down", "T", "t@t",
                new PublishWorkspaceRequest("o/r", null, null), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(400));
            Assert.That(error, Does.Contain("inacessível"));
        });
    }

    [Test]
    public async Task Publish_RepoNovo_CriaViaApi_MiraMain_Push502()
    {
        RequireGit();
        await _config.SetAsync("u:u1:github.token", "tok", CancellationToken.None);
        await _config.SetAsync("u:u1:github.login", "afonso", CancellationToken.None);
        var posts = 0;
        var github = new GitHubService(new StubHttpFactory(new StubHandler(req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                posts++;
                return Json(HttpStatusCode.Created, "{}");
            }
            return Json(HttpStatusCode.NotFound, "{}");
        })), _config);
        var dir = await _repos.ResolveWorkdirAsync("u1", "chat-new", CancellationToken.None);
        File.WriteAllText(Path.Join(dir, "a.txt"), "x");

        var (_, status, _) = await new WorkspacePublishService(_repos, github)
            .PublishAsync("u1", "chat-new", "T", "t@t",
                new PublishWorkspaceRequest("repo-novo", null, null), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(posts, Is.EqualTo(1), "repo inexistente → POST /user/repos");
            Assert.That(status, Is.EqualTo(502), "push https falha sem credencial real");
        });
    }

    [Test]
    public async Task Publish_RepoExistente_MiraChatBranch_Push502()
    {
        RequireGit();
        await _config.SetAsync("u:u1:github.token", "tok", CancellationToken.None);
        var github = new GitHubService(new StubHttpFactory(new StubHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/branches")
                ? Json(HttpStatusCode.OK, "[{\"name\":\"dev\"}]")
                : Json(HttpStatusCode.OK, "{\"default_branch\":\"dev\"}"))), _config);
        var dir = await _repos.ResolveWorkdirAsync("u1", "chat-exist", CancellationToken.None);
        File.WriteAllText(Path.Join(dir, "a.txt"), "x");

        var (_, status, _) = await new WorkspacePublishService(_repos, github)
            .PublishAsync("u1", "chat-exist", "T", "t@t",
                new PublishWorkspaceRequest("o/r", null, null), CancellationToken.None);

        Assert.That(status, Is.EqualTo(502));
    }

    [Test]
    public async Task Publish_SemGitInit_InicializaAntesDeCommitar()
    {
        RequireGit();
        var bare = Path.Join(_root, "semgit.git");
        Run(_root, "init", "--bare", "-q", bare);

        // Pasta criada "por fora" — sem .git — cobre o init defensivo.
        var dir = ScratchPath("chat-nogit");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "a.txt"), "x");

        var (result, status, error) = await NewPublish().PublishAsync(
            "u1", "chat-nogit", "T", "t@t",
            new PublishWorkspaceRequest(null, new Uri(bare).AbsoluteUri, null),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(0), error);
            Assert.That(Directory.Exists(Path.Join(dir, ".git")), Is.True);
            Assert.That(result!.Branch, Is.EqualTo("chat/chat-nogit"));
        });
    }

    // ---------------- RF-006: GitHubService slug/ensure ----------------

    [Test]
    public async Task ResolveSlug_OwnerRepo_PassaDireto()
    {
        var slug = await NewGithub(_ => Json(HttpStatusCode.OK, "{}"))
            .ResolveRepoSlugAsync("u1", "dono/repo", null, CancellationToken.None);
        Assert.That(slug, Is.EqualTo("dono/repo"));
    }

    [Test]
    public async Task ResolveSlug_Invalido_Null()
    {
        var github = NewGithub(_ => Json(HttpStatusCode.OK, "{}"));
        Assert.Multiple(async () =>
        {
            Assert.That(await github.ResolveRepoSlugAsync(
                "u1", "nome com espaço", null, CancellationToken.None), Is.Null);
            Assert.That(await github.ResolveRepoSlugAsync(
                "u1", new string('x', 201), null, CancellationToken.None), Is.Null);
        });
    }

    [Test]
    public async Task ResolveSlug_BareName_UsaLoginSalvo()
    {
        await _config.SetAsync("u:u1:github.login", "afonso", CancellationToken.None);
        var slug = await NewGithub(_ => Json(HttpStatusCode.OK, "{}"))
            .ResolveRepoSlugAsync("u1", "meu-repo", null, CancellationToken.None);
        Assert.That(slug, Is.EqualTo("afonso/meu-repo"));
    }

    [Test]
    public async Task ResolveSlug_BareName_TokenAdHoc_ConsultaUser()
    {
        var slug = await NewGithub(req =>
                req.RequestUri!.AbsolutePath == "/user"
                    ? Json(HttpStatusCode.OK, "{\"login\":\"u-adhoc\"}")
                    : Json(HttpStatusCode.NotFound, "{}"))
            .ResolveRepoSlugAsync("u1", "repo", "token-adhoc", CancellationToken.None);
        Assert.That(slug, Is.EqualTo("u-adhoc/repo"));
    }

    [Test]
    public async Task ResolveSlug_BareName_SemLogin_Null()
    {
        var slug = await NewGithub(_ => Json(HttpStatusCode.OK, "{}"))
            .ResolveRepoSlugAsync("u1", "repo", null, CancellationToken.None);
        Assert.That(slug, Is.Null);
    }

    [Test]
    public async Task EnsureRepo_SemToken_False()
    {
        var (ok, created) = await NewGithub(_ => Json(HttpStatusCode.OK, "{}"))
            .EnsureRepoExistsAsync("u1", "o/r", null, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(created, Is.False);
        });
    }

    [Test]
    public async Task EnsureRepo_Existe_OkSemCriar()
    {
        await _config.SetAsync("u:u1:github.token", "tok", CancellationToken.None);
        var (ok, created) = await NewGithub(_ =>
                Json(HttpStatusCode.OK, "{\"default_branch\":\"main\"}"))
            .EnsureRepoExistsAsync("u1", "o/r", null, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(created, Is.False);
        });
    }

    [Test]
    public async Task EnsureRepo_ErroApi_False()
    {
        await _config.SetAsync("u:u1:github.token", "tok", CancellationToken.None);
        var (ok, _) = await NewGithub(_ =>
                Json(HttpStatusCode.InternalServerError, "{}"))
            .EnsureRepoExistsAsync("u1", "o/r", null, CancellationToken.None);
        Assert.That(ok, Is.False);
    }

    [Test]
    public async Task EnsureRepo_Inexistente_CriaPrivado()
    {
        await _config.SetAsync("u:u1:github.token", "tok", CancellationToken.None);
        string? postedBody = null;
        var (ok, created) = await NewGithub(req =>
            {
                if (req.Method == HttpMethod.Post)
                {
                    postedBody = req.Content!.ReadAsStringAsync().Result;
                    return Json(HttpStatusCode.Created, "{}");
                }
                return Json(HttpStatusCode.NotFound, "{}");
            })
            .EnsureRepoExistsAsync("u1", "afonso/novo-repo", null, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(created, Is.True);
            Assert.That(postedBody, Does.Contain("\"novo-repo\"").And.Contain("\"private\":true"));
        });
    }

    [Test]
    public async Task EnsureRepo_Conflito422_OkSemCriar()
    {
        await _config.SetAsync("u:u1:github.token", "tok", CancellationToken.None);
        var (ok, created) = await NewGithub(req =>
                req.Method == HttpMethod.Post
                    ? Json(HttpStatusCode.UnprocessableEntity, "{}")
                    : Json(HttpStatusCode.NotFound, "{}"))
            .EnsureRepoExistsAsync("u1", "o/r", null, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True, "422 = já existia — concorrência");
            Assert.That(created, Is.False);
        });
    }

    [Test]
    public async Task EnsureRepo_Excecao_False()
    {
        await _config.SetAsync("u:u1:github.token", "tok", CancellationToken.None);
        var (ok, _) = await NewGithub(_ => throw new HttpRequestException("rede"))
            .EnsureRepoExistsAsync("u1", "o/r", null, CancellationToken.None);
        Assert.That(ok, Is.False);
    }

    [Test]
    public async Task SetGlobalToken_Valido_PersisteTokenELogin()
    {
        var github = NewGithub(_ => Json(HttpStatusCode.OK, "{\"login\":\"afonso\"}"));

        var login = await github.SetGlobalTokenAsync("tok-global", CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(login, Is.EqualTo("afonso"));
            Assert.That(await _config.GetAsync<string?>(
                "github.token", null, CancellationToken.None), Is.EqualTo("tok-global"));
            Assert.That(await _config.GetAsync<string?>(
                "github.login", null, CancellationToken.None), Is.EqualTo("afonso"));
        });
    }

    [Test]
    public async Task SetGlobalToken_Invalido_NaoPersiste()
    {
        var github = NewGithub(_ => Json(HttpStatusCode.Unauthorized, "{}"));

        var login = await github.SetGlobalTokenAsync("tok-ruim", CancellationToken.None);

        Assert.Multiple(async () =>
        {
            Assert.That(login, Is.Null);
            Assert.That(await _config.GetAsync<string?>(
                "github.token", null, CancellationToken.None), Is.Null);
        });
    }

    [Test]
    public void ArgPattern_JsonNuloOuInvalido_DevolvePadraoEstavel()
    {
        // TryGetString defensivo: json nulo → null; inválido → catch.
        // Em todos os casos o padrão default da tool é devolvido.
        Assert.Multiple(() =>
        {
            Assert.That(PermissionRuleset.ArgPattern("shell_exec", null),
                Is.EqualTo("exec:*"));
            Assert.That(PermissionRuleset.ArgPattern("shell_exec", "{invalido"),
                Is.EqualTo("exec:*"));
            Assert.That(PermissionRuleset.ArgPattern("file_write", "\"so-string\""),
                Is.EqualTo("write:./**"));
        });
    }

    [Test]
    public async Task RunGit_DirInexistente_ErroControlado()
    {
        // Workdir que não existe → Win32Exception → (-1, "", msg), nunca throw.
        var (code, _, err) = await WorkspaceRepoService.RunAsync(
            "/definitivamente/nao/existe", CancellationToken.None, null, "status");
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.EqualTo(-1));
            Assert.That(err, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public async Task SetCommands_SemBinding_NaoFazemNada()
    {
        // Guards: sem binding global as setters retornam cedo.
        await _repos.SetTestCommandAsync("u1", "make test", CancellationToken.None);
        await _repos.SetTestCommandAsync("u1", "chat-x", "make test", CancellationToken.None);
        await _repos.SetFormatCommandAsync("u1", "dotnet format", CancellationToken.None);
        // Nenhuma exceção e nenhum binding criado de propósito.
        var (binding, _) = await _repos.ResolveBindingAsync("u1", "chat-x", CancellationToken.None);
        Assert.That(binding, Is.Null);
    }

    [Test]
    public async Task ListPulls_ChecksSemArrays_GuardasDefensivas()
    {
        // Resposta de checks sem "statuses"/"check_runs" → guardas saem
        // cedo e o rollup vira pending (nunca exceção).
        await _config.SetAsync("u:u1:github.token", "tok", CancellationToken.None);
        var github = NewGithub(req =>
            req.RequestUri!.AbsolutePath switch
            {
                "/repos/o/r/pulls" => Json(HttpStatusCode.OK, """
                    [{"number":7,"title":"feat","draft":false,
                      "user":{"login":"afonso"},"head":{"ref":"dev","sha":"abc1"},
                      "updated_at":"2026-10-09T10:00:00Z","html_url":"https://x/pr/7"}]
                    """),
                _ => Json(HttpStatusCode.OK, "{}"),
            });

        var pulls = await github.ListPullRequestsAsync("u1", "o", "r", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pulls, Is.Not.Null);
            Assert.That(pulls!.Pulls, Has.Count.EqualTo(1));
            Assert.That(pulls.Pulls[0].Checks.State, Is.EqualTo("pending"));
        });
    }

    private GitHubService NewGithub(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new StubHttpFactory(new StubHandler(respond)), _config);

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    private WorkspacePublishService NewPublish() =>
        new(_repos, new GitHubService(new StubHttpFactory(), _config));

    private static void RequireGit()
    {
        try { Run(Path.GetTempPath(), "--version"); }
        catch (Exception) { Assert.Ignore("git indisponível no ambiente"); }
    }

    private sealed class StubHttpFactory(HttpMessageHandler? handler = null)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            handler is null ? new() : new HttpClient(handler);
    }

    /// <summary>Handler roteável por request — usado p/ simular a API do GitHub.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static void Run(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = cwd,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        if (!Directory.Exists(cwd))
        {
            Directory.CreateDirectory(cwd);
            psi.WorkingDirectory = cwd;
        }
        using var p = Process.Start(psi)!;
        p.WaitForExit(30_000);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} falhou: {p.StandardError.ReadToEnd()}");
        }
    }

    private sealed class StubEnv(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
