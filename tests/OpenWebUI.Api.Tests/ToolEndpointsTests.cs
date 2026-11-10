using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes das tools (function calling via HTTP): CRUD, validação de spec,
/// seleção por chat e o loop de tool calling no endpoint de completions.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ToolEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _dbPath = null!;
    private string _mockBaseUrl = null!;
    private int _toolExecutions;

    private const string Spec =
        "{\"type\":\"function\",\"function\":{\"name\":\"minha_tool\"," +
        "\"description\":\"retorna 42\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}}";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _mockBaseUrl = StartMock();
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-tools-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("OPENAI_API_BASE_URL", _mockBaseUrl);
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@tools.local", "senha123");
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
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

    /// <summary>Sobe um provedor OpenAI + endpoint de tool mockados na porta livre.</summary>
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

            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var (status, json) = Route(ctx.Request.Url!.AbsolutePath, body);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    /// <summary>
    /// Rotas do mock: /models vazio; /chat/completions pede tool_call na 1ª
    /// chamada e devolve o resultado da tool na resposta final; /tool conta a execução.
    /// </summary>
    private (int, string) Route(string path, string body)
    {
        switch (path)
        {
            case "/models":
                return (200, "{\"data\":[]}");
            case "/tool":
                Interlocked.Increment(ref _toolExecutions);
                return (200, "\"42\"");
            case "/chat/completions":
                if (body.Contains("\"role\":\"tool\""))
                {
                    return (200,
                        "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\"," +
                        "\"content\":\"O resultado da tool é 42\"}}]}");
                }
                return (200,
                    "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":null," +
                    "\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":" +
                    "{\"name\":\"minha_tool\",\"arguments\":\"{}\"}}]}}]}");
            default:
                return (404, "{}");
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email, string password)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, password));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<ToolResponse> CreateToolAsync(string? url = null)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/tools/",
            new ToolUpsertRequest("Calculadora", "soma", Spec,
                url ?? $"{_mockBaseUrl}/tool"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ToolResponse>())!;
    }

    [Test]
    public async Task Crud_CriaListaAtualizaEDeleta()
    {
        var user = await SignUpAsync("User", "user@tools.local", "senha123");
        UseToken(user.Token);

        var created = await CreateToolAsync();
        Assert.That(created.Name, Is.EqualTo("Calculadora"));

        var list = await _client.GetFromJsonAsync<List<ToolResponse>>("/api/v1/tools/");
        Assert.Multiple(() =>
        {
            // A lista inclui a tool criada e as tools built-in do catálogo (SPEC-20261007-chat-agent-tools).
            // Não conta não-builtin == 1: o banco do fixture é compartilhado entre os testes da classe.
            Assert.That(list!.Any(t => t.Id == created.Id && t.Source != "builtin"), Is.True);
            Assert.That(list!.Count(t => t.Source == "builtin"), Is.GreaterThan(0));
        });

        var updated = await _client.PutAsJsonAsync($"/api/v1/tools/{created.Id}",
            new ToolUpsertRequest("Calc", null, Spec, null, null, false));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var tool = (await updated.Content.ReadFromJsonAsync<ToolResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(tool.Enabled, Is.False);
            Assert.That(tool.Name, Is.EqualTo("Calc"));
        });

        var deleted = await _client.DeleteAsync($"/api/v1/tools/{created.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var after = await _client.GetFromJsonAsync<List<ToolResponse>>("/api/v1/tools/");
        Assert.That(after!.All(t => t.Id != created.Id), Is.True);
    }

    [Test]
    public async Task Create_SpecInvalida_Retorna400()
    {
        var user = await SignUpAsync("User2", "user2@tools.local", "senha123");
        UseToken(user.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/tools/",
            new ToolUpsertRequest("Ruim", null, "{não é json", $"{_mockBaseUrl}/tool"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var semNome = await _client.PostAsJsonAsync("/api/v1/tools/",
            new ToolUpsertRequest("Ruim", null, "{\"type\":\"function\"}", $"{_mockBaseUrl}/tool"));
        Assert.That(semNome.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task OutroUsuario_NaoEnxergaTool()
    {
        var dono = await SignUpAsync("Dono", "dono@tools.local", "senha123");
        var outro = await SignUpAsync("Outro", "outro@tools.local", "senha123");

        UseToken(dono.Token);
        var tool = await CreateToolAsync();

        UseToken(outro.Token);
        var update = await _client.PutAsJsonAsync($"/api/v1/tools/{tool.Id}",
            new ToolUpsertRequest("X", null, Spec, null, null, true));
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var delete = await _client.DeleteAsync($"/api/v1/tools/{tool.Id}");
        Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task ChatCompletions_ProviderPedeTool_ExecutaEDevolveResultado()
    {
        var user = await SignUpAsync("Loopy", "loopy@tools.local", "senha123");
        UseToken(user.Token);
        var tool = await CreateToolAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/completions")
        {
            Content = JsonContent.Create(new ChatCompletionRequest(
                "gpt-mock",
                [new ChatCompletionMessage("user", "quanto é?")],
                Stream: true,
                ToolIds: [tool.Id])),
        };
        var response = await _client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var sse = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(sse, Does.Contain("resultado da tool"));
            Assert.That(_toolExecutions, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Chat_ToolIds_PersistePorChat()
    {
        var user = await SignUpAsync("Keep", "keep@tools.local", "senha123");
        UseToken(user.Token);
        var tool = await CreateToolAsync();

        var created = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest("t", ["gpt-mock"], [], [tool.Id]));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.That(chat.ToolIds, Is.EqualTo(new[] { tool.Id }));

        // Atualização sem ToolIds preserva a seleção existente.
        var updated = await _client.PostAsJsonAsync($"/api/v1/chats/{chat.Id}",
            new ChatUpsertRequest("t2", ["gpt-mock"], []));
        var chat2 = (await updated.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.That(chat2.ToolIds, Is.EqualTo(new[] { tool.Id }));
    }
}
