using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura da SPEC-20261009-checkpoints-revert (E16 S6):
/// <see cref="CheckpointService"/> em workdir git real (round-trip
/// snapshot→edit→revert, conflito de drift, prune, lock, invariante
/// "git do usuário intocado") + fallback de manifesto em workdir sem
/// .git + as rotas <c>/api/v1/workspace/repo/checkpoints*</c> (404 sem
/// repo vinculado, 409 com run ativa, round-trip via endpoint).
/// </summary>
[TestFixture, IsolateEnvironment]
public class CheckpointServiceTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _contentRoot = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-cp-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _contentRoot = _factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;

        var admin = await SignUpAsync("Admin", "admin@cp.local");
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    // ---------------- Backend git: snapshot→edit→revert ----------------

    [Test]
    public async Task Git_SnapshotEditRevert_RestauraEstado()
    {
        RequireGit();
        var dir = NewWorkdir();
        try
        {
            Git(dir, "init");
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            CommitAll(dir, "base");

            var svc = Service();
            var c0 = await svc.SnapshotAsync(dir, "r1", 0, CancellationToken.None);
            Assert.That(c0, Is.Not.Null);

            // "run" edita: altera a.txt e cria b.txt — vira checkpoint do turno 1.
            File.WriteAllText(Path.Join(dir, "a.txt"), "v2\n");
            File.WriteAllText(Path.Join(dir, "b.txt"), "novo\n");
            var c1 = await svc.SnapshotAsync(dir, "r1", 1, CancellationToken.None);
            Assert.That(c1, Is.Not.Null);
            Assert.That(c1!.Files, Does.Contain("a.txt").And.Contain("b.txt"));

            // Revert do pré-run: volta a.txt e remove b.txt — só o que o
            // snapshot cobre (patch do opencode).
            var result = await svc.RevertAsync(dir, c0!.Hash, force: false, CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(result.Reverted, Does.Contain("a.txt").And.Contain("b.txt"));
                Assert.That(result.Conflicts, Is.Empty);
                Assert.That(File.ReadAllText(Path.Join(dir, "a.txt")), Is.EqualTo("v1\n"));
                Assert.That(File.Exists(Path.Join(dir, "b.txt")), Is.False);
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Git_Revert_DriftViraConflito_ForceSobrescreve()
    {
        RequireGit();
        var dir = NewWorkdir();
        try
        {
            Git(dir, "init");
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            CommitAll(dir, "base");

            var svc = Service();
            var c0 = await svc.SnapshotAsync(dir, "r1", 0, CancellationToken.None);
            File.WriteAllText(Path.Join(dir, "a.txt"), "v2\n");
            await svc.SnapshotAsync(dir, "r1", 1, CancellationToken.None);

            // Drift fora da trilha: arquivo mudou DEPOIS do último
            // checkpoint — current ≠ tip → conflito, não sobrescreve.
            File.WriteAllText(Path.Join(dir, "a.txt"), "v3-drift\n");
            var result = await svc.RevertAsync(dir, c0!.Hash, force: false, CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(result.Conflicts, Does.Contain("a.txt"));
                Assert.That(result.Reverted, Does.Not.Contain("a.txt"));
                Assert.That(File.ReadAllText(Path.Join(dir, "a.txt")),
                    Is.EqualTo("v3-drift\n"));
            });

            var forced = await svc.RevertAsync(dir, c0.Hash, force: true, CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(forced.Reverted, Does.Contain("a.txt"));
                Assert.That(File.ReadAllText(Path.Join(dir, "a.txt")), Is.EqualTo("v1\n"));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Git_LogEStatusDoUsuario_FicamIntocados()
    {
        RequireGit();
        var dir = NewWorkdir();
        try
        {
            Git(dir, "init");
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            CommitAll(dir, "base");
            var logAntes = GitOut(dir, "log", "--format=%s");

            var svc = Service();
            var c0 = await svc.SnapshotAsync(dir, "r1", 0, CancellationToken.None);
            Assert.That(c0, Is.Not.Null);
            File.WriteAllText(Path.Join(dir, "a.txt"), "v2\n");
            File.WriteAllText(Path.Join(dir, "b.txt"), "novo\n");
            var c1 = await svc.SnapshotAsync(dir, "r1", 1, CancellationToken.None);
            Assert.That(c1, Is.Not.Null);

            // Invariante RF-005: log/refs/status/stash do repo do usuário
            // não veem nada dos checkpoints (eles vivem num git dir
            // separado) — o status mostra só as edições "da run".
            Assert.That(GitOut(dir, "log", "--format=%s"), Is.EqualTo(logAntes));
            Assert.That(GitOut(dir, "for-each-ref"), Does.Not.Contain("openwebui"));
            Assert.That(GitOut(dir, "status", "--porcelain"),
                Is.EqualTo(" M a.txt\n?? b.txt\n"));
            Assert.That(GitOut(dir, "stash", "list"), Is.Empty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Git_Patch_ListaArquivosDivergentes()
    {
        RequireGit();
        var dir = NewWorkdir();
        try
        {
            Git(dir, "init");
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            CommitAll(dir, "base");

            var svc = Service();
            var c0 = await svc.SnapshotAsync(dir, "r1", 0, CancellationToken.None);
            File.WriteAllText(Path.Join(dir, "a.txt"), "v2\n");
            File.WriteAllText(Path.Join(dir, "b.txt"), "novo\n");

            var patch = await svc.PatchAsync(dir, c0!.Hash, CancellationToken.None);
            Assert.That(patch, Does.Contain("a.txt").And.Contain("b.txt"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Git_Lock_SerializaSnapshotsConcorrentes()
    {
        RequireGit();
        var dir = NewWorkdir();
        try
        {
            Git(dir, "init");
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            CommitAll(dir, "base");

            var svc = Service();
            // Determinístico: segurando o SemaphoreSlim do workdir, um
            // snapshot concorrente tem que esperar — e completar ao liberar.
            var gate = svc.LockFor(dir);
            await gate.WaitAsync();
            var pending = svc.SnapshotAsync(dir, "r1", 0, CancellationToken.None);
            Assert.That(pending.Wait(TimeSpan.FromMilliseconds(400)), Is.False,
                "snapshot deveria esperar o lock do workdir");
            gate.Release();
            var info = await pending;
            Assert.That(info, Is.Not.Null);

            // Segunda entrada independente no mesmo workdir usa o mesmo gate.
            Assert.That(svc.LockFor(dir), Is.SameAs(gate));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Git_Prune_RemoveCheckpointsAntigos()
    {
        RequireGit();
        var dir = NewWorkdir();
        try
        {
            Git(dir, "init");
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            CommitAll(dir, "base");

            var root = NewWorkdir();
            var svc = Service(new Dictionary<string, string?>
            {
                ["Checkpoints:MaxAgeDays"] = "0",
            }, root: root);
            var c0 = await svc.SnapshotAsync(dir, "r1", 0, CancellationToken.None);
            File.WriteAllText(Path.Join(dir, "a.txt"), "v2\n");
            var c1 = await svc.SnapshotAsync(dir, "r1", 1, CancellationToken.None);

            // Objeto órfão real no git dir do snapshot (inexistente em
            // qualquer ref) — é isso que `gc --prune` remove. Commits da
            // cadeia (c0 é pai de c1) permanecem alcançáveis e não são podados.
            var gd = GitDirOf(root, dir);
            var lixo = Path.Join(dir, "lixo.tmp");
            File.WriteAllText(lixo, "junk\n");
            var orphan = GitOut(dir, "--git-dir", gd, "--work-tree", dir,
                "hash-object", "-w", "--", "lixo.tmp").Trim();
            Assert.That(GitExitCode(dir, "--git-dir", gd, "cat-file", "-t", orphan),
                Is.EqualTo(0));

            await svc.PruneAsync(dir, CancellationToken.None);

            // TTL=0 → gc --prune=now: órfão some; commits do checkpoint (c0
            // pai de c1, c1 na ref) continuam acessíveis.
            Assert.That(GitExitCode(dir, "--git-dir", gd, "cat-file", "-t", orphan),
                Is.Not.EqualTo(0), "objeto órfão deveria ser podado");
            Assert.That(GitExitCode(dir, "--git-dir", gd, "cat-file", "-t", c0!.Hash),
                Is.EqualTo(0), "checkpoint pai (alcançável) permanece");
            Assert.That(GitExitCode(dir, "--git-dir", gd, "cat-file", "-t", c1!.Hash),
                Is.EqualTo(0), "checkpoint tip permanece");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------- Fallback sem git (manifesto) ----------------

    [Test]
    public async Task Fallback_SnapshotEditRevert_RestauraEstado()
    {
        var dir = NewWorkdir(); // sem .git → backend de manifesto
        var cpRoot = NewWorkdir();
        try
        {
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            var svc = Service(root: cpRoot);

            var c0 = await svc.SnapshotAsync(dir, "r1", 0, CancellationToken.None);
            Assert.That(c0, Is.Not.Null, "manifesto deveria produzir checkpoint");

            File.WriteAllText(Path.Join(dir, "a.txt"), "v2\n");
            File.WriteAllText(Path.Join(dir, "b.txt"), "novo\n");
            var c1 = await svc.SnapshotAsync(dir, "r1", 1, CancellationToken.None);
            Assert.That(c1, Is.Not.Null);

            var result = await svc.RevertAsync(dir, c0!.Hash, force: false, CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(result.Reverted, Does.Contain("a.txt").And.Contain("b.txt"));
                Assert.That(File.ReadAllText(Path.Join(dir, "a.txt")), Is.EqualTo("v1\n"));
                Assert.That(File.Exists(Path.Join(dir, "b.txt")), Is.False);
            });
            // Artefatos ficam fora do workdir (data/checkpoints/files/...).
            Assert.That(
                Directory.EnumerateDirectories(
                    Path.Join(cpRoot, "data", "checkpoints", "files")).Any(), Is.True);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(cpRoot, recursive: true);
        }
    }

    // ---------------- Endpoints ----------------

    [Test]
    public async Task Checkpoints_SemRepoVinculado_404()
    {
        var auth = await SignUpAsync("CPNB", "cpnb@cp.local");
        UseToken(auth.Token);
        var response = await _client.GetAsync("/api/v1/workspace/repo/checkpoints");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Revert_ComRunAtiva_409()
    {
        RequireGit();
        var auth = await SignUpAsync("CPAC", "cpac@cp.local");
        UseToken(auth.Token);
        var workdir = await BindRepoAsync(auth.User.Id);

        // Run ativa (queued) do usuário → revert proibido: os dois lados
        // escreveriam no mesmo workdir (decisão da SPEC).
        await SeedRunAsync(auth.User.Id, ChatRunStatus.Queued);
        var snapshot = await FactoryCheckpoints().SnapshotAsync(
            workdir, "r1", 0, CancellationToken.None);
        Assert.That(snapshot, Is.Not.Null);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workspace/repo/checkpoints/{snapshot!.Hash}/revert",
            new WorkspaceCheckpointRevertRequest(false));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task Revert_ViaEndpoint_RoundTrip()
    {
        RequireGit();
        var auth = await SignUpAsync("CPRT", "cprt@cp.local");
        UseToken(auth.Token);
        var workdir = await BindRepoAsync(auth.User.Id);

        var c0 = await FactoryCheckpoints().SnapshotAsync(
            workdir, "r1", 0, CancellationToken.None);
        Assert.That(c0, Is.Not.Null);
        File.WriteAllText(Path.Join(workdir, "readme.md"), "mudou\n");
        var c1 = await FactoryCheckpoints().SnapshotAsync(
            workdir, "r1", 1, CancellationToken.None);
        Assert.That(c1, Is.Not.Null);

        var list = await _client.GetFromJsonAsync<List<WorkspaceCheckpointItem>>(
            "/api/v1/workspace/repo/checkpoints");
        Assert.That(list, Is.Not.Null.And.Not.Empty);
        Assert.That(list!.Select(c => c.Hash), Does.Contain(c0!.Hash));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workspace/repo/checkpoints/{c0.Hash}/revert",
            new WorkspaceCheckpointRevertRequest(false));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        var result = await response.Content
            .ReadFromJsonAsync<WorkspaceCheckpointRevertResponse>();
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Reverted, Does.Contain("readme.md"));
        Assert.That(File.ReadAllText(Path.Join(workdir, "readme.md")), Is.EqualTo("oi\n"));
    }

    [Test]
    public async Task Detail_ViaEndpoint_DevolveArquivosEDiff()
    {
        RequireGit();
        var auth = await SignUpAsync("CPDT", "cpdt@cp.local");
        UseToken(auth.Token);
        var workdir = await BindRepoAsync(auth.User.Id);

        var c0 = await FactoryCheckpoints().SnapshotAsync(
            workdir, "r1", 0, CancellationToken.None);
        Assert.That(c0, Is.Not.Null);
        File.WriteAllText(Path.Join(workdir, "readme.md"), "mudou\n");

        var detail = await _client.GetFromJsonAsync<WorkspaceCheckpointDetailResponse>(
            $"/api/v1/workspace/repo/checkpoints/{c0.Hash}");
        Assert.That(detail, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(detail!.Hash, Is.EqualTo(c0.Hash));
            Assert.That(detail.Files, Does.Contain("readme.md"));
            Assert.That(detail.Diff, Is.Not.Null.And.Contains("mudou"));
        });
    }

    [Test]
    public async Task Detail_HashInexistente_404()
    {
        RequireGit();
        var auth = await SignUpAsync("CPD4", "cpd4@cp.local");
        UseToken(auth.Token);
        await BindRepoAsync(auth.User.Id);

        var response = await _client.GetAsync(
            "/api/v1/workspace/repo/checkpoints/0000000000000000000000000000000000000000");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---------------- Caminhos nulos do serviço ----------------

    [Test]
    public async Task Snapshot_DesabilitadoOuWorkdirInexistente_DevolveNull()
    {
        var off = Service(new Dictionary<string, string?>
        {
            ["Checkpoints:Enabled"] = "false",
        });
        var dir = NewWorkdir();
        try
        {
            Assert.That(await off.SnapshotAsync(
                dir, "r1", 0, CancellationToken.None), Is.Null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        var svc = Service();
        var ghost = Path.Join(Path.GetTempPath(), "nao-existe-" + Guid.NewGuid().ToString("N"));
        Assert.That(await svc.SnapshotAsync(
            ghost, "r1", 0, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Diff_WorkdirSemGit_DevolveNull()
    {
        var dir = NewWorkdir(); // sem .git → fallback, sem diff unificado
        try
        {
            var svc = Service();
            Assert.That(await svc.DiffAsync(
                dir, "qualquer-hash", CancellationToken.None), Is.Null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------- helpers ----------------

    private CheckpointService FactoryCheckpoints() =>
        _factory.Services.GetRequiredService<CheckpointService>();

    /// <summary>Vincula um repo local de teste ao usuário (kv + checkout real).</summary>
    private async Task<string> BindRepoAsync(string userId)
    {
        var workdir = Path.Join(
            _contentRoot, "data", "workspaces", userId, "repos", "afonsoft__cp");
        Directory.CreateDirectory(workdir);
        Git(workdir, "init");
        File.WriteAllText(Path.Join(workdir, "readme.md"), "oi\n");
        CommitAll(workdir, "base");

        await using var scope = _factory.Services.CreateAsyncScope();
        var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
        await config.SetAsync($"u:{userId}:workspace.repo",
            new WorkspaceRepoBinding("afonsoft/cp", "main", "repos/afonsoft__cp"),
            CancellationToken.None);
        return workdir;
    }

    private async Task SeedRunAsync(string userId, string status)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var chat = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Chat cp", ["llama3"], []));
        Assert.That(chat.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var chatId = (await chat.Content.ReadFromJsonAsync<ChatResponse>())!.Id;
        db.ChatRuns.Add(new ChatRun
        {
            ChatId = chatId,
            UserId = userId,
            Model = "llama3",
            Status = status,
            RequestJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Serviço isolado de teste: raiz temporária + config opcional.</summary>
    private CheckpointService Service(
        Dictionary<string, string?>? config = null, string? root = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config ?? new Dictionary<string, string?>())
            .Build();
        return new CheckpointService(
            new TestEnv(root ?? NewWorkdir()), configuration,
            NullLogger<CheckpointService>.Instance);
    }

    /// <summary>git dir do snapshot para um workdir (data/checkpoints/repo/{hash}).</summary>
    private static string GitDirOf(string root, string workdir)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(workdir)));
        var key = Convert.ToHexString(bytes)[..16].ToLowerInvariant();
        return Path.Join(root, "data", "checkpoints", "repo", key);
    }

    private static string NewWorkdir()
    {
        var dir = Path.Join(Path.GetTempPath(), $"owui-cp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CommitAll(string dir, string message)
    {
        Git(dir, "-c", "user.email=t@t", "-c", "user.name=t", "add", ".");
        Git(dir, "-c", "user.email=t@t", "-c", "user.name=t",
            "commit", "-m", message);
    }

    private static void RequireGit()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            p!.WaitForExit(5000);
            if (p.ExitCode != 0)
            {
                Assert.Ignore("git indisponível neste ambiente.");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Ignore("git indisponível neste ambiente.");
        }
        catch (InvalidOperationException)
        {
            Assert.Ignore("git indisponível neste ambiente.");
        }
    }

    private static void Git(string workdir, params string[] args)
    {
        var exit = GitExitCode(workdir, args);
        Assert.That(exit, Is.EqualTo(0), $"git {string.Join(' ', args)} falhou");
    }

    private static string GitOut(string workdir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var process = Process.Start(psi);
        var stdout = process!.StandardOutput.ReadToEnd();
        process.WaitForExit(15000);
        return stdout;
    }

    private static int GitExitCode(string workdir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var process = Process.Start(psi);
        process!.WaitForExit(15000);
        return process.ExitCode;
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, "senha123"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    /// <summary>IHostEnvironment mínimo apontando para uma raiz temporária.</summary>
    private sealed class TestEnv(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new PhysicalFileProvider(contentRoot);
    }


    [Test]
    public async Task Fallback_SegundoSnapshot_ReusaBlobDedup()
    {
        var dir = NewWorkdir();
        var cpRoot = NewWorkdir();
        try
        {
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            var svc = Service(root: cpRoot);

            var c0 = await svc.SnapshotAsync(dir, "r1", 0, CancellationToken.None);
            var filesDir = Path.Join(cpRoot, "data", "checkpoints", "files");
            var blobsAntes = Directory.Exists(filesDir)
                ? Directory.EnumerateFiles(filesDir, "a.txt", SearchOption.AllDirectories).Count()
                : 0;
            Assert.That(blobsAntes, Is.EqualTo(1));

            // Mesmo conteúdo: entry reaproveita o Stored do manifesto anterior.
            var c1 = await svc.SnapshotAsync(dir, "r1", 1, CancellationToken.None);
            Assert.That(c1, Is.Not.Null);
            var blobsDepois = Directory.EnumerateFiles(
                filesDir, "a.txt", SearchOption.AllDirectories).Count();
            Assert.That(blobsDepois, Is.EqualTo(1), "blob idêntico não recopiado");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(cpRoot, recursive: true);
        }
    }

    [Test]
    public async Task Fallback_Revert_BlobAusente_ViraConflito()
    {
        var dir = NewWorkdir();
        var cpRoot = NewWorkdir();
        try
        {
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            var svc = Service(root: cpRoot);
            var c0 = await svc.SnapshotAsync(dir, "r1", 0, CancellationToken.None);
            Assert.That(c0, Is.Not.Null);

            // Apaga só o blob armazenado — restore marca conflito em vez de crash.
            var filesDir = Path.Join(cpRoot, "data", "checkpoints", "files");
            File.Delete(Directory.EnumerateFiles(
                filesDir, "a.txt", SearchOption.AllDirectories).Single());
            // Drift força a necessidade de restaurar o blob ausente.
            File.WriteAllText(Path.Join(dir, "a.txt"), "v2-drift\n");
            var result = await svc.RevertAsync(dir, c0!.Hash, force: false, CancellationToken.None);
            Assert.That(result.Conflicts, Does.Contain("a.txt"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(cpRoot, recursive: true);
        }
    }

    [Test]
    public async Task Fallback_WalkFiles_PulaSkipDirsSymlinkESemPermissao()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("trancar dir via UnixFileMode não existe em Windows");
            return;
        }
        var dir = NewWorkdir();
        var cpRoot = NewWorkdir();
        var locked = Path.Join(dir, "locked");
        try
        {
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            Directory.CreateDirectory(Path.Join(dir, "node_modules", "pkg"));
            File.WriteAllText(Path.Join(dir, "node_modules", "pkg", "x.js"), "x");
            var alvo = Path.Join(dir, "alvo");
            Directory.CreateDirectory(alvo);
            File.WriteAllText(Path.Join(alvo, "dentro.txt"), "y");
            try
            {
                Directory.CreateSymbolicLink(Path.Join(dir, "link"), alvo);
            }
            catch (IOException) { /* FS sem symlink */ }
            Directory.CreateDirectory(locked);
            File.WriteAllText(Path.Join(locked, "s.txt"), "z");
            File.SetUnixFileMode(locked, UnixFileMode.None);

            var svc = Service(root: cpRoot);
            var c0 = await svc.SnapshotAsync(dir, "r1", 0, CancellationToken.None);
            Assert.That(c0, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(c0!.Files, Does.Contain("a.txt"));
                Assert.That(c0.Files, Does.Not.Contain("node_modules/pkg/x.js"));
                Assert.That(c0.Files, Does.Contain("alvo/dentro.txt"),
                    "o dir alvo é real e é andado — só o link é pulado");
                Assert.That(c0.Files, Does.Not.Contain("link/dentro.txt"));
                Assert.That(c0.Files, Does.Not.Contain("locked/s.txt"));
            });
        }
        finally
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(locked, UnixFileMode.UserRead
                        | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            catch (IOException) { }
            Directory.Delete(dir, recursive: true);
            Directory.Delete(cpRoot, recursive: true);
        }
    }

    [Test]
    public async Task Prune_WorkdirSemGit_FallbackNaoFalha()
    {
        var dir = NewWorkdir();
        var cpRoot = NewWorkdir();
        try
        {
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            var svc = Service(root: cpRoot);
            await svc.PruneAsync(dir, CancellationToken.None);
            await svc.PruneAsync(dir, CancellationToken.None); // gate do intervalo
            Assert.That(File.Exists(Path.Join(dir, "a.txt")), Is.True);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(cpRoot, recursive: true);
        }
    }
}
