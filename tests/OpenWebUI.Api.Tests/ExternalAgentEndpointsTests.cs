using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Api.Endpoints;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura HTTP de <c>/api/v1/external-agents</c> (SPEC-20261016-external-
/// agent-registry): detect via PATH e toggle que instala/remove a skill
/// ponte no HOME fake — os testes de serviço em
/// <see cref="ExternalAgentTests"/> não passam pela camada de rota.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable] // muta ExternalAgentService.HomeResolver/PathResolver
public class ExternalAgentEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _root = null!;
    private string _home = null!;
    private string _pathDir = null!;
    private Func<string> _homeOriginal = null!;
    private Func<string?> _pathOriginal = null!;
    private string _adminToken = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-ext-ep-{Guid.NewGuid():N}");
        _home = Path.Join(_root, "home");
        _pathDir = Path.Join(_root, "bin");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_pathDir);
        _homeOriginal = ExternalAgentService.HomeResolver;
        _pathOriginal = ExternalAgentService.PathResolver;
        ExternalAgentService.HomeResolver = () => _home;
        ExternalAgentService.PathResolver = () => _pathDir;
        ExternalAgentService.ResetDetectCache();

        _dbPath = Path.Join(_root, "test.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@ext.local");
        _adminToken = admin.Token;
        UseToken(_adminToken);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        ExternalAgentService.HomeResolver = _homeOriginal;
        ExternalAgentService.PathResolver = _pathOriginal;
        ExternalAgentService.ResetDetectCache();
        TestInfra.DeleteDb(_dbPath);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, "senha123"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    private string StubBin(string name)
    {
        var path = Path.Join(_pathDir, name);
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserExecute | UnixFileMode.UserRead);
        }
        ExternalAgentService.ResetDetectCache();
        return path;
    }

    [Test]
    public async Task Detect_RetornaListaComFlags()
    {
        var resp = await _client.GetAsync("/api/v1/external-agents/detect");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var list = await resp.Content
            .ReadFromJsonAsync<List<ExternalAgentStatus>>();
        Assert.Multiple(() =>
        {
            Assert.That(list, Is.Not.Null.And.Not.Empty);
            Assert.That(list!.Any(a => a.Id == "claude"), Is.True);
            Assert.That(list!.All(a => a.Installed == false), Is.True); // PATH fake vazio
        });
    }

    [Test]
    public async Task SetSelected_Desconhecido_404_SemBinario_409_ComBinario_200()
    {
        var unknown = await _client.PutAsJsonAsync(
            "/api/v1/external-agents/fantasma/selected",
            new ExternalAgentSelectRequest(true));
        Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Agente conhecido mas sem binário no PATH → 409.
        var missing = await _client.PutAsJsonAsync(
            "/api/v1/external-agents/claude/selected",
            new ExternalAgentSelectRequest(true));
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        // Com binário stubado no PATH fake → instala a skill ponte no home.
        StubBin("claude");
        var ok = await _client.PutAsJsonAsync(
            "/api/v1/external-agents/claude/selected",
            new ExternalAgentSelectRequest(true));
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Desabilitar remove a seleção.
        var off = await _client.PutAsJsonAsync(
            "/api/v1/external-agents/claude/selected",
            new ExternalAgentSelectRequest(false));
        Assert.That(off.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Detect_SemAuth_Unauthorized()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        try
        {
            var resp = await _client.GetAsync("/api/v1/external-agents/detect");
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
        finally
        {
            UseToken(_adminToken);
        }
    }
}
