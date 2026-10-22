using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Terminal;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do terminal PTY embutido (SPEC-20261007-chat-agent-tools RF-005):
/// <see cref="PtySession"/> real via <c>script</c>, manager
/// (criação/listagem/isolamento/cap/attach com scrollback) e endpoints
/// <c>/api/v1/terminal/*</c> + <c>/ws/terminal/{id}</c> com a feature flag.
/// </summary>
public class TerminalPtyTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;
    private AuthResponse _user = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-term-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("TermAdmin", "admin@term.local", "senha123");
        UseToken(_admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        var enabled = await _client.PutAsJsonAsync(
            "/api/v1/terminal/config", new { enabled = true });
        Assert.That(enabled.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        _user = await SignUpAsync("TermUser", "user@term.local", "senha123");
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

    private async Task<AuthResponse> SignUpAsync(string name, string email, string password)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, password));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    // ---------------- PtySession (real `script` PTY) ----------------

    [Test]
    public async Task PtySession_SpawnShell_EcoaInputESai()
    {
        if (!File.Exists("/usr/bin/script"))
        {
            Assert.Ignore("binário 'script' indisponível neste host");
        }

        var dir = Path.Join(Path.GetTempPath(), $"pty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var session = new PtySession(
            dir, NullLogger<PtySession>.Instance, cols: 80, rows: 24);
        session.OutputReceived += chunk => output.Append(chunk);
        session.Exited += code => exited.TrySetResult(code);
        session.Start();

        Assert.That(session.IsRunning, Is.True);
        var marker = $"mk{Guid.NewGuid():N}"[..12];
        await session.WriteAsync($"echo {marker}\n");
        await SpinWaitAsync(() => output.ToString().Contains(marker), 10_000);

        await session.ResizeAsync(100, 40); // ioctl best-effort — não pode falhar
        await session.WriteAsync("exit\n");
        var code = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(output.ToString(), Does.Contain(marker));
        Assert.That(code, Is.EqualTo(0));
        await session.DisposeAsync();
    }

    [Test]
    public async Task PtySession_ShellPadrao_ExportaEnvDeCor()
    {
        if (!File.Exists("/usr/bin/script"))
        {
            Assert.Ignore("binário 'script' indisponível neste host");
        }

        var dir = Path.Join(Path.GetTempPath(), $"pty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var session = new PtySession(
            dir, NullLogger<PtySession>.Instance, cols: 80, rows: 24);
        session.OutputReceived += chunk => output.Append(chunk);
        session.Exited += code => exited.TrySetResult(code);
        session.Start();

        // SPEC terminal-color RF-002/003: TERM+COLORTERM sempre, HOME
        // garantido e LS_COLORS presente (default não-destrutivo).
        await session.WriteAsync(
            "echo \"E=$TERM|$COLORTERM|${LS_COLORS:+yes}|${HOME:+yes}\"\n");
        await SpinWaitAsync(
            () => output.ToString().Contains("E=xterm-256color|truecolor|yes|yes"), 10_000);

        await session.WriteAsync("exit\n");
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.DisposeAsync();
    }

    [Test]
    public async Task PtySession_ShellPadrao_AliasLsComCor()
    {
        if (!File.Exists("/usr/bin/script"))
        {
            Assert.Ignore("binário 'script' indisponível neste host");
        }

        var dir = Path.Join(Path.GetTempPath(), $"pty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var session = new PtySession(
            dir, NullLogger<PtySession>.Instance, cols: 80, rows: 24);
        session.OutputReceived += chunk => output.Append(chunk);
        session.Exited += code => exited.TrySetResult(code);
        session.Start();

        // O shim via PROMPT_COMMAND roda antes do 1º prompt — quando o
        // input chega, o alias já está definido (RF-003).
        await session.WriteAsync("alias ls\n");
        await SpinWaitAsync(() => output.ToString().Contains("--color=auto"), 10_000);

        await session.WriteAsync("exit\n");
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.DisposeAsync();
    }

    [Test]
    public async Task PtySession_ComandoCustom_SemShimDeCor()
    {
        if (!File.Exists("/usr/bin/script"))
        {
            Assert.Ignore("binário 'script' indisponível neste host");
        }

        var dir = Path.Join(Path.GetTempPath(), $"pty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        // argv custom: TERM/COLORTERM/HOME são universais, mas o shim de
        // cor (aliases/__owui) fica restrito ao shell default (RF-003).
        var session = new PtySession(
            dir, NullLogger<PtySession>.Instance, cols: 80, rows: 24,
            command: ["env"]);
        session.OutputReceived += chunk => output.Append(chunk);
        session.Exited += code => exited.TrySetResult(code);
        session.Start();

        await SpinWaitAsync(
            () => output.ToString().Contains("COLORTERM=truecolor"), 10_000);
        Assert.That(output.ToString(), Does.Not.Contain("__owui"));

        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.DisposeAsync();
    }

    // ---------------- TerminalSessionManager ----------------

    private sealed class TestEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } =
            Path.Join(Path.GetTempPath(), $"owui-term-root-{Guid.NewGuid():N}");
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private TerminalSessionManager NewManager(
        Func<string, int, int, IPtySession>? factory = null,
        TimeSpan? idleTimeout = null) =>
        new(new TestEnv(), NullLoggerFactory.Instance,
            NullLogger<TerminalSessionManager>.Instance,
            factory, idleTimeout ?? TimeSpan.FromHours(1));

    [Test]
    public async Task Manager_CriaListaAnexaEMata()
    {
        if (!File.Exists("/usr/bin/script"))
        {
            Assert.Ignore("binário 'script' indisponível neste host");
        }

        await using var manager = NewManager();
        var id = manager.Create("u-term");

        var sessions = manager.List("u-term");
        Assert.That(sessions, Has.Count.EqualTo(1));
        Assert.That(sessions[0].IsRunning, Is.True);

        var output = new StringBuilder();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handle = manager.Attach(
            "u-term", id,
            chunk => { output.Append(chunk); return Task.CompletedTask; },
            code => { exited.TrySetResult(code); return Task.CompletedTask; });
        Assert.That(handle, Is.Not.Null);
        Assert.That(handle!.Scrollback, Is.Not.Null); // pode estar vazio — spawn async

        var marker = $"mk{Guid.NewGuid():N}"[..12];
        await handle.WriteAsync($"echo {marker}\n");
        await SpinWaitAsync(() => output.ToString().Contains(marker), 10_000);

        // Attach tardio → scrollback replay traz o marker.
        using var late = manager.Attach(
            "u-term", id, _ => Task.CompletedTask, _ => Task.CompletedTask);
        Assert.That(late!.Scrollback, Does.Contain(marker));

        // Isolamento: outro usuário não anexa nem mata.
        Assert.That(manager.Attach("outro", id, _ => Task.CompletedTask, _ => Task.CompletedTask),
            Is.Null);
        Assert.That(await manager.KillAsync("outro", id), Is.False);

        Assert.That(await manager.KillAsync("u-term", id), Is.True);
        Assert.That(await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)), Is.GreaterThanOrEqualTo(-1));
        Assert.That(manager.List("u-term"), Is.Empty);
    }

    [Test]
    public async Task Manager_RespeitaCapPorUsuario()
    {
        Func<string, int, int, IPtySession> fakeFactory = (dir, c, r) =>
            new FakePtySession();

        await using var manager = NewManager(fakeFactory);
        for (var i = 0; i < TerminalSessionManager.MaxSessionsPerUser; i++)
        {
            manager.Create("u-cap");
        }

        Assert.That(() => manager.Create("u-cap"), Throws.InvalidOperationException);
        Assert.That(manager.List("u-cap"), Has.Count.EqualTo(TerminalSessionManager.MaxSessionsPerUser));
        manager.Create("u-outro"); // cap é por usuário
        Assert.That(manager.List("u-outro"), Has.Count.EqualTo(1));
    }

    // ---- SPEC-20261022: binding de sessão a chat/run ----

    [Test]
    public async Task Manager_Binding_ChatId_Filtra_E_KillForChat_Mata_So_O_Dono()
    {
        Func<string, int, int, IPtySession> fakeFactory = (dir, c, r) =>
            new FakePtySession();
        await using var manager = NewManager(fakeFactory);

        var bound = manager.Create(
            "u-bind", workdir: "/tmp/wt-chat1", chatId: "chat-1");
        var bound2 = manager.Create(
            "u-bind", workdir: "/tmp/wt-chat1b", chatId: "chat-1");
        var free = manager.Create("u-bind");
        var otherChat = manager.Create(
            "u-bind", workdir: "/tmp/wt-chat2", chatId: "chat-2");

        // Filtro por chat + metadados de binding na listagem.
        var only = manager.List("u-bind", chatId: "chat-1");
        Assert.Multiple(() =>
        {
            Assert.That(only.Select(s => s.Id),
                Is.EquivalentTo(new[] { bound, bound2 }));
            Assert.That(only[0].Workdir, Is.EqualTo("/tmp/wt-chat1"));
            Assert.That(manager.List("u-bind"), Has.Count.EqualTo(4));
        });

        // KillForChat remove só as do chat — livres e de outros chats ficam.
        var killed = await manager.KillForChatAsync("u-bind", "chat-1");
        Assert.Multiple(() =>
        {
            Assert.That(killed, Is.EqualTo(2));
            Assert.That(manager.List("u-bind").Select(s => s.Id),
                Is.EquivalentTo(new[] { free, otherChat }));
        });
    }

    [Test]
    public async Task Terminal_Create_Com_Workdir_Fora_De_Data_400()
    {
        UseToken(_user.Token);
        var res = await _client.PostAsJsonAsync("/api/v1/terminal/sessions",
            new { workdir = "/etc" });
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Terminal_Create_Bindada_Expoe_ChatId_Na_Listagem()
    {
        UseToken(_user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/terminal/sessions",
            new { chatId = "chat-inexistente-ok" });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created),
            await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();

        var list = await _client.GetFromJsonAsync<JsonElement>(
            "/api/v1/terminal/sessions?chatId=chat-inexistente-ok");
        var sessions = list.EnumerateArray().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(sessions.Any(
                s => s.GetProperty("id").GetString() == id), Is.True);
            Assert.That(sessions[0].GetProperty("chatId").GetString(),
                Is.EqualTo("chat-inexistente-ok"));
        });

        await _client.DeleteAsync($"/api/v1/terminal/sessions/{id}");
    }

    private sealed class FakePtySession : IPtySession
    {
        public event Action<string>? OutputReceived;
        public event Action<int>? Exited;
        public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;
        public bool IsRunning { get; private set; } = true;
        public void Start() { }
        public Task WriteAsync(string data)
        {
            LastActivityUtc = DateTimeOffset.UtcNow;
            OutputReceived?.Invoke(data);
            return Task.CompletedTask;
        }
        public Task ResizeAsync(int cols, int rows) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            Exited?.Invoke(0);
            return ValueTask.CompletedTask;
        }
    }

    // ---------------- Endpoints ----------------

    [Test]
    public async Task Terminal_SemAuth_401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        Assert.That((await _client.GetAsync("/api/v1/terminal/config")).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await _client.PostAsync("/api/v1/terminal/sessions", null)).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Terminal_Config_RequerAdmin()
    {
        UseToken(_user.Token);
        var put = await _client.PutAsJsonAsync("/api/v1/terminal/config", new { enabled = false });
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Terminal_FlagOff_RecusaSessao()
    {
        UseToken(_admin.Token);
        var off = await _client.PutAsJsonAsync("/api/v1/terminal/config", new { enabled = false });
        Assert.That(off.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        UseToken(_user.Token);
        var post = await _client.PostAsync("/api/v1/terminal/sessions", null);
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_admin.Token);
        var on = await _client.PutAsJsonAsync("/api/v1/terminal/config", new { enabled = true });
        Assert.That(on.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Terminal_SessaoCriaListaEMata()
    {
        if (!File.Exists("/usr/bin/script"))
        {
            Assert.Ignore("binário 'script' indisponível neste host");
        }

        UseToken(_user.Token);
        var created = await _client.PostAsJsonAsync(
            "/api/v1/terminal/sessions", new { cols = 80, rows = 24 });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetString()!;

        var list = await _client.GetFromJsonAsync<List<JsonElement>>("/api/v1/terminal/sessions");
        Assert.That(list!.Any(s => s.GetProperty("id").GetString() == id), Is.True);
        Assert.That(list!.First(s => s.GetProperty("id").GetString() == id)
            .GetProperty("isRunning").GetBoolean(), Is.True);

        var del = await _client.DeleteAsync($"/api/v1/terminal/sessions/{id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await _client.DeleteAsync($"/api/v1/terminal/sessions/{id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Terminal_IsolamentoEntreUsuarios()
    {
        if (!File.Exists("/usr/bin/script"))
        {
            Assert.Ignore("binário 'script' indisponível neste host");
        }

        UseToken(_user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/terminal/sessions", new { });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        var outro = await SignUpAsync("TermOutro", "outro@term.local", "senha123");
        UseToken(outro.Token);
        Assert.That((await _client.DeleteAsync($"/api/v1/terminal/sessions/{id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
        var list = await _client.GetFromJsonAsync<List<JsonElement>>("/api/v1/terminal/sessions");
        Assert.That(list!.Any(s => s.GetProperty("id").GetString() == id), Is.False);

        UseToken(_user.Token);
        await _client.DeleteAsync($"/api/v1/terminal/sessions/{id}");
    }

    [Test]
    public async Task Terminal_Ws_EcoaInput()
    {
        if (!File.Exists("/usr/bin/script"))
        {
            Assert.Ignore("binário 'script' indisponível neste host");
        }

        UseToken(_user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/terminal/sessions", new { });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        var wsClient = _factory.Server.CreateWebSocketClient();
        var uri = new Uri(
            $"http://localhost/ws/terminal/{id}?access_token={_user.Token}");
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        var marker = $"mk{Guid.NewGuid():N}"[..12];
        await ws.SendAsync(
            Encoding.UTF8.GetBytes($"{{\"type\":\"input\",\"data\":\"echo {marker}\\n\"}}"),
            WebSocketMessageType.Text, true, CancellationToken.None);

        var received = new StringBuilder();
        var buffer = new byte[8192];
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!received.ToString().Contains(marker) && DateTime.UtcNow < deadline)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            var frame = Encoding.UTF8.GetString(buffer, 0, result.Count);
            try
            {
                using var doc = JsonDocument.Parse(frame);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    received.Append(data.GetString());
                }
            }
            catch (JsonException)
            {
                received.Append(frame);
            }
        }

        Assert.That(received.ToString(), Does.Contain(marker));
        try
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (IOException)
        {
            // Remote já fechou — TestWebSocket descarta a saída.
        }
        catch (WebSocketException)
        {
            // WebSocket já encerrado — teardown best-effort.
        }

        UseToken(_user.Token);
        await _client.DeleteAsync($"/api/v1/terminal/sessions/{id}");
    }

    /// <summary>SPEC-20261022 RF-A2: uma conexão WS multiplexa N sessões —
    /// <c>sessions</c> inicial, <c>create</c>+attach automático, input
    /// roteado por <c>sid</c>.</summary>
    [Test]
    public async Task Terminal_Multiplex_Uma_Conexao_N_Sessoes()
    {
        if (!File.Exists("/usr/bin/script"))
        {
            Assert.Ignore("binário 'script' indisponível neste host");
        }

        UseToken(_user.Token);
        var wsClient = _factory.Server.CreateWebSocketClient();
        var uri = new Uri(
            $"http://localhost/ws/terminal?access_token={_user.Token}");
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        var buffer = new byte[8192];
        async Task<JsonDocument> NextAsync()
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            return JsonDocument.Parse(
                Encoding.UTF8.GetString(buffer, 0, result.Count));
        }

        // 1) Frame inicial lista as sessões do usuário.
        using var hello = await NextAsync();
        Assert.That(hello.RootElement.GetProperty("type").GetString(),
            Is.EqualTo("sessions"));

        // 2) create + attach automático → {type:"created", sid}.
        var marker = $"mux{Guid.NewGuid():N}"[..12];
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(
                """{"type":"create","cols":80,"rows":24}"""),
            WebSocketMessageType.Text, true, CancellationToken.None);

        string? sid = null;
        var echoed = new StringBuilder();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var sentInput = false;
        while (DateTime.UtcNow < deadline
               && (sid is null || !echoed.ToString().Contains(marker)))
        {
            using var frame = await NextAsync();
            var root = frame.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "created")
            {
                sid = root.GetProperty("sid").GetString()!;
            }
            else if (type == "output" && sid is not null
                     && root.GetProperty("sid").GetString() == sid)
            {
                echoed.Append(root.GetProperty("data").GetString());
            }
            if (sid is not null && !sentInput)
            {
                sentInput = true;
                await ws.SendAsync(
                    Encoding.UTF8.GetBytes(
                        $$"""{"type":"input","sid":"{{sid}}","data":"echo {{marker}}\n"}"""),
                    WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(sid, Is.Not.Null);
            Assert.That(echoed.ToString(), Does.Contain(marker));
        });

        if (sid is not null)
        {
            await ws.SendAsync(
                Encoding.UTF8.GetBytes(
                    $$"""{"type":"kill","sid":"{{sid}}"}"""),
                WebSocketMessageType.Text, true, CancellationToken.None);
        }
        try
        {
            await ws.CloseAsync(
                WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (IOException) { }
        catch (WebSocketException) { }
    }

    [Test]
    public async Task Terminal_Multiplex_FramesRoteados_E_Hostis()
    {
        if (!File.Exists("/usr/bin/script"))
        {
            Assert.Ignore("binário 'script' indisponível neste host");
        }

        UseToken(_user.Token);
        var created = await _client.PostAsJsonAsync(
            "/api/v1/terminal/sessions", new { });
        var sid = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        var wsClient = _factory.Server.CreateWebSocketClient();
        var uri = new Uri(
            $"http://localhost/ws/terminal?access_token={_user.Token}");
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        var buffer = new byte[8192];
        // Receive com timeout real — sem token o teste trava se o servidor
        // ficar mudo (o deadline externo só é checado entre receives).
        async Task<string?> NextTypeAsync(int timeoutMs = 4000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                var result = await ws.ReceiveAsync(
                    buffer, cts.Token);
                using var doc = JsonDocument.Parse(
                    Encoding.UTF8.GetString(buffer, 0, result.Count));
                return doc.RootElement.TryGetProperty("type", out var t)
                    ? t.GetString() : null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        // Hello inicial.
        Assert.That(await NextTypeAsync(), Is.EqualTo("sessions"));

        // attach a sid inexistente → frame "error".
        await ws.SendAsync(Encoding.UTF8.GetBytes(
                """{"type":"attach","sid":"nao-existe"}"""),
            WebSocketMessageType.Text, true, CancellationToken.None);
        var gotError = false;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!gotError && DateTime.UtcNow < deadline)
        {
            var t = await NextTypeAsync();
            if (t is null)
            {
                break;
            }
            gotError = t == "error";
        }
        Assert.That(gotError, Is.True, "attach a sid inválido não respondeu error");

        // attach real + detach + kill + frames ignorados/hostis — nada
        // deve quebrar a conexão (input em sid desanexado, resize com
        // tipos errados, tipo desconhecido, sid não-string).
        var marker = $"mx{Guid.NewGuid():N}"[..10];
        var frames = new[]
        {
            $$"""{"type":"attach","sid":"{{sid}}"}""",
            $$"""{"type":"input","sid":"{{sid}}","data":"echo {{marker}}\n"}""",
            $$"""{"type":"resize","sid":"{{sid}}","cols":"x"}""",
            $$"""{"type":"detach","sid":"{{sid}}"}""",
            $$"""{"type":"input","sid":"{{sid}}","data":"echo never\n"}""",
            $$"""{"type":"input","sid":"{{sid}}"}""",
            $$"""{"type":"input","sid":123,"data":"x"}""",
            """{"type":"attach"}""",
            """{"type":"desconhecido"}""",
            $$"""{"type":"kill","sid":"{{sid}}"}""",
        };
        foreach (var f in frames)
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(f),
                WebSocketMessageType.Text, true, CancellationToken.None);
            await Task.Delay(60);
        }

        // Drena frames de saída (output/exit) com timeout por receive.
        var drained = 0;
        while (drained < 12 && await NextTypeAsync(3000) is not null)
        {
            drained++;
        }
        Assert.That(drained, Is.GreaterThanOrEqualTo(1),
            "mux não emitiu nenhum frame após attach/input/kill");
    }

    private static async Task SpinWaitAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.That(condition(), Is.True, "condição não atingida dentro do timeout");
    }

    [Test]
    public async Task Terminal_Ws_FramesControle_Incompletos_NaoQuebram()
    {
        if (!File.Exists("/usr/bin/script"))
        {
            Assert.Ignore("binário 'script' indisponível neste host");
        }

        UseToken(_user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/terminal/sessions", new { });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        var wsClient = _factory.Server.CreateWebSocketClient();
        var uri = new Uri(
            $"http://localhost/ws/terminal/{id}?access_token={_user.Token}");
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        // Frames de controle cobrindo os ternários de cols/rows e o
        // fallback do JsonException — nenhum deve derrubar a sessão.
        var frames = new[]
        {
            "{\"type\":\"resize\"}",
            "{\"type\":\"resize\",\"cols\":\"x\",\"rows\":200}",
            "{\"type\":\"resize\",\"cols\":90,\"rows\":40}",
            "{\"type\":\"input\"}",
            "{\"type\":\"input\",\"data\":123}",
            "{\"type\":123}",
            "{\"type\":\"desconhecido\",\"x\":1}",
            "{isto-parecia-json-mas-nao-e",
        };
        foreach (var f in frames)
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(f),
                WebSocketMessageType.Text, true, CancellationToken.None);
        }

        // Sessão segue viva: input normal ainda ecoa.
        var marker = $"ok{Guid.NewGuid():N}"[..10];
        await ws.SendAsync(
            Encoding.UTF8.GetBytes(
                $"{{\"type\":\"input\",\"data\":\"echo {marker}\\n\"}}"),
            WebSocketMessageType.Text, true, CancellationToken.None);

        var received = new StringBuilder();
        var buffer = new byte[8192];
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!received.ToString().Contains(marker)
               && DateTime.UtcNow < deadline)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            received.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        }
        Assert.That(received.ToString(), Does.Contain(marker));

        try
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done",
                CancellationToken.None);
        }
        catch (IOException) { }
        catch (WebSocketException) { }

        UseToken(_user.Token);
        await _client.DeleteAsync($"/api/v1/terminal/sessions/{id}");
    }
}
