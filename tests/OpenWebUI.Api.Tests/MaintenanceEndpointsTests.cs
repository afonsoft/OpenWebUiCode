using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Api.Endpoints;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura HTTP dos endpoints de manutenção (SPEC-20261016-session-
/// maintenance RF-004/RF-005): status, disparo sob demanda (admin) e
/// config — os testes de serviço em <see cref="MaintenanceServicesTests"/>
/// não exercitam a camada de rota.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable] // RetentionService.TryEnterCycle é gate estático
public class MaintenanceEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;
    private string _userToken = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-maint-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@maint.local");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);

        var user = await SignUpAsync("User", "user@maint.local");
        _userToken = user.Token;
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
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    [Test]
    public async Task Status_RetornaResumoDoCiclo()
    {
        UseToken(_userToken);
        var resp = await _client.GetAsync("/api/v1/maintenance/status");
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await resp.Content.ReadFromJsonAsync<MaintenanceStatusResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(body, Is.Not.Null);
            Assert.That(body!.DbSizeBytes, Is.GreaterThan(0));
            Assert.That(body.ChatDays, Is.GreaterThanOrEqualTo(0));
        });
    }

    [Test]
    public async Task Run_DryRun_ComoAdmin_Ok_NaoAdmin_403()
    {
        UseToken(_userToken);
        var forbidden = await _client.PostAsJsonAsync("/api/v1/maintenance/run",
            new MaintenanceRunRequest(true));
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_adminToken);
        var resp = await _client.PostAsJsonAsync("/api/v1/maintenance/run",
            new MaintenanceRunRequest(false));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await resp.Content.ReadAsStringAsync());
        var result = await resp.Content.ReadFromJsonAsync<MaintenanceCycleResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(result!.DryRun, Is.False);
            Assert.That(result.FinishedAt, Is.GreaterThanOrEqualTo(result.StartedAt));
        });

        // Após o ciclo, o status reflete o último run.
        var status = await _client.GetFromJsonAsync<MaintenanceStatusResponse>(
            "/api/v1/maintenance/status");
        Assert.That(status!.LastRun, Is.Not.Null);
    }

    [Test]
    public async Task Run_EmAndamento_Retorna409()
    {
        Assert.That(RetentionService.TryEnterCycle(), Is.True);
        try
        {
            UseToken(_adminToken);
            var resp = await _client.PostAsJsonAsync("/api/v1/maintenance/run",
                new MaintenanceRunRequest(false));
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        }
        finally
        {
            RetentionService.ExitCycle();
        }
    }

    [Test]
    public async Task SetConfig_AdminOk_BadRange_NaoAdmin403()
    {
        UseToken(_adminToken);
        var ok = await _client.PutAsJsonAsync("/api/v1/maintenance/config",
            new MaintenanceConfigRequest(30));
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var bad = await _client.PutAsJsonAsync("/api/v1/maintenance/config",
            new MaintenanceConfigRequest(-1));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        UseToken(_userToken);
        var forbidden = await _client.PutAsJsonAsync("/api/v1/maintenance/config",
            new MaintenanceConfigRequest(30));
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        // Config gravada reflete no status.
        UseToken(_adminToken);
        var status = await _client.GetFromJsonAsync<MaintenanceStatusResponse>(
            "/api/v1/maintenance/status");
        Assert.That(status!.ChatDays, Is.EqualTo(30));
    }

    [Test]
    public async Task Endpoints_SemAuth_Unauthorized()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        try
        {
            var resp = await _client.GetAsync("/api/v1/maintenance/status");
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

            var run = await _client.PostAsJsonAsync("/api/v1/maintenance/run",
                new MaintenanceRunRequest(true));
            Assert.That(run.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

            var config = await _client.PutAsJsonAsync("/api/v1/maintenance/config",
                new MaintenanceConfigRequest(30));
            Assert.That(config.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        }
        finally
        {
            UseToken(_adminToken);
        }
    }
}
