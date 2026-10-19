using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do SPEC-20261014-config-layers: resolvedor em camadas
/// (default → global → user, RF-001), catálogo efetivo de MCP por
/// usuário (RF-002), fallback de PAT do GitHub (RF-003), cadeias
/// globais de papéis de modelo (RF-004), chaves BYOK (RF-005) e o
/// invariante "deny admin &gt; tudo" das repo-skills (RF-006).
/// </summary>
[TestFixture]
public class ConfigLayersTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp() => _dbPath = TestInfra.NewDbPath("config-layers");

    [TearDown]
    public void TearDown() => TestInfra.DeleteDb(_dbPath);

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static User NewUser(string id) =>
        new() { Id = id, Name = id, Email = $"{id}@t.dev", Role = "user" };

    private static McpServer Server(string name, string? userId, bool enabled = true) =>
        new()
        {
            Name = name, Transport = "http", Url = $"https://{name}.example.com",
            Enabled = enabled, UserId = userId,
        };

    // ---------------- RF-001: resolvedor em camadas ----------------

    [Test]
    public async Task GetAsync_Provenance_Default_Global_User()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        var overlay = new UserConfigOverlayService(db, config);

        var semValor = await overlay.GetAsync<string>("u1", "github.org", ct: default);
        Assert.That(semValor.Source, Is.EqualTo(ConfigSource.Default));

        await config.SetAsync("github.org", "acme", default);
        var global = await overlay.GetAsync<string>("u1", "github.org", ct: default);
        Assert.Multiple(() =>
        {
            Assert.That(global.Source, Is.EqualTo(ConfigSource.Global));
            Assert.That(global.Value, Is.EqualTo("acme"));
        });

        await overlay.SetUserAsync("u1", "github.org", "minha-org", default);
        var user = await overlay.GetAsync<string>("u1", "github.org", ct: default);
        Assert.Multiple(() =>
        {
            Assert.That(user.Source, Is.EqualTo(ConfigSource.User));
            Assert.That(user.Value, Is.EqualTo("minha-org"));
        });

        // Reset do override → volta a herdar o global.
        await overlay.SetUserAsync<string>("u1", "github.org", null, default);
        var herdado = await overlay.GetAsync<string>("u1", "github.org", ct: default);
        Assert.That(herdado.Source, Is.EqualTo(ConfigSource.Global));
    }

    // ---------------- RF-002: catálogo efetivo de MCP ----------------

    [Test]
    public async Task Mcp_Catalogo_IsolaServidoresPorUsuario()
    {
        await using var db = NewDb();
        db.McpServers.AddRange(
            Server("global-a", null),
            Server("do-b", "u2"),
            Server("do-a", "u1"));
        await db.SaveChangesAsync();

        var overlay = new UserConfigOverlayService(db, new ConfigService(db, TestCache.Create()));
        var a = await overlay.EffectiveMcpServersAsync("u1", default);
        var b = await overlay.EffectiveMcpServersAsync("u2", default);

        Assert.Multiple(() =>
        {
            Assert.That(a.Select(e => e.Server.Name),
                Is.EquivalentTo(new[] { "global-a", "do-a" }));
            Assert.That(b.Select(e => e.Server.Name),
                Is.EquivalentTo(new[] { "global-a", "do-b" }));
            Assert.That(a.Single(e => e.Server.Name == "global-a").Source,
                Is.EqualTo(ConfigSource.Global));
            Assert.That(a.Single(e => e.Server.Name == "do-a").Source,
                Is.EqualTo(ConfigSource.User));
        });
    }

    [Test]
    public async Task Mcp_DesligarGlobal_AfetaSoEsseUsuario()
    {
        await using var db = NewDb();
        var global = Server("corp", null);
        db.McpServers.Add(global);
        await db.SaveChangesAsync();

        var config = new ConfigService(db, TestCache.Create());
        var overlay = new UserConfigOverlayService(db, config);
        await overlay.SetMcpDisabledAsync("u1", global.Id, disabled: true, default);

        var a = await overlay.EffectiveMcpServersAsync("u1", default);
        var b = await overlay.EffectiveMcpServersAsync("u2", default);
        var hiddenA = await overlay.HiddenMcpServerIdsAsync("u1", default);
        var hiddenB = await overlay.HiddenMcpServerIdsAsync("u2", default);
        Assert.Multiple(() =>
        {
            Assert.That(a.Single(e => e.Server.Name == "corp").EnabledForMe, Is.False);
            Assert.That(b.Single(e => e.Server.Name == "corp").EnabledForMe, Is.True);
            Assert.That(hiddenA, Does.Contain(global.Id));
            Assert.That(hiddenB, Does.Not.Contain(global.Id));
        });
    }

    [Test]
    public async Task Mcp_SombreamentoPorNome_EAdminDisabled()
    {
        await using var db = NewDb();
        var global = Server("corp", null);
        var adminOff = Server("off", null, enabled: false);
        var sombra = Server("corp", "u1");
        db.McpServers.AddRange(global, adminOff, sombra);
        await db.SaveChangesAsync();

        var overlay = new UserConfigOverlayService(db, new ConfigService(db, TestCache.Create()));
        var a = await overlay.EffectiveMcpServersAsync("u1", default);
        var hidden = await overlay.HiddenMcpServerIdsAsync("u1", default);

        Assert.Multiple(() =>
        {
            // O servidor próprio "corp" sombreia o global homônimo;
            // o desligado pelo admin some do catálogo.
            Assert.That(a.Select(e => e.Server.Name), Is.EquivalentTo(new[] { "corp" }));
            Assert.That(a[0].Server.UserId, Is.EqualTo("u1"));
            Assert.That(hidden, Does.Contain(global.Id));
            Assert.That(hidden, Does.Contain(adminOff.Id));
        });
    }

    // ---------------- RF-003: GitHub user → global ----------------

    [Test]
    public async Task GitHub_TokenDoUsuarioVenceGlobal_EFallback()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        var github = new GitHubService(new StubHttpFactory(), config);

        // Sem camadas: não configurado.
        Assert.That((await github.GetTokenAsync("u1", default)), Is.Null);

        await config.SetAsync(GitHubService.GlobalTokenKey, "pat-global", default);
        Assert.Multiple(() =>
        {
            Assert.That(github.GetTokenAsync("u1", default).Result, Is.EqualTo("pat-global"));
            Assert.That(github.GetStatusAsync("u1", default).Result.Source,
                Is.EqualTo("global"));
            Assert.That(github.GetTokenAsync("u2", default).Result, Is.EqualTo("pat-global"));
        });

        await config.SetAsync("u:u1:github.token", "pat-u1", default);
        Assert.Multiple(() =>
        {
            Assert.That(github.GetTokenAsync("u1", default).Result, Is.EqualTo("pat-u1"));
            Assert.That(github.GetStatusAsync("u1", default).Result.Source, Is.EqualTo("user"));
            Assert.That(github.GetTokenAsync("u2", default).Result, Is.EqualTo("pat-global"));
        });
    }

    /// <summary>Factory stub — os testes de resolução nunca chegam ao HTTP.</summary>
    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // ---------------- RF-004: model-roles com fallback global ----------------

    [Test]
    public async Task ModelRoles_GlobalCaiQuandoUsuarioNaoTem()
    {
        await using var db = NewDb();
        db.Users.Add(NewUser("u1"));
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        var resolver = new ModelRoleResolver(db, config,
            _ => Task.FromResult(new List<ModelInfo>
            {
                new("m-global", "g", "ollama", "x"),
                new("m-user", "u", "ollama", "x"),
            }),
            NullLogger<ModelRoleResolver>.Instance);

        await resolver.SetGlobalRolesAsync(
            new Dictionary<string, List<string>> { ["tasks"] = ["m-global"] }, default);

        // Sem override do usuário → cadeia global.
        Assert.That(await resolver.ResolveAsync("tasks", "u1", "chat", default),
            Is.EqualTo("m-global"));

        // Override do usuário vence a cadeia global.
        await resolver.SetRolesAsync("u1",
            new Dictionary<string, List<string>> { ["tasks"] = ["m-user"] }, default);
        Assert.That(await resolver.ResolveAsync("tasks", "u1", "chat", default),
            Is.EqualTo("m-user"));

        // Usuário com cadeia inválida cai na global (concatenada).
        await resolver.SetRolesAsync("u1",
            new Dictionary<string, List<string>> { ["memory"] = ["nao-existe"] }, default);
        await resolver.SetGlobalRolesAsync(
            new Dictionary<string, List<string>> { ["memory"] = ["m-global"] }, default);
        Assert.That(await resolver.ResolveAsync("memory", "u1", "chat", default),
            Is.EqualTo("m-global"));
    }

    // ---------------- RF-005: BYOK ----------------

    [Test]
    public async Task Byok_FlagOff_IgnoraChaveDoUsuario()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync(
            UserConfigOverlayService.ProviderKeysKey("u1"),
            new Dictionary<string, string> { ["openai"] = "sk-user" }, default);

        var providers = NewProviderService(config);
        var connections = new ConnectionsConfig([], ["https://api"], ["sk-global"]);
        var eff = await providers.ApplyUserKeysAsync(connections, "u1", default);
        Assert.That(eff.OpenAiApiKeys[0], Is.EqualTo("sk-global"));
    }

    [Test]
    public async Task Byok_FlagOn_ChaveDoUsuarioCobreTipo()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync(UserConfigOverlayService.UserProviderKeysFlag, true, default);
        await config.SetAsync(
            UserConfigOverlayService.ProviderKeysKey("u1"),
            new Dictionary<string, string>
            {
                ["openai"] = "sk-user",
                ["anthropic"] = "sk-ant-user",
            }, default);

        var providers = NewProviderService(config);
        var connections = new ConnectionsConfig(
            [], ["https://api"], ["sk-global"],
            Providers:
            [
                new ProviderConnection("anthropic", "https://ant", "sk-ant-global"),
                new ProviderConnection("google", "https://goog", "sk-goog-global"),
            ]);
        var eff = await providers.ApplyUserKeysAsync(connections, "u1", default);

        Assert.Multiple(() =>
        {
            Assert.That(eff.OpenAiApiKeys[0], Is.EqualTo("sk-user"));
            Assert.That(eff.ProvidersOrEmpty.Single(p => p.Type == "anthropic").ApiKey,
                Is.EqualTo("sk-ant-user"));
            // Sem chave do usuário para google → permanece a global.
            Assert.That(eff.ProvidersOrEmpty.Single(p => p.Type == "google").ApiKey,
                Is.EqualTo("sk-goog-global"));
        });

        // Outro usuário sem chaves recebe a config global intacta.
        var outro = await providers.ApplyUserKeysAsync(connections, "u2", default);
        Assert.That(outro.OpenAiApiKeys[0], Is.EqualTo("sk-global"));
    }

    private static ProviderService NewProviderService(ConfigService config) =>
        new(new StubHttpFactory(), config,
            NullLogger<ProviderService>.Instance, TestCache.Create(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    // ---------------- RF-006: repo-skills deny absoluto ----------------

    [Test]
    public async Task Skills_DenyAdmin_AbsolutoEUneComUsuario()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        var overlay = new UserConfigOverlayService(db, config);

        await config.SetAsync(
            UserConfigOverlayService.GlobalSkillsDisabledKey, new List<string> { "perigosa" }, default);
        await overlay.SetSkillDisabledAsync("u1", "minha-off", disabled: true, default);

        var hidden = await overlay.HiddenSkillNamesAsync("u1", default);
        Assert.Multiple(() =>
        {
            Assert.That(hidden, Does.Contain("perigosa"));
            Assert.That(hidden, Does.Contain("minha-off"));
        });

        // Toggle do usuário não afeta o deny global nem outro usuário.
        await overlay.SetSkillDisabledAsync("u1", "minha-off", disabled: false, default);
        var depois = await overlay.HiddenSkillNamesAsync("u1", default);
        Assert.Multiple(() =>
        {
            Assert.That(depois, Does.Not.Contain("minha-off"));
            Assert.That(depois, Does.Contain("perigosa"));
        });
        Assert.That(await overlay.HiddenSkillNamesAsync("u2", default),
            Is.EquivalentTo(new[] { "perigosa" }));
    }
}

/// <summary>
/// E2E dos endpoints de camada: catálogo MCP do usuário com provenance,
/// isolamento entre usuários e BYOK expondo só <c>hasKey</c>.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ConfigLayersE2ETests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-cfglayers-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@cfg.local");
        _adminToken = admin.Token;
        UseToken(admin.Token);
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
        TestInfra.DeleteDb(_dbPath);
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name, email, password = "senha123" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private sealed record AuthResponse(string Token);

    [Test]
    public async Task UserMcp_CrudIsoladoEToggleGlobal()
    {
        var user = await SignUpAsync("B", "b@cfg.local");

        // Admin cria um servidor global (transporte remoto http).
        UseToken(_adminToken);
        var created = await _client.PostAsJsonAsync("/api/v1/mcp/servers",
            new
            {
                name = "corp-mcp", transport = "http",
                url = "https://mcp.example.com/sse", enabled = true,
            });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var globalId = (await created.Content
            .ReadFromJsonAsync<JsonElementDict>())!.GetProperty("id").GetString()!;

        // Usuário vê o global com provenance "global" e adiciona o seu.
        UseToken(user.Token);
        var list = await _client.GetFromJsonAsync<JsonElementDict[]>(
            "/api/v1/users/user/mcp-servers");
        Assert.That(list!.Single(e => e.GetProperty("server")
            .GetProperty("name").GetString() == "corp-mcp")
            .GetProperty("source").GetString(), Is.EqualTo("global"));

        var mine = await _client.PostAsJsonAsync("/api/v1/users/user/mcp-servers",
            new
            {
                name = "meu-mcp", transport = "http",
                url = "https://meu.example.com/sse", enabled = true,
            });
        Assert.That(mine.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await mine.Content.ReadAsStringAsync());
        var mineId = (await mine.Content
            .ReadFromJsonAsync<JsonElementDict>())!.GetProperty("id").GetString()!;

        // O servidor do usuário não aparece para o admin nem no catálogo global.
        UseToken(_adminToken);
        var adminList = await _client.GetFromJsonAsync<JsonElementDict[]>("/api/v1/mcp/servers");
        Assert.That(adminList!.Select(e => e.GetProperty("name").GetString()),
            Is.EquivalentTo(new[] { "corp-mcp" }));

        // Desligar o global só para o usuário.
        UseToken(user.Token);
        var off = await _client.PutAsJsonAsync(
            $"/api/v1/users/user/mcp-servers/{globalId}/enabled", new { enabled = false });
        Assert.That(off.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var after = await _client.GetFromJsonAsync<JsonElementDict[]>(
            "/api/v1/users/user/mcp-servers");
        Assert.That(after!.Single(e => e.GetProperty("server")
            .GetProperty("name").GetString() == "corp-mcp")
            .GetProperty("enabledForMe").GetBoolean(), Is.False);

        // stdio de usuário é recusado (admin-only).
        var stdio = await _client.PostAsJsonAsync("/api/v1/users/user/mcp-servers",
            new { name = "x", transport = "stdio", command = "/bin/true", enabled = true });
        Assert.That(stdio.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Delete próprio ok; delete do global é 404 para o usuário.
        var del = await _client.DeleteAsync($"/api/v1/users/user/mcp-servers/{mineId}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var delGlobal = await _client.DeleteAsync($"/api/v1/users/user/mcp-servers/{globalId}");
        Assert.That(delGlobal.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task UserMcp_UpdateRefreshEToggleProprio()
    {
        var user = await SignUpAsync("B6", "b6@cfg.local");
        UseToken(user.Token);

        var created = await _client.PostAsJsonAsync("/api/v1/users/user/mcp-servers",
            new
            {
                name = "srv", transport = "http",
                url = "http://127.0.0.1:1/sse", enabled = true,
                headers = new Dictionary<string, string> { ["x-a"] = "1" },
            });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var id = (await created.Content
            .ReadFromJsonAsync<JsonElementDict>())!.GetProperty("id").GetString()!;

        // Update feliz: troca nome/url, mantém header mascarado, remove ausentes.
        var upd = await _client.PutAsJsonAsync($"/api/v1/users/user/mcp-servers/{id}",
            new
            {
                name = "srv2", transport = "http",
                url = "http://127.0.0.1:2/sse", enabled = false,
                headers = new Dictionary<string, string> { ["x-a"] = "***", ["x-b"] = "2" },
            });
        Assert.That(upd.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await upd.Content.ReadAsStringAsync());
        var body = await upd.Content.ReadFromJsonAsync<JsonElementDict>();
        Assert.That(body!.GetProperty("name").GetString(), Is.EqualTo("srv2"));

        // Transporte stdio de usuário → 400; validate falha → 400; id inexistente → 404.
        var stdio = await _client.PutAsJsonAsync($"/api/v1/users/user/mcp-servers/{id}",
            new { name = "s", transport = "stdio", command = "/bin/true", enabled = true });
        Assert.That(stdio.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var badUrl = await _client.PutAsJsonAsync($"/api/v1/users/user/mcp-servers/{id}",
            new { name = "s", transport = "http", url = "nao-url", enabled = true });
        Assert.That(badUrl.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var notFound = await _client.PutAsJsonAsync("/api/v1/users/user/mcp-servers/nope",
            new { name = "s", transport = "http", url = "http://x.io/s", enabled = true });
        Assert.That(notFound.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Refresh: 404 em id inexistente; 400 quando o servidor está inacessível.
        var refresh404 = await _client.PostAsync("/api/v1/users/user/mcp-servers/nope/refresh", null);
        Assert.That(refresh404.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var refreshBad = await _client.PostAsync($"/api/v1/users/user/mcp-servers/{id}/refresh", null);
        Assert.That(refreshBad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Toggle em servidor próprio muda Enabled direto (sem tombstone).
        var off = await _client.PutAsJsonAsync(
            $"/api/v1/users/user/mcp-servers/{id}/enabled", new { enabled = true });
        Assert.That(off.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var list = await _client.GetFromJsonAsync<JsonElementDict[]>(
            "/api/v1/users/user/mcp-servers");
        Assert.That(list!.Single(e => e.GetProperty("server")
            .GetProperty("name").GetString() == "srv2")
            .GetProperty("enabledForMe").GetBoolean(), Is.True);
    }

    [Test]
    public async Task ProviderKeys_ExpoemSoHasKey_ERespeitamFlag()
    {
        var user = await SignUpAsync("B2", "b2@cfg.local");

        // Flag off → PUT recusado e GET sem provedores.
        UseToken(user.Token);
        var denied = await _client.PutAsJsonAsync(
            "/api/v1/configs/user/provider-keys/openai", new { key = "sk-x" });
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        UseToken(_adminToken);
        var flag = await _client.PostAsJsonAsync(
            "/api/v1/configs/user_provider_keys", new { enabled = true });
        Assert.That(flag.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        UseToken(user.Token);
        var put = await _client.PutAsJsonAsync(
            "/api/v1/configs/user/provider-keys/openai", new { key = "sk-user-secret" });
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var get = await _client.GetAsync("/api/v1/configs/user/provider-keys");
        var body = await get.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body, Does.Contain("openai"));
            // O valor da chave nunca sai na resposta.
            Assert.That(body, Does.Not.Contain("sk-user-secret"));
        });

        // Usuário comum não escreve o flag global.
        UseToken(user.Token);
        var notAdmin = await _client.PostAsJsonAsync(
            "/api/v1/configs/user_provider_keys", new { enabled = false });
        Assert.That(notAdmin.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task ProviderKeys_ToggleGetEDeleteEValidacoes()
    {
        var user = await SignUpAsync("BPK", "bpk@cfg.local");

        // GET do toggle é legível por qualquer usuário autenticado.
        UseToken(user.Token);
        var toggle = await _client.GetAsync("/api/v1/configs/user_provider_keys");
        Assert.That(toggle.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        UseToken(_adminToken);
        var on = await _client.PostAsJsonAsync(
            "/api/v1/configs/user_provider_keys", new { enabled = true });
        Assert.That(on.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        UseToken(user.Token);
        // Validações: provider fora da allowlist e key vazia.
        var badProvider = await _client.PutAsJsonAsync(
            "/api/v1/configs/user/provider-keys/outlook", new { key = "sk-x" });
        Assert.That(badProvider.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var emptyKey = await _client.PutAsJsonAsync(
            "/api/v1/configs/user/provider-keys/openai", new { key = "  " });
        Assert.That(emptyKey.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var put = await _client.PutAsJsonAsync(
            "/api/v1/configs/user/provider-keys/openai", new { key = "sk-del" });
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // DELETE remove a chave → GET não a expõe mais.
        var del = await _client.DeleteAsync(
            "/api/v1/configs/user/provider-keys/openai");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var list = await _client.GetFromJsonAsync<JsonElementDict>(
            "/api/v1/configs/user/provider-keys");
        Assert.That(list!.GetProperty("providers").EnumerateArray(),
            Is.Empty);
    }

    [Test]
    public async Task MemorySync_OverrideEAdminDeny()
    {
        var user = await SignUpAsync("B3", "b3@cfg.local");

        // Default: ligado, 6h/30d, sem negativa do admin.
        UseToken(user.Token);
        var initial = await _client.GetFromJsonAsync<JsonElementDict>(
            "/api/v1/configs/user/memory-sync");
        Assert.Multiple(() =>
        {
            Assert.That(initial!.GetProperty("enabled").GetBoolean(), Is.True);
            Assert.That(initial.GetProperty("intervalHours").GetInt32(), Is.EqualTo(6));
            Assert.That(initial.GetProperty("maxAgeDays").GetInt32(), Is.EqualTo(30));
            Assert.That(initial.GetProperty("adminDisabled").GetBoolean(), Is.False);
        });

        // Override do usuário persiste.
        var put = await _client.PutAsJsonAsync("/api/v1/configs/user/memory-sync",
            new { enabled = false, intervalHours = 12 });
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var after = await _client.GetFromJsonAsync<JsonElementDict>(
            "/api/v1/configs/user/memory-sync");
        Assert.Multiple(() =>
        {
            Assert.That(after!.GetProperty("enabled").GetBoolean(), Is.False);
            Assert.That(after.GetProperty("intervalHours").GetInt32(), Is.EqualTo(12));
        });

        // Faixa inválida rejeitada.
        var bad = await _client.PutAsJsonAsync("/api/v1/configs/user/memory-sync",
            new { intervalHours = 0 });
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Admin desliga globalmente — negativa absoluta: nem reopt-in vale.
        UseToken(_adminToken);
        var adminOff = await _client.PutAsJsonAsync("/api/v1/configs/memory-sync",
            new { enabled = false });
        Assert.That(adminOff.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        UseToken(user.Token);
        var locked = await _client.GetFromJsonAsync<JsonElementDict>(
            "/api/v1/configs/user/memory-sync");
        Assert.Multiple(() =>
        {
            Assert.That(locked!.GetProperty("enabled").GetBoolean(), Is.False);
            Assert.That(locked.GetProperty("adminDisabled").GetBoolean(), Is.True);
        });
        var reoptin = await _client.PutAsJsonAsync("/api/v1/configs/user/memory-sync",
            new { enabled = true });
        Assert.That(reoptin.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var locked2 = await _client.GetFromJsonAsync<JsonElementDict>(
            "/api/v1/configs/user/memory-sync");
        Assert.That(locked2!.GetProperty("enabled").GetBoolean(), Is.False);

        // Usuário não escreve a camada global; admin limpa a negativa.
        var notAdmin = await _client.PutAsJsonAsync("/api/v1/configs/memory-sync",
            new { enabled = true });
        Assert.That(notAdmin.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        UseToken(_adminToken);
        await _client.PutAsJsonAsync("/api/v1/configs/memory-sync",
            new { enabled = (bool?)null });
    }

    [Test]
    public async Task ModelRoles_UserEAdmin_CrudEValidacao()
    {
        var user = await SignUpAsync("B4", "b4@cfg.local");

        UseToken(user.Token);
        var empty = await _client.GetFromJsonAsync<JsonElementDict>(
            "/api/v1/configs/user/model-roles");
        Assert.That(empty, Is.Not.Null);

        // Papel desconhecido → 400.
        var badRole = await _client.PutAsJsonAsync(
            "/api/v1/configs/user/model-roles",
            new { roles = new Dictionary<string, string[]> { ["fantasma"] = [] } });
        Assert.That(badRole.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Modelo fora do catálogo (sem conexões configuradas → catálogo vazio) → 400.
        var badModel = await _client.PutAsJsonAsync(
            "/api/v1/configs/user/model-roles",
            new { roles = new Dictionary<string, string[]> { ["memory"] = ["nope:1"] } });
        Assert.That(badModel.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Mapa válido (vazio passa na validação de catálogo) persiste.
        var ok = await _client.PutAsJsonAsync(
            "/api/v1/configs/user/model-roles",
            new { roles = new Dictionary<string, string[]> { ["memory"] = [] } });
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await ok.Content.ReadAsStringAsync());

        // Camada global: usuário comum é proibido; admin lê/escreve.
        var forbidden = await _client.GetAsync("/api/v1/configs/model-roles");
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var forbiddenPut = await _client.PutAsJsonAsync("/api/v1/configs/model-roles",
            new { roles = new Dictionary<string, string[]>() });
        Assert.That(forbiddenPut.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_adminToken);
        var globalGet = await _client.GetFromJsonAsync<JsonElementDict>(
            "/api/v1/configs/model-roles");
        Assert.That(globalGet, Is.Not.Null);
        var globalBadRole = await _client.PutAsJsonAsync("/api/v1/configs/model-roles",
            new { roles = new Dictionary<string, string[]> { ["fantasma"] = [] } });
        Assert.That(globalBadRole.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var globalBadModel = await _client.PutAsJsonAsync("/api/v1/configs/model-roles",
            new { roles = new Dictionary<string, string[]> { ["advisor"] = ["nope:2"] } });
        Assert.That(globalBadModel.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var globalOk = await _client.PutAsJsonAsync("/api/v1/configs/model-roles",
            new { roles = new Dictionary<string, string[]> { ["advisor"] = [] } });
        Assert.That(globalOk.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await globalOk.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task GitHubConfig_GlobalAdmin_EStatusDoUsuario()
    {
        var user = await SignUpAsync("B5", "b5@cfg.local");

        // Usuário comum não toca a config global.
        UseToken(user.Token);
        var forbiddenGet = await _client.GetAsync("/api/v1/configs/github");
        Assert.That(forbiddenGet.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var forbiddenPut = await _client.PutAsJsonAsync("/api/v1/configs/github",
            new { token = "ghp_x" });
        Assert.That(forbiddenPut.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var forbiddenDel = await _client.DeleteAsync("/api/v1/configs/github");
        Assert.That(forbiddenDel.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        // Admin: GET (não configurado), PUT vazio 400, DELETE idempotente.
        UseToken(_adminToken);
        var status = await _client.GetFromJsonAsync<JsonElementDict>(
            "/api/v1/configs/github");
        Assert.That(status!.GetProperty("configured").GetBoolean(), Is.False);
        var empty = await _client.PutAsJsonAsync("/api/v1/configs/github",
            new { token = "   " });
        Assert.That(empty.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var del = await _client.DeleteAsync("/api/v1/configs/github");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Endpoints do usuário sem token: rápidos e sem rede.
        UseToken(user.Token);
        var mine = await _client.GetFromJsonAsync<JsonElementDict>(
            "/api/v1/github/config");
        Assert.That(mine!.GetProperty("configured").GetBoolean(), Is.False);
        var repos = await _client.GetFromJsonAsync<JsonElementDict[]>(
            "/api/v1/github/repos");
        Assert.That(repos, Is.Empty);
        var branches = await _client.GetAsync("/api/v1/github/repos/o/r/branches");
        Assert.That(branches.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var emptyToken = await _client.PutAsJsonAsync("/api/v1/github/config",
            new { token = "" });
        Assert.That(emptyToken.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var clear = await _client.DeleteAsync("/api/v1/github/config");
        Assert.That(clear.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Workspace-repo: binding vazio, validação de open, unbind/pulls sem binding.
        var binding = await _client.GetFromJsonAsync<JsonElementDict>(
            "/api/v1/workspace/repo/");
        Assert.That(binding!.GetProperty("repo").ValueKind,
            Is.EqualTo(System.Text.Json.JsonValueKind.Null));
        var openInvalid = await _client.PostAsJsonAsync("/api/v1/workspace/repo/open",
            new { repo = "", branch = "" });
        Assert.That(openInvalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var unbind = await _client.DeleteAsync("/api/v1/workspace/repo/");
        Assert.That(unbind.StatusCode,
            Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.NotFound));
        var pulls = await _client.GetAsync("/api/v1/workspace/repo/pulls");
        Assert.That(pulls.StatusCode,
            Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task ZombieToken_GuardsDeUsuarioInexistente()
    {
        // Token JWT válido de usuário removido do banco: passa no
        // middleware de auth e cai no guard `user is null` de cada handler.
        var zombie = await SignUpAsync("Z", "z@cfg.local");
        UseToken(zombie.Token);
        var me = await _client.GetFromJsonAsync<JsonElementDict>("/api/v1/auths/");
        var zombieId = me!.GetProperty("id").GetString()
            ?? me.GetProperty("user").GetProperty("id").GetString()!;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<OpenWebUI.Infrastructure.Data.AppDbContext>();
            var row = await db.Users.FindAsync(zombieId);
            db.Users.Remove(row!);
            await db.SaveChangesAsync();
        }

        var probes = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Get, "/api/v1/users/user/mcp-servers"),
            (HttpMethod.Put, "/api/v1/users/user/mcp-servers/nope/enabled"),
            (HttpMethod.Get, "/api/v1/configs/user/model-roles"),
            (HttpMethod.Get, "/api/v1/configs/user/memory-sync"),
            (HttpMethod.Get, "/api/v1/configs/user/provider-keys"),
            (HttpMethod.Get, "/api/v1/github/config"),
            (HttpMethod.Get, "/api/v1/github/repos"),
            (HttpMethod.Get, "/api/v1/github/repos/o/r/branches"),
            (HttpMethod.Get, "/api/v1/workspace/repo/"),
            (HttpMethod.Get, "/api/v1/workspace/repo/pulls"),
            (HttpMethod.Delete, "/api/v1/workspace/repo/"),
            (HttpMethod.Get, "/api/v1/channels/"),
            (HttpMethod.Get, "/api/v1/channels/nope"),
        };
        foreach (var (method, url) in probes)
        {
            using var req = new HttpRequestMessage(method, url);
            if (method is not null && (method == HttpMethod.Put || method == HttpMethod.Post))
            {
                req.Content = new StringContent("{}", System.Text.Encoding.UTF8,
                    "application/json");
            }
            var resp = await _client.SendAsync(req);
            Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized),
                $"{method} {url} devolveu {resp.StatusCode}");
        }
    }

    [Test]
    public async Task Channel_CrudMensagensEPermissoes()
    {
        var a = await SignUpAsync("CA", "ca@cfg.local");
        var b = await SignUpAsync("CB", "cb@cfg.local");
        UseToken(b.Token);
        var bId = (await _client.GetFromJsonAsync<JsonElementDict>(
            "/api/v1/auths/"))!.GetProperty("id").GetString()!;

        // Nome obrigatório.
        UseToken(a.Token);
        var semNome = await _client.PostAsJsonAsync("/api/v1/channels/",
            new { name = "  ", description = (string?)null, memberIds = (string[]?)null });
        Assert.That(semNome.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var created = await _client.PostAsJsonAsync("/api/v1/channels/",
            new { name = "canal-t", description = "d", memberIds = (string[]?)null });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var channelId = (await created.Content
            .ReadFromJsonAsync<JsonElementDict>())!.GetProperty("id").GetString()!;

        // List + get do dono.
        var list = await _client.GetFromJsonAsync<JsonElementDict[]>("/api/v1/channels/");
        Assert.That(list!.Any(c => c.GetProperty("id").GetString() == channelId), Is.True);
        var get = await _client.GetAsync($"/api/v1/channels/{channelId}");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Mensagem: vazia → 400; parent inexistente → 400; feliz → 200.
        var emptyMsg = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channelId}/messages",
            new { content = " ", parentId = (string?)null });
        Assert.That(emptyMsg.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var badParent = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channelId}/messages",
            new { content = "oi", parentId = "nope" });
        Assert.That(badParent.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var msg = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channelId}/messages",
            new { content = "primeira", parentId = (string?)null });
        Assert.That(msg.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await msg.Content.ReadAsStringAsync());
        var msgId = (await msg.Content
            .ReadFromJsonAsync<JsonElementDict>())!.GetProperty("id").GetString()!;

        // Não-membro não posta nem lê.
        UseToken(b.Token);
        var outsiderPost = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channelId}/messages",
            new { content = "alheio", parentId = (string?)null });
        var outsiderGet = await _client.GetAsync($"/api/v1/channels/{channelId}/messages");
        Assert.Multiple(() =>
        {
            Assert.That(outsiderPost.StatusCode,
                Is.AnyOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound));
            Assert.That(outsiderGet.StatusCode,
                Is.AnyOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound));
        });

        // Dono adiciona B; B posta e reage.
        UseToken(a.Token);
        var add = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channelId}/members",
            new { userIds = new[] { bId }, role = "member" });
        Assert.That(add.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await add.Content.ReadAsStringAsync());

        UseToken(b.Token);
        var memberPost = await _client.PostAsJsonAsync(
            $"/api/v1/channels/{channelId}/messages",
            new { content = "agora sim", parentId = (string?)null });
        Assert.That(memberPost.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Reação + pin + read.
        var react = await _client.PostAsync(
            $"/api/v1/channels/{channelId}/messages/{msgId}/reactions/👍", null);
        Assert.That(react.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var pin = await _client.PostAsync(
            $"/api/v1/channels/{channelId}/messages/{msgId}/pin", null);
        Assert.That(pin.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var pinned = await _client.GetFromJsonAsync<JsonElementDict[]>(
            $"/api/v1/channels/{channelId}/pinned");
        Assert.That(pinned, Is.Not.Empty);
        var unpin = await _client.DeleteAsync(
            $"/api/v1/channels/{channelId}/messages/{msgId}/pin");
        Assert.That(unpin.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var unreact = await _client.DeleteAsync(
            $"/api/v1/channels/{channelId}/messages/{msgId}/reactions/👍");
        Assert.That(unreact.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var read = await _client.PostAsync(
            $"/api/v1/channels/{channelId}/read", null);
        Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Update + delete (dono).
        UseToken(a.Token);
        var upd = await _client.PutAsJsonAsync($"/api/v1/channels/{channelId}",
            new { name = "canal-t2", description = (string?)null });
        Assert.That(upd.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var del = await _client.DeleteAsync($"/api/v1/channels/{channelId}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Users_DeletePermissionsEOAuthSessions()
    {
        var victim = await SignUpAsync("V", "v@cfg.local");
        UseToken(victim.Token);
        var vId = (await _client.GetFromJsonAsync<JsonElementDict>(
            "/api/v1/auths/"))!.GetProperty("id").GetString()!;
        var outsider = await SignUpAsync("W", "w@cfg.local");
        UseToken(outsider.Token);
        var wId = (await _client.GetFromJsonAsync<JsonElementDict>(
            "/api/v1/auths/"))!.GetProperty("id").GetString()!;

        // Self: lista/revoga sessões OAuth (vazio → 404 na revogação).
        var sessions = await _client.GetAsync($"/api/v1/users/{wId}/oauth/sessions");
        Assert.That(sessions.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var revokeMissing = await _client.DeleteAsync(
            $"/api/v1/users/{wId}/oauth/sessions/nope");
        Assert.That(revokeMissing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Outro usuário sem admin → Forbidden na revogação e no delete.
        var revokeAlien = await _client.DeleteAsync(
            $"/api/v1/users/{vId}/oauth/sessions/nope");
        var delAlien = await _client.DeleteAsync($"/api/v1/users/{vId}");
        Assert.Multiple(() =>
        {
            Assert.That(revokeAlien.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(delAlien.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        });

        // Admin: permissões do usuário (get inválido/put/get) e delete.
        UseToken(_adminToken);
        var perm404 = await _client.GetAsync($"/api/v1/users/nope/permissions");
        Assert.That(perm404.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var permPut = await _client.PutAsJsonAsync(
            $"/api/v1/users/{vId}/permissions",
            new { workspace = new { tools = true } });
        Assert.That(permPut.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var permGet = await _client.GetAsync($"/api/v1/users/{vId}/permissions");
        Assert.That(permGet.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await permGet.Content.ReadAsStringAsync(),
            Does.Contain("tools"));

        // Admin não deleta a si mesmo; deleta outro usuário.
        var adminId = (await _client.GetFromJsonAsync<JsonElementDict>(
            "/api/v1/auths/"))!.GetProperty("id").GetString()!;
        var selfDel = await _client.DeleteAsync($"/api/v1/users/{adminId}");
        Assert.That(selfDel.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var del404 = await _client.DeleteAsync("/api/v1/users/nope");
        Assert.That(del404.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var del = await _client.DeleteAsync($"/api/v1/users/{vId}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>Envelope flexível para serialização camelCase arbitrária.</summary>
    private sealed class JsonElementDict : Dictionary<string, System.Text.Json.JsonElement>
    {
        public System.Text.Json.JsonElement GetProperty(string name) =>
            this.TryGetValue(name, out var v) ? v : default;
    }
}
