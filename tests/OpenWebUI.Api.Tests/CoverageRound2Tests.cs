using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Browser;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using Microsoft.Playwright;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Round-2 do coverage gate: caminhos testáveis dos arquivos da entrega
/// (secrets→env, process runner com stdin/timeout, jobs com env, workspace
/// git rename/commit/erro, tools de browser restantes).
/// </summary>
[TestFixture]
public class CoverageRound2Tests
{
    private string _dbPath = null!;
    private string _tmp = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = TestInfra.NewDbPath("covr2");
        _tmp = Path.Join(Path.GetTempPath(), $"covr2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
    }

    [TearDown]
    public void TearDown()
    {
        TestInfra.DeleteDb(_dbPath);
        try { Directory.Delete(_tmp, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static BuiltinToolContext Ctx(string ws, string up) =>
        new("u1", "c1", "r1", ws, up);

    // ---------------- UserSecretsService (unit) ----------------

    [Test]
    public async Task Secrets_Globais_ListResolveEDelete()
    {
        var db = NewDb();
        var svc = new UserSecretsService(new ConfigService(db, TestCache.Create()));

        Assert.That(await svc.SetGlobalAsync("SONAR_TOKEN", "glob-val", default), Is.Null);
        Assert.That(await svc.SetUserAsync("u1", "MY_KEY", "user-val", default), Is.Null);

        var globals = await svc.ListGlobalAsync(default);
        Assert.That(globals.Select(e => e.Name), Does.Contain("SONAR_TOKEN"));
        Assert.That(globals.First(e => e.Name == "SONAR_TOKEN").Source, Is.EqualTo("global"));

        var resolved = await svc.ResolveValuesAsync("u1", default);
        Assert.That(resolved["SONAR_TOKEN"], Is.EqualTo("glob-val"));
        Assert.That(resolved["MY_KEY"], Is.EqualTo("user-val"));

        // Delete de nome inexistente é no-op; global some depois do delete.
        await svc.DeleteUserAsync("u1", "NAO_EXISTE", default);
        await svc.DeleteGlobalAsync("SONAR_TOKEN", default);
        Assert.That(await svc.ListGlobalAsync(default), Is.Empty);
    }

    [Test]
    public async Task Secrets_ValorLongoDemais_Rejeitado()
    {
        var db = NewDb();
        var svc = new UserSecretsService(new ConfigService(db, TestCache.Create()));
        var err = await svc.SetUserAsync("u1", "K", new string('x', 20_000), default);
        Assert.That(err, Does.Contain("excede"));
    }

    // ---------------- AgentSecretsEnvService ----------------

    private AgentSecretsEnvService EnvSvc(AppDbContext db, out UserSecretsService secrets)
    {
        var config = new ConfigService(db, TestCache.Create());
        secrets = new UserSecretsService(config);
        var github = new GitHubService(new StubHttpFactory(), config);
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tmp);
        env.ContentRootFileProvider.Returns(new PhysicalFileProvider(_tmp));
        env.EnvironmentName.Returns("Development");
        return new AgentSecretsEnvService(secrets, github, env);
    }

    [Test]
    public async Task Env_BuildGlobal_SecretsEPatGlobal_GeraAskpass()
    {
        var db = NewDb();
        var env = EnvSvc(db, out var secrets);
        await secrets.SetGlobalAsync("MY_FLAG", "f1", default);
        // PAT global direto no kv (mesma chave que GitHubService lê).
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync(GitHubService.GlobalTokenKey, "ghp_globalpat", default);

        var agentEnv = await env.BuildGlobalAsync(default);
        Assert.That(agentEnv.Vars["MY_FLAG"], Is.EqualTo("f1"));
        Assert.That(agentEnv.Vars["GITHUB_TOKEN"], Is.EqualTo("ghp_globalpat"));
        Assert.That(agentEnv.Vars["GIT_ASKPASS"], Does.Contain("git-askpass.sh"));
        Assert.That(File.Exists(agentEnv.Vars["GIT_ASKPASS"]), Is.True);
        Assert.That(agentEnv.SecretsToScrub, Does.Contain("ghp_globalpat"));
    }

    [Test]
    public async Task Env_BuildGlobal_Vazio_DevolveEmpty()
    {
        var env = EnvSvc(NewDb(), out _);
        var agentEnv = await env.BuildGlobalAsync(default);
        Assert.That(agentEnv.Vars, Is.Empty);
    }

    [Test]
    public async Task Env_BuildAsync_SemPat_RemoveAskpass()
    {
        var db = NewDb();
        var env = EnvSvc(db, out var secrets);
        await secrets.SetUserAsync("u1", "ONLY", "v", default);
        // askpass pré-existente do usuário → sem PAT é removido.
        var credDir = Path.Join(DataPaths.Root(_tmp), "credentials", "u1");
        Directory.CreateDirectory(credDir);
        var askpass = Path.Join(credDir, "git-askpass.sh");
        File.WriteAllText(askpass, "#!/bin/sh\n");

        var agentEnv = await env.BuildAsync("u1", default);
        Assert.That(agentEnv.Vars, Does.Not.ContainKey("GIT_ASKPASS"));
        Assert.That(File.Exists(askpass), Is.False);
    }

    // ---------------- ChatProcessRunner ----------------

    [Test]
    public async Task Runner_StdinEnvETimeoutKill()
    {
        // env + stdin cobrem os ramos pendentes do RunAsync.
        var ok = await ChatProcessRunner.RunAsync(
            "cat && echo $R2_VAR", _tmp, TimeSpan.FromSeconds(10),
            stdin: "stdin-line\n",
            env: new Dictionary<string, string> { ["R2_VAR"] = "env-val" });
        Assert.That(ok.Output, Does.Contain("stdin-line"));
        Assert.That(ok.Output, Does.Contain("env-val"));

        // timeout → kill da árvore (linhas de kill do runner).
        var slow = await ChatProcessRunner.RunAsync(
            "sleep 30", _tmp, TimeSpan.FromMilliseconds(300));
        Assert.That(slow.TimedOut, Is.True);
    }

    // ---------------- ChatJobService (env path) ----------------

    [Test]
    public async Task Job_EnvResolvida_InjetaVarsNoSpawn()
    {
        var jobDb = Path.Join(_tmp, "jobs.db");
        var ws = Path.Join(_tmp, "ws");
        Directory.CreateDirectory(ws);
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o =>
            o.UseSqlite($"Data Source={jobDb}"));
        services.AddHybridCache();
        services.AddSingleton<ConfigService>();
        services.AddSingleton<UserSecretsService>();
        services.AddSingleton<IHttpClientFactory>(new StubHttpFactory());
        services.AddSingleton<GitHubService>();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tmp);
        services.AddSingleton(env);
        services.AddSingleton<AgentSecretsEnvService>();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await DatabaseMigrator.MigrateAsync(db);
            var sec = scope.ServiceProvider.GetRequiredService<UserSecretsService>();
            await sec.SetUserAsync("u1", "JOB_VAR", "job-secret-1", default);
        }

        var jobs = new ChatJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ChatJobService>.Instance);
        // ChatId null — job fora de chat (FK de ChatJob.ChatId → Chat).
        var job = await jobs.StartAsync(
            "echo $JOB_VAR", new BuiltinToolContext("u1", null, "r1", ws, ws), default);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        ChatJob? done = null;
        while (DateTime.UtcNow < deadline)
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            done = await db.ChatJobs.AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == job.Id);
            if (done?.Status is ChatJobStatus.Completed or ChatJobStatus.Failed)
            {
                break;
            }
            await Task.Delay(120);
        }

        Assert.That(done?.Status, Is.EqualTo(ChatJobStatus.Completed));
        // Segredo resolvido mas mascarado no log (scrub literal).
        var log = await File.ReadAllTextAsync(done!.OutputPath!);
        Assert.That(log, Does.Not.Contain("job-secret-1"));
        Assert.That(log, Does.Contain("[REDACTED_SECRET]"));
    }

    // ---------------- WorkspaceGitService ----------------

    private static async Task GitAsync(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi);
        using var timeout = new CancellationTokenSource(15000);
        await p!.WaitForExitAsync(timeout.Token);
    }

    private static async Task CommitAllAsync(string dir, string msg)
    {
        await GitAsync(dir, "-c", "user.email=t@t", "-c", "user.name=t", "add", "-A");
        await GitAsync(dir, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", msg);
    }

    /// <summary>Pula o teste quando o binário <c>git</c> não existe no
    /// host — Process.Start lança antes de qualquer ExitCode.</summary>
    private static async Task RequireGitAsync()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            { RedirectStandardOutput = true, UseShellExecute = false });
            if (p is null)
            {
                Assert.Ignore("git indisponível");
                return;
            }
            using var timeout = new CancellationTokenSource(5000);
            await p.WaitForExitAsync(timeout.Token);
            if (p.ExitCode != 0)
            {
                Assert.Ignore("git indisponível");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Assert.Ignore($"git indisponível: {ex.Message}");
        }
    }

    [Test]
    public async Task Git_RenameCommitado_StatusREmOldPath()
    {
        await RequireGitAsync();
        var dir = Path.Join(_tmp, "repo-r");
        Directory.CreateDirectory(dir);
        await GitAsync(dir, "init");
        File.WriteAllText(Path.Join(dir, "velho.txt"), "a\nb\nc\nd\ne\nf\ng\nh\n");
        await CommitAllAsync(dir, "base");
        var bpsi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        bpsi.ArgumentList.Add("branch");
        bpsi.ArgumentList.Add("--show-current");
        using var bp = Process.Start(bpsi);
        var baseBranch = (await bp!.StandardOutput.ReadToEndAsync()).Trim();
        await bp.WaitForExitAsync();

        await GitAsync(dir, "checkout", "-b", "run-r");
        await GitAsync(dir, "mv", "velho.txt", "novo.txt");
        await CommitAllAsync(dir, "rename");

        var info = await new WorkspaceGitService()
            .GetInfoAsync(dir, default, baseRef: baseBranch);

        var renamed = info.Files.FirstOrDefault(f => f.Path == "novo.txt");
        Assert.That(renamed, Is.Not.Null);
        Assert.That(renamed!.Status, Is.EqualTo("R"));
        Assert.That(renamed.OldPath, Is.EqualTo("velho.txt"));
    }

    [Test]
    public async Task Git_CommitDoServico_CriaCommit()
    {
        await RequireGitAsync();
        var dir = Path.Join(_tmp, "repo-c");
        Directory.CreateDirectory(dir);
        await GitAsync(dir, "init");
        File.WriteAllText(Path.Join(dir, "x.txt"), "1\n");
        await CommitAllAsync(dir, "base");
        File.WriteAllText(Path.Join(dir, "x.txt"), "1\n2\n");

        var svc = new WorkspaceGitService();
        var sha = await svc.CommitAsync(dir, "commit via servico", default);
        Assert.That(sha, Has.Length.GreaterThanOrEqualTo(7));

        var lpsi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        lpsi.ArgumentList.Add("log");
        lpsi.ArgumentList.Add("--oneline");
        lpsi.ArgumentList.Add("-1");
        using var log = Process.Start(lpsi)!;
        var msg = await log.StandardOutput.ReadToEndAsync();
        await log.WaitForExitAsync();
        Assert.That(msg, Does.Contain("commit via servico"));
    }

    [Test]
    public async Task Git_NaoRepo_DevolveNaoRepo()
    {
        var dir = Path.Join(_tmp, "nao-repo");
        Directory.CreateDirectory(dir);
        var info = await new WorkspaceGitService().GetInfoAsync(dir, default);
        Assert.That(info.IsRepo, Is.False);
    }

    // ---------------- Browser tools (resto dos ramos) ----------------

    private static IBrowserSessions Sessions(IPage page)
    {
        var s = Substitute.For<IBrowserSessions>();
        s.GetPageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(page));
        s.TryConsumeAction(Arg.Any<string>()).Returns(true);
        s.MaxActionsPerRun.Returns(50);
        s.ResetChatAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        s.CloseChatAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        return s;
    }

    private static IPage MiniPage()
    {
        var page = Substitute.For<IPage>();
        page.EvaluateAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns(Task.FromResult(JsonDocument.Parse("[]").RootElement.Clone()));
        page.ScreenshotAsync(Arg.Any<PageScreenshotOptions?>())
            .Returns(Task.FromResult(new byte[] { 1 }));
        page.Mouse.Returns(Substitute.For<IMouse>());
        page.Mouse.ClickAsync(Arg.Any<float>(), Arg.Any<float>(), Arg.Any<MouseClickOptions?>())
            .Returns(Task.CompletedTask);
        page.Keyboard.Returns(Substitute.For<IKeyboard>());
        page.Keyboard.PressAsync(Arg.Any<string>(), Arg.Any<KeyboardPressOptions?>())
            .Returns(Task.CompletedTask);
        return page;
    }

    private BrowserSessionToolBase Act(AppDbContext db, IBrowserSessions s) =>
        new BrowserActBuiltinTool(s, new TesseractOcr(NullLogger<TesseractOcr>.Instance),
            new ConfigService(db, TestCache.Create()), EnabledCfg(), db);

    private static IConfiguration EnabledCfg() =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["BrowserTools:Enabled"] = "true" }).Build();

    [Test]
    public async Task Act_PressESelectNoLocator()
    {
        var db = NewDb();
        var page = MiniPage();
        page.TitleAsync().Returns(Task.FromResult("t"));
        page.Url.Returns("http://localhost/x");
        var loc = Substitute.For<ILocator>();
        loc.First.Returns(loc);
        loc.PressAsync(Arg.Any<string>(), Arg.Any<LocatorPressOptions?>())
            .Returns(Task.CompletedTask);
        loc.SelectOptionAsync(Arg.Any<string>(), Arg.Any<LocatorSelectOptionOptions?>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));
        page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(loc);

        var up = Path.Join(_tmp, "up");
        await Act(db, Sessions(page)).ExecuteAsync(
            JsonDocument.Parse("""{"action":"press","index":0,"key":"Enter"}""").RootElement,
            Ctx(_tmp, up), default);
        await loc.Received(1).PressAsync("Enter", Arg.Any<LocatorPressOptions?>());

        await Act(db, Sessions(page)).ExecuteAsync(
            JsonDocument.Parse("""{"action":"select","index":0,"value":"b"}""").RootElement,
            Ctx(_tmp, up), default);
        await loc.Received(1).SelectOptionAsync("b", Arg.Any<LocatorSelectOptionOptions?>());
    }

    [Test]
    public async Task Observe_TituloFalha_SegueComVazio()
    {
        var db = NewDb();
        var page = MiniPage();
        page.TitleAsync().Returns<Task<string>>(_ => throw new PlaywrightException("no title"));
        page.Url.Returns("http://localhost/y");
        var sessions = Sessions(page);

        var tool = new BrowserObserveBuiltinTool(sessions,
            new TesseractOcr(NullLogger<TesseractOcr>.Instance),
            new ConfigService(db, TestCache.Create()), EnabledCfg(), db);
        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("{}").RootElement, Ctx(_tmp, Path.Join(_tmp, "up2")), default);
        Assert.That(result.Text, Does.Contain("http://localhost/y"));
    }

    [Test]
    public async Task Observe_InvalidOperation_NaoReseta()
    {
        var db = NewDb();
        var sessions = Substitute.For<IBrowserSessions>();
        sessions.GetPageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IPage>>(_ => throw new InvalidOperationException("x"));
        sessions.ResetChatAsync(Arg.Any<string>()).Returns(Task.CompletedTask);

        var tool = new BrowserObserveBuiltinTool(sessions,
            new TesseractOcr(NullLogger<TesseractOcr>.Instance),
            new ConfigService(db, TestCache.Create()), EnabledCfg(), db);
        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("{}").RootElement, Ctx(_tmp, _tmp), default);
        Assert.That(result.Text, Does.Contain("browser_observe falhou"));
        await sessions.DidNotReceive().ResetChatAsync(Arg.Any<string>());
        Assert.That(tool.SafeInParallel, Is.True);
    }

    // ---------------- BrowserElements (puro) ----------------

    [Test]
    public void Elements_Parse_EstruturasParciais()
    {
        // Não-array → lista vazia.
        Assert.That(BrowserElements.Parse(
            JsonDocument.Parse("""{"a":1}""").RootElement), Is.Empty);

        // Item sem props → defaults; null → "".
        var list = BrowserElements.Parse(JsonDocument.Parse("""
            [{},{"i":7,"tag":null,"role":"link","name":"Entrar","href":"/x","type":null}]
            """).RootElement);
        Assert.That(list, Has.Count.EqualTo(2));
        Assert.That(list[0].Index, Is.EqualTo(0));
        Assert.That(list[0].Tag, Is.EqualTo(""));
        Assert.That(list[1].Index, Is.EqualTo(7));
        Assert.That(list[1].Tag, Is.EqualTo(""));
        Assert.That(list[1].Name, Is.EqualTo("Entrar"));
    }

    [Test]
    public void Elements_Format_Rank_Caminhos()
    {
        Assert.That(BrowserElements.Format([]), Does.Contain("nenhum elemento"));
        var el = new List<BrowserElement>
        {
            new(0, "button", "", "Salvar", "", ""),
            new(1, "a", "link", "Cancelar pedido", "/cancel", ""),
            new(2, "input", "", "email", "", "text"),
        };
        var fmt = BrowserElements.Format(el);
        Assert.That(fmt, Does.Contain("[1] a/link"));

        // Query vazia/nula → [].
        Assert.That(BrowserElements.Rank(el, null), Is.Empty);
        Assert.That(BrowserElements.Rank(el, "  "), Is.Empty);
        // Sem match → [] (filtro score>0).
        Assert.That(BrowserElements.Rank(el, "zzz"), Is.Empty);
        // Match exato (2) supera prefixo (1); ordena por score depois índice.
        var ranked = BrowserElements.Rank(el, "cancelar", take: 5);
        Assert.That(ranked[0].Index, Is.EqualTo(1));
        // take limita.
        Assert.That(BrowserElements.Rank(el, "a", take: 1), Has.Count.LessThanOrEqualTo(1));
    }

    // ---------------- TesseractOcr.FindTextBox (puro) ----------------

    [Test]
    public void Ocr_FindTextBox_TsvCaminhos()
    {
        const string tsv = """
            1	1	0	0	0	0	0	0	100	50	-1	0
            5	1	1	1	1	0	10	20	30	10	95	Login
            5	1	1	1	1	0	50	40	40	12	95	Entrar
            5	1	1	1	1	0	bad	x	y	z	95	quebrado
            4	1	1	1	1	0	0	0	0	0	-1	
            """;
        // Query vazia → null.
        Assert.That(TesseractOcr.FindTextBox(tsv, "  "), Is.Null);
        // Sem match → null.
        Assert.That(TesseractOcr.FindTextBox(tsv, "inexistente"), Is.Null);
        // Match nas duas palavras → centro da união dos boxes.
        var box = TesseractOcr.FindTextBox(tsv, "login entrar");
        Assert.That(box, Is.Not.Null);
        Assert.That(box!.Value.X, Is.EqualTo((10 + 90) / 2));
        Assert.That(box.Value.Y, Is.EqualTo((20 + 52) / 2));
    }

    // ---------------- SecretScrubber literal ----------------

    [Test]
    public void Scrub_Literals_NulosECurtos()
    {
        Assert.That(SecretScrubber.Scrub(null, ["x"]), Is.Null);
        Assert.That(SecretScrubber.Scrub("ok", null), Is.EqualTo("ok"));
        // Literal <4 chars não é mascarado (evita destruir output).
        Assert.That(SecretScrubber.Scrub("abc", ["abc"]), Is.EqualTo("abc"));
        // Maior primeiro: sufixo sobreposto não sobra.
        var r = SecretScrubber.Scrub("tok=abcdefgh", ["cdef", "abcdefgh"]);
        Assert.That(r, Is.EqualTo("tok=[REDACTED_SECRET]"));
    }

    // ---------------- BrowserSessionService (config props) ----------------

    [Test]
    public void BrowserCfg_ParseValidosEDefaults()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["BrowserTools:ActionTimeoutSeconds"] = "7",
                ["BrowserTools:MaxActionsPerRun"] = "9",
                ["BrowserTools:ViewportWidth"] = "640",
                ["BrowserTools:ViewportHeight"] = "480",
                ["BrowserTools:AutoInstall"] = "false",
                ["BrowserTools:Ocr"] = "true",
            }).Build();
        var svc = new BrowserSessionService(cfg,
            NullLogger<BrowserSessionService>.Instance);
        Assert.That(svc.ActionTimeoutMs, Is.EqualTo(7000));
        Assert.That(svc.MaxActionsPerRun, Is.EqualTo(9));
        Assert.That(svc.Viewport.Width, Is.EqualTo(640));
        Assert.That(svc.Viewport.Height, Is.EqualTo(480));
        Assert.That(svc.AutoInstall, Is.False);
        Assert.That(svc.OcrEnabled, Is.True);

        var bad = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["BrowserTools:ActionTimeoutSeconds"] = "x",
                ["BrowserTools:MaxActionsPerRun"] = "-3",
                ["BrowserTools:ViewportWidth"] = "0",
                ["BrowserTools:ViewportHeight"] = "abc",
            }).Build();
        var svc2 = new BrowserSessionService(bad,
            NullLogger<BrowserSessionService>.Instance);
        Assert.That(svc2.ActionTimeoutMs, Is.EqualTo(30000));
        Assert.That(svc2.MaxActionsPerRun, Is.EqualTo(50));
        Assert.That(svc2.Viewport.Width, Is.EqualTo(1280));
        Assert.That(svc2.Viewport.Height, Is.EqualTo(800));
        Assert.That(svc2.AutoInstall, Is.True);
        Assert.That(svc2.OcrEnabled, Is.False);
    }

    // ---------------- UserSecretsService edge cases ----------------

    [Test]
    public async Task Secrets_Set_ValorELimiteEUpdate()
    {
        var db = NewDb();
        var cfg = new ConfigService(db, TestCache.Create());
        var svc = new UserSecretsService(cfg);
        // Valor acima do limite → erro.
        var tooBig = await svc.SetUserAsync("u1", "BIG", new string('x', 8193), default);
        Assert.That(tooBig, Does.Contain("excede"));
        // Update preserva CreatedAt (dict.TryGetValue true).
        await svc.SetUserAsync("u1", "K", "v1", default);
        var first = (await svc.GetUserSecretsAsync("u1", default))["K"];
        await Task.Delay(1100);
        await svc.SetUserAsync("u1", "K", "v2", default);
        var second = (await svc.GetUserSecretsAsync("u1", default))["K"];
        Assert.That(second.CreatedAt, Is.EqualTo(first.CreatedAt));
        Assert.That(second.UpdatedAt, Is.GreaterThan(first.UpdatedAt));
        // Limite por nível: preenche o kv direto e tenta adicionar mais um.
        var full = Enumerable.Range(0, 50)
            .ToDictionary(i => $"K{i}", i => new StoredSecret("v", 1, 1));
        await cfg.SetAsync("u:u1:secrets", full, default);
        var err = await svc.SetUserAsync("u1", "EXTRA", "v", default);
        Assert.That(err, Does.Contain("Limite"));
        // Update em nível cheio passa (ContainsKey true).
        Assert.That(await svc.SetUserAsync("u1", "K0", "v2", default), Is.Null);
    }

    // ---------------- WorkspaceGitService delete/rename ----------------

    [Test]
    public async Task Workspace_DeleteFile_StatusD()
    {
        var dir = Path.Join(_tmp, "repo-del");
        Directory.CreateDirectory(dir);
        await GitAsync(dir, "init");
        File.WriteAllText(Path.Join(dir, "vai.txt"), "x\n");
        await CommitAllAsync(dir, "base");
        await GitAsync(dir, "checkout", "-b", "run-del");
        File.Delete(Path.Join(dir, "vai.txt"));

        var svc = new WorkspaceGitService();
        var info = await svc.GetInfoAsync(dir, default);
        Assert.That(info.Files.Any(f => f.Status == "D" && f.Path == "vai.txt"),
            Is.True, "arquivo deletado deve aparecer como D");
        Assert.That(info.Diff, Does.Contain("deleted file"));
    }

    // ---------------- Stub HTTP ----------------

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(
                System.Net.HttpStatusCode.NotFound));
    }
}
