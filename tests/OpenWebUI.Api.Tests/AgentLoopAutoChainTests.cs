using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261019-run-autonomy-continue RF-002/RF-004: run em modo
/// <c>build</c> + preset <c>always-allow</c> que bate o teto de steps
/// (<c>u:{id}:agentLoop.maxRounds</c>) com trabalho pendente cria uma run
/// filha (<c>ParentRunId</c> + <c>meta.autoChain {index,of}</c>) sem input
/// do usuário — limitada por <c>agentLoop.maxAutoChain</c>. Sem chain em
/// <c>autoChain=false</c>, preset≠always-allow ou modo≠build. O mock
/// (HttpListener) faz de Ollama: emite <c>builtin:todo_write</c>/
/// <c>builtin:file_read</c> até a última mensagem ser role=tool.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class AgentLoopAutoChainTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _mockBase = null!;
    private string _userId = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;

    /// <summary>Tool call emitido enquanto a última mensagem não é role=tool.</summary>
    private volatile string _emit = "todo_write";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-chain-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _mockBase = StartMock();

        var admin = await SignUpAsync("Admin", "admin@chain.local");
        _userId = admin.User.Id;
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());

        var connResponse = await _client.PostAsJsonAsync(
            "/api/v1/configs/connections", new ConnectionsConfig([_mockBase], [], []));
        Assert.That(connResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mockCts.Cancel();
        _mock.Stop();
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
        try
        {
            var dir = Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..",
                "..", "src", "OpenWebUI.Api", "data", "workspaces", _userId);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Limpeza best-effort — data/ é ignorada pelo git.
        }
    }

    private string StartMock()
    {
        var random = new Random();
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port = random.Next(40000, 60000);
            _mock = new HttpListener();
            _mock.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _mock.Start();
                break;
            }
            catch (HttpListenerException)
            {
                _mock.Close();
            }
        }
        if (!_mock.IsListening)
        {
            throw new InvalidOperationException("Nenhuma porta livre para o mock.");
        }
        _mockCts = new CancellationTokenSource();
        _ = Task.Run(() => MockLoopAsync(_mockCts.Token));
        return $"http://localhost:{port}";
    }

    private async Task MockLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _mock.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            string body = string.Empty;
            if (ctx.Request.HasEntityBody)
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                body = await reader.ReadToEndAsync(ct);
            }

            var path = ctx.Request.Url!.AbsolutePath;
            string json;
            if (path == "/api/tags")
            {
                json = "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}";
            }
            else if (path == "/api/chat")
            {
                // Emite tool_call enquanto houver tools anunciadas e a última
                // mensagem não for o resultado de uma tool — depois, texto.
                // No último round (lastStep) o pipeline remove as tools, o
                // que por si só já devolve texto → HitStepCap.
                var hasTools = body.Contains("\"tools\"", StringComparison.Ordinal);
                var lastRole = body.LastIndexOf("\"role\":\"", StringComparison.Ordinal);
                var lastIsTool = lastRole >= 0
                    && body.AsSpan(lastRole).StartsWith(
                        "\"role\":\"tool\"", StringComparison.Ordinal);
                json = hasTools && !lastIsTool
                    ? _emit == "file_read"
                        ? "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"id\":\"call-fr\",\"function\":{\"name\":\"builtin:file_read\",\"arguments\":{\"path\":\"x.txt\"}}}]}}"
                        : "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"id\":\"call-todo\",\"function\":{\"name\":\"builtin:todo_write\",\"arguments\":{\"todos\":[{\"content\":\"tarefa pendente\",\"status\":\"pending\"}]}}}]}}"
                    : "{\"message\":{\"role\":\"assistant\",\"content\":\"resposta final\"}}";
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
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

    /// <summary>Chat em modo build + preset informado (binding fake de repo).</summary>
    private async Task<ChatResponse> CriarChatBuildAsync(string preset = "always-allow")
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Chat chain", ["fake:1"], []));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;

        using (var scope = _factory.Services.CreateScope())
        {
            var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
            await config.SetAsync(
                $"chat:{chat.Id}:workspace.repo",
                new WorkspaceRepoBinding("a/b", "main", "repos/a__b", null, null),
                default);
        }
        var patched = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}", new ChatPatchRequest(preset, Mode: "build"));
        Assert.That(patched.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await patched.Content.ReadAsStringAsync());
        return (await patched.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    /// <summary>Define os overrides do loop do agente do usuário autenticado.</summary>
    private async Task SetAgentLoopAsync(int? maxRounds, bool? autoChain, int? maxAutoChain)
    {
        var put = await _client.PutAsJsonAsync("/api/v1/configs/user/agent-loop",
            new AgentLoopPrefs(maxRounds, autoChain, maxAutoChain));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await put.Content.ReadAsStringAsync());
    }

    private async Task<ChatRunResponse> EnfileirarAsync(string chatId, params string[] toolIds)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chatId}/messages",
            new EnqueueChatRunRequest("implemente tudo", "fake:1", ToolIds: toolIds));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChatRunResponse>())!;
    }

    /// <summary>Aguarda a run e toda a prole encadeada chegarem a estado terminal.</summary>
    private async Task<List<ChatRun>> AguardarRunsDoChatAsync(string chatId, int minimo)
    {
        var limite = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < limite)
        {
            List<ChatRun> runs;
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                runs = await db.ChatRuns.AsNoTracking()
                    .Where(r => r.ChatId == chatId)
                    .OrderBy(r => r.CreatedAt)
                    .ToListAsync();
            }
            if (runs.Count >= minimo
                && runs.All(r => r.Status is
                    ChatRunStatus.Completed or ChatRunStatus.Failed
                        or ChatRunStatus.Stopped or ChatRunStatus.Interrupted
                        or ChatRunStatus.Paused))
            {
                return runs;
            }
            await Task.Delay(200);
        }
        Assert.Fail($"Runs do chat {chatId} não estabilizaram em 90s.");
        return null!;
    }

    // ---- RF-002: chain dispara, herda e é limitada por maxAutoChain ----

    [Test]
    public async Task AutoChain_CapComPendencias_CriaFilhasAteOLimite()
    {
        _emit = "todo_write";
        await SetAgentLoopAsync(maxRounds: 2, autoChain: true, maxAutoChain: 2);
        var chat = await CriarChatBuildAsync("always-allow");

        var run = await EnfileirarAsync(chat.Id, "builtin:todo_write");
        var runs = await AguardarRunsDoChatAsync(chat.Id, 3);

        // Parent + 2 filhas (index 1 e 2); a chain para em maxAutoChain=2.
        Assert.That(runs, Has.Count.EqualTo(3));
        Assert.That(runs.All(r => r.Status == ChatRunStatus.Completed), Is.True);

        var filha1 = runs.Single(r => r.ParentRunId == run.Id);
        var filha2 = runs.Single(r => r.ParentRunId == filha1.Id);
        Assert.Multiple(() =>
        {
            Assert.That(filha1.MetaJson, Does.Contain("\"index\":1").And.Contain("\"of\":2"));
            Assert.That(filha2.MetaJson, Does.Contain("\"index\":2").And.Contain("\"of\":2"));
            // Prompt sintético vive no RequestJson da filha — nunca no chat.
            Assert.That(filha1.RequestJson,
                Does.Contain("limite de passos com trabalho ainda"));
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userMsgs = await db.ChatMessages.AsNoTracking()
                .CountAsync(m => m.ChatId == chat.Id && m.Role == "user");
            Assert.That(userMsgs, Is.EqualTo(1),
                "a continuação sintética não pode virar mensagem user do chat");
        }
    }

    [Test]
    public async Task AutoChain_Desligada_NaoEncadeia()
    {
        _emit = "todo_write";
        await SetAgentLoopAsync(maxRounds: 2, autoChain: false, maxAutoChain: 3);
        var chat = await CriarChatBuildAsync("always-allow");

        await EnfileirarAsync(chat.Id, "builtin:todo_write");
        var runs = await AguardarRunsDoChatAsync(chat.Id, 1);
        await Task.Delay(1500); // janela para uma filha indevida aparecer
        runs = await AguardarRunsDoChatAsync(chat.Id, 1);
        Assert.That(runs, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AutoChain_PresetOuModoErrados_NaoEncadeia()
    {
        _emit = "todo_write";
        await SetAgentLoopAsync(maxRounds: 2, autoChain: true, maxAutoChain: 3);

        // build mas preset ≠ always-allow → gate de aprovação não é o dono
        // declarando autonomia plena.
        var chatApprove = await CriarChatBuildAsync("approve-mutations");
        await EnfileirarAsync(chatApprove.Id, "builtin:todo_write");
        var runsA = await AguardarRunsDoChatAsync(chatApprove.Id, 1);
        await Task.Delay(1500);
        Assert.That(
            (await AguardarRunsDoChatAsync(chatApprove.Id, 1)), Has.Count.EqualTo(1));
        Assert.That(runsA[0].Status, Is.EqualTo(ChatRunStatus.Completed));

        // always-allow mas modo chat → chain é privilégio do modo build.
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Chat modo", ["fake:1"], []));
        var chatMode = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
        var patched = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chatMode.Id}", new ChatPatchRequest("always-allow"));
        Assert.That(patched.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await EnfileirarAsync(chatMode.Id, "builtin:todo_write");
        await AguardarRunsDoChatAsync(chatMode.Id, 1);
        await Task.Delay(1500);
        Assert.That(
            (await AguardarRunsDoChatAsync(chatMode.Id, 1)), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task AutoChain_SemTodoList_FallbackBuildEncadeia()
    {
        // PendingTodos == -1: o modelo nunca escreveu a lista — em build +
        // always-allow o encerramento por cap ainda assim continua (fallback
        // autônomo do SPEC RF-002).
        _emit = "file_read";
        await SetAgentLoopAsync(maxRounds: 2, autoChain: true, maxAutoChain: 1);
        var chat = await CriarChatBuildAsync("always-allow");

        var run = await EnfileirarAsync(chat.Id, "builtin:file_read");
        var runs = await AguardarRunsDoChatAsync(chat.Id, 2);

        Assert.That(runs, Has.Count.EqualTo(2));
        var filha = runs.Single(r => r.ParentRunId == run.Id);
        Assert.That(filha.MetaJson, Does.Contain("\"index\":1").And.Contain("\"of\":1"));
    }

    // ---- RF-004: endpoint de settings do usuário ----

    [Test]
    public async Task AgentLoopEndpoint_GetPutValidaELimpaOverride()
    {
        // Overrides de testes anteriores são do mesmo usuário — limpa antes
        // de conferir os defaults efetivos.
        await SetAgentLoopAsync(null, null, null);
        var inicial = await _client.GetFromJsonAsync<AgentLoopResponse>(
            "/api/v1/configs/user/agent-loop");
        Assert.Multiple(() =>
        {
            Assert.That(inicial!.MaxRounds, Is.EqualTo(99999));
            Assert.That(inicial.AutoChain, Is.True);
            Assert.That(inicial.MaxAutoChain, Is.EqualTo(3));
            Assert.That(inicial.User.MaxRounds, Is.Null);
        });

        // PUT persiste a camada do usuário; efetivo a reflete.
        var put = await _client.PutAsJsonAsync("/api/v1/configs/user/agent-loop",
            new AgentLoopPrefs(50, false, 5));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var depois = await put.Content.ReadFromJsonAsync<AgentLoopResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(depois!.MaxRounds, Is.EqualTo(50));
            Assert.That(depois.AutoChain, Is.False);
            Assert.That(depois.MaxAutoChain, Is.EqualTo(5));
            Assert.That(depois.User.MaxRounds, Is.EqualTo(50));
        });

        // Validação de faixa.
        var ruim = await _client.PutAsJsonAsync("/api/v1/configs/user/agent-loop",
            new AgentLoopPrefs(0, null, 99));
        Assert.That(ruim.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Null limpa o override → efetivo volta ao default global.
        var limpa = await _client.PutAsJsonAsync("/api/v1/configs/user/agent-loop",
            new AgentLoopPrefs(null, null, null));
        Assert.That(limpa.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var herdado = await limpa.Content.ReadFromJsonAsync<AgentLoopResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(herdado!.MaxRounds, Is.EqualTo(99999));
            Assert.That(herdado.User.MaxRounds, Is.Null);
        });
    }
}
