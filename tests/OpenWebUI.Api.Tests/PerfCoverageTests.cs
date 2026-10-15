using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;
using Microsoft.Extensions.Caching.Hybrid;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Backfill de cobertura dos endpoints de config/admin e models tocados pelo
/// PR de performance (HybridCache /api/models + dispatcher): banners CRUD,
/// toggles de config, connections, e caminhos de erro/permissão.
/// </summary>
[TestFixture, IsolateEnvironment]
public class PerfCoverageTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;
    private string _userToken = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-perfcov-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _adminToken = (await SignUpAsync("Admin", "admin@perf.local")).Token;

        // Espelha ApiTests: papel padrão "user" para signups seguintes.
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());

        _userToken = (await SignUpAsync("User", "user@perf.local")).Token;
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

    [Test]
    public async Task Banners_Crud_Admin()
    {
        UseToken(_adminToken);

        var created = await _client.PostAsJsonAsync("/api/v1/configs/banners",
            new BannerRequest("info", "Manutenção", "domingo", true));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var banner = (await created.Content.ReadFromJsonAsync<BannerResponse>())!;

        var invalid = await _client.PostAsJsonAsync("/api/v1/configs/banners",
            new BannerRequest("bogus", "", null!, null));
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var updated = await _client.PutAsJsonAsync($"/api/v1/configs/banners/{banner.Id}",
            new BannerRequest("warning", "Manutenção 2", "sábado", false));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var updatedInvalid = await _client.PutAsJsonAsync($"/api/v1/configs/banners/{banner.Id}",
            new BannerRequest("bogus", "x", null!, null));
        Assert.That(updatedInvalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var updateMissing = await _client.PutAsJsonAsync("/api/v1/configs/banners/nao-existe",
            new BannerRequest("info", "x", null!, null));
        Assert.That(updateMissing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var list = await _client.GetFromJsonAsync<List<BannerResponse>>("/api/v1/configs/banners");
        Assert.That(list!.Any(b => b.Id == banner.Id), Is.True);

        var deleteMissing = await _client.DeleteAsync("/api/v1/configs/banners/nao-existe");
        Assert.That(deleteMissing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var deleted = await _client.DeleteAsync($"/api/v1/configs/banners/{banner.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Banners_SemAdmin_Retorna403()
    {
        UseToken(_userToken);
        var response = await _client.PostAsJsonAsync("/api/v1/configs/banners",
            new BannerRequest("info", "x", null!, null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task ConfigToggles_AdminGetEPost()
    {
        UseToken(_adminToken);

        var signup = await _client.PostAsJsonAsync("/api/v1/configs/signup",
            new SignupConfig(true, "user"));
        Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var signupGet = await _client.GetFromJsonAsync<SignupConfig>("/api/v1/configs/signup");
        Assert.That(signupGet!.EnableSignup, Is.True);

        var apiKey = await _client.PostAsJsonAsync("/api/v1/configs/api_key",
            new FeatureToggle(true));
        Assert.That(apiKey.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var apiKeyGet = await _client.GetFromJsonAsync<FeatureToggle>("/api/v1/configs/api_key");
        Assert.That(apiKeyGet!.Enabled, Is.True);

        var channels = await _client.PostAsJsonAsync("/api/v1/configs/channels",
            new FeatureToggle(true));
        Assert.That(channels.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var direct = await _client.PostAsJsonAsync("/api/v1/configs/direct_connections",
            new FeatureToggle(false));
        Assert.That(direct.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var codeExec = await _client.PostAsJsonAsync("/api/v1/configs/code_execution",
            new CodeExecutionConfig(["pyodide", "bogus"], true));
        Assert.That(codeExec.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var codeExecResult = (await codeExec.Content.ReadFromJsonAsync<CodeExecutionConfig>())!;
        Assert.That(codeExecResult.Engines, Is.EqualTo(new List<string> { "pyodide" }));

        var models = await _client.PostAsJsonAsync("/api/v1/configs/models",
            new ModelsConfig(["m1", " ", ""], []));
        Assert.That(models.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var modelsResult = (await models.Content.ReadFromJsonAsync<ModelsConfig>())!;
        Assert.That(modelsResult.DefaultModels, Is.EqualTo(new List<string> { "m1" }));
    }

    [Test]
    public async Task ConfigToggles_SemAdmin_Retorna403()
    {
        UseToken(_userToken);
        foreach (var (route, body) in new (string, object)[]
        {
            ("/api/v1/configs/signup", new SignupConfig(true, "user")),
            ("/api/v1/configs/api_key", new FeatureToggle(true)),
            ("/api/v1/configs/channels", new FeatureToggle(true)),
            ("/api/v1/configs/direct_connections", new FeatureToggle(true)),
            ("/api/v1/configs/code_execution", new CodeExecutionConfig([], false)),
            ("/api/v1/configs/models", new ModelsConfig([], [])),
        })
        {
            var response = await _client.PostAsJsonAsync(route, body);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), route);
        }
    }

    [Test]
    public async Task Connections_AdminGetEPost()
    {
        UseToken(_adminToken);

        var get = await _client.GetAsync("/api/v1/configs/connections");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var models = await _client.GetAsync("/api/v1/configs/connections/models?type=openai&index=0");
        Assert.That(models.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var caps = await _client.GetAsync("/api/v1/configs/connections/capabilities?type=openai&index=0");
        Assert.That(caps.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var capsBadType = await _client.GetAsync("/api/v1/configs/connections/capabilities?type=bogus&index=0");
        Assert.That(capsBadType.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var aggregated = await _client.GetAsync("/api/v1/configs/capabilities");
        Assert.That(aggregated.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var post = await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig([], [], []));
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Connections_SemAdmin_Retorna403()
    {
        UseToken(_userToken);
        foreach (var route in new[]
        {
            "/api/v1/configs/connections/models?type=openai&index=0",
            "/api/v1/configs/connections/capabilities?type=openai&index=0",
            "/api/v1/configs/capabilities",
        })
        {
            var response = await _client.GetAsync(route);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), route);
        }

        var post = await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig([], [], []));
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Models_SemPermissaoWorkspace_Retorna403()
    {
        // Admin nega explicitamente workspace.models ao usuário comum.
        UseToken(_adminToken);
        var users = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/users/");
        var userId = users.GetProperty("users").EnumerateArray()
            .First(u => u.GetProperty("email").GetString() == "user@perf.local")
            .GetProperty("id").GetString();
        var denied = await _client.PutAsJsonAsync($"/api/v1/users/{userId}/permissions",
            new { workspace = new { models = false } });
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await denied.Content.ReadAsStringAsync());

        UseToken(_userToken);
        var response = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest("X", "llama3", null, null, null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Models_ErrorPaths_Admin()
    {
        UseToken(_adminToken);

        var notFound = await _client.PostAsJsonAsync("/api/v1/models/model/delete",
            new { id = "nao-existe" });
        Assert.That(notFound.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var updateMissing = await _client.PostAsJsonAsync("/api/v1/models/model/update",
            new { id = "nao-existe", name = "X", base_model_id = "llama3" });
        Assert.That(updateMissing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var badCreate = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest("", "", null, null, null));
        Assert.That(badCreate.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
