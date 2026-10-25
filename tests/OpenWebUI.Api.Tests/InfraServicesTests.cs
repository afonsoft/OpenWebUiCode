using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes de integração dos serviços de infraestrutura: providers SSE (Ollama/OpenAI), embeddings+RAG, schedule, JWT, OAuth e tools.</summary>
[TestFixture, IsolateEnvironment]
public class InfraServicesTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _mockBaseUrl = null!;
    private volatile string _lastOllamaChatBody = string.Empty;
    private volatile string _lastEmbedPath = string.Empty;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-infra-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@infra.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);

        // Um único mock atende Ollama (/api/*) e OpenAI (/models, /chat/completions, /embeddings).
        _mockBaseUrl = StartMock();
        var connections = new ConnectionsConfig([_mockBaseUrl], [_mockBaseUrl], ["sk-mock"]);
        var connResponse = await _client.PostAsJsonAsync("/api/v1/configs/connections", connections);
        Assert.That(connResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
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

            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var (status, contentType, payload) = Route(ctx.Request.Url!.AbsolutePath, body);
            var bytes = Encoding.UTF8.GetBytes(payload);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    /// <summary>Rotas do mock: Ollama (tags/chat NDJSON/embed), OpenAI (models/chat SSE/embeddings) e endpoints de tool.</summary>
    private (int Status, string ContentType, string Payload) Route(string path, string body)
    {
        const string Json = "application/json";
        var streaming = body.Contains("\"stream\":true") || body.Contains("\"stream\": true");
        switch (path)
        {
            // Ollama
            case "/api/tags":
                return (200, Json, "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}");
            case "/api/chat":
                _lastOllamaChatBody = body;
                if (streaming)
                {
                    var ndjson =
                        "{\"message\":{\"role\":\"assistant\",\"content\":\"resposta\"},\"done\":false}\n" +
                        "{\"message\":{\"role\":\"assistant\",\"content\":\" do mock\"},\"done\":false}\n" +
                        "{\"done\":true}\n";
                    return (200, "application/x-ndjson", ndjson);
                }
                return (200, Json, "{\"message\":{\"content\":\"resposta do mock\"},\"done\":true}");
            case "/api/embed":
                _lastEmbedPath = path;
                return (200, Json, "{\"embeddings\":[[0.9,0.1,0.05]]}");

            // OpenAI
            case "/models":
                return (200, Json, "{\"data\":[{\"id\":\"gpt-fake\",\"owned_by\":\"mock-openai\"}]}");
            case "/chat/completions":
                if (streaming)
                {
                    var sse =
                        "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"resposta\"}}]}\n\n" +
                        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\" openai\"}}]}\n\n" +
                        "data: [DONE]\n\n";
                    return (200, "text/event-stream", sse);
                }
                return (200, Json,
                    "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"resposta openai\"}}]}");
            case "/embeddings":
                _lastEmbedPath = path;
                return (200, Json, "{\"data\":[{\"embedding\":[0.2,0.8,0.3]}]}");

            // Endpoints de tool
            case "/tool-ok":
                return (200, Json, "resultado da tool");
            case "/tool-big":
                return (200, Json, new string('x', 4500));
            case "/tool-fail":
                return (500, Json, "{}");
            default:
                return (404, Json, "{}");
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
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<string> PostChatCompletionsAsync(ChatCompletionRequest request)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat/completions")
        {
            Content = JsonContent.Create(request),
        };
        var response = await _client.SendAsync(httpRequest);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await response.Content.ReadAsStringAsync();
    }

    private async Task<string> UploadTextFileAsync(string name, string content)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", name);
        var response = await _client.PostAsync("/api/v1/files/", form);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private async Task<bool> IndexFileViaKnowledgeAsync(string fileId, string collectionName)
    {
        var created = await _client.PostAsJsonAsync("/api/v1/knowledge",
            new CreateKnowledgeRequest(collectionName, null));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var collection = (await created.Content.ReadFromJsonAsync<KnowledgeResponse>())!;

        var added = await _client.PostAsJsonAsync(
            $"/api/v1/knowledge/{collection.Id}/files", new AddKnowledgeFileRequest(fileId));
        Assert.That(added.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var json = JsonDocument.Parse(await added.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("indexed").GetBoolean();
    }

    [Test]
    public async Task Models_OllamaEOpenAI_AgregaOsDoisProvedores()
    {
        UseToken(_adminToken);

        var result = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            var ollama = result!.Data.FirstOrDefault(m => m.Id == "fake:1");
            Assert.That(ollama, Is.Not.Null);
            Assert.That(ollama!.Provider, Is.EqualTo("ollama"));

            var openAi = result.Data.FirstOrDefault(m => m.Id == "gpt-fake");
            Assert.That(openAi, Is.Not.Null);
            Assert.That(openAi!.Provider, Is.EqualTo("openai"));
            Assert.That(openAi.OwnedBy, Is.EqualTo("mock-openai"));
        });
    }

    [Test]
    public async Task ChatCompletions_Ollama_StreamNDJSON_ViraSSE()
    {
        var user = await SignUpAsync("Ollama", "ollama@infra.local", "senha123");
        UseToken(user.Token);

        var sse = await PostChatCompletionsAsync(new ChatCompletionRequest(
            "fake:1",
            [new ChatCompletionMessage("user", "oi")],
            Stream: true));

        // ProviderService converte NDJSON do Ollama em chunks SSE formato OpenAI.
        Assert.Multiple(() =>
        {
            Assert.That(sse, Does.Contain("chat.completion.chunk"));
            Assert.That(sse, Does.Contain("resposta"));
            Assert.That(sse, Does.Contain("data: [DONE]"));
        });
        // O mock recebeu o payload com stream:true.
        Assert.That(_lastOllamaChatBody, Does.Contain("\"stream\":true"));
    }

    [Test]
    public async Task ChatCompletions_OpenAI_StreamSSE_EncaminhaLinhas()
    {
        var user = await SignUpAsync("OpenAi", "openai@infra.local", "senha123");
        UseToken(user.Token);

        // "gpt-fake" não existe no Ollama → resolução cai no provider OpenAI.
        var sse = await PostChatCompletionsAsync(new ChatCompletionRequest(
            "gpt-fake",
            [new ChatCompletionMessage("user", "oi")],
            Stream: true));

        Assert.Multiple(() =>
        {
            Assert.That(sse, Does.Contain("data: {"));
            Assert.That(sse, Does.Contain(" openai"));
            Assert.That(sse, Does.Contain("data: [DONE]"));
        });
    }

    [Test]
    public async Task ChatCompletions_ConexaoInexistente_RetornaErroSSE()
    {
        var user = await SignUpAsync("BadConn", "badconn@infra.local", "senha123");
        UseToken(user.Token);

        var sse = await PostChatCompletionsAsync(new ChatCompletionRequest(
            "fake:1",
            [new ChatCompletionMessage("user", "oi")],
            Stream: true,
            Connection: "inexistente"));

        Assert.Multiple(() =>
        {
            Assert.That(sse, Does.Contain("\"error\""));
            // A mensagem vai JSON-escapada no SSE, então asserção usa trecho ASCII.
            Assert.That(sse, Does.Contain("configurada atende ao modelo"));
            Assert.That(sse, Does.Contain("data: [DONE]"));
        });
    }

    [Test]
    public async Task Knowledge_UploadIndexa_ComEmbeddingOllama_EChatInjetaContextoRag()
    {
        var user = await SignUpAsync("Rag", "rag@infra.local", "senha123");
        UseToken(user.Token);

        var fileId = await UploadTextFileAsync("manual-rag.txt",
            "O manual diz que a senha mestra é batata-frita.");
        var indexed = await IndexFileViaKnowledgeAsync(fileId, "manuais-rag");

        Assert.Multiple(() =>
        {
            Assert.That(indexed, Is.True);
            Assert.That(_lastEmbedPath, Is.EqualTo("/api/embed"));
        });

        // Chat com FileIds → RetrieveAsync embeds a query e SearchAsync injeta o chunk.
        var sse = await PostChatCompletionsAsync(new ChatCompletionRequest(
            "fake:1",
            [new ChatCompletionMessage("user", "qual a senha mestra?")],
            Stream: true,
            FileIds: [fileId]));
        Assert.That(sse, Does.Contain("data: [DONE]"));

        // attachments-v2 RF-001: FileIds anexados viram <file> inline na
        // mensagem do usuário (RAG segue só para referências "#nome").
        Assert.Multiple(() =>
        {
            Assert.That(_lastOllamaChatBody, Does.Contain("file name="));
            Assert.That(_lastOllamaChatBody, Does.Contain("manual-rag.txt"));
            Assert.That(_lastOllamaChatBody, Does.Contain("batata-frita"));
        });
    }

    [Test]
    public async Task Embedding_SemOllama_UsaRotaEmbeddingsOpenAI()
    {
        UseToken(_adminToken);
        var restore = new ConnectionsConfig([_mockBaseUrl], [_mockBaseUrl], ["sk-mock"]);
        try
        {
            // Sem URL Ollama: EmbedAsync salta direto para o loop OpenAI (/embeddings).
            var openAiOnly = new ConnectionsConfig([], [_mockBaseUrl], ["sk-mock"]);
            var connResponse = await _client.PostAsJsonAsync("/api/v1/configs/connections", openAiOnly);
            Assert.That(connResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var user = await SignUpAsync("Emb", "emb@infra.local", "senha123");
            UseToken(user.Token);
            var fileId = await UploadTextFileAsync("doc-openai.txt", "conteúdo indexado via openai");
            var indexed = await IndexFileViaKnowledgeAsync(fileId, "docs-openai");

            Assert.Multiple(() =>
            {
                Assert.That(indexed, Is.True);
                Assert.That(_lastEmbedPath, Is.EqualTo("/embeddings"));
            });
        }
        finally
        {
            UseToken(_adminToken);
            await _client.PostAsJsonAsync("/api/v1/configs/connections", restore);
        }
    }

    [Test]
    public void AutomationSchedule_Interval_SomaMinutosEClampMinimo()
    {
        var now = new DateTimeOffset(2026, 6, 17, 10, 0, 0, TimeSpan.Zero);
        var automation = new Automation { ScheduleKind = "interval", IntervalMinutes = 15 };

        var next = AutomationSchedule.ComputeNextRun(automation, now);

        Assert.That(next, Is.EqualTo(now.AddMinutes(15).ToUnixTimeSeconds()));

        // Intervalo abaixo de 1 minuto é clampado para 1.
        var clamped = AutomationSchedule.ComputeNextRun(
            new Automation { ScheduleKind = "interval", IntervalMinutes = 0 }, now);
        Assert.That(clamped, Is.EqualTo(now.AddMinutes(1).ToUnixTimeSeconds()));
    }

    [Test]
    public void AutomationSchedule_Daily_BordasDeHorarioUtc()
    {
        var now = new DateTimeOffset(2026, 6, 17, 10, 0, 0, TimeSpan.Zero); // quarta-feira

        // Horário ainda por vir hoje.
        var future = AutomationSchedule.ComputeNextRun(
            new Automation { ScheduleKind = "daily", TimeOfDay = "15:30" }, now);
        Assert.That(future, Is.EqualTo(
            new DateTimeOffset(2026, 6, 17, 15, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds()));

        // Horário já passado → amanhã.
        var past = AutomationSchedule.ComputeNextRun(
            new Automation { ScheduleKind = "daily", TimeOfDay = "09:00" }, now);
        Assert.That(past, Is.EqualTo(
            new DateTimeOffset(2026, 6, 18, 9, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()));

        // Exatamente agora (candidate <= now) → amanhã.
        var exact = AutomationSchedule.ComputeNextRun(
            new Automation { ScheduleKind = "daily", TimeOfDay = "10:00" }, now);
        Assert.That(exact, Is.EqualTo(
            new DateTimeOffset(2026, 6, 18, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()));

        // Horário inválido ou ausente → null.
        Assert.That(AutomationSchedule.ComputeNextRun(
            new Automation { ScheduleKind = "daily", TimeOfDay = "lixo" }, now), Is.Null);
        Assert.That(AutomationSchedule.ComputeNextRun(
            new Automation { ScheduleKind = "daily" }, now), Is.Null);
    }

    [Test]
    public void AutomationSchedule_Weekly_DiaDaSemanaEBordasUtc()
    {
        var now = new DateTimeOffset(2026, 6, 17, 10, 0, 0, TimeSpan.Zero); // quarta (3)

        // Mesmo dia da semana, horário futuro → hoje.
        var sameDay = AutomationSchedule.ComputeNextRun(
            new Automation { ScheduleKind = "weekly", Weekday = 3, TimeOfDay = "15:00" }, now);
        Assert.That(sameDay, Is.EqualTo(
            new DateTimeOffset(2026, 6, 17, 15, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()));

        // Mesmo dia, horário já passado → +7 dias.
        var nextWeek = AutomationSchedule.ComputeNextRun(
            new Automation { ScheduleKind = "weekly", Weekday = 3, TimeOfDay = "09:00" }, now);
        Assert.That(nextWeek, Is.EqualTo(
            new DateTimeOffset(2026, 6, 24, 9, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()));

        // Outro dia da semana: sexta (5) 08:00 → daqui a 2 dias.
        var friday = AutomationSchedule.ComputeNextRun(
            new Automation { ScheduleKind = "weekly", Weekday = 5, TimeOfDay = "08:00" }, now);
        Assert.That(friday, Is.EqualTo(
            new DateTimeOffset(2026, 6, 19, 8, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()));

        // Weekday nulo cai no default 0 (domingo); fora do range é clampado.
        var sunday = AutomationSchedule.ComputeNextRun(
            new Automation { ScheduleKind = "weekly", Weekday = null, TimeOfDay = "12:00" }, now);
        Assert.That(DateTimeOffset.FromUnixTimeSeconds(sunday!.Value).DayOfWeek,
            Is.EqualTo(DayOfWeek.Sunday));

        // Kind desconhecido → null.
        Assert.That(AutomationSchedule.ComputeNextRun(
            new Automation { ScheduleKind = "cron" }, now), Is.Null);
    }

    [Test]
    public async Task ConfigService_JwtSecret_IdempotenteEPersistido()
    {
        string first;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
            first = await config.GetOrCreateJwtSecretAsync();
            var second = await config.GetOrCreateJwtSecretAsync();
            Assert.That(second, Is.EqualTo(first));
        }

        Assert.That(first, Is.Not.Null.And.Not.Empty);
        Assert.That(Convert.FromBase64String(first), Has.Length.EqualTo(64));

        // Novo escopo lê o mesmo valor já persistido no banco.
        await using var scope2 = _factory.Services.CreateAsyncScope();
        var config2 = scope2.ServiceProvider.GetRequiredService<ConfigService>();
        var third = await config2.GetOrCreateJwtSecretAsync();
        Assert.That(third, Is.EqualTo(first));
    }

    [Test]
    public async Task OAuthCatalog_ProvidersRegistrados_ViaEnv()
    {
        var original = new Dictionary<string, string?>
        {
            ["GOOGLE_CLIENT_ID"] = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID"),
            ["GOOGLE_CLIENT_SECRET"] = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET"),
            ["GITHUB_CLIENT_ID"] = Environment.GetEnvironmentVariable("GITHUB_CLIENT_ID"),
            ["GITHUB_CLIENT_SECRET"] = Environment.GetEnvironmentVariable("GITHUB_CLIENT_SECRET"),
        };
        try
        {
            Environment.SetEnvironmentVariable("GOOGLE_CLIENT_ID", "gid-mock");
            Environment.SetEnvironmentVariable("GOOGLE_CLIENT_SECRET", "gsecret-mock");
            Environment.SetEnvironmentVariable("GITHUB_CLIENT_ID", "ghid-mock");
            Environment.SetEnvironmentVariable("GITHUB_CLIENT_SECRET", "ghsecret-mock");

            var google = OAuthProviderCatalog.Resolve("google");
            Assert.Multiple(() =>
            {
                Assert.That(google, Is.Not.Null);
                Assert.That(google!.AuthorizeUrl, Does.Contain("accounts.google.com"));
                Assert.That(google.Scope, Does.Contain("openid"));
            });

            var configured = OAuthProviderCatalog.ConfiguredProviders();
            Assert.Multiple(() =>
            {
                Assert.That(configured, Does.Contain("google"));
                Assert.That(configured, Does.Contain("github"));
            });

            Assert.That(OAuthProviderCatalog.Resolve("inexistente"), Is.Null);

            // O catálogo aparece na configuração pública da API.
            var resp = await _client.GetAsync("/api/config");
            if (!resp.IsSuccessStatusCode)
            {
                Assert.Fail($"GET /api/config -> {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
            }
            var appConfig = await resp.Content.ReadFromJsonAsync<AppConfigResponse>();
            Assert.That(appConfig!.OAuthProviders, Does.Contain("google"));
        }
        finally
        {
            foreach (var (key, value) in original)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    [Test]
    public async Task ToolExecutor_LoadEnabled_ECaminhosDeErro()
    {
        var user = await SignUpAsync("Tools", "tools@infra.local", "senha123");
        UseToken(user.Token);

        // Tool persistida via endpoint para exercitar LoadEnabledAsync.
        var spec = "{\"type\":\"function\",\"function\":{\"name\":\"fn_ok\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}}";
        var created = await _client.PostAsJsonAsync("/api/v1/tools/",
            new ToolUpsertRequest("Ok", null, spec, $"{_mockBaseUrl}/tool-ok"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var tool = (await created.Content.ReadFromJsonAsync<ToolResponse>())!;

        await using var scope = _factory.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<ToolExecutor>();

        var loaded = await executor.LoadEnabledAsync(user.User.Id, [tool.Id]);
        Assert.That(loaded, Has.Count.EqualTo(1));
        Assert.That(await executor.LoadEnabledAsync(user.User.Id, []), Is.Empty);

        static Tool FakeTool(string name, string url) => new()
        {
            SpecJson = $"{{\"function\":{{\"name\":\"{name}\"}}}}",
            Url = url,
            Enabled = true,
        };
        var tools = new List<Tool>
        {
            FakeTool("fn_ok", $"{_mockBaseUrl}/tool-ok"),
            FakeTool("fn_fail", $"{_mockBaseUrl}/tool-fail"),
            FakeTool("fn_big", $"{_mockBaseUrl}/tool-big"),
            FakeTool("fn_down", "http://localhost:1/tool"),
        };

        var ok = (await executor.ExecuteAsync(tools, "fn_ok", "{}")).Text;
        Assert.That(ok, Is.EqualTo("resultado da tool"));

        var fail = (await executor.ExecuteAsync(tools, "fn_fail", "{}")).Text;
        Assert.That(fail, Does.Contain("respondeu 500"));

        // Saída acima de 4000 chars é truncada.
        var big = (await executor.ExecuteAsync(tools, "fn_big", "{}")).Text;
        Assert.That(big, Has.Length.EqualTo(4000));

        // URL inacessível → mensagem de erro para o modelo (não lança).
        var down = (await executor.ExecuteAsync(tools, "fn_down", "{}")).Text;
        Assert.That(down, Does.Contain("Erro ao executar tool 'fn_down'"));

        var missing = (await executor.ExecuteAsync(tools, "nao_existe", "{}")).Text;
        Assert.That(missing, Does.Contain("não está habilitada"));
    }
}
