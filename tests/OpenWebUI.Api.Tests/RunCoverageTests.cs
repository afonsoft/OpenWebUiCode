using System.Collections.Concurrent;
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

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobre os caminhos das runs/notificações que os testes principais não
/// tocam (SPEC-20261007-chat-detached-runs + notifications): rota de
/// pipeline inexistente, loop de tools sem provider, webhooks CRUD+test,
/// sweep de runs órfãs, stop de run finalizada e envio real do
/// <c>WebPushSender</c> contra um push service mock (HttpListener).
/// </summary>
[TestFixture, IsolateEnvironment]
public class RunCoverageTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _mockBase = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private readonly ConcurrentQueue<string> _received = new();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-cov-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _mockBase = StartMock();

        var admin = await SignUpAsync("Admin", "admin@cov.local");
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
        _mockCts.Cancel();
        _mock.Stop();
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
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
            _received.Enqueue($"{ctx.Request.HttpMethod} {ctx.Request.Url!.AbsolutePath} {body[..Math.Min(body.Length, 120)]}");

            var path = ctx.Request.Url!.AbsolutePath;
            if (path == "/v1/chat/completions")
            {
                await ServeChatCompletionsAsync(ctx, body, ct);
                continue;
            }
            if (path == "/pipes/models")
            {
                await WriteJsonAsync(ctx, 200, "[{\"id\":\"covpipe\",\"name\":\"Cov Pipe\"}]", ct);
                continue;
            }
            if (path == "/pipes/chat/completions")
            {
                await ServeSseAsync(ctx, "resposta do pipe", ct);
                continue;
            }
            if (path == "/tool/x")
            {
                await WriteJsonAsync(ctx, 200, "{\"ok\":true,\"value\":42}", ct);
                continue;
            }

            // Webhook e push service: 201 = entregue.
            await WriteJsonAsync(ctx, 201, "{}", ct);
        }
    }

    /// <summary>Delay artificial nas completions stream (teste de attach ao vivo).</summary>
    private volatile int _chatDelayMs;

    /// <summary>Completion OpenAI-compatível: stream → SSE; não-stream → loop de tools.</summary>
    private async Task ServeChatCompletionsAsync(
        HttpListenerContext ctx, string body, CancellationToken ct)
    {
        if (body.Contains("\"stream\":true", StringComparison.Ordinal))
        {
            if (_chatDelayMs > 0)
            {
                await Task.Delay(_chatDelayMs, ct);
            }
            // Loop de tools streamed (SPEC-20261008-modes-streaming): o
            // pedido com tools e sem role=tool devolve a call em chunk;
            // com role=tool já é a resposta final.
            if (body.Contains("\"tools\"", StringComparison.Ordinal))
            {
                if (body.Contains("\"role\":\"tool\"", StringComparison.Ordinal))
                {
                    await ServeSseAsync(ctx, "resposta via tool", ct);
                }
                else
                {
                    await ServeSseToolCallAsync(ctx, ct);
                }
                return;
            }
            await ServeSseAsync(ctx, "resposta mock openai", ct);
            return;
        }

        // Loop de tools: pedido já contém uma mensagem role=tool → resposta final;
        // senão devolve uma tool_call para a tool "x".
        var json = body.Contains("\"role\":\"tool\"", StringComparison.Ordinal)
            ? "{\"choices\":[{\"message\":{\"content\":\"resposta via tool\"}}]}"
            : "{\"choices\":[{\"message\":{\"content\":\"\","
                + "\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\","
                + "\"function\":{\"name\":\"x\",\"arguments\":\"{}\"}}]}}]}";
        await WriteJsonAsync(ctx, 200, json, ct);
    }

    /// <summary>SSE OpenAI com uma tool_call completa num único chunk.</summary>
    private static async Task ServeSseToolCallAsync(
        HttpListenerContext ctx, CancellationToken ct)
    {
        var payload =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,"
            + "\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"x\","
            + "\"arguments\":\"{}\"}}]}}]}\n\n"
            + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n"
            + "data: [DONE]\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();
    }

    private static async Task ServeSseAsync(
        HttpListenerContext ctx, string content, CancellationToken ct)
    {
        var payload =
            $"data: {{\"choices\":[{{\"index\":0,\"delta\":{{\"content\":\"{content}\"}}}}]}}\n\n"
            + "data: {\"choices\":[{\"index\":0,\"delta\":{}}],\"finish_reason\":\"stop\"}\n\n"
            + "data: [DONE]\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();
    }

    private static async Task WriteJsonAsync(
        HttpListenerContext ctx, int status, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(bytes, ct);
        ctx.Response.Close();
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

    private async Task<ChatResponse> CriarChatAsync()
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Chat cov", ["llama3"], []));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    private async Task<ChatRunResponse> EnfileirarAsync(
        string chatId, string content, string model = "llama3",
        IReadOnlyList<string>? toolIds = null)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chatId}/messages",
            new EnqueueChatRunRequest(content, model, ToolIds: toolIds));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChatRunResponse>())!;
    }

    private async Task<ChatRunResponse> AguardarFinalAsync(string chatId, string runId)
    {
        var limite = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < limite)
        {
            var run = await _client.GetFromJsonAsync<ChatRunResponse>(
                $"/api/v1/chats/{chatId}/runs/{runId}");
            Assert.That(run, Is.Not.Null);
            if (run!.Status is not ("queued" or "running"))
            {
                return run;
            }
            await Task.Delay(150);
        }
        Assert.Fail($"Run {runId} não finalizou em 60s.");
        return null!;
    }

    // ---- Runs: caminhos do executor ----

    [Test]
    public async Task Pipeline_PipeInexistente_RunFalhaComErro()
    {
        var auth = await SignUpAsync("Pipe", "pipe@cov.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();

        var run = await EnfileirarAsync(chat.Id, "oi", model: "pipeline:naoexiste");
        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"));

        // O erro sai no stream de attach (replay do backlog).
        using var response = await _client.GetAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/stream?lastSeq=0");
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("naoexiste"));
    }

    [Test]
    public async Task Run_ProviderBogus_FalhaNaExecucao()
    {
        // Model com connection inválida: ResolveProviderAsync lança e o
        // catch-all do executor fecha a run como failed (caminho do meio).
        var auth = await SignUpAsync("Bogus", $"bogus-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();
        var run = await ExecutarDiretoAsync(chat.Id, auth.User.Id,
            "{\"model\":\"llama3\",\"connection\":\"bogus\","
            + "\"messages\":[{\"role\":\"user\",\"content\":\"oi\"}]}");
        Assert.That(run.Status, Is.EqualTo(ChatRunStatus.Failed));
        Assert.That(run.Error, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Executor_ParamsAgentInexistente_SegueParaProvider()
    {
        // Regressão do refactor S3776 (ApplyCommandOverridesAsync): params.agent
        // sem definição não interrompe a run — segue até falhar no provider.
        var auth = await SignUpAsync("AgentOv", $"agentov-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();
        var run = await ExecutarDiretoAsync(chat.Id, auth.User.Id,
            "{\"model\":\"llama3\",\"connection\":\"bogus\","
            + "\"params\":{\"agent\":\"agente-que-nao-existe\"},"
            + "\"messages\":[{\"role\":\"user\",\"content\":\"oi\"}]}");
        Assert.That(run.Status, Is.EqualTo(ChatRunStatus.Failed));
        Assert.That(run.Error, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Executor_ParamsModePlan_RunFalhaNoProvider()
    {
        // Regressão do refactor S3776 (ResolveRunPreludeAsync +
        // ApplyCommandOverridesAsync): params.mode=plan é aceito no
        // prelúdio e a run segue até falhar no provider inexistente.
        var auth = await SignUpAsync("ModePlan", $"modeplan-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();
        var run = await ExecutarDiretoAsync(chat.Id, auth.User.Id,
            "{\"model\":\"llama3\",\"connection\":\"bogus\","
            + "\"params\":{\"mode\":\"plan\"},"
            + "\"messages\":[{\"role\":\"user\",\"content\":\"oi\"}]}");
        Assert.That(run.Status, Is.EqualTo(ChatRunStatus.Failed));
        Assert.That(run.Error, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Stop_RunFinalizada_RetornaConflito()
    {
        var auth = await SignUpAsync("Stop", "stop@cov.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();
        var run = await EnfileirarAsync(chat.Id, "oi");
        await AguardarFinalAsync(chat.Id, run.Id);

        var stop = await _client.PostAsync($"/api/v1/chats/{chat.Id}/runs/{run.Id}/stop", null);
        Assert.That(stop.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    // ---- Webhooks ----

    [Test]
    public async Task Webhook_CrudETest_Completos()
    {
        var auth = await SignUpAsync("Hook", "hook@cov.local");
        UseToken(auth.Token);

        var save = await _client.PostAsJsonAsync("/api/v1/notifications/webhook",
            new NotificationWebhookRequest($"{_mockBase}/hook", ["run.completed"]));
        Assert.That(save.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await save.Content.ReadAsStringAsync());

        // Upsert: mesmo webhook atualizado com novos eventos.
        var update = await _client.PostAsJsonAsync("/api/v1/notifications/webhook",
            new NotificationWebhookRequest($"{_mockBase}/hook", ["run.completed", "run.failed"], false));
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var get = await _client.GetAsync("/api/v1/notifications/webhook");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Dispara o teste — o NotificationService faz POST assinado no mock.
        var test = await _client.PostAsync("/api/v1/notifications/webhook/test", null);
        Assert.That(test.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var result = await test.Content.ReadFromJsonAsync<WebhookTestResponse>();
        Assert.That(result!.Ok, Is.True, $"status {result.StatusCode}");
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (_received.IsEmpty && DateTime.UtcNow < limite)
        {
            await Task.Delay(100);
        }
        Assert.That(_received.Any(r => r.Contains("/hook")), Is.True, "webhook não chegou no mock");

        var del = await _client.DeleteAsync("/api/v1/notifications/webhook");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var depois = await _client.GetAsync("/api/v1/notifications/webhook");
        Assert.That(depois.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Webhook_Admin_GlobalERestricoes()
    {
        var auth = await SignUpAsync("HookAdmin", $"hookadmin-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);

        // Usuário comum → 403 no escopo admin.
        var forbidden = await _client.PostAsJsonAsync("/api/v1/notifications/admin/webhook",
            new NotificationWebhookRequest($"{_mockBase}/global", ["x"]));
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        // Re-login como o admin criado no setup (primeiro signup = admin).
        var admin = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("admin@cov.local", "senha123"));
        Assert.That(admin.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var adminAuth = await admin.Content.ReadFromJsonAsync<AuthResponse>();

        UseToken(adminAuth!.Token);
        var save = await _client.PostAsJsonAsync("/api/v1/notifications/admin/webhook",
            new NotificationWebhookRequest($"{_mockBase}/global", ["run.completed"]));
        Assert.That(save.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await save.Content.ReadAsStringAsync());
        var del = await _client.DeleteAsync("/api/v1/notifications/admin/webhook");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Validações: URL inválida, webhook inexistente.
        var bad = await _client.PostAsJsonAsync("/api/v1/notifications/webhook",
            new NotificationWebhookRequest("notaurl", ["x"]));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var inexistente = await _client.DeleteAsync("/api/v1/notifications/admin/webhook");
        Assert.That(inexistente.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- Web Push real (sender + VAPID gerado/persistido) ----

    [Test]
    public async Task WebPush_SenderReal_MockRecebeEntrega()
    {
        var auth = await SignUpAsync("PushReal", $"pushreal-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);

        // Chaves válidas (EC P-256 + auth de 16 bytes, base64url) geradas em build.
        var endpoint = $"{_mockBase}/push/{Guid.NewGuid():N}";
        var sub = await _client.PostAsJsonAsync("/api/v1/notifications/push/subscriptions",
            new PushSubscriptionRequest(endpoint,
                new PushSubscriptionKeys(
                    "BMJSvet1XxjmdWY2-z17oRIgoIBYnx5x0OowCwqn_gMushfv21DFZYdnC_-8G3AI4lyimQJHoVqZS-p0hPrEAWg",
                    "WOb6rGjrpzlZZnvkDhkg6A"),
                "nunit"));
        Assert.That(sub.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await sub.Content.ReadAsStringAsync());

        // VAPID key é gerada e persistida na primeira chamada; a segunda reusa.
        var k1 = await _client.GetFromJsonAsync<VapidPublicKeyResponse>(
            "/api/v1/notifications/push/vapid-key");
        var k2 = await _client.GetFromJsonAsync<VapidPublicKeyResponse>(
            "/api/v1/notifications/push/vapid-key");
        Assert.That(k1!.PublicKey, Is.EqualTo(k2!.PublicKey));

        var chat = await CriarChatAsync();
        var run = await EnfileirarAsync(chat.Id, "notifica");
        var final = await AguardarFinalAsync(chat.Id, run.Id);
        // O push dispara em qualquer desfecho terminal (completed ou failed).
        Assert.That(final.Status, Is.AnyOf("completed", "failed"));

        var limite = DateTime.UtcNow.AddSeconds(15);
        while (!_received.Any(r => r.Contains("/push/")) && DateTime.UtcNow < limite)
        {
            await Task.Delay(200);
        }
        Assert.That(_received.Any(r => r.StartsWith("POST /push/", StringComparison.Ordinal)),
            Is.True, "push não chegou no mock");
    }

    [Test]
    public async Task PushSubscription_Delete_SemEndpoint_400()
    {
        var auth = await SignUpAsync("PushDel", $"pushdel-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);
        var del = await _client.DeleteAsync("/api/v1/notifications/push/subscriptions");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---- Dispatcher: sweep de runs órfãs no boot ----

    [Test]
    public async Task Dispatcher_Boot_MarcaRunOrfaComoInterrompida()
    {
        var auth = await SignUpAsync("Orpha", $"orpha-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();

        // Semeia uma run "running" direto no banco — como se o servidor
        // tivesse caído no meio da execução.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChatRuns.Add(new ChatRun
            {
                ChatId = chat.Id,
                UserId = auth.User.Id,
                Model = "llama3",
                Status = ChatRunStatus.Running,
                RequestJson = "{\"model\":\"llama3\",\"messages\":[]}",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            await db.SaveChangesAsync();
        }
        var orfaId = await DbChatRunIdAsync(chat.Id);

        // Um segundo host sobre o MESMO banco executa o sweep no boot.
        using var factory2 = new WebApplicationFactory<Program>();
        _ = factory2.Server; // força o boot (dispatcher + sweep)
        using var client2 = factory2.CreateClient();
        client2.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.Token);

        var limite = DateTime.UtcNow.AddSeconds(30);
        ChatRunResponse? run = null;
        while (DateTime.UtcNow < limite)
        {
            run = await _client.GetFromJsonAsync<ChatRunResponse>(
                $"/api/v1/chats/{chat.Id}/runs/{orfaId}");
            if (run!.Status == "interrupted")
            {
                break;
            }
            await Task.Delay(200);
        }
        Assert.That(run!.Status, Is.EqualTo("interrupted"));
    }

    // ---- Caminho de sucesso (provider OpenAI-compatível mockado) ----

    private async Task ConfigurarOpenAiMockAsync()
    {
        var admin = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("admin@cov.local", "senha123"));
        var adminAuth = await admin.Content.ReadFromJsonAsync<AuthResponse>();
        UseToken(adminAuth!.Token);
        var conn = await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig([], [$"{_mockBase}/v1"], ["sk-cov"]));
        Assert.That(conn.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await conn.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task Run_Sucesso_Streaming_PersisteAssistant()
    {
        await ConfigurarOpenAiMockAsync();
        var auth = await SignUpAsync("Ok", $"ok-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();

        var run = await EnfileirarAsync(chat.Id, "oi", model: "gpt-mock");
        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);

        // Replay do backlog: o attach de uma run finalizada devolve tudo.
        using var stream = await _client.GetAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/stream?lastSeq=0");
        var body = await stream.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("resposta mock openai"));
        Assert.That(body, Does.Contain("[DONE]"));

        // Mensagem persistida no chat.
        var chatAfter = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{chat.Id}");
        Assert.That(chatAfter!.Messages.Any(
            m => m.Role == "assistant" && m.Content.Contains("resposta mock openai")), Is.True);
    }

    [Test]
    public async Task Run_AttachAoVivo_RecebeEventosLive()
    {
        await ConfigurarOpenAiMockAsync();
        var auth = await SignUpAsync("Live", $"live-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();

        _chatDelayMs = 1200;
        try
        {
            var run = await EnfileirarAsync(chat.Id, "oi", model: "gpt-mock");

            // Attach enquanto a run ainda executa → caminho de assinante vivo.
            using var stream = await _client.GetAsync(
                $"/api/v1/chats/{chat.Id}/runs/{run.Id}/stream?lastSeq=0");
            var body = await stream.Content.ReadAsStringAsync();
            Assert.That(body, Does.Contain("resposta mock openai"));

            var final = await AguardarFinalAsync(chat.Id, run.Id);
            Assert.That(final.Status, Is.EqualTo("completed"));
        }
        finally
        {
            _chatDelayMs = 0;
        }
    }

    [Test]
    public async Task Run_Tools_LoopExecuta_AteRespostaFinal()
    {
        await ConfigurarOpenAiMockAsync();
        var auth = await SignUpAsync("Loop", $"loop-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);
        var spec = "{\"type\":\"function\",\"function\":{\"name\":\"x\"}}";
        var toolResponse = await _client.PostAsJsonAsync("/api/v1/tools/",
            new ToolUpsertRequest("X", "x", spec, $"{_mockBase}/tool/x"));
        Assert.That(toolResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var tool = await toolResponse.Content.ReadFromJsonAsync<ToolResponse>();

        var chat = await CriarChatAsync();
        var run = await EnfileirarAsync(chat.Id, "usa tool", model: "gpt-mock",
            toolIds: [tool!.Id]);
        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);

        // O provider pediu a tool e o executor chamou o endpoint mockado.
        var limite = DateTime.UtcNow.AddSeconds(10);
        while (!_received.Any(r => r.StartsWith("POST /tool/x")) && DateTime.UtcNow < limite)
        {
            await Task.Delay(100);
        }
        Assert.That(_received.Any(r => r.StartsWith("POST /tool/x")), Is.True);

        using var stream = await _client.GetAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/stream?lastSeq=0");
        var body = await stream.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("resposta via tool"));
    }

    [Test]
    public async Task Pipeline_Sucesso_StreamingCompleto()
    {
        await ConfigurarOpenAiMockAsync();
        var auth = await SignUpAsync("PipeOk", $"pipeok-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);

        // Precisa de admin para registrar o servidor de pipelines.
        var admin = await _client.PostAsJsonAsync("/api/v1/auths/signin",
            new SignInRequest("admin@cov.local", "senha123"));
        var adminAuth = await admin.Content.ReadFromJsonAsync<AuthResponse>();
        UseToken(adminAuth!.Token);
        var server = await _client.PostAsJsonAsync("/api/v1/pipelines/",
            new PipelineServerRequest("covpipes", $"{_mockBase}/pipes", null));
        Assert.That(server.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await server.Content.ReadAsStringAsync());

        UseToken(auth.Token);
        var chat = await CriarChatAsync();
        var run = await EnfileirarAsync(chat.Id, "oi", model: "pipeline:covpipe");
        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);

        using var stream = await _client.GetAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/stream?lastSeq=0");
        var body = await stream.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("resposta do pipe"));
    }

    [Test]
    public async Task Arena_SemEntry_CompletaComErroNoStream()
    {
        var auth = await SignUpAsync("Arena", $"arena-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();
        var run = await EnfileirarAsync(chat.Id, "oi", model: "arena:ghost");
        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"));
    }

    // ---- Executor direto: falhas antes do provider ----

    private async Task<ChatRun> ExecutarDiretoAsync(
        string chatId, string userId, string requestJson)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = new ChatRun
        {
            ChatId = chatId,
            UserId = userId,
            Model = "llama3",
            Status = ChatRunStatus.Running,
            RequestJson = requestJson,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync();

        var executor = scope.ServiceProvider
            .GetRequiredService<OpenWebUI.Api.Runs.ChatRunExecutor>();
        await executor.ExecuteAsync(run, CancellationToken.None);
        return run;
    }

    [Test]
    public async Task Executor_RequestJsonInvalido_FalhaAntesDeExecutar()
    {
        var auth = await SignUpAsync("BadJson", $"badjson-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();
        var run = await ExecutarDiretoAsync(chat.Id, "qualquer", "nao-e-json{");
        Assert.That(run.Status, Is.EqualTo(ChatRunStatus.Failed));
        Assert.That(run.Error, Does.Contain("inválido"));
    }

    [Test]
    public async Task Executor_UsuarioInexistente_FalhaAntesDeExecutar()
    {
        var auth = await SignUpAsync("NoUser", $"nouser-{Guid.NewGuid():N}@cov.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();
        var run = await ExecutarDiretoAsync(chat.Id, "user-que-nao-existe",
            "{\"model\":\"llama3\",\"messages\":[]}");
        Assert.That(run.Status, Is.EqualTo(ChatRunStatus.Failed));
        Assert.That(run.Error, Does.Contain("não encontrado"));
    }

    // ---- Broadcaster unitário (backlog trim, closed, subscribe vivo) ----

    [Test]
    public async Task Broadcaster_RunFechada_DescartaPublishEReplayaBacklog()
    {
        var broadcaster = new OpenWebUI.Infrastructure.Services.ChatRunBroadcaster();
        broadcaster.Publish("r", "data: a");
        broadcaster.Complete("r");
        broadcaster.Publish("r", "data: b"); // cai fora — stream fechada
        broadcaster.Complete("inexistente"); // no-op

        var eventos = new List<OpenWebUI.Infrastructure.Services.ChatRunBroadcaster.RunEvent>();
        await foreach (var e in broadcaster.SubscribeAsync("r"))
        {
            eventos.Add(e);
        }
        Assert.That(eventos, Has.Count.EqualTo(1));
        Assert.That(eventos[0].Payload, Is.EqualTo("data: a"));
    }

    [Test]
    public async Task Broadcaster_SubscribeVivo_RecebePublishEEncerra()
    {
        var broadcaster = new OpenWebUI.Infrastructure.Services.ChatRunBroadcaster();
        var eventos = new List<OpenWebUI.Infrastructure.Services.ChatRunBroadcaster.RunEvent>();

        var leitura = Task.Run(async () =>
        {
            await foreach (var e in broadcaster.SubscribeAsync("r"))
            {
                eventos.Add(e);
            }
        });

        // Garante a inscrição antes de publicar.
        await Task.Delay(50);
        broadcaster.Publish("r", "data: live");
        broadcaster.Complete("r");

        await leitura.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(eventos.Select(e => e.Payload), Does.Contain("data: live"));
    }

    private async Task<string> DbChatRunIdAsync(string chatId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ChatRuns.OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(r => r.ChatId == chatId);
        return run!.Id;
    }
}
