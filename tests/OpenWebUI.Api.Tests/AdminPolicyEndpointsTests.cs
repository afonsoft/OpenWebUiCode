using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// E2E dos endpoints <c>/api/v1/admin/policies</c> (SPEC-20261018-policies-admin):
/// CRUD de statements, simulação dry-run, listagem de denials e o gate
/// admin-only (<c>403</c> para usuário comum, <c>401</c> sem token).
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class AdminPolicyEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;
    private string _userToken = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-policies-e2e-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@policies.local");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());

        var user = await SignUpAsync("User", "user@policies.local");
        _userToken = user.Token;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        TestInfra.DeleteDb(_dbPath);
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new { name, email, password = "senha123" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string? token) =>
        _client.DefaultRequestHeaders.Authorization =
            token is null ? null : new AuthenticationHeaderValue("Bearer", token);

    private sealed record AuthResponse(string Token);

    private sealed record PolicyDto(string Id, int Order, string Action,
        string Resource, string Effect, string? Note);

    [Test]
    public async Task Policies_CrudCompleto_AdminSomente()
    {
        // ---- gate admin: sem token 401, usuário comum 403 ----
        UseToken(null);
        var anon = await _client.GetAsync("/api/v1/admin/policies/");
        Assert.That(anon.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        UseToken(_userToken);
        var forbid = await _client.GetAsync("/api/v1/admin/policies/");
        Assert.That(forbid.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var forbidCreate = await _client.PostAsJsonAsync("/api/v1/admin/policies/",
            new { action = "tool.use", resource = "shell:*" });
        Assert.That(forbidCreate.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_adminToken);

        // ---- validação: action inválida e resource vazio ----
        var badAction = await _client.PostAsJsonAsync("/api/v1/admin/policies/",
            new { action = "bogus.use", resource = "x" });
        Assert.That(badAction.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var badResource = await _client.PostAsJsonAsync("/api/v1/admin/policies/",
            new { action = "tool.use", resource = "  " });
        Assert.That(badResource.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // ---- create: ordem automática ----
        var created = await _client.PostAsJsonAsync("/api/v1/admin/policies/",
            new { action = "tool.use", resource = "shell:rm *", note = "bloqueia rm" });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var policy = (await created.Content.ReadFromJsonAsync<PolicyDto>())!;
        Assert.Multiple(() =>
        {
            Assert.That(policy.Effect, Is.EqualTo("deny"));
            Assert.That(policy.Action, Is.EqualTo("tool.use"));
            Assert.That(policy.Order, Is.GreaterThanOrEqualTo(1));
        });

        // ---- list ----
        var list = await _client.GetFromJsonAsync<PolicyDto[]>("/api/v1/admin/policies/");
        Assert.That(list!.Single(p => p.Id == policy.Id).Resource,
            Is.EqualTo("shell:rm *"));

        // ---- update ----
        var updated = await _client.PutAsJsonAsync(
            $"/api/v1/admin/policies/{policy.Id}",
            new { action = "tool.use", resource = "shell:sudo *", order = 5 });
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var after = (await updated.Content.ReadFromJsonAsync<PolicyDto>())!;
        Assert.Multiple(() =>
        {
            Assert.That(after.Resource, Is.EqualTo("shell:sudo *"));
            Assert.That(after.Order, Is.EqualTo(5));
        });
        var missing = await _client.PutAsJsonAsync(
            "/api/v1/admin/policies/nope",
            new { action = "tool.use", resource = "x" });
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // ---- simulate: match e não-match + action inválida ----
        var denied = await _client.PostAsJsonAsync("/api/v1/admin/policies/simulate",
            new { action = "tool.use", resource = "shell:sudo apt" });
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await denied.Content.ReadAsStringAsync(),
            Does.Contain("\"denied\":true"));

        var allowed = await _client.PostAsJsonAsync("/api/v1/admin/policies/simulate",
            new { action = "tool.use", resource = "file_read:x" });
        Assert.That(await allowed.Content.ReadAsStringAsync(),
            Does.Contain("\"denied\":false"));

        var badSim = await _client.PostAsJsonAsync("/api/v1/admin/policies/simulate",
            new { action = "bogus", resource = "x" });
        Assert.That(badSim.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // ---- denials (lista vazia é resposta válida) ----
        var denials = await _client.GetAsync("/api/v1/admin/policies/denials");
        Assert.That(denials.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // ---- delete ----
        var deleted = await _client.DeleteAsync($"/api/v1/admin/policies/{policy.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        var delMissing = await _client.DeleteAsync($"/api/v1/admin/policies/{policy.Id}");
        Assert.That(delMissing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
