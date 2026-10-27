using System.Diagnostics;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using OpenWebUI.Application.Contracts;
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

    private WorkspacePublishService NewPublish() =>
        new(_repos, new GitHubService(new StubHttpFactory(), _config));

    private static void RequireGit()
    {
        try { Run(Path.GetTempPath(), "--version"); }
        catch (Exception) { Assert.Ignore("git indisponível no ambiente"); }
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
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
