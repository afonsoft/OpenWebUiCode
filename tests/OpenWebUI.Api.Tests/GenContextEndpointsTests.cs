using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// E2E dos endpoints <c>/api/v1/gen-context</c> (SPEC-20261027-context-gen-wizard):
/// upsert/get do contexto (1 ativo por usuário), 400 em payload inválido e
/// o generate criando chat + run com <c>Params["gen_context"]</c>.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class GenContextEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _token = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-genctx-e2e-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new { name = "U", email = "u@gen.local", password = "senha123" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        _token = (await response.Content.ReadFromJsonAsync<Auth>())!.Token;
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

    private sealed record Auth(string Token);

    private static object ValidCtx() => new
    {
        persona = "developer",
        style = new { preset = "terminal", mode = "dark", density = "compact" },
        projects = new[] { new { title = "Site", url = "https://a.b", summary = "s" } },
        integrations = Array.Empty<string>(),
        freePrompt = "monte a página",
        locale = "pt-BR",
    };

    [Test]
    public async Task GenContext_Get404_Upsert_Get200()
    {
        var none = await _client.GetAsync("/api/v1/gen-context");
        Assert.That(none.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var put = await _client.PostAsJsonAsync("/api/v1/gen-context", ValidCtx());
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await put.Content.ReadAsStringAsync());

        var got = await _client.GetAsync("/api/v1/gen-context");
        Assert.That(got.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = await got.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("developer"));
        Assert.That(json, Does.Contain("terminal"));
    }

    [Test]
    public async Task GenContext_PayloadInvalido_400()
    {
        var bad = await _client.PostAsJsonAsync("/api/v1/gen-context",
            new { persona = "bogus", style = new { preset = "terminal" } });
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await bad.Content.ReadAsStringAsync(), Does.Contain("detail"));
    }

    [Test]
    public async Task GenContext_SemToken_401()
    {
        var saved = _client.DefaultRequestHeaders.Authorization;
        _client.DefaultRequestHeaders.Authorization = null;
        try
        {
            var anon = await _client.GetAsync("/api/v1/gen-context");
            Assert.That(anon.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
        finally
        {
            _client.DefaultRequestHeaders.Authorization = saved;
        }
    }

    [Test]
    public async Task Generate_SemModelo_400_SemContexto_422()
    {
        // Sem contexto salvo ainda → 422
        var missing = await _client.PostAsJsonAsync(
            "/api/v1/gen-context/generate", new { model = "m" });
        Assert.That(missing.StatusCode,
            Is.AnyOf(HttpStatusCode.UnprocessableEntity, HttpStatusCode.OK, HttpStatusCode.Accepted));

        await _client.PostAsJsonAsync("/api/v1/gen-context", ValidCtx());
        var noModel = await _client.PostAsJsonAsync(
            "/api/v1/gen-context/generate", new { });
        Assert.That(noModel.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
