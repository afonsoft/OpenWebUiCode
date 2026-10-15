using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Api.Runs;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.Terminal;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Backfill de cobertura da SPEC-20261010-coverage-backfill-r2: blocos
/// ainda descobertos medidos no cobertura XML — <see cref="LdapService"/>,
/// <see cref="McpClientService"/>, <see cref="PtySession"/>,
/// <see cref="PythonToolExecutor"/>, <see cref="ChatJobService"/>,
/// <see cref="ChatRunDispatcher"/>, <see cref="CheckpointService"/> e as
/// rotas <c>/api/v1/videos/*</c>. Só caminhos determinísticos — sem LDAP,
/// PTY real nem rede externa.
/// </summary>
[TestFixture, IsolateEnvironment]
public class LdapServiceBackfillTests
{
    [Test]
    public void IsEnabled_SemConfig_ou_ComConfig()
    {
        Assert.That(LdapService.IsEnabled, Is.False);

        Environment.SetEnvironmentVariable("LDAP_SERVER", "ldap.local");
        Assert.That(LdapService.IsEnabled, Is.False, "falta o template de DN");

        Environment.SetEnvironmentVariable("LDAP_USER_DN_TEMPLATE", "uid={username},dc=x");
        Assert.That(LdapService.IsEnabled, Is.True);
    }

    [Test]
    public async Task TryBind_Desabilitado_OuSenhaVazia_Null()
    {
        // Sem config → desabilitado.
        Assert.That(await LdapService.TryBindAsync("u", "p"), Is.Null);

        Environment.SetEnvironmentVariable("LDAP_SERVER", "127.0.0.1");
        Environment.SetEnvironmentVariable("LDAP_USER_DN_TEMPLATE", "uid={username},dc=x");
        Assert.That(await LdapService.TryBindAsync("u", ""), Is.Null,
            "senha vazia nunca toca a rede");
        Assert.That(await LdapService.TryBindAsync("u", "   "), Is.Null);
    }

    [Test]
    public async Task TryBind_TemplateSemPlaceholder_Null()
    {
        Environment.SetEnvironmentVariable("LDAP_SERVER", "127.0.0.1");
        Environment.SetEnvironmentVariable("LDAP_USER_DN_TEMPLATE", "uid=fixo,dc=x");
        Assert.That(await LdapService.TryBindAsync("u", "p"), Is.Null,
            "dn==template → config inválida");
    }

    [Test]
    public async Task TryBind_ServidorInalcancavel_Null()
    {
        // Porta 1 fechada → connection refused rápido → LdapException → null.
        Environment.SetEnvironmentVariable("LDAP_SERVER", "127.0.0.1");
        Environment.SetEnvironmentVariable("LDAP_PORT", "1");
        Environment.SetEnvironmentVariable("LDAP_USER_DN_TEMPLATE", "uid={username},dc=x");
        Environment.SetEnvironmentVariable("LDAP_MAIL_ATTRIBUTE", "mail");
        Environment.SetEnvironmentVariable("LDAP_NAME_ATTRIBUTE", "cn");
        Environment.SetEnvironmentVariable("LDAP_SEARCH_BASE", "dc=x");

        Assert.That(await LdapService.TryBindAsync("u", "p"), Is.Null);
    }
}

/// <summary>Backfill: helpers estáticos e guardas do <see cref="McpClientService"/>.</summary>
[TestFixture, IsolateEnvironment]
public class McpClientServiceBackfillTests
{
    private static AppDbContext NewDb(string path)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        DatabaseMigrator.MigrateAsync(db).GetAwaiter().GetResult();
        return db;
    }

    [Test]
    public void VirtualFunctionName_SlugificaETrunca()
    {
        var server = new McpServer { Name = "meu server!" };
        Assert.That(McpClientService.VirtualFunctionName(server, "tool name"),
            Is.EqualTo("mcp_meu_server__tool_name"));

        var longo = new McpServer { Name = new string('n', 80) };
        Assert.That(McpClientService.VirtualFunctionName(longo, new string('t', 80)),
            Has.Length.EqualTo(64));
    }

    [Test]
    public void ParseVirtualUrl_FormasValidasEInvalidas()
    {
        Assert.That(McpClientService.ParseVirtualUrl("mcp://srv-1/minha-tool"),
            Is.EqualTo(("srv-1", "minha-tool")));
        Assert.That(McpClientService.ParseVirtualUrl("http://x/y"), Is.Null);
        Assert.That(McpClientService.ParseVirtualUrl("mcp://sem-slash"), Is.Null);
        Assert.That(McpClientService.ParseVirtualUrl("mcp:///tool"), Is.Null);
    }

    [Test]
    public void ParseStringList_ToleranteAMalformado()
    {
        Assert.That(McpClientService.ParseStringList(null), Is.Empty);
        Assert.That(McpClientService.ParseStringList("  "), Is.Empty);
        Assert.That(McpClientService.ParseStringList("{não json"), Is.Empty);
        Assert.That(McpClientService.ParseStringList("[\"a\",\"b\"]"),
            Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public void ParseHeaders_ToleranteAMalformado()
    {
        Assert.That(McpClientService.ParseHeaders(null), Is.Empty);
        Assert.That(McpClientService.ParseHeaders("{bad"), Is.Empty);
        var parsed = McpClientService.ParseHeaders("{\"Authorization\":\"Bearer x\"}");
        Assert.That(parsed["Authorization"], Is.EqualTo("Bearer x"));
    }

    [Test]
    public async Task CallTool_ServidorDesabilitado_ErroLegivel()
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"mcp-bf-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = NewDb(dbPath);
            var cache = TestCache.Create();
            var svc = new McpClientService(db, cache);

            var server = new McpServer { Name = "srv-off", Enabled = false };
            var result = await svc.CallToolAsync(server, "tool", "{}");
            Assert.That(result, Does.Contain("desabilitado"));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.RefreshToolsAsync(server));
            Assert.That(ex!.Message, Does.Contain("desabilitado"));
        }
        finally
        {
            TestInfra.DeleteDb(dbPath);
        }
    }

    [Test]
    public async Task CallTool_HttpInalcancavel_ErroLegivel()
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"mcp-bf-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = NewDb(dbPath);
            var cache = TestCache.Create();
            var svc = new McpClientService(db, cache);

            // Porta 1 fechada → HttpRequestException → mensagem de erro.
            var server = new McpServer
            {
                Name = "srv-http",
                Transport = "http",
                Url = "http://127.0.0.1:1/mcp",
                Enabled = true,
            };
            var result = await svc.CallToolAsync(server, "tool", "{\"a\":1}");
            Assert.That(result, Does.Contain("Erro ao executar a tool MCP 'tool'"));
        }
        finally
        {
            TestInfra.DeleteDb(dbPath);
        }
    }

    [Test]
    public async Task Refresh_HttpInalcancavel_GravaLastErrorELanca()
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"mcp-bf-{Guid.NewGuid():N}.db");
        try
        {
            await using var db = NewDb(dbPath);
            var cache = TestCache.Create();
            var svc = new McpClientService(db, cache);

            var server = new McpServer
            {
                Name = "srv-http",
                Transport = "http",
                Url = "http://127.0.0.1:1/mcp",
                Enabled = true,
            };
            db.McpServers.Add(server);
            await db.SaveChangesAsync();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.RefreshToolsAsync(server));
            Assert.That(ex!.Message, Does.Contain("servidor MCP"));

            // LastError persistido no banco.
            await using var db2 = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath}").Options);
            var saved = await db2.McpServers.FindAsync(server.Id);
            Assert.That(saved!.LastError, Is.Not.Null.And.Not.Empty);
        }
        finally
        {
            TestInfra.DeleteDb(dbPath);
        }
    }
}

/// <summary>Backfill: guards e lifecycle do <see cref="PtySession"/> sem PTY real.</summary>
[TestFixture, IsolateEnvironment]
public class PtySessionBackfillTests
{
    [Test]
    public void ShellQuote_EscapaSingleQuotes()
    {
        Assert.That(PtySession.ShellQuote("simples"), Is.EqualTo("'simples'"));
        Assert.That(PtySession.ShellQuote("a'b"), Is.EqualTo("'a'\"'\"'b'"));
        Assert.That(PtySession.ShellQuote(""), Is.EqualTo("''"));
    }

    [Test]
    public async Task CicloDeVida_SemStart_Guards()
    {
        var session = new PtySession(
            Path.GetTempPath(), NullLogger<PtySession>.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(session.IsRunning, Is.False);
            Assert.That(session.LastActivityUtc, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow));
        });

        // Write/Resize antes do Start são no-op.
        await session.WriteAsync("echo x\n");
        await session.ResizeAsync(0, 0);      // fora dos limites
        await session.ResizeAsync(501, 600);  // fora dos limites
        await session.ResizeAsync(120, 30);   // igual ao inicial → no-op

        await session.DisposeAsync();
        await session.DisposeAsync(); // segunda chamada → early return
    }

    [Test]
    public void Start_SemBinarioScript_Lanca()
    {
        var session = new PtySession(
            Path.GetTempPath(), NullLogger<PtySession>.Instance,
            executableLocator: _ => null);
        Assert.Throws<InvalidOperationException>(() => session.Start());
    }

    [Test]
    public async Task Start_ComBinarioFake_PumpExitedEDispose()
    {
        // "script" fake: um sh que imprime marcador e espelha stdin via cat —
        // cobre Start, PumpAsync, WriteAsync, ResizeAsync e DisposeAsync
        // sem precisar de um PTY de verdade.
        if (OperatingSystem.IsWindows())
        {
            // PTY + bit de execução são Unix-only — sem equivalente no host Windows.
            Assert.Ignore("PTY/Unix file mode não existe em Windows");
            return;
        }
        var dir = Path.Join(Path.GetTempPath(), $"ptybf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var fake = Path.Join(dir, "fake-script.sh");
        await File.WriteAllTextAsync(fake, "#!/bin/sh\necho FAKE_READY\ncat\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserExecute | UnixFileMode.UserRead);

        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new PtySession(
            dir, NullLogger<PtySession>.Instance, cols: 90, rows: 25,
            executableLocator: name => name == "script" ? fake : null);

        try
        {
            session.OutputReceived += chunk => output.Append(chunk);
            session.Exited += code => exited.TrySetResult(code);
            session.Start();
            session.Start(); // idempotente: segundo Start é no-op

            Assert.That(session.IsRunning, Is.True);
            await SpinWaitAsync(() => output.ToString().Contains("FAKE_READY"), 5_000);

            var before = session.LastActivityUtc;
            var marker = $"mk{Guid.NewGuid():N}"[..12];
            await session.WriteAsync($"{marker}\n");
            Assert.That(session.LastActivityUtc, Is.GreaterThanOrEqualTo(before));
            await SpinWaitAsync(() => output.ToString().Contains(marker), 5_000);

            // Resize válido → tenta ioctl; sem pts slave é no-op silencioso.
            await session.ResizeAsync(100, 40);
        }
        finally
        {
            await session.DisposeAsync();
            Assert.That(session.IsRunning, Is.False);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task SpinWaitAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.That(condition(), Is.True, "condição não atingida dentro do timeout");
    }
}

/// <summary>Backfill: erros e bordas do <see cref="PythonToolExecutor"/>.</summary>
[TestFixture, IsolateEnvironment]
public class PythonToolExecutorBackfillTests
{
    private static bool HasPython() =>
        Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
            .Any(dir => File.Exists(Path.Join(dir, "python3"))
                     || File.Exists(Path.Join(dir, "python3.exe")));

    private static Tool CodeTool(string? code) => new()
    {
        UserId = "u1",
        Name = "PyTool",
        SpecJson = "{}",
        Code = code,
    };

    private static PythonToolExecutor Executor(Dictionary<string, string?>? config = null) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(config ?? new Dictionary<string, string?>())
            .Build());

    [Test]
    public async Task InterpretadorInexistente_ErroLegivel()
    {
        var executor = Executor(new Dictionary<string, string?>
        {
            ["Python:Path"] = "/nonexistent/python-xyz",
        });
        var result = await executor.ExecuteAsync(CodeTool("x=1"), "f", "{}");
        Assert.That(result, Does.Contain("interpretador Python").And.Contain("não encontrado"));
    }

    [Test]
    public async Task ArgumentsJsonInvalido_CaiParaObjetoVazio()
    {
        if (!HasPython()) Assert.Ignore("python3 não disponível no ambiente.");
        var tool = CodeTool("""
            class Tools:
                def ping(self) -> str:
                    return "pong"
            """);
        var result = await Executor().ExecuteAsync(tool, "ping", "{json quebrado");
        Assert.That(result, Is.EqualTo("pong"));
    }

    [Test]
    public async Task ErroDeSintaxe_StderrViraDetalhe()
    {
        if (!HasPython()) Assert.Ignore("python3 não disponível no ambiente.");
        // Código que não compila → processo sai sem linha marcada → stderr.
        var result = await Executor().ExecuteAsync(
            CodeTool("def broken(:\n"), "f", "{}");
        Assert.That(result, Does.StartWith("Erro na tool 'f'").And.Contain("Error"));
    }

    [Test]
    public async Task SaidaSemMarcadorESemStderr_RelataExitCode()
    {
        if (!HasPython()) Assert.Ignore("python3 não disponível no ambiente.");
        // SystemExit não é Exception → runner não captura → saída limpa,
        // código não-zero, sem stderr.
        var result = await Executor().ExecuteAsync(
            CodeTool("import sys\nsys.exit(3)\nclass Tools:\n    pass\n"), "f", "{}");
        Assert.That(result, Is.EqualTo("Erro: tool 'f' terminou com código 3 sem resultado."));
    }

    [Test]
    public async Task ResultadoLongo_TruncaEm4000()
    {
        if (!HasPython()) Assert.Ignore("python3 não disponível no ambiente.");
        var tool = CodeTool("""
            class Tools:
                def big(self) -> str:
                    return "x" * 9000
            """);
        var result = await Executor().ExecuteAsync(tool, "big", "{}");
        Assert.That(result, Has.Length.EqualTo(4000));
    }

    [Test]
    public async Task Cancelamento_MataProcessoEPropaga()
    {
        if (!HasPython()) Assert.Ignore("python3 não disponível no ambiente.");
        var tool = CodeTool("""
            class Tools:
                def slow(self) -> str:
                    import time
                    time.sleep(60)
                    return "done"
            """);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.CatchAsync<OperationCanceledException>(
            () => Executor().ExecuteAsync(tool, "slow", "{}", cts.Token));
    }
}

/// <summary>
/// Backfill: <see cref="ChatJobService"/> — spawn real de processo curto
/// (determinístico em Linux), kill, sweep de órfãos e isolamento.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ChatJobServiceBackfillTests
{
    private string _dbPath = null!;
    private string _workspace = null!;
    private ServiceProvider _provider = null!;
    private ChatJobService _svc = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"jobs-bf-{Guid.NewGuid():N}.db");
        _workspace = Path.Join(Path.GetTempPath(), $"jobs-bf-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o =>
            o.UseSqlite($"Data Source={_dbPath}"));
        _provider = services.BuildServiceProvider();
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await DatabaseMigrator.MigrateAsync(db);
        }

        _svc = new ChatJobService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ChatJobService>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _provider.DisposeAsync();
        TestInfra.DeleteDb(_dbPath);
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    private BuiltinToolContext Ctx() => new(
        "u-job", null, null, _workspace, _workspace);

    private async Task<ChatJob?> JobAsync(string id)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ChatJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id);
    }

    private static async Task WaitStatusAsync(
        Func<Task<ChatJob?>> get, string id, string status, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var job = await get();
            if (job?.Status == status)
            {
                return;
            }

            await Task.Delay(100);
        }

        var last = await get();
        Assert.That(last?.Status, Is.EqualTo(status),
            $"job {id} não chegou a {status} (ficou {last?.Status})");
    }

    [Test]
    public async Task Start_ComandoNegado_Lanca()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.StartAsync("shutdown now", Ctx(), default));
        Assert.That(ex!.Message, Does.Contain("negado"));

        // Comando vazio também é fail-closed.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.StartAsync("   ", Ctx(), default));
    }

    [Test]
    public async Task Start_Echo_CompletaComLog()
    {
        var job = await _svc.StartAsync("echo hello-bf", Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(job.Status, Is.EqualTo(ChatJobStatus.Running));
            Assert.That(job.Pid, Is.Not.Null);
            Assert.That(job.OutputPath, Does.Contain(".chat-jobs"));
        });

        await WaitStatusAsync(() => JobAsync(job.Id), job.Id, ChatJobStatus.Completed);
        var done = (await JobAsync(job.Id))!;
        Assert.That(done.ExitCode, Is.EqualTo(0));
        Assert.That(await File.ReadAllTextAsync(done.OutputPath!),
            Does.Contain("hello-bf"));
    }

    [Test]
    public async Task Start_ExitCodeNaoZero_MarcaFailed()
    {
        var job = await _svc.StartAsync("exit 3", Ctx(), default);
        await WaitStatusAsync(() => JobAsync(job.Id), job.Id, ChatJobStatus.Failed);
        var done = (await JobAsync(job.Id))!;
        Assert.That(done.ExitCode, Is.EqualTo(3));
    }

    [Test]
    public async Task Kill_JobVivo_MataEMarcaKilled()
    {
        var job = await _svc.StartAsync("sleep 60", Ctx(), default);
        Assert.That(await _svc.KillAsync("u-job", job.Id, default), Is.True);

        var dead = (await JobAsync(job.Id))!;
        Assert.That(dead.Status, Is.EqualTo(ChatJobStatus.Killed));

        // Segundo kill → false (já terminou).
        Assert.That(await _svc.KillAsync("u-job", job.Id, default), Is.False);
        // Job de outro usuário → false.
        Assert.That(await _svc.KillAsync("outro", job.Id, default), Is.False);
    }

    [Test]
    public async Task Sweep_PidMortoViraKilled_PidVivoFica()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChatJobs.Add(new ChatJob
            {
                Id = "bf-dead",
                UserId = "u-job",
                Command = "sleep 1",
                WorkspacePath = _workspace,
                Status = ChatJobStatus.Running,
                Pid = 999_999_990, // pid certamente morto
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            db.ChatJobs.Add(new ChatJob
            {
                Id = "bf-alive",
                UserId = "u-job",
                Command = "sleep 1",
                WorkspacePath = _workspace,
                Status = ChatJobStatus.Running,
                Pid = Environment.ProcessId, // este processo está vivo
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            await db.SaveChangesAsync();
        }

        await _svc.SweepOrphansAsync(default);

        Assert.Multiple(async () =>
        {
            Assert.That((await JobAsync("bf-dead"))!.Status,
                Is.EqualTo(ChatJobStatus.Killed));
            Assert.That((await JobAsync("bf-dead"))!.Error, Does.Contain("reiniciado"));
            Assert.That((await JobAsync("bf-alive"))!.Status,
                Is.EqualTo(ChatJobStatus.Running));
        });
    }
}

/// <summary>
/// Backfill: <see cref="ChatRunDispatcher"/> — TryStop, Enqueue de run
/// inexistente/não-queued e o caminho queued→failed via executor.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ChatRunDispatcherBackfillTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"owui-disp-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@disp.local");
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
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

    private ChatRunDispatcher Dispatcher() =>
        _factory.Services.GetRequiredService<ChatRunDispatcher>();

    private async Task<ChatRun> SeedRunAsync(string userId, string status, string requestJson)
    {
        var chat = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Chat disp", ["llama3"], []));
        Assert.That(chat.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var chatId = (await chat.Content.ReadFromJsonAsync<ChatResponse>())!.Id;

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = new ChatRun
        {
            ChatId = chatId,
            UserId = userId,
            Model = "llama3",
            Status = status,
            RequestJson = requestJson,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private async Task<ChatRun?> RunAsync(string id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ChatRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
    }

    [Test]
    public void TryStop_RunInexistente_False()
    {
        Assert.That(Dispatcher().TryStop("run-inexistente"), Is.False);
    }

    [Test]
    public async Task Enqueue_RunInexistente_NaoFalha()
    {
        // Id que não existe no banco → worker retorna cedo.
        var dispatcher = Dispatcher();
        dispatcher.Enqueue("run-que-nao-existe");
        await Task.Delay(500);
        Assert.That(await RunAsync("run-que-nao-existe"), Is.Null);
    }

    [Test]
    public async Task Enqueue_RunNaoQueued_RetornaCedo()
    {
        var auth = await SignUpAsync("DispA", "dispa@disp.local");
        UseToken(auth.Token);
        var run = await SeedRunAsync(auth.User.Id, ChatRunStatus.Failed, "{}");

        Dispatcher().Enqueue(run.Id);
        await Task.Delay(500);
        var after = await RunAsync(run.Id);
        Assert.That(after!.Status, Is.EqualTo(ChatRunStatus.Failed));
    }

    [Test]
    public async Task Enqueue_RequestJsonInvalido_FalhaViaExecutor()
    {
        var auth = await SignUpAsync("DispB", "dispb@disp.local");
        UseToken(auth.Token);
        var run = await SeedRunAsync(auth.User.Id, ChatRunStatus.Queued, "{json-invalido");

        Dispatcher().Enqueue(run.Id);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var current = await RunAsync(run.Id);
            if (current!.Status is not (ChatRunStatus.Queued or ChatRunStatus.Running))
            {
                Assert.That(current.Status, Is.EqualTo(ChatRunStatus.Failed));
                Assert.That(current.StartedAt, Is.Not.Null);
                Assert.That(current.CompletedAt, Is.Not.Null);
                return;
            }

            await Task.Delay(200);
        }

        Assert.Fail("run não finalizou em 30s");
    }
}

/// <summary>
/// Backfill: bordas do <see cref="CheckpointService"/> não cobertas pelo
/// round-trip principal — DiffAsync, ListAsync (git e manifesto), revert
/// de hash inválido, prune do fallback e walk ignorando dirs gerados.
/// </summary>
[TestFixture, IsolateEnvironment]
public class CheckpointServiceBackfillTests
{
    private sealed class TestEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private static CheckpointService Service(
        Dictionary<string, string?>? config = null, string? root = null) =>
        new(new TestEnv(root ?? NewWorkdir()),
            new ConfigurationBuilder()
                .AddInMemoryCollection(config ?? new Dictionary<string, string?>())
                .Build(),
            NullLogger<CheckpointService>.Instance);

    private static string NewWorkdir()
    {
        var dir = Path.Join(Path.GetTempPath(), $"owui-cpbf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Git(string workdir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-c", "user.email=t@t", "-c", "user.name=t" })
        {
            psi.ArgumentList.Add(a);
        }
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
        Assert.That(p.ExitCode, Is.EqualTo(0), $"git {string.Join(' ', args)} falhou");
    }

    private static bool HasGit()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            p.WaitForExit(5_000);
            return p.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [Test]
    public async Task Snapshot_Desabilitado_OuWorkdirAusente_Null()
    {
        var off = Service(new Dictionary<string, string?>
        {
            ["Checkpoints:Enabled"] = "false",
        });
        var dir = NewWorkdir();
        try
        {
            Assert.That(await off.SnapshotAsync(dir, "r", 0, default), Is.Null);
            Assert.That(await Service().SnapshotAsync(
                Path.Join(dir, "nao-existe"), "r", 0, default), Is.Null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Git_DiffAsync_DevolveUnifiedDiff()
    {
        if (!HasGit()) Assert.Ignore("git indisponível.");
        var dir = NewWorkdir();
        try
        {
            Git(dir, "init");
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            Git(dir, "add", ".");
            Git(dir, "commit", "-m", "base");

            var svc = Service();
            var c0 = await svc.SnapshotAsync(dir, "r1", 0, default);
            Assert.That(c0, Is.Not.Null);

            File.WriteAllText(Path.Join(dir, "a.txt"), "v2\n");
            var diff = await svc.DiffAsync(dir, c0!.Hash, default);
            Assert.That(diff, Does.Contain("a.txt").And.Contain("-v1").And.Contain("+v2"));

            // Backend de manifesto → sem diff unificado.
            var plain = NewWorkdir();
            var svcPlain = Service();
            var m0 = await svcPlain.SnapshotAsync(plain, "r", 0, default);
            Assert.That(await svcPlain.DiffAsync(plain, m0!.Hash, default), Is.Null);
            Directory.Delete(plain, recursive: true);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Git_SnapshotSemMudanca_DedupeRetornaNull()
    {
        if (!HasGit()) Assert.Ignore("git indisponível.");
        var dir = NewWorkdir();
        try
        {
            Git(dir, "init");
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            Git(dir, "add", ".");
            Git(dir, "commit", "-m", "base");

            var svc = Service();
            var c0 = await svc.SnapshotAsync(dir, "r1", 0, default);
            Assert.That(c0, Is.Not.Null);

            // Tree idêntica ao tip → dedupe, nenhum checkpoint novo.
            var dup = await svc.SnapshotAsync(dir, "r1", 1, default);
            Assert.That(dup, Is.Null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Git_ListAsync_TurnosEDescricao()
    {
        if (!HasGit()) Assert.Ignore("git indisponível.");
        var dir = NewWorkdir();
        try
        {
            Git(dir, "init");
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            Git(dir, "add", ".");
            Git(dir, "commit", "-m", "base");

            var svc = Service();
            // Sem checkpoints ainda → lista vazia.
            Assert.That(await svc.ListAsync(dir, 10, default), Is.Empty);

            var c0 = await svc.SnapshotAsync(dir, "r1", 0, default);
            File.WriteAllText(Path.Join(dir, "a.txt"), "v2\n");
            var c1 = await svc.SnapshotAsync(dir, "r1", 1, default);

            var list = await svc.ListAsync(dir, 10, default);
            Assert.Multiple(() =>
            {
                Assert.That(list, Has.Count.EqualTo(2));
                Assert.That(list[0].Hash, Is.EqualTo(c1!.Hash));
                Assert.That(list[0].Turn, Is.EqualTo(1));
                Assert.That(list[1].Hash, Is.EqualTo(c0!.Hash));
                Assert.That(list[1].Turn, Is.EqualTo(0));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Git_Revert_HashInvalido_ResultadoVazio()
    {
        if (!HasGit()) Assert.Ignore("git indisponível.");
        var dir = NewWorkdir();
        try
        {
            Git(dir, "init");
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            Git(dir, "add", ".");
            Git(dir, "commit", "-m", "base");

            var svc = Service();
            var result = await svc.RevertAsync(
                dir, "0000000000000000000000000000000000000000", false, default);
            Assert.Multiple(() =>
            {
                Assert.That(result.Reverted, Is.Empty);
                Assert.That(result.Conflicts, Is.Empty);
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Fallback_PatchListRevertDrift()
    {
        var dir = NewWorkdir();
        var root = NewWorkdir();
        try
        {
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            var svc = Service(root: root);

            var m0 = await svc.SnapshotAsync(dir, "r", 0, default);
            Assert.That(m0, Is.Not.Null);

            File.WriteAllText(Path.Join(dir, "a.txt"), "v2\n");
            var m1 = await svc.SnapshotAsync(dir, "r", 1, default);

            // Patch: a.txt diverge do checkpoint base.
            var patch = await svc.PatchAsync(dir, m0!.Hash, default);
            Assert.That(patch, Does.Contain("a.txt"));

            // List (manifesto): mais novo primeiro.
            var list = await svc.ListAsync(dir, 10, default);
            Assert.That(list.Select(c => c.Hash),
                Is.EqualTo(new[] { m1!.Hash, m0.Hash }));

            // Drift fora da trilha → conflito; force → reverte.
            File.WriteAllText(Path.Join(dir, "a.txt"), "v3-drift\n");
            var drift = await svc.RevertAsync(dir, m0.Hash, force: false, default);
            Assert.That(drift.Conflicts, Does.Contain("a.txt"));
            var forced = await svc.RevertAsync(dir, m0.Hash, force: true, default);
            Assert.That(forced.Reverted, Does.Contain("a.txt"));
            Assert.That(File.ReadAllText(Path.Join(dir, "a.txt")), Is.EqualTo("v1\n"));

            // Hash inexistente → resultado vazio.
            var none = await svc.RevertAsync(dir, "m999999", false, default);
            Assert.That(none.Reverted, Is.Empty.And.Empty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Fallback_Prune_ExpiraSnapshotsAntigos()
    {
        var dir = NewWorkdir();
        var root = NewWorkdir();
        try
        {
            File.WriteAllText(Path.Join(dir, "a.txt"), "v1\n");
            var svc = Service(
                new Dictionary<string, string?> { ["Checkpoints:MaxAgeDays"] = "0" },
                root: root);

            var m0 = await svc.SnapshotAsync(dir, "r", 0, default);
            Assert.That(m0, Is.Not.Null);
            File.WriteAllText(Path.Join(dir, "a.txt"), "v2\n");
            await svc.SnapshotAsync(dir, "r", 1, default);

            // MaxAgeDays=0 → cutoff=agora. Os manifests guardam epoch em
            // segundos — espera >1s para garantir CreatedAt < cutoff.
            await Task.Delay(1100);
            await svc.PruneAsync(dir, default);

            var list = await svc.ListAsync(dir, 10, default);
            Assert.That(list, Is.Empty);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Fallback_Walk_IgnoraDirsGerados()
    {
        var dir = NewWorkdir();
        var root = NewWorkdir();
        try
        {
            Directory.CreateDirectory(Path.Join(dir, "node_modules"));
            File.WriteAllText(Path.Join(dir, "node_modules", "pkg.js"), "x\n");
            Directory.CreateDirectory(Path.Join(dir, "bin"));
            File.WriteAllText(Path.Join(dir, "bin", "a.dll"), "x\n");
            File.WriteAllText(Path.Join(dir, "real.txt"), "ok\n");

            var svc = Service(root: root);
            var m0 = await svc.SnapshotAsync(dir, "r", 0, default);
            Assert.That(m0, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(m0!.Files, Does.Contain("real.txt"));
                Assert.That(m0.Files, Does.Not.Contain("node_modules/pkg.js"));
                Assert.That(m0.Files, Does.Not.Contain("bin/a.dll"));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }
}

/// <summary>
/// Backfill: rotas <c>/api/v1/videos/generations</c> — prompt vazio,
/// feature desabilitada e upstream inalcançável (502).
/// </summary>
[TestFixture, IsolateEnvironment]
public class VideoEndpointsBackfillTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"owui-vidbf-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("Admin", "admin@vidbf.local");
        UseToken(_admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
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

    [Test]
    public async Task Generations_SemAuth_401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var r = await _client.PostAsJsonAsync("/api/v1/videos/generations",
            new VideoGenerationRequest("gato", 4, null));
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Generations_PromptVazio_400()
    {
        var user = await SignUpAsync("VidB", "vidb@vidbf.local");
        UseToken(user.Token);
        var r = await _client.PostAsJsonAsync("/api/v1/videos/generations",
            new VideoGenerationRequest("  ", 4, null));
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Generations_Desabilitada_501()
    {
        // Garante a config desabilitada independente da ordem dos testes.
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync("/api/v1/videos/config",
            VideoConfig.Default with { Enabled = false });

        var user = await SignUpAsync("VidC", "vidc@vidbf.local");
        UseToken(user.Token);
        var r = await _client.PostAsJsonAsync("/api/v1/videos/generations",
            new VideoGenerationRequest("ondas do mar", 4, null));
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.NotImplemented));
    }

    [Test]
    public async Task Generations_UpstreamInalcancavel_502()
    {
        // Admin aponta a engine para uma porta fechada → HttpRequestException.
        UseToken(_admin.Token);
        var cfg = await _client.PostAsJsonAsync("/api/v1/videos/config",
            VideoConfig.Default with
            {
                Enabled = true,
                BaseUrl = "http://127.0.0.1:1/v1",
                TimeoutSeconds = 5,
            });
        Assert.That(cfg.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await cfg.Content.ReadAsStringAsync());

        var user = await SignUpAsync("VidD", "vidd@vidbf.local");
        UseToken(user.Token);
        var r = await _client.PostAsJsonAsync("/api/v1/videos/generations",
            new VideoGenerationRequest("um cometa", 4, null));
        Assert.That((int)r.StatusCode,
            Is.AnyOf(502, 501, 504),
            $"esperava erro de gateway, veio {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");

        // Restaura desabilitada para não contaminar outros testes do fixture.
        UseToken(_admin.Token);
        await _client.PostAsJsonAsync("/api/v1/videos/config",
            VideoConfig.Default with { Enabled = false });
    }

    [Test]
    public async Task Config_TimeoutClampeado()
    {
        UseToken(_admin.Token);
        var r = await _client.PostAsJsonAsync("/api/v1/videos/config",
            VideoConfig.Default with { Enabled = false, TimeoutSeconds = 9999 });
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await r.Content.ReadFromJsonAsync<VideoConfig>();
        Assert.That(body!.TimeoutSeconds, Is.EqualTo(600));
    }
}
