using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura do endpoint JSON-RPC 2.0 (SPEC-20261021-harness-seams RF-C2):
/// envelope de resposta, métodos <c>ping/chats.list/chats.create/
/// runs.start/runs.get</c> e códigos de erro do protocolo
/// (-32700/-32600/-32601/-32602/-32000) + auth obrigatória.
/// </summary>
[TestFixture, IsolateEnvironment]
public class RpcEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-rpc-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default",
            $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        var admin = await SignUpAsync("Admin", "admin@rpc.local");
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync(
            "/api/v1/auths/admin/config", config);
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

    private static async Task<JsonElement> CallAsync(
        HttpClient client, string method, object? parms = null,
        object? id = null)
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = id ?? 1,
            method,
            @params = parms,
        });
        var resp = await client.PostAsync("/api/v1/rpc",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await resp.Content.ReadAsStringAsync());
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc;
    }

    [Test]
    public async Task Rpc_Ping_ChatsCreate_List_E_RunsGet()
    {
        var auth = await SignUpAsync("R", "r@rpc.local");
        UseToken(auth.Token);

        var pong = await CallAsync(_client, "ping");
        Assert.That(pong.GetProperty("result").GetString(),
            Is.EqualTo("pong"));

        var created = await CallAsync(_client, "chats.create",
            new { title = "via rpc", model = "llama3" });
        var chatId = created.GetProperty("result")
            .GetProperty("chatId").GetString();
        Assert.That(chatId, Is.Not.Null.And.Not.Empty);

        var list = await CallAsync(_client, "chats.list");
        var chats = list.GetProperty("result").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()).ToList();
        Assert.That(chats, Does.Contain(chatId));

        // runs.get de run inexistente → erro de domínio -32000.
        var notFound = await CallAsync(_client, "runs.get",
            new { runId = "run-inexistente" });
        var err = notFound.GetProperty("error");
        Assert.Multiple(() =>
        {
            Assert.That(err.GetProperty("code").GetInt32(),
                Is.EqualTo(-32000));
            Assert.That(err.GetProperty("message").GetString(),
                Does.Contain("não encontrada"));
        });
    }

    [Test]
    public async Task Rpc_Erros_De_Protocolo()
    {
        var auth = await SignUpAsync("P", "p@rpc.local");
        UseToken(auth.Token);

        // JSON malformado → -32700.
        var resp = await _client.PostAsync("/api/v1/rpc",
            new StringContent("{invalid", Encoding.UTF8, "application/json"));
        var parseErr = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(parseErr.GetProperty("error").GetProperty("code")
            .GetInt32(), Is.EqualTo(-32700));

        // Envelope sem method → -32600.
        var noMethod = await _client.PostAsync("/api/v1/rpc",
            new StringContent("""{"jsonrpc":"2.0","id":9}""",
                Encoding.UTF8, "application/json"));
        var invalidReq = await noMethod.Content
            .ReadFromJsonAsync<JsonElement>();
        Assert.That(invalidReq.GetProperty("error").GetProperty("code")
            .GetInt32(), Is.EqualTo(-32600));

        // Método desconhecido → -32601.
        var unknown = await CallAsync(_client, "foo.bar");
        Assert.That(unknown.GetProperty("error").GetProperty("code")
            .GetInt32(), Is.EqualTo(-32601));

        // Params incompletos → -32602.
        var badParams = await CallAsync(_client, "runs.start",
            new { chatId = "c" });
        Assert.That(badParams.GetProperty("error").GetProperty("code")
            .GetInt32(), Is.EqualTo(-32602));
    }

    [Test]
    public async Task Rpc_RunsStart_ChatInexistente_Da_Erro_Dominio()
    {
        var auth = await SignUpAsync("S", "s@rpc.local");
        UseToken(auth.Token);
        var res = await CallAsync(_client, "runs.start",
            new { chatId = "chat-que-nao-existe", prompt = "oi", model = "m" });
        var err = res.GetProperty("error");
        Assert.That(err.GetProperty("code").GetInt32(), Is.EqualTo(-32000));
    }

    [Test]
    public async Task Rpc_SemToken_401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var resp = await _client.PostAsync("/api/v1/rpc",
            new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""",
                Encoding.UTF8, "application/json"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var resp = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new SignUpRequest(name, email, "senha123"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    /// <summary>Happy path completo: create → runs.start → runs.get —
    /// cobre RunsStartAsync/RunsGetAsync de ponta a ponta (a run falha sem
    /// provider configurado, mas todo o caminho de enqueue/status roda).</summary>
    [Test]
    public async Task Rpc_RunsStart_E_RunsGet_HappyPath()
    {
        var auth = await SignUpAsync("H", "h@rpc.local");
        UseToken(auth.Token);

        var created = await CallAsync(_client, "chats.create",
            new { title = "run via rpc", model = "llama3" });
        var chatId = created.GetProperty("result")
            .GetProperty("chatId").GetString()!;

        var started = await CallAsync(_client, "runs.start",
            new { chatId, prompt = "diga oi", model = "llama3" });
        var runId = started.GetProperty("result")
            .GetProperty("runId").GetString();
        Assert.That(runId, Is.Not.Null.And.Not.Empty);

        var got = await CallAsync(_client, "runs.get", new { runId });
        var status = got.GetProperty("result")
            .GetProperty("status").GetString();
        Assert.That(status, Is.Not.Null.And.Not.Empty);
    }
}
