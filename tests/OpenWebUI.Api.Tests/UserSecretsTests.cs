using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261017-agent-secrets-env: store de secrets em 2 níveis com
/// override user→global, validação de nomes reservados, injeção de env e
/// scrub literal nos processos (shell/jobs/PTY).
/// </summary>
[TestFixture, IsolateEnvironment]
public class UserSecretsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-secrets-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("ROOT", "root@sec.local");
        UseToken(_admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
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

    // ---------------- API user/admin + override ----------------

    [Test]
    public async Task Secrets_Crud_EOverrideUserGlobal()
    {
        var user = await SignUpAsync("SUSR", "susr@sec.local");

        // Admin define o global; usuário vê como source=global.
        UseToken(_admin.Token);
        var put = await _client.PutAsJsonAsync(
            "/api/v1/configs/secrets/SONAR_TOKEN", new SecretUpsertRequest("sqp_global"));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await put.Content.ReadAsStringAsync());

        UseToken(user.Token);
        var list = await _client.GetFromJsonAsync<List<SecretEntryResponse>>("/api/v1/secrets");
        var entry = list!.Single(s => s.Name == "SONAR_TOKEN");
        Assert.That(entry.Source, Is.EqualTo("global"));

        // Override do usuário vence; remoção do override restaura o global.
        await _client.PutAsJsonAsync(
            "/api/v1/secrets/SONAR_TOKEN", new SecretUpsertRequest("sqp_user"));
        list = await _client.GetFromJsonAsync<List<SecretEntryResponse>>("/api/v1/secrets");
        Assert.That(list!.Single(s => s.Name == "SONAR_TOKEN").Source, Is.EqualTo("user"));

        await _client.DeleteAsync("/api/v1/secrets/SONAR_TOKEN");
        list = await _client.GetFromJsonAsync<List<SecretEntryResponse>>("/api/v1/secrets");
        Assert.That(list!.Single(s => s.Name == "SONAR_TOKEN").Source, Is.EqualTo("global"));

        // Valor nunca aparece na listagem.
        var raw = await (await _client.GetAsync("/api/v1/secrets")).Content.ReadAsStringAsync();
        Assert.That(raw, Does.Not.Contain("sqp_global").And.Not.Contain("sqp_user"));
    }

    [Test]
    public async Task Secrets_NomesReservados_EInvalidos_400()
    {
        var user = await SignUpAsync("SBAD", "sbad@sec.local");
        UseToken(user.Token);

        foreach (var name in new[] { "GITHUB_TOKEN", "GH_TOKEN", "GIT_ASKPASS",
                                     "PATH", "HOME", "LD_PRELOAD", "1BAD", "COM ESPACO" })
        {
            var resp = await _client.PutAsJsonAsync(
                $"/api/v1/secrets/{Uri.EscapeDataString(name)}",
                new SecretUpsertRequest("valor123"));
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
                $"{name} devia ser rejeitado");
        }
    }

    [Test]
    public async Task Secrets_IsolamentoEntreUsuarios()
    {
        var a = await SignUpAsync("SUSA", "susa@sec.local");
        var b = await SignUpAsync("SUSB", "susb@sec.local");

        UseToken(a.Token);
        await _client.PutAsJsonAsync(
            "/api/v1/secrets/MY_KEY", new SecretUpsertRequest("aaa12345"));

        UseToken(b.Token);
        var list = await _client.GetFromJsonAsync<List<SecretEntryResponse>>("/api/v1/secrets");
        Assert.That(list!.Any(s => s.Name == "MY_KEY"), Is.False,
            "secret de outro usuário não vaza na listagem");
    }

    [Test]
    public async Task Secrets_Global_ExigeAdmin()
    {
        var user = await SignUpAsync("SNAD", "snad@sec.local");
        UseToken(user.Token);

        var resp = await _client.PutAsJsonAsync(
            "/api/v1/configs/secrets/X", new SecretUpsertRequest("v1234"));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var del = await _client.DeleteAsync("/api/v1/configs/secrets/X");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    // ---------------- Env + scrub ----------------

    /// <summary>RF-005/007: env injetada no processo e literal mascarado na saída.</summary>
    [Test]
    public async Task Env_Injetada_EScrubNaSaida()
    {
        var dir = Path.Join(Path.GetTempPath(), $"owui-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var env = new Dictionary<string, string> { ["SONAR_TOKEN"] = "sqp_secreto" };
            var outcome = await ChatProcessRunner.RunAsync(
                "echo tok=$SONAR_TOKEN", dir, TimeSpan.FromSeconds(10),
                new ChatProcessRunner.RunOptions(Env: env, SecretsToScrub: env.Values.ToList()));

            Assert.That(outcome.ExitCode, Is.EqualTo(0));
            Assert.That(outcome.Output, Does.Contain($"tok={SecretScrubber.Redacted}"));
            Assert.That(outcome.Output, Does.Not.Contain("sqp_secreto"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>RF-005: PAT resolve → vars gerenciadas + askpass; scrub cobre o PAT.</summary>
    [Test]
    public async Task Env_PatGithub_ViraManagedVars()
    {
        var user = await SignUpAsync("SGH", "sgh@sec.local");
        await using var scope = _factory.Services.CreateAsyncScope();
        var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
        await config.SetAsync($"u:{user.User.Id}:github.token",
            "ghp_teste0123456789abcdef0123456789abcdef", CancellationToken.None);

        var envService = scope.ServiceProvider.GetRequiredService<AgentSecretsEnvService>();
        var env = await envService.BuildAsync(user.User.Id, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(env.Vars["GITHUB_TOKEN"], Does.StartWith("ghp_teste"));
            Assert.That(env.Vars["GH_TOKEN"], Is.EqualTo(env.Vars["GITHUB_TOKEN"]));
            Assert.That(env.Vars["GIT_TERMINAL_PROMPT"], Is.EqualTo("0"));
            Assert.That(env.Vars["GIT_ASKPASS"], Does.EndWith("git-askpass.sh"));
            Assert.That(env.SecretsToScrub, Does.Contain(env.Vars["GITHUB_TOKEN"]));
        });

        // O script existe, é executável e NÃO contém o token literal.
        var script = File.ReadAllText(env.Vars["GIT_ASKPASS"]);
        Assert.That(script, Does.Contain("GITHUB_TOKEN"));
        Assert.That(script, Does.Not.Contain("ghp_teste"));
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(env.Vars["GIT_ASKPASS"]);
            Assert.That(mode.HasFlag(UnixFileMode.UserExecute), Is.True);
        }

        // Secret de nome reservado não pode existir — managed vence por construção.
        await config.SetAsync($"u:{user.User.Id}:github.token", (string?)null,
            CancellationToken.None);
        var envSemPat = await envService.BuildAsync(user.User.Id, CancellationToken.None);
        Assert.That(envSemPat.Vars.ContainsKey("GITHUB_TOKEN"), Is.False);
        Assert.That(File.Exists(env.Vars["GIT_ASKPASS"]), Is.False,
            "PAT removido → askpass limpo");
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
}
