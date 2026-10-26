using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Api.Endpoints;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>CRUD HTTP de /api/v1/references (SPEC-20261018-references).</summary>
[TestFixture, IsolateEnvironment]
public class ReferenceEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _dir = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-refs-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _dir = Path.Join(Path.GetTempPath(), $"refs-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Join(_dir, "readme.md"), "# ref");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        // Primeiro usuário = admin; libera "user" como papel padrão
        // (senão os signups seguintes ficam pending e dão 401).
        var admin = await SignUpAsync("Admin", "admin@refs.local", "senha123");
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync(
            "/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
    }

    private int _userSeq;

    /// <summary>Cada teste autentica um usuário novo — refs são por usuário,
    /// sem isolamento a lista de um vazaria no outro.</summary>
    private async Task<AuthResponse> NewUserAsync()
    {
        var n = Interlocked.Increment(ref _userSeq);
        var auth = await SignUpAsync($"Refs{n}", $"refs{n}@test.local", "senha123");
        UseToken(auth.Token);
        return auth;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath)) TestInfra.DeleteDb(_dbPath);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
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
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    [Test]
    public async Task Crud_CicloCompleto()
    {
        await NewUserAsync();
        // create
        var created = await _client.PostAsJsonAsync(
            "/api/v1/references", new ReferenceEndpoints.ReferenceBody(
                "docs", _dir, null, "documentação", false));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created),
            await created.Content.ReadAsStringAsync());

        // list
        var list = await _client.GetFromJsonAsync<JsonElement[]>(
            "/api/v1/references");
        Assert.That(list, Has.Length.EqualTo(1));
        var id = list![0].GetProperty("id").GetString()!;
        Assert.That(list[0].GetProperty("alias").GetString(), Is.EqualTo("docs"));

        // tree — resolve o dir local e lista o arquivo.
        var tree = await _client.GetFromJsonAsync<JsonElement>(
            "/api/v1/references/docs/tree");
        Assert.That(
            tree.GetProperty("paths").EnumerateArray()
                .Select(p => p.GetString()), Does.Contain("readme.md"));

        // refresh (path local — só marca pronto).
        var refreshed = await _client.PostAsync(
            $"/api/v1/references/{id}/refresh", null);
        Assert.That(refreshed.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // patch — alias novo + hidden.
        var patched = await _client.PatchAsJsonAsync(
            $"/api/v1/references/{id}", new ReferenceEndpoints.ReferenceBody(
                "docs2", null, null, null, true));
        Assert.That(patched.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await patched.Content.ReadAsStringAsync());
        Assert.That(patched.Headers, Is.Not.Null);
        var view = await patched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(view.GetProperty("alias").GetString(), Is.EqualTo("docs2"));
        Assert.That(view.GetProperty("hidden").GetBoolean(), Is.True);

        // visible=true filtra a oculta.
        var visible = await _client.GetFromJsonAsync<JsonElement[]>(
            "/api/v1/references?visible=true");
        Assert.That(visible, Is.Empty);

        // delete
        var deleted = await _client.DeleteAsync($"/api/v1/references/{id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var after = await _client.GetFromJsonAsync<JsonElement[]>(
            "/api/v1/references");
        Assert.That(after, Is.Empty);
    }

    [Test]
    public async Task Create_RejeitaAliasInvalidoEDuplicado()
    {
        await NewUserAsync();
        var bad = await _client.PostAsJsonAsync(
            "/api/v1/references", new ReferenceEndpoints.ReferenceBody(
                "Alias Ruim!", _dir, null, null, null));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var noSource = await _client.PostAsJsonAsync(
            "/api/v1/references", new ReferenceEndpoints.ReferenceBody(
                "ok-alias", " ", null, null, null));
        Assert.That(noSource.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var first = await _client.PostAsJsonAsync(
            "/api/v1/references", new ReferenceEndpoints.ReferenceBody(
                "dup", _dir, null, null, null));
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var second = await _client.PostAsJsonAsync(
            "/api/v1/references", new ReferenceEndpoints.ReferenceBody(
                "dup", _dir, null, null, null));
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task NotFound_PatchDeleteTreeRefresh()
    {
        await NewUserAsync();
        var missing = Guid.NewGuid().ToString("N");
        Assert.That((await _client.PatchAsJsonAsync(
            $"/api/v1/references/{missing}", new ReferenceEndpoints.ReferenceBody(
                "x", null, null, null, null)))
            .StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.DeleteAsync(
            $"/api/v1/references/{missing}"))
            .StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.PostAsync(
            $"/api/v1/references/{missing}/refresh", null))
            .StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.GetAsync(
            "/api/v1/references/inexistente/tree"))
            .StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Endpoints_ExigemAuth()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        Assert.That((await _client.GetAsync("/api/v1/references"))
            .StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
