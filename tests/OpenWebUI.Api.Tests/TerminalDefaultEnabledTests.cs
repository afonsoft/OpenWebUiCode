using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Trava o default da feature flag do terminal PTY: sem
/// <c>Terminal:Enabled</c>, <c>TERMINAL_ENABLED</c> ou valor persistido em
/// <c>terminal.enabled</c>, o endpoint deve reportar <c>enabled=true</c>.
/// Fixture próprio porque <see cref="TerminalPtyTests"/> persiste
/// <c>enabled=true</c> no setup e não observaria o default.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class TerminalDefaultEnabledTests
{
    [Test]
    public async Task Config_SemFlagNemPersistencia_DefaultHabilitado()
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"openwebui-termdef-{Guid.NewGuid():N}.db");
        try
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={dbPath}");
            Environment.SetEnvironmentVariable("TERMINAL_ENABLED", null);
            await using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();

            var signup = await client.PostAsJsonAsync(
                "/api/v1/auths/signup", new SignUpRequest("TermDef", "t@termdef.local", "senha123"));
            Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var auth = await signup.Content.ReadFromJsonAsync<AuthResponse>();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", auth!.Token);

            var response = await client.GetAsync("/api/v1/terminal/config");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.That(body.GetProperty("enabled").GetBoolean(), Is.True);
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                TestInfra.DeleteDb(dbPath);
            }
        }
    }
}
