using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Mcp;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261022-ade-surfaces cap. B: protocolo MCP stdio —
/// <c>initialize</c>/<c>tools/list</c>/<c>tools/call</c> e auth fail-closed.
/// O <see cref="OwuiApiClient"/> fala com a API real via TestServer —
/// nenhum caminho de execução novo.
/// </summary>
public class McpServerTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _user = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-mcp-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _user = await SignUpAsync("McpUser", "user@mcp.local", "senha123");
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

    private async Task<AuthResponse> SignUpAsync(
        string name, string email, string password)
    {
        var res = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name, email, password });
        Assert.That(res.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
        var body = await res.Content.ReadFromJsonAsync<AuthResponse>();
        return body!;
    }

    /// <summary>Cliente MCP apontando pro TestServer com a API key do usuário.</summary>
    private OwuiApiClient Api(string? token = null)
    {
        var http = new HttpClient(_factory.Server.CreateHandler());
        return new OwuiApiClient(
            http, "http://localhost", token ?? _user.Token);
    }

    private static JsonDocument Parse(string? json) =>
        JsonDocument.Parse(json ?? "null");

    [Test]
    public async Task Mcp_Initialize_E_ToolsList()
    {
        var server = new McpServer();
        var api = Api();

        var init = Parse(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize"}""",
            api, CancellationToken.None));
        Assert.Multiple(() =>
        {
            Assert.That(init.RootElement.GetProperty("result")
                .GetProperty("protocolVersion").GetString(),
                Is.EqualTo(McpServer.ProtocolVersion));
            Assert.That(init.RootElement.GetProperty("result")
                .GetProperty("serverInfo").GetProperty("name").GetString(),
                Is.EqualTo("openwebui-mcp"));
        });

        var list = Parse(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            api, CancellationToken.None));
        var names = list.RootElement.GetProperty("result")
            .GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.That(names, Is.EquivalentTo(new[]
        {
            "chats.list", "chats.create", "runs.start",
            "runs.status", "runs.stop", "teams.status", "attention.list",
        }));

        // Notification sem id → sem resposta.
        var notif = await server.HandleLineAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            api, CancellationToken.None);
        Assert.That(notif, Is.Null);
    }

    [Test]
    public async Task Mcp_SemCredencial_ToolsCall_Falha_Fechado()
    {
        var server = new McpServer();
        var http = new HttpClient(_factory.Server.CreateHandler());
        var api = new OwuiApiClient(http, "http://localhost", null);

        var res = Parse(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"chats.list","arguments":{}}}""",
            api, CancellationToken.None));
        Assert.Multiple(() =>
        {
            Assert.That(
                res.RootElement.GetProperty("result")
                    .GetProperty("isError").GetBoolean(), Is.True);
            Assert.That(
                res.RootElement.GetProperty("result").GetProperty("content")[0]
                    .GetProperty("text").GetString(),
                Does.Contain("OPENWEBUI_API_KEY"));
        });
    }

    [Test]
    public async Task Mcp_Metodo_Desconhecido_Erro_32601()
    {
        var server = new McpServer();
        var res = Parse(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":9,"method":"bogus/x"}""",
            Api(), CancellationToken.None));
        Assert.That(res.RootElement.GetProperty("error")
            .GetProperty("code").GetInt32(), Is.EqualTo(-32601));
    }

    [Test]
    public async Task Mcp_ToolsCall_ChatsCreate_E_List_Real()
    {
        var server = new McpServer();
        var api = Api();

        var create = Parse(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"chats.create","arguments":{"title":"via-mcp","model":"m1"}}}""",
            api, CancellationToken.None));
        var text = create.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.That(text, Does.Contain("via-mcp"));

        var list = Parse(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"chats.list","arguments":{}}}""",
            api, CancellationToken.None));
        var listText = list.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.That(listText, Does.Contain("via-mcp"));
    }

    [Test]
    public async Task Mcp_ToolsCall_TeamStatus_Chat_Inexistente_Erro()
    {
        var server = new McpServer();
        var res = Parse(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"teams.status","arguments":{"chatId":"nope"}}}""",
            Api(), CancellationToken.None));
        Assert.That(res.RootElement.GetProperty("result")
            .GetProperty("isError").GetBoolean(), Is.True);
    }

    [Test]
    public async Task Mcp_Erros_De_Protocolo_E_Tools_Restantes()
    {
        var server = new McpServer();
        var api = Api();

        // Linha não-JSON → -32700; envelope sem method → -32600; ping → ok.
        var bad = Parse(await server.HandleLineAsync(
            "{isto não é json", api, CancellationToken.None));
        Assert.That(bad.RootElement.GetProperty("error")
            .GetProperty("code").GetInt32(), Is.EqualTo(-32700));

        var noMethod = Parse(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":7}""", api, CancellationToken.None));
        Assert.That(noMethod.RootElement.GetProperty("error")
            .GetProperty("code").GetInt32(), Is.EqualTo(-32600));

        var ping = Parse(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":8,"method":"ping"}""",
            api, CancellationToken.None));
        Assert.That(ping.RootElement.TryGetProperty("result", out _),
            Is.True);

        // tools/call sem "name" → -32602; tool desconhecida → isError.
        var noName = Parse(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{}}""",
            api, CancellationToken.None));
        Assert.That(noName.RootElement.GetProperty("error")
            .GetProperty("code").GetInt32(), Is.EqualTo(-32602));

        var unknown = Parse(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"x.y","arguments":{}}}""",
            api, CancellationToken.None));
        Assert.That(unknown.RootElement.GetProperty("result")
            .GetProperty("isError").GetBoolean(), Is.True);

        // runs.start com chatId ausente → marcador inválido → API 404 →
        // isError; cobre Req() + o braço runs.start do switch.
        var runStart = Parse(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"runs.start","arguments":{}}}""",
            api, CancellationToken.None));
        Assert.That(runStart.RootElement.GetProperty("result")
            .GetProperty("isError").GetBoolean(), Is.True);

        // runs.status/runs.stop com ids ausentes — mesmo caminho.
        var runsStatus = Parse(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"runs.status","arguments":{}}}""",
            api, CancellationToken.None));
        Assert.That(runsStatus.RootElement.GetProperty("result")
            .GetProperty("isError").GetBoolean(), Is.True);

        var runsStop = Parse(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":14,"method":"tools/call","params":{"name":"runs.stop","arguments":{}}}""",
            api, CancellationToken.None));
        Assert.That(runsStop.RootElement.GetProperty("result")
            .GetProperty("isError").GetBoolean(), Is.True);

        // attention.list — resposta real (lista vazia ou com itens).
        var att = Parse(await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":14,"method":"tools/call","params":{"name":"attention.list","arguments":{}}}""",
            api, CancellationToken.None));
        var attResult = att.RootElement.GetProperty("result");
        var attIsError = attResult.TryGetProperty("isError", out var ae)
            && ae.GetBoolean();
        Assert.That(attIsError, Is.False);
    }
}
