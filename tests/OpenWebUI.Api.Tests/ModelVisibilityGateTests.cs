using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do gate global de modelos e das conexões por usuário
/// (SPEC-20261104-model-visibility-gate): catálogo admin completo, denylist,
/// filtragem das listagens, 403 em completion, isolamento das conexões do
/// usuário e escopo dos combos.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ModelVisibilityGateTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;
    private string _adminUrl = null!;
    private string _userUrl = null!;
    private HttpListener _adminMock = null!;
    private HttpListener _userMock = null!;
    private CancellationTokenSource _mockCts = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = TestInfra.NewDbPath("openwebui-gate");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _mockCts = new CancellationTokenSource();

        // Conexão do admin: adm-a/adm-b. Conexão do usuário: user-a + adm-a
        // (a duplicata prova que a fonte do usuário vence o gate).
        _adminMock = StartMock(out _adminUrl, "adm-a", "adm-b");
        _userMock = StartMock(out _userUrl, "user-a", "adm-a");

        var admin = await SignUpAsync("Admin", "admin@gate.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        Assert.That(
            (await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config))
                .StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        Assert.That(
            (await _client.PostAsJsonAsync("/api/v1/configs/connections",
                new ConnectionsConfig([_adminUrl], [], []))).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mockCts.Cancel();
        _adminMock.Stop();
        _userMock.Stop();
        _adminMock.Close();
        _userMock.Close();
        _mockCts.Dispose();
        _client.Dispose();
        _factory.Dispose();
        TestInfra.DeleteDb(_dbPath);
    }

    private HttpListener StartMock(out string baseUrl, params string[] models)
    {
        var random = new Random();
        HttpListener? listener = null;
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port = random.Next(40000, 60000);
            listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                listener.Start();
                break;
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }
        if (listener is not { IsListening: true })
        {
            throw new InvalidOperationException("Nenhuma porta livre para o mock.");
        }
        var tags = string.Join(',',
            models.Select(m => $"{{\"model\":\"{m}\",\"name\":\"{m}\"}}"));
        _ = Task.Run(() => MockLoopAsync(listener, tags, _mockCts.Token));
        baseUrl = $"http://localhost:{port}";
        return listener;
    }

    private static async Task MockLoopAsync(
        HttpListener listener, string tagsJson, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var path = ctx.Request.Url!.AbsolutePath;
            var (status, json) = (200, "{}");
            if (path == "/api/tags")
            {
                json = $"{{\"models\":[{tagsJson}]}}";
            }
            else if (path == "/api/chat")
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                var model = JsonDocument.Parse(body).RootElement
                    .TryGetProperty("model", out var m) ? m.GetString() : null;
                json = $"{{\"message\":{{\"content\":\"ok de {model}\"}}}}";
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email, string password)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, password));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private Task<HttpResponseMessage> DisableAsync(params string[] keys) =>
        _client.PutAsJsonAsync("/api/v1/configs/models/disabled", keys.ToList());

    // ---------------- Catálogo admin + denylist (RF-001/RF-003) ----------------

    [Test]
    public async Task Catalog_SoAdmin_ListaTodosComEstadoDoGate()
    {
        UseToken(_adminToken);
        var catalog = await _client.GetFromJsonAsync<CatalogModelListResponse>(
            "/api/v1/configs/models/catalog");
        Assert.Multiple(() =>
        {
            Assert.That(catalog!.Data.Select(m => m.Id),
                Is.SupersetOf(new[] { "adm-a", "adm-b" }));
            Assert.That(catalog.Data.All(m => m.Enabled), Is.True);
            Assert.That(catalog.Data.All(m => m.Source == "global"), Is.True);
        });

        var user = await SignUpAsync("NoAdm", "noadm@gate.local", "senha123");
        UseToken(user.Token);
        var forbidden = await _client.GetAsync("/api/v1/configs/models/catalog");
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Disable_FiltraListasEBloqueiaCompletion()
    {
        var user = await SignUpAsync("Fil", "fil@gate.local", "senha123");

        UseToken(_adminToken);
        Assert.That((await DisableAsync("ollama:adm-a")).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));

        // Cache invalidado: a mudança reflete na hora para todos.
        UseToken(user.Token);
        var models = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");
        Assert.Multiple(() =>
        {
            Assert.That(models!.Data.Any(m => m.Id == "adm-a"), Is.False);
            Assert.That(models.Data.Any(m => m.Id == "adm-b"), Is.True);
        });
        var catalog = await _client.GetFromJsonAsync<CatalogModelListResponse>(
            "/api/v1/models/all");
        Assert.That(catalog!.Data.Any(m => m.Id == "adm-a"), Is.False);

        // Admin também não vê o desabilitado nas listagens comuns (RF-002).
        UseToken(_adminToken);
        var adminModels = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");
        Assert.That(adminModels!.Data.Any(m => m.Id == "adm-a"), Is.False);

        // Mas o catálogo admin mostra tudo com Enabled=false.
        var adminCatalog = await _client.GetFromJsonAsync<CatalogModelListResponse>(
            "/api/v1/configs/models/catalog");
        Assert.That(adminCatalog!.Data.Single(m => m.Id == "adm-a").Enabled, Is.False);

        // Completion do modelo bloqueado → 403 (RF-008).
        var blocked = await _client.PostAsJsonAsync("/api/chat/completions",
            new ChatCompletionRequest("adm-a",
                [new ChatCompletionMessage("user", "oi")], Stream: false));
        Assert.That(blocked.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        // Reabilitar limpa a denylist e devolve o modelo às listagens.
        Assert.That((await DisableAsync()).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var restored = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");
        Assert.That(restored!.Data.Any(m => m.Id == "adm-a"), Is.True);
    }

    [Test]
    public async Task PostModels_SemDisabled_PreservaDenylist()
    {
        UseToken(_adminToken);
        await DisableAsync("ollama:adm-a");

        var post = await _client.PostAsJsonAsync("/api/v1/configs/models",
            new ModelsConfig(["adm-b"], []));
        Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var models = await _client.GetFromJsonAsync<ModelsConfig>("/api/v1/configs/models");
        Assert.Multiple(() =>
        {
            Assert.That(models!.DefaultModels, Is.EqualTo(new[] { "adm-b" }));
            Assert.That(models.DisabledModels, Is.EqualTo(new[] { "ollama:adm-a" }));
        });
        await DisableAsync();
    }

    // ---------------- Conexões do usuário (RF-006) ----------------

    [Test]
    public async Task UserConnections_FlagOff_403()
    {
        var user = await SignUpAsync("Off", "off@gate.local", "senha123");
        UseToken(_adminToken);
        await _client.PostAsJsonAsync("/api/v1/configs/user_connections",
            new FeatureToggle(false));
        UseToken(user.Token);
        var get = await _client.GetAsync("/api/v1/configs/user/connections");
        var put = await _client.PostAsJsonAsync("/api/v1/configs/user/connections",
            new ConnectionsConfig([_userUrl], [], []));
        Assert.Multiple(() =>
        {
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        });
    }

    [Test]
    public async Task UserConnections_MascaraChavesENaoVazaGlobal()
    {
        var user = await SignUpAsync("Uc", "uc@gate.local", "senha123");
        UseToken(_adminToken);
        Assert.That(
            (await _client.PostAsJsonAsync("/api/v1/configs/user_connections",
                new FeatureToggle(true))).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));

        UseToken(user.Token);
        var put = await _client.PostAsJsonAsync("/api/v1/configs/user/connections",
            new ConnectionsConfig([_userUrl], ["http://openai.local/v1"],
                ["sk-user-secret"]));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var get = await _client.GetFromJsonAsync<ConnectionsConfigResponse>(
            "/api/v1/configs/user/connections");
        Assert.Multiple(() =>
        {
            Assert.That(get!.OllamaBaseUrls, Is.EqualTo(new[] { _userUrl }));
            Assert.That(get.OpenAiKeyConfigured, Is.EqualTo(new[] { true }));
            // A conexão do ADMIN nunca aparece na resposta do usuário.
            Assert.That(get.OllamaBaseUrls, Does.Not.Contain(_adminUrl));
        });
    }

    [Test]
    public async Task UserConnections_ModelosAparecemComSourceUser()
    {
        var user = await SignUpAsync("Src", "src@gate.local", "senha123");
        UseToken(_adminToken);
        await _client.PostAsJsonAsync("/api/v1/configs/user_connections",
            new FeatureToggle(true));
        UseToken(user.Token);
        await _client.PostAsJsonAsync("/api/v1/configs/user/connections",
            new ConnectionsConfig([_userUrl], [], []));

        var catalog = await _client.GetFromJsonAsync<CatalogModelListResponse>(
            "/api/v1/models/all");
        Assert.Multiple(() =>
        {
            Assert.That(catalog!.Data.Single(m => m.Id == "user-a").Source,
                Is.EqualTo("user"));
            // adm-a existe nas duas fontes — a do usuário vence o dedup.
            Assert.That(catalog.Data.Single(m => m.Id == "adm-a").Source,
                Is.EqualTo("user"));
            Assert.That(catalog.Data.Single(m => m.Id == "adm-b").Source,
                Is.EqualTo("global"));
        });
    }

    [Test]
    public async Task Gate_NaoCobre_ModeloDaConnDoUsuario()
    {
        var user = await SignUpAsync("Exm", "exm@gate.local", "senha123");
        UseToken(_adminToken);
        await _client.PostAsJsonAsync("/api/v1/configs/user_connections",
            new FeatureToggle(true));
        UseToken(user.Token);
        await _client.PostAsJsonAsync("/api/v1/configs/user/connections",
            new ConnectionsConfig([_userUrl], [], []));

        UseToken(_adminToken);
        await DisableAsync("ollama:adm-a");

        // O usuário continua vendo e usando adm-a — a conexão é dele (Q4 da SPEC).
        UseToken(user.Token);
        var catalog = await _client.GetFromJsonAsync<CatalogModelListResponse>(
            "/api/v1/models/all");
        Assert.That(catalog!.Data.Single(m => m.Id == "adm-a").Source,
            Is.EqualTo("user"));

        var ok = await _client.PostAsJsonAsync("/api/chat/completions",
            new ChatCompletionRequest("adm-a",
                [new ChatCompletionMessage("user", "oi")], Stream: false));
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await ok.Content.ReadAsStringAsync());
        Assert.That(await ok.Content.ReadAsStringAsync(),
            Does.Contain("ok de adm-a"));
        await DisableAsync();
    }

    // ---------------- Combos (RF-004) ----------------

    [Test]
    public async Task Combos_GlobalDesabilitado_OcultoDoUsuario()
    {
        var user = await SignUpAsync("Cb", "cb@gate.local", "senha123");
        UseToken(_adminToken);
        var created = await _client.PostAsJsonAsync("/api/v1/combos",
            new ComboUpsertRequest("combo-off", "priority",
                [new ComboItemRequest("adm-a")]) with { Global = true, Enabled = false });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var combo = await created.Content.ReadFromJsonAsync<ComboResponse>();

        UseToken(user.Token);
        var list = await _client.GetFromJsonAsync<List<ComboResponse>>("/api/v1/combos");
        Assert.That(list!.Any(c => c.Id == combo!.Id), Is.False);
        Assert.That((await _client.GetAsync($"/api/v1/combos/{combo!.Id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));

        // Habilitado volta a aparecer para o usuário.
        UseToken(_adminToken);
        var enable = await _client.PutAsJsonAsync($"/api/v1/combos/{combo.Id}",
            new ComboUpsertRequest("combo-off", "priority",
                [new ComboItemRequest("adm-a")]) with { Enabled = true });
        Assert.That(enable.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        UseToken(user.Token);
        var relist = await _client.GetFromJsonAsync<List<ComboResponse>>("/api/v1/combos");
        Assert.That(relist!.Any(c => c.Id == combo.Id), Is.True);
    }

    [Test]
    public async Task Combos_ItemForaDoCatalogoEfetivo_400()
    {
        var user = await SignUpAsync("Inv", "inv@gate.local", "senha123");
        UseToken(user.Token);
        var bad = await _client.PostAsJsonAsync("/api/v1/combos",
            new ComboUpsertRequest("bad", "priority",
                [new ComboItemRequest("modelo-inexistente")]));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Modelo global desabilitado também não entra no catálogo efetivo.
        UseToken(_adminToken);
        await DisableAsync("ollama:adm-a");
        UseToken(user.Token);
        var gated = await _client.PostAsJsonAsync("/api/v1/combos",
            new ComboUpsertRequest("gated", "priority",
                [new ComboItemRequest("adm-a")]));
        Assert.That(gated.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        await DisableAsync();
    }
}
