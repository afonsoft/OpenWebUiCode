using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes e2e de <c>/api/v1/users/user/tool-approvals</c>
/// (SPEC-20261013-approval-tiers RF-002/RF-007): o GET devolve o catálogo
/// de built-ins com tier + override vigente; o PUT mescla patches
/// (<c>allow|deny|prompt</c>; null/"" remove), valida ações e é isolado
/// por usuário.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class ToolApprovalsEndpointTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = TestInfra.NewDbPath("tool-approvals-e2e");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        // O primeiro signup vira admin; depois dele o papel padrão dos
        // próximos precisa ser "user" — senão nascem pending e a API
        // devolve 401 nas rotas autorizadas.
        var adminToken = await SignUpTokenAsync("admin@approvals.local");
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", adminToken);
        var updated = await _client.PostAsJsonAsync(
            "/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        TestInfra.DeleteDb(_dbPath);
    }

    private async Task<string> SignUpTokenAsync(string email)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest("User", email, "senha123"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!.Token;
    }

    [Test]
    public async Task Catalogo_RequerAutenticacao() =>
        Assert.That(
            (await _client.GetAsync("/api/v1/users/user/tool-approvals"))
                .StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));

    [Test]
    public async Task CatalogoEPatch_RoundTrip()
    {
        var token = await SignUpTokenAsync("a@approvals.local");
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var catalog = await _client.GetFromJsonAsync<ToolApprovalsResponse>(
            "/api/v1/users/user/tool-approvals");
        Assert.That(catalog, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(catalog!.Tools, Is.Not.Empty);
            Assert.That(catalog.Tools.All(t => t.Override is null), Is.True);
            Assert.That(
                catalog.Tools.First(t => t.Name == "shell_exec").Tier,
                Is.EqualTo("exec"));
            Assert.That(
                catalog.Tools.First(t => t.Name == "file_read").Tier,
                Is.EqualTo("read"));
        });

        var merged = await PutAsync(new UpdateToolApprovalsRequest(
            new Dictionary<string, string?>
            {
                ["shell_exec"] = "deny",
                ["fetch_url"] = "prompt",
            }));
        Assert.Multiple(() =>
        {
            Assert.That(merged!.Tools.First(t => t.Name == "shell_exec")
                .Override, Is.EqualTo("deny"));
            Assert.That(merged.Tools.First(t => t.Name == "fetch_url")
                .Override, Is.EqualTo("prompt"));
            Assert.That(merged.Tools.First(t => t.Name == "file_read")
                .Override, Is.Null);
        });

        // null remove o override.
        var removed = await PutAsync(new UpdateToolApprovalsRequest(
            new Dictionary<string, string?> { ["shell_exec"] = null }));
        Assert.That(removed!.Tools.First(t => t.Name == "shell_exec")
            .Override, Is.Null);
    }

    [Test]
    public async Task Patch_ValidaAcoes()
    {
        var token = await SignUpTokenAsync("b@approvals.local");
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var bad = await _client.PutAsJsonAsync(
            "/api/v1/users/user/tool-approvals",
            new UpdateToolApprovalsRequest(
                new Dictionary<string, string?> { ["shell_exec"] = "talvez" }));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var huge = new UpdateToolApprovalsRequest(
            Enumerable.Range(0, 201)
                .ToDictionary(i => $"t{i}", _ => (string?)"allow"));
        var tooBig = await _client.PutAsJsonAsync(
            "/api/v1/users/user/tool-approvals", huge);
        Assert.That(tooBig.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Patch_IsoladoPorUsuario()
    {
        var tokenA = await SignUpTokenAsync("c@approvals.local");
        var tokenB = await SignUpTokenAsync("d@approvals.local");
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenA);
        await PutAsync(new UpdateToolApprovalsRequest(
            new Dictionary<string, string?> { ["web_search"] = "allow" }));

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenB);
        var catalogB = await _client.GetFromJsonAsync<ToolApprovalsResponse>(
            "/api/v1/users/user/tool-approvals");
        Assert.That(catalogB!.Tools.First(t => t.Name == "web_search")
            .Override, Is.Null);
    }

    private async Task<ToolApprovalsResponse?> PutAsync(
        UpdateToolApprovalsRequest request)
    {
        var response = await _client.PutAsJsonAsync(
            "/api/v1/users/user/tool-approvals", request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return await response.Content
            .ReadFromJsonAsync<ToolApprovalsResponse>();
    }
}
