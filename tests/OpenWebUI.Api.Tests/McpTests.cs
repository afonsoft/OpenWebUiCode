using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do SPEC-20261003-mcp-tool-servers: CRUD admin de servers MCP,
/// discovery (<c>tools/list</c>) materializando tools virtuais e execução
/// (<c>tools/call</c>) roteada pelo <see cref="ToolExecutor"/>.
/// Usa um server MCP fake sobre streamable HTTP (JSON-RPC puro).
/// </summary>
[TestFixture, IsolateEnvironment]
public class McpTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private FakeMcpServer _mcp = null!;
    private string _adminToken = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-mcp-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _mcp = new FakeMcpServer();
        _mcp.Start();

        var admin = await SignUpAsync("Admin", "admin@mcp.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mcp.Dispose();
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
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    private static BuiltinToolRegistry EmptyRegistry() =>
        new([], new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private McpServerUpsertRequest HttpRequest(string name, Dictionary<string, string>? headers = null) =>
        new(name, "http", Url: _mcp.Url, Headers: headers ?? new Dictionary<string, string>());

    private async Task<McpServerResponse> CreateServerAsync(McpServerUpsertRequest? request = null)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/mcp/servers/", request ?? HttpRequest($"srv-{Guid.NewGuid():N}",
                new Dictionary<string, string> { ["X-Token"] = "segredo" }));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<McpServerResponse>())!;
    }

    private async Task<JsonElement> RefreshAsync(string id, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await _client.PostAsync($"/api/v1/mcp/servers/{id}/refresh", null);
        Assert.That(response.StatusCode, Is.EqualTo(expected), await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    private static string NomeFuncao(List<Tool> tools, string name) =>
        ToolExecutor.FunctionName(tools.First(t => t.Name == name))!;

    // ---- RF-001: CRUD admin ----

    [Test]
    public async Task Crud_AdminCriaEListaComHeadersMascarados()
    {
        var server = await CreateServerAsync(new McpServerUpsertRequest(
            "Fake MCP", "http", Url: _mcp.Url,
            Headers: new Dictionary<string, string> { ["X-Token"] = "segredo" }));

        Assert.That(server.Name, Is.EqualTo("Fake MCP"));
        Assert.That(server.Url, Is.EqualTo(_mcp.Url));
        Assert.That(server.Headers["X-Token"], Is.EqualTo("********"));

        var list = await _client.GetFromJsonAsync<List<McpServerResponse>>("/api/v1/mcp/servers/");
        var found = list!.First(s => s.Id == server.Id);
        Assert.That(found.Headers["X-Token"], Is.EqualTo("********"),
            "valor do header nunca pode sair em claro na leitura");
    }

    [Test]
    public async Task Crud_UsuarioComum_Recebe403()
    {
        var user = await SignUpAsync("Comum", "comum@mcp.local", "senha123");
        UseToken(user.Token);
        try
        {
            var create = await _client.PostAsJsonAsync("/api/v1/mcp/servers/", HttpRequest("x"));
            Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

            var list = await _client.GetAsync("/api/v1/mcp/servers/");
            Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }
        finally
        {
            UseToken(_adminToken);
        }
    }

    [Test]
    public async Task Validacao_CamposObrigatoriosEUrlMetadata()
    {
        var cases = new[]
        {
            new McpServerUpsertRequest("", "http", Url: _mcp.Url),
            new McpServerUpsertRequest("sem url", "http"),
            new McpServerUpsertRequest("url ruim", "http", Url: "ftp://x"),
            new McpServerUpsertRequest("metadata aws", "http", Url: "http://169.254.169.254/latest"),
            new McpServerUpsertRequest("metadata gcp", "http", Url: "http://metadata.google.internal/x"),
            new McpServerUpsertRequest("stdio sem cmd", "stdio"),
            new McpServerUpsertRequest("transporte ruim", "socket", Url: _mcp.Url),
        };

        foreach (var bad in cases)
        {
            var response = await _client.PostAsJsonAsync("/api/v1/mcp/servers/", bad);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
                $"esperava 400 para '{bad.Name}': {await response.Content.ReadAsStringAsync()}");
        }
    }

    // ---- RF-002: refresh/discovery ----

    [Test]
    public async Task Refresh_MaterializaToolsVirtuaisNoCatalogo()
    {
        var server = await CreateServerAsync();
        var refresh = await RefreshAsync(server.Id);
        Assert.That(refresh.GetProperty("tools").GetInt32(), Is.EqualTo(3));

        var tools = await _client.GetFromJsonAsync<List<McpToolResponse>>(
            $"/api/v1/mcp/servers/{server.Id}/tools");
        Assert.That(tools!.Select(t => t.Name), Is.EquivalentTo(new[] { "echo", "boom", "long" }));
        Assert.That(tools!.All(t => t.Enabled), Is.True);
        Assert.That(tools!.All(t => t.FunctionName.StartsWith("mcp_")), Is.True);

        // Tools virtuais aparecem no catálogo geral (seletor do chat) com Source=mcp.
        var all = await _client.GetFromJsonAsync<List<ToolResponse>>("/api/v1/tools/");
        var virtuais = all!.Where(t => t.Source == "mcp" && (t.Url ?? "").StartsWith($"mcp://{server.Id}/")).ToList();
        Assert.That(virtuais, Has.Count.EqualTo(3));
        Assert.That(virtuais.First(t => t.Name == "echo").SpecJson, Does.Contain("\"msg\""),
            "spec materializada do inputSchema do server");
    }

    [Test]
    public async Task Refresh_SemCredencial_GravaLastError()
    {
        var server = await CreateServerAsync(HttpRequest("sem-auth"));
        var refresh = await RefreshAsync(server.Id, HttpStatusCode.BadRequest);
        Assert.That(refresh.GetProperty("detail").GetString(), Is.Not.Empty);

        var list = await _client.GetFromJsonAsync<List<McpServerResponse>>("/api/v1/mcp/servers/");
        Assert.That(list!.First(s => s.Id == server.Id).LastError, Is.Not.Null.And.Not.Empty,
            "server inacessível registra o erro em LastError");

        var tools = await _client.GetFromJsonAsync<List<McpToolResponse>>(
            $"/api/v1/mcp/servers/{server.Id}/tools");
        Assert.That(tools, Is.Empty);
    }

    [Test]
    public async Task Put_HeaderMascarado_PreservaValorGravado()
    {
        var server = await CreateServerAsync();
        await RefreshAsync(server.Id);

        // PUT com "********" mantém o segredo gravado — o fake rejeita sem o token.
        var update = await _client.PutAsJsonAsync($"/api/v1/mcp/servers/{server.Id}",
            HttpRequest(server.Name, new Dictionary<string, string> { ["X-Token"] = "********" }));
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await update.Content.ReadAsStringAsync());

        await RefreshAsync(server.Id); // ainda funciona → header preservado

        // PUT sem a chave remove o header → próxima chamada falha.
        update = await _client.PutAsJsonAsync($"/api/v1/mcp/servers/{server.Id}",
            HttpRequest(server.Name));
        Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await RefreshAsync(server.Id, HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Desabilitar_DesligaToolsVirtuaisSemApagar()
    {
        var server = await CreateServerAsync();
        await RefreshAsync(server.Id);

        var updated = await _client.PutAsJsonAsync($"/api/v1/mcp/servers/{server.Id}",
            HttpRequest(server.Name, new Dictionary<string, string> { ["X-Token"] = "********" })
                with { Enabled = false });
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Tools continuam existindo (não deletadas) mas fora do catálogo do chat.
        var tools = await _client.GetFromJsonAsync<List<McpToolResponse>>(
            $"/api/v1/mcp/servers/{server.Id}/tools");
        Assert.That(tools, Has.Count.EqualTo(3));
        Assert.That(tools!.All(t => !t.Enabled), Is.True);

        var all = await _client.GetFromJsonAsync<List<ToolResponse>>("/api/v1/tools/");
        Assert.That(all!.Any(t => (t.Url ?? "").StartsWith($"mcp://{server.Id}/")), Is.False);
    }

    [Test]
    public async Task Delete_RemoveServidorEToolsVirtuais()
    {
        var server = await CreateServerAsync();
        await RefreshAsync(server.Id);

        var deleted = await _client.DeleteAsync($"/api/v1/mcp/servers/{server.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = await _client.GetFromJsonAsync<List<McpServerResponse>>("/api/v1/mcp/servers/");
        Assert.That(list!.Any(s => s.Id == server.Id), Is.False);
        var all = await _client.GetFromJsonAsync<List<ToolResponse>>("/api/v1/tools/");
        Assert.That(all!.Any(t => (t.Url ?? "").StartsWith($"mcp://{server.Id}/")), Is.False);
    }

    // ---- RF-003: execução via ToolExecutor ----

    [Test]
    public async Task Execucao_RoteiaMcpUrl_ParaCallTool()
    {
        var server = await CreateServerAsync();
        await RefreshAsync(server.Id);

        await using var db = CreateContext();
        var mc2 = TestCache.Create();
        var executor = new ToolExecutor(db, new StubHttpClientFactory(),
            new PythonToolExecutor(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            new McpClientService(db, mc2), EmptyRegistry(), new ConfigService(db, TestCache.Create()));

        var tools = await db.Tools.Where(t => (t.Url ?? "").StartsWith($"mcp://{server.Id}/")).ToListAsync();

        var result = (await executor.ExecuteAsync(tools, NomeFuncao(tools, "echo"), "{\"msg\":\"ola\"}")).Text;
        Assert.That(result, Is.EqualTo("echo:ola"));

        // Erro de protocolo vira mensagem para o modelo — nunca exceção.
        var boom = (await executor.ExecuteAsync(tools, NomeFuncao(tools, "boom"), "{}")).Text;
        Assert.That(boom, Does.Contain("Erro").And.Contain("kaboom"));

        // Saída grande trunca em 4000 chars, igual às tools HTTP.
        var longResult = (await executor.ExecuteAsync(tools, NomeFuncao(tools, "long"), "{}")).Text;
        Assert.That(longResult, Has.Length.EqualTo(4000));
    }

    [Test]
    public async Task Execucao_ServidorRemovido_OuDesabilitado_RetornaErro()
    {
        var server = await CreateServerAsync();
        await RefreshAsync(server.Id);

        await using var db = CreateContext();
        var mc1 = TestCache.Create();
        var mcp = new McpClientService(db, mc1);
        var tools = await db.Tools.Where(t => (t.Url ?? "").StartsWith($"mcp://{server.Id}/")).ToListAsync();

        // Server desabilitado → mensagem de erro (não exceção).
        var disabled = await mcp.CallToolAsync(
            new McpServer { Id = server.Id, Name = "x", Transport = "http", Url = _mcp.Url, Enabled = false },
            "echo", "{}");
        Assert.That(disabled, Does.Contain("desabilitado"));

        // Server deletado → executor devolve erro amigável.
        await _client.DeleteAsync($"/api/v1/mcp/servers/{server.Id}");
        var executor = new ToolExecutor(db, new StubHttpClientFactory(),
            new PythonToolExecutor(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            mcp, EmptyRegistry(), new ConfigService(db, TestCache.Create()));
        var gone = (await executor.ExecuteAsync(tools, NomeFuncao(tools, "echo"), "{}")).Text;
        Assert.That(gone, Does.Contain("não existe mais"));
    }
}

/// <summary>
/// Server MCP fake sobre streamable HTTP: atende JSON-RPC 2.0 com
/// <c>initialize</c>, <c>tools/list</c> e <c>tools/call</c>. Exige o header
/// <c>X-Token: segredo</c> para provar que headers do registro chegam ao server.
/// </summary>
internal sealed class FakeMcpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public string Url { get; private set; } = "";

    public void Start()
    {
        var port = FreePort();
        Url = $"http://127.0.0.1:{port}/mcp";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(() => LoopAsync(_stop.Token));
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                // DELETE/GET (cleanup de sessão, standalone SSE) — aceita e encerra.
                ctx.Response.StatusCode = ctx.Request.HttpMethod == "DELETE" ? 200 : 405;
                ctx.Response.Close();
                return;
            }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
            {
                body = await reader.ReadToEndAsync();
            }
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString() ?? "";

            // Notificações (sem id) → 202 sem corpo.
            if (!root.TryGetProperty("id", out var idProp))
            {
                ctx.Response.StatusCode = 202;
                ctx.Response.Close();
                return;
            }

            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(idProp.GetRawText()),
            };

            if (ctx.Request.Headers["X-Token"] != "segredo")
            {
                response["error"] = new JsonObject { ["code"] = -32000, ["message"] = "unauthorized" };
            }
            else
            {
                switch (method)
                {
                    case "initialize":
                        response["result"] = new JsonObject
                        {
                            ["protocolVersion"] = "2025-06-18",
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                            ["serverInfo"] = new JsonObject { ["name"] = "fake-mcp", ["version"] = "1.0" },
                        };
                        break;
                    case "tools/list":
                        response["result"] = new JsonObject
                        {
                            ["tools"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["name"] = "echo",
                                    ["description"] = "Repete o texto",
                                    ["inputSchema"] = new JsonObject
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new JsonObject
                                        {
                                            ["msg"] = new JsonObject { ["type"] = "string" },
                                        },
                                        ["required"] = new JsonArray("msg"),
                                    },
                                },
                                new JsonObject
                                {
                                    ["name"] = "boom",
                                    ["description"] = "Sempre falha",
                                    ["inputSchema"] = new JsonObject
                                    {
                                        ["type"] = "object", ["properties"] = new JsonObject(),
                                    },
                                },
                                new JsonObject
                                {
                                    ["name"] = "long",
                                    ["description"] = "Texto grande",
                                    ["inputSchema"] = new JsonObject
                                    {
                                        ["type"] = "object", ["properties"] = new JsonObject(),
                                    },
                                },
                            },
                        };
                        break;
                    case "tools/call":
                        response["result"] = CallTool(root.GetProperty("params"));
                        break;
                    default:
                        response["error"] = new JsonObject
                        {
                            ["code"] = -32601, ["message"] = $"method {method} not found",
                        };
                        break;
                }
            }

            var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch (HttpListenerException) { /* cliente desconectou — ignora */ }
        catch (IOException) { /* cliente desconectou — ignora */ }
        catch (ObjectDisposedException) { /* listener parou */ }
    }

    private static JsonObject CallTool(JsonElement p)
    {
        var name = p.GetProperty("name").GetString() ?? "";
        var msg = p.TryGetProperty("arguments", out var args)
            && args.TryGetProperty("msg", out var m)
            ? m.GetString() ?? ""
            : "";
        var text = name switch
        {
            "echo" => $"echo:{msg}",
            "boom" => "kaboom",
            "long" => new string('x', 5000),
            _ => "unknown tool",
        };
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = name == "boom",
        };
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _listener.Close();
        _stop.Dispose();
    }
}
