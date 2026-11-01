using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// RF-003/RF-004 (SPEC-20261009-ide-mentions-tests): matriz manifesto→comando,
/// parser de saída dos runners, 422 sem manifesto, gate de aprovação
/// (WorkspaceWrite → comando visível antes de rodar) e ciclo de vida do
/// job via <c>/api/v1/workspace/repo/test-run</c>.
/// </summary>
[TestFixture, IsolateEnvironment]
public class WorkspaceTestRunTests
{
    private string _apiDir = null!;

    [SetUp]
    public void SetUp() => _apiDir = FindApiDir();

    // ---------- RF-003: matriz manifesto → comando ----------

    [Test]
    public void Detect_Slnx_ProdutoDotnetTest()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Join(dir, "App.slnx"), "<Solution/>");
        Assert.That(TestCommandDetector.Detect(dir, null), Is.EqualTo("dotnet test"));
    }

    [Test]
    public void Detect_SlnOuCsproj_ProdutoDotnetTest()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Join(dir, "App.sln"), "x");
        Assert.That(TestCommandDetector.Detect(dir, null), Is.EqualTo("dotnet test"));

        var dir2 = TempDir();
        File.WriteAllText(Path.Join(dir2, "Lib.csproj"), "<Project/>");
        Assert.That(TestCommandDetector.Detect(dir2, null), Is.EqualTo("dotnet test"));
    }

    [Test]
    public void Detect_PackageJson_NpmTest_BunLockVence()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Join(dir, "package.json"), "{}");
        Assert.That(TestCommandDetector.Detect(dir, null), Is.EqualTo("npm test"));

        File.WriteAllText(Path.Join(dir, "bun.lock"), "x");
        Assert.That(TestCommandDetector.Detect(dir, null), Is.EqualTo("bun test"));
    }

    [Test]
    public void Detect_Pyproject_Pytest_GoMod_GoTest()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Join(dir, "pyproject.toml"), "[project]");
        Assert.That(TestCommandDetector.Detect(dir, null), Is.EqualTo("pytest"));

        var dir2 = TempDir();
        File.WriteAllText(Path.Join(dir2, "go.mod"), "module x");
        Assert.That(TestCommandDetector.Detect(dir2, null), Is.EqualTo("go test ./..."));
    }

    [Test]
    public void Detect_SemManifesto_Null_OverrideVence()
    {
        var dir = TempDir();
        Assert.That(TestCommandDetector.Detect(dir, null), Is.Null);
        Assert.That(TestCommandDetector.Detect(dir, "make test"), Is.EqualTo("make test"));
    }

    // ---------- RF-003: parser de saída dos runners ----------

    [Test]
    public void Parse_DotnetSummary_ExtraiContagens()
    {
        var output = "test run\nPassed!  - Failed: 0, Passed: 42, Skipped: 3\n";
        var s = TestRunOutputParser.Parse("dotnet test", output);
        Assert.Multiple(() =>
        {
            Assert.That(s!.Passed, Is.EqualTo(42));
            Assert.That(s.Failed, Is.EqualTo(0));
            Assert.That(s.Skipped, Is.EqualTo(3));
        });
    }

    [Test]
    public void Parse_DotnetMultiProjeto_SomaContagens()
    {
        var output = "Passed!  - Failed: 0, Passed: 40, Skipped: 0\n"
                   + "Failed!  - Failed: 2, Passed: 10, Skipped: 1\n";
        var s = TestRunOutputParser.Parse("dotnet test", output);
        Assert.Multiple(() =>
        {
            Assert.That(s!.Passed, Is.EqualTo(50));
            Assert.That(s.Failed, Is.EqualTo(2));
            Assert.That(s.Skipped, Is.EqualTo(1));
        });
    }

    [Test]
    public void Parse_DotnetTotalTests_Com_Sem_Skipped()
    {
        var s = TestRunOutputParser.Parse("dotnet test",
            "Total tests: 55\nPassed: 50\nFailed: 3\nSkipped: 2\n");
        Assert.Multiple(() =>
        {
            Assert.That(s!.Passed, Is.EqualTo(50));
            Assert.That(s.Failed, Is.EqualTo(3));
            Assert.That(s.Skipped, Is.EqualTo(2));
        });

        var s2 = TestRunOutputParser.Parse("dotnet test",
            "Total tests: 10 Passed: 9 Failed: 1");
        Assert.That(s2!.Skipped, Is.Null);
    }

    [Test]
    public void Parse_JestSummary_ExtraiContagens()
    {
        var output = "Tests:       1 failed, 9 passed, 10 total\nTime: 2.1s\n";
        var s = TestRunOutputParser.Parse("npm test", output);
        Assert.Multiple(() =>
        {
            Assert.That(s!.Passed, Is.EqualTo(9));
            Assert.That(s.Failed, Is.EqualTo(1));
        });
    }

    [Test]
    public void Parse_PytestSummary_ExtraiContagens()
    {
        var output = "=== short test summary info ===\n==== 12 passed, 3 failed, 1 skipped in 4.56s ====\n";
        var s = TestRunOutputParser.Parse("pytest", output);
        Assert.Multiple(() =>
        {
            Assert.That(s!.Passed, Is.EqualTo(12));
            Assert.That(s.Failed, Is.EqualTo(3));
            Assert.That(s.Skipped, Is.EqualTo(1));
        });
    }

    [Test]
    public void Parse_GoTally_ContaResultados()
    {
        var output = "--- PASS: TestA (0.01s)\n--- FAIL: TestB (0.02s)\n--- SKIP: TestC (0.00s)\nFAIL\tpkg/x\t0.05s\n";
        var s = TestRunOutputParser.Parse("go test ./...", output);
        Assert.Multiple(() =>
        {
            Assert.That(s!.Passed, Is.EqualTo(1));
            Assert.That(s.Failed, Is.EqualTo(1));
            Assert.That(s.Skipped, Is.EqualTo(1));
        });
    }

    [Test]
    public void Parse_BunSummary_ExtraiContagens()
    {
        var output = " 42 pass\n 1 fail\n 2 skip\nRan 45 tests across 1 files.\n";
        var s = TestRunOutputParser.Parse("bun test", output);
        Assert.Multiple(() =>
        {
            Assert.That(s!.Passed, Is.EqualTo(42));
            Assert.That(s.Failed, Is.EqualTo(1));
            Assert.That(s.Skipped, Is.EqualTo(2));
        });
    }

    [Test]
    public void Parse_SaidaIrreconhecivel_NullNuncaInventa()
    {
        Assert.That(TestRunOutputParser.Parse("make test", "hello world\n"), Is.Null);
        Assert.That(TestRunOutputParser.Parse("dotnet test", ""), Is.Null);
    }

    // ---------- RF-003/RF-004: endpoints ----------

    [Test]
    public async Task TestRun_SemAuth_401()
    {
        using var dbScope = TestInfra.UseDb();
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var post = await client.PostAsJsonAsync("/api/v1/workspace/repo/test-run",
            new TestRunStartRequest(false));
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task TestRun_SemBinding_404()
    {
        using var ctx = await NewAppAsync(bound: false);
        var post = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/test-run",
            new TestRunStartRequest(false));
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task TestRun_SemManifesto_422ComSugestao()
    {
        using var ctx = await NewAppAsync(bound: true);
        var post = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/test-run",
            new TestRunStartRequest(false));
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
        var body = await post.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("detail").GetString(), Does.Contain("manifesto"));
            Assert.That(body.GetProperty("suggested").GetString(), Does.Contain("TestCommand"));
        });
    }

    [Test]
    public async Task TestRun_PackageJson_ExigeAprovacaoComComandoVisivel()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, "package.json"), "{}");

        var post = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/test-run",
            new TestRunStartRequest(false));
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await post.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("requiresApproval").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("command").GetString(), Is.EqualTo("npm test"));
            Assert.That(body.GetProperty("jobId").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    [Test]
    public async Task TestRun_TestCommandEcho_CicloCompleto()
    {
        // "echo" é WorkspaceWrite → primeiro pedido exige aprovação;
        // confirmado, o job roda e a saída aparece no GET.
        using var ctx = await NewAppAsync(bound: true, testCommand: "echo ola-teste");

        var gated = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/test-run",
            new TestRunStartRequest(false));
        var gatedBody = await gated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(gatedBody.GetProperty("requiresApproval").GetBoolean(), Is.True);
        Assert.That(gatedBody.GetProperty("command").GetString(), Is.EqualTo("echo ola-teste"));

        var start = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/test-run",
            new TestRunStartRequest(true));
        var startBody = await start.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = startBody.GetProperty("jobId").GetString();
        Assert.That(jobId, Is.Not.Null.And.Not.Empty);

        var status = await PollTestRunAsync(ctx.Client, jobId!);
        Assert.Multiple(() =>
        {
            Assert.That(status.GetProperty("state").GetString(), Is.EqualTo("completed"));
            Assert.That(status.GetProperty("tail").GetString(), Does.Contain("ola-teste"));
            Assert.That(status.GetProperty("command").GetString(), Is.EqualTo("echo ola-teste"));
        });
    }

    [Test]
    public async Task TestRun_Csproj_DotnetTestSeguro_SemAprovacao()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, "Lib.csproj"), "<Project/>");

        var post = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/test-run",
            new TestRunStartRequest(false));
        var body = await post.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("requiresApproval").GetBoolean(), Is.False);
            Assert.That(body.GetProperty("command").GetString(), Is.EqualTo("dotnet test"));
            Assert.That(body.GetProperty("jobId").GetString(), Is.Not.Null.And.Not.Empty);
        });
        // Job pode falhar (projeto inválido) — o ponto é que iniciou sem gate.
    }

    [Test]
    public async Task TestCommand_PutDefineOverride_VoltaNaDeteccao()
    {
        using var ctx = await NewAppAsync(bound: true);
        var put = await ctx.Client.PutAsJsonAsync("/api/v1/workspace/repo/test-command",
            new TestCommandRequest("echo custom"));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var post = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/test-run",
            new TestRunStartRequest(false));
        var body = await post.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("command").GetString(), Is.EqualTo("echo custom"));
    }

    [Test]
    public async Task TestRun_JobInexistente_404()
    {
        using var ctx = await NewAppAsync(bound: true);
        var get = await ctx.Client.GetAsync("/api/v1/workspace/repo/test-run/nope123");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---------- fixture ----------

    private static async Task<JsonElement> PollTestRunAsync(HttpClient client, string jobId)
    {
        for (var i = 0; i < 40; i++)
        {
            var get = await client.GetAsync($"/api/v1/workspace/repo/test-run/{jobId}");
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = await get.Content.ReadFromJsonAsync<JsonElement>();
            if (body.GetProperty("state").GetString() != "running")
            {
                return body;
            }
            await Task.Delay(250);
        }
        Assert.Fail("test run não concluiu em 10s");
        return default;
    }

    private sealed class Ctx : IDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;

        public Ctx(WebApplicationFactory<Program> factory, HttpClient client,
            string? workdir, string userId)
        {
            _factory = factory;
            Client = client;
            Workdir = workdir;
            UserId = userId;
        }

        public HttpClient Client { get; }
        public string? Workdir { get; }
        public string UserId { get; }

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
            CleanupBoundWorkspace(UserId);
        }
    }

    /// <summary>App real + usuário; quando <paramref name="bound"/>, semeia binding + workdir.</summary>
    private async Task<Ctx> NewAppAsync(bool bound, string? testCommand = null)
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"openwebui-tr-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={dbPath}");
        var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var signup = await client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest($"TR{tag}", $"tr{tag}@tr.local", "senha123"));
        Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await signup.Content.ReadAsStringAsync());
        var auth = (await signup.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.Token);

        var uid = auth.User.Id;
        string? workdir = null;
        if (bound)
        {
            const string dir = "repos/test__repo";
            workdir = Path.Join(_apiDir, "data", "workspaces", uid, dir);
            Directory.CreateDirectory(Path.Join(workdir, ".git"));
            await SeedBindingAsync(dbPath, uid, dir, testCommand);
        }
        return new Ctx(factory, client, workdir, uid);
    }

    /// <summary>Semeia o kv <c>u:{uid}:workspace.repo</c> direto no SQLite da app.</summary>
    private static async Task SeedBindingAsync(
        string dbPath, string uid, string dir, string? testCommand)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options);
        var json = JsonSerializer.Serialize(
            new WorkspaceRepoBinding("test/repo", "main", dir, testCommand));
        db.ConfigEntries.Add(new ConfigEntry { Key = $"u:{uid}:workspace.repo", ValueJson = json });
        await db.SaveChangesAsync();
    }

    private static void CleanupBoundWorkspace(string uid)
    {
        try
        {
            var dir = Path.Join(FindApiDir(), "data", "workspaces", uid);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static string FindApiDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Join(dir.FullName, "src", "OpenWebUI.Api")))
        {
            dir = dir.Parent;
        }
        Assert.That(dir, Is.Not.Null, "repo root não encontrado a partir do bin de testes");
        return Path.Join(dir!.FullName, "src", "OpenWebUI.Api");
    }

    private static string TempDir()
    {
        var dir = Path.Join(Path.GetTempPath(), $"detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
