using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// E2E de <c>GET /api/v1/agents</c> (SPEC-20261014-agent-definitions
/// RF-004): catálogo efetivo com provenance e opt-in de workspace.
/// </summary>
[TestFixture]
public class AgentsEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _token = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-agents-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default",
            $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        var signup = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name = "U", email = "u@agents.local", password = "senha123" });
        signup.EnsureSuccessStatusCode();
        _token = (await signup.Content
            .ReadFromJsonAsync<AuthResponse>())!.Token;
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _token);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        TestInfra.DeleteDb(_dbPath);
    }

    private sealed record AuthResponse(string Token);

    [Test]
    public async Task Agents_ListaBuiltinComProvenance()
    {
        var resp = await _client.GetAsync("/api/v1/agents");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await resp.Content.ReadAsStringAsync());

        var body = await resp.Content.ReadFromJsonAsync<
            Dictionary<string, System.Text.Json.JsonElement>>();
        var names = body!["agents"].EnumerateArray()
            .Select(a => a.GetProperty("name").GetString()).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(names, Does.Contain("code-reviewer"));
            Assert.That(names, Does.Contain("researcher"));
            Assert.That(body["workspaceOptIn"].GetBoolean(), Is.False);
        });
        var reviewer = body["agents"].EnumerateArray()
            .First(a => a.GetProperty("name").GetString() == "code-reviewer");
        Assert.Multiple(() =>
        {
            Assert.That(reviewer.GetProperty("source").GetString(),
                Is.EqualTo("builtin"));
            Assert.That(reviewer.GetProperty("model").GetString(),
                Is.EqualTo("role:advisor"));
        });
    }

    [Test]
    public async Task Agents_WorkspaceOptIn_Persiste()
    {
        var put = await _client.PutAsJsonAsync(
            "/api/v1/agents/workspace-enabled", new { enabled = true });
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var resp = await _client.GetFromJsonAsync<
            Dictionary<string, System.Text.Json.JsonElement>>("/api/v1/agents");
        Assert.That(resp!["workspaceOptIn"].GetBoolean(), Is.True);
    }

    [Test]
    public async Task Agents_SemAuth_Unauthorized()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        try
        {
            var resp = await _client.GetAsync("/api/v1/agents");
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
        finally
        {
            _client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _token);
        }
    }
}
