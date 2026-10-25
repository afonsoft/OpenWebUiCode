using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes de integração da superfície da API: config pública, completions enriquecidas, OAuth/OIDC e automações.</summary>
[TestFixture, IsolateEnvironment]
public class ApiSurfaceTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpClient _noRedirect = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private readonly List<string> _chatPayloads = [];
    private readonly object _chatLock = new();
    private readonly Dictionary<string, string?> _envBackup = new();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-surface-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _noRedirect = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var admin = await SignUpAsync("Admin", "admin@surface.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user", EnableMemories = true };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());

        var baseUrl = StartMock();

        // OIDC genérico apontando para o mock: habilita os caminhos de
        // login/callback do OAuthEndpoints sem depender de serviços externos.
        SetEnv("OPENID_PROVIDER_URL", baseUrl);
        SetEnv("OPENID_CLIENT_ID", "cliente-teste");
        SetEnv("OPENID_CLIENT_SECRET", "segredo-teste");

        var connections = new ConnectionsConfig([baseUrl], [], []);
        var connResponse = await _client.PostAsJsonAsync("/api/v1/configs/connections", connections);
        Assert.That(connResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mockCts.Cancel();
        _mock.Stop();
        _client.Dispose();
        _noRedirect.Dispose();
        _factory.Dispose();
        foreach (var (name, value) in _envBackup)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private void SetEnv(string name, string value)
    {
        if (!_envBackup.ContainsKey(name))
        {
            _envBackup[name] = Environment.GetEnvironmentVariable(name);
        }
        Environment.SetEnvironmentVariable(name, value);
    }

    private string StartMock()
    {
        var random = new Random();
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port = random.Next(40000, 60000);
            _mock = new HttpListener();
            _mock.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _mock.Start();
                break;
            }
            catch (HttpListenerException)
            {
                _mock.Close();
            }
        }
        if (!_mock.IsListening)
        {
            throw new InvalidOperationException("Nenhuma porta livre para o mock.");
        }
        _mockCts = new CancellationTokenSource();
        _ = Task.Run(() => MockLoopAsync(_mockCts.Token));
        return $"http://localhost:{port}";
    }

    private async Task MockLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _mock.GetContextAsync();
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
            var (status, json) = path switch
            {
                "/api/tags" => (200, "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}"),
                "/api/chat" => (200, ChatResponse(ctx)),
                "/oauth/token" => (200, "{\"access_token\":\"tok-oidc\",\"token_type\":\"Bearer\"}"),
                "/userinfo" => (200, "{\"sub\":\"oidc-sub-1\",\"email\":\"oidc@surface.local\",\"name\":\"Oidc User\"}"),
                _ => (404, "{}"),
            };
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private string ChatResponse(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream);
        lock (_chatLock)
        {
            _chatPayloads.Add(reader.ReadToEnd());
        }
        return "{\"message\":{\"content\":\"resposta do mock\"},\"done\":true}";
    }

    private JsonElement UltimoPayloadChat()
    {
        string? body;
        lock (_chatLock)
        {
            body = _chatPayloads.Count > 0 ? _chatPayloads[^1] : null;
        }
        Assert.That(body, Is.Not.Null.And.Not.Empty, "O mock não recebeu chamada de chat.");
        return JsonDocument.Parse(body!).RootElement;
    }

    private static string SystemContent(JsonElement payload)
    {
        foreach (var message in payload.GetProperty("messages").EnumerateArray())
        {
            if (message.GetProperty("role").GetString() == "system")
            {
                return message.GetProperty("content").GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static string LastUserContent(JsonElement payload)
    {
        var content = string.Empty;
        foreach (var message in payload.GetProperty("messages").EnumerateArray())
        {
            if (message.GetProperty("role").GetString() == "user")
            {
                content = message.GetProperty("content").GetString() ?? string.Empty;
            }
        }
        return content;
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
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<FileResponse> UploadAsync(string filename, string content)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", filename);
        var response = await _client.PostAsync("/api/v1/files/", form);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<FileResponse>())!;
    }

    private async Task<string> CompletarAsync(ChatCompletionRequest request)
    {
        var response = await _client.PostAsJsonAsync("/api/chat/completions", request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("resposta do mock"));
        Assert.That(body, Does.Contain("[DONE]"));
        return body;
    }

    [Test]
    public async Task AppConfig_Get_RetornaFlagsEProvedores()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var config = await _client.GetFromJsonAsync<AppConfigResponse>("/api/config");

        Assert.That(config, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(config!.Status, Is.True);
            Assert.That(config.Version, Does.Contain("dotnet"));
            Assert.That(config.Features.Auth, Is.True);
            Assert.That(config.Features.EnableMemories, Is.True);
            Assert.That(config.OAuthProviders, Does.Contain("oidc"));
        });
    }

    [Test]
    public async Task AppConfig_Post_ExigeAdmin_EAtualiza()
    {
        // Sem token: a rota exige autenticação.
        _client.DefaultRequestHeaders.Authorization = null;
        var anon = await _client.PostAsJsonAsync("/api/config", AdminConfig.Default);
        Assert.That(anon.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        // Usuário comum: autenticado mas sem papel admin.
        var user = await SignUpAsync("CfgUser", "cfguser@surface.local", "senha123");
        UseToken(user.Token);
        var forbidden = await _client.PostAsJsonAsync("/api/config", AdminConfig.Default);
        Assert.That(forbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        // Admin: REPLACE total do AdminConfig refletido no GET público.
        UseToken(_adminToken);
        var renamed = AdminConfig.Default with
        {
            DefaultUserRole = "user",
            EnableMemories = true,
            WebUiName = "Instancia Cobertura",
        };
        var posted = await _client.PostAsJsonAsync("/api/config", renamed);
        Assert.That(posted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var config = await _client.GetFromJsonAsync<AppConfigResponse>("/api/config");
        Assert.That(config!.Name, Is.EqualTo("Instancia Cobertura"));

        var restore = AdminConfig.Default with { DefaultUserRole = "user", EnableMemories = true };
        await _client.PostAsJsonAsync("/api/config", restore);
    }

    [Test]
    public async Task VersionUpdates_Get_RetornaVersaoAtual()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var result = await _client.GetFromJsonAsync<VersionUpdateResponse>("/api/version/updates");

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Current, Does.Contain("dotnet"));
            Assert.That(result.Latest, Is.EqualTo(result.Current));
        });
    }

    [Test]
    public async Task Changelog_Get_RetornaLista()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var response = await _client.GetAsync("/api/changelog");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(json.RootElement.GetProperty("releases").ValueKind,
            Is.EqualTo(JsonValueKind.Array));
    }

    [Test]
    public async Task ConfigExportImport_Admin_FazRoundTrip()
    {
        var user = await SignUpAsync("ExpUser", "expuser@surface.local", "senha123");

        // Usuário comum não pode exportar nem importar.
        UseToken(user.Token);
        var exportForbidden = await _client.GetAsync("/api/v1/configs/export");
        Assert.That(exportForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var importForbidden = await _client.PostAsJsonAsync(
            "/api/v1/configs/import", new Dictionary<string, object> { ["apitest.flag"] = true });
        Assert.That(importForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_adminToken);
        var export = await _client.GetAsync("/api/v1/configs/export");
        Assert.That(export.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var exported = JsonDocument.Parse(await export.Content.ReadAsStringAsync());
        Assert.That(exported.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object));
        Assert.That(exported.RootElement.TryGetProperty("admin.config", out _), Is.True);

        var imported = await _client.PostAsJsonAsync(
            "/api/v1/configs/import", new Dictionary<string, object> { ["apitest.flag"] = true });
        Assert.That(imported.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var after = await _client.GetAsync("/api/v1/configs/export");
        var afterJson = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.That(afterJson.RootElement.GetProperty("apitest.flag").GetString(),
            Is.EqualTo("true"));
    }

    [Test]
    public async Task Completions_ModeloCustomizado_AplicaBaseSystemEParams()
    {
        var user = await SignUpAsync("Custom", "custom@surface.local", "senha123");
        UseToken(user.Token);

        var created = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest(
                "Assistente Poeta", "fake:1", "Responda sempre em versos.",
                "{\"temperature\":0.5}", null));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var model = (await created.Content.ReadFromJsonAsync<ModelEntryResponse>())!;

        // O modelo personalizado aparece na listagem agregada.
        var models = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");
        Assert.That(models!.Data.Any(m => m.Id == model.Id), Is.True);

        await CompletarAsync(new ChatCompletionRequest(
            model.Id, [new ChatCompletionMessage("user", "Escreva sobre o mar")]));

        var payload = UltimoPayloadChat();
        Assert.Multiple(() =>
        {
            // EnrichRequestAsync redireciona para o modelo base do provedor.
            Assert.That(payload.GetProperty("model").GetString(), Is.EqualTo("fake:1"));
            Assert.That(SystemContent(payload), Does.Contain("Responda sempre em versos."));
            // ParamsJson do modelo vira "options" no payload do Ollama.
            Assert.That(
                payload.GetProperty("options").GetProperty("temperature").GetDouble(),
                Is.EqualTo(0.5));
        });
    }

    [Test]
    public async Task Completions_AnexoDeArquivo_InjetaContexto()
    {
        var user = await SignUpAsync("Attach", "attach@surface.local", "senha123");
        UseToken(user.Token);
        var file = await UploadAsync("anexo.txt", "CONTEUDO-ANEXO-77AB");

        await CompletarAsync(new ChatCompletionRequest(
            "fake:1",
            [new ChatCompletionMessage("user", "Resuma o anexo")],
            FileIds: [file.Id]));

        var payload = UltimoPayloadChat();
        // attachments-v2 RF-001: anexo vai inline na mensagem do usuário.
        Assert.That(LastUserContent(payload), Does.Contain("CONTEUDO-ANEXO-77AB"));
        Assert.That(LastUserContent(payload), Does.Contain("file name="));
    }

    [Test]
    public async Task Completions_ReferenciaArquivoPorHash_InjetaContexto()
    {
        var user = await SignUpAsync("RefFile", "reffile@surface.local", "senha123");
        UseToken(user.Token);
        await UploadAsync("relatorio.txt", "CONTEUDO-REFERENCIA-9F2C");

        // "#relatorio.txt" resolve o arquivo pelo nome (ResolveReferenceFileIdsAsync).
        await CompletarAsync(new ChatCompletionRequest(
            "fake:1",
            [new ChatCompletionMessage("user", "Analise #relatorio.txt por favor")]));

        var payload = UltimoPayloadChat();
        Assert.That(SystemContent(payload), Does.Contain("CONTEUDO-REFERENCIA-9F2C"));
    }

    [Test]
    public async Task Completions_ReferenciaColecaoPorHash_InjetaContexto()
    {
        var user = await SignUpAsync("RefCol", "refcol@surface.local", "senha123");
        UseToken(user.Token);
        var file = await UploadAsync("guia.md", "CONTEUDO-COLECAO-55DD");

        var created = await _client.PostAsJsonAsync(
            "/api/v1/knowledge", new CreateKnowledgeRequest("docs", "Documentos"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var collection = (await created.Content.ReadFromJsonAsync<KnowledgeResponse>())!;

        var linked = await _client.PostAsJsonAsync(
            $"/api/v1/knowledge/{collection.Id}/files", new AddKnowledgeFileRequest(file.Id));
        Assert.That(linked.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // "#docs" resolve todos os arquivos vinculados à coleção.
        await CompletarAsync(new ChatCompletionRequest(
            "fake:1",
            [new ChatCompletionMessage("user", "Liste o conteúdo de #docs")]));

        var payload = UltimoPayloadChat();
        Assert.That(SystemContent(payload), Does.Contain("CONTEUDO-COLECAO-55DD"));
    }

    [Test]
    public async Task Completions_MemoriaDoUsuario_EntraNoSystem()
    {
        var user = await SignUpAsync("MemUser", "memuser@surface.local", "senha123");
        UseToken(user.Token);

        var mem = await _client.PostAsJsonAsync(
            "/api/v1/memories/add", new MemoryUpsertRequest("Gosta de respostas curtas"));
        Assert.That(mem.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await CompletarAsync(new ChatCompletionRequest(
            "fake:1", [new ChatCompletionMessage("user", "oi")]));

        var payload = UltimoPayloadChat();
        Assert.That(SystemContent(payload), Does.Contain("Gosta de respostas curtas"));
    }

    [Test]
    public async Task Completions_ProvedorFora_EmiteErroNoSse()
    {
        var user = await SignUpAsync("NoProv", "noprov@surface.local", "senha123");

        // Quebra as conexões (admin) para cobrir o catch HttpRequestException.
        UseToken(_adminToken);
        await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig(["http://localhost:1"], [], []));
        UseToken(user.Token);

        var response = await _client.PostAsJsonAsync("/api/chat/completions",
            new ChatCompletionRequest("fake:1", [new ChatCompletionMessage("user", "oi")]));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Falha ao contactar o provedor"));
        Assert.That(body, Does.Contain("[DONE]"));

        // Restaura o mock para os demais testes desta fixture.
        UseToken(_adminToken);
        var mockUrl = _mock.Prefixes.First();
        await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig([mockUrl.TrimEnd('/')], [], []));
    }

    [Test]
    public async Task OAuth_ProviderNaoConfigurado_Retorna404()
    {
        // Slug fora do catálogo: nunca resolve, com ou sem env.
        var login = await _noRedirect.GetAsync("/oauth/facebook/login");
        Assert.That(login.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var callback = await _noRedirect.GetAsync(
            "/oauth/facebook/callback?code=abc&state=xyz");
        Assert.That(callback.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task OAuth_LoginOidc_RedirecionaParaAuthorizeComState()
    {
        var response = await _noRedirect.GetAsync("/oauth/oidc/login");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        var location = response.Headers.Location!.ToString();
        Assert.Multiple(() =>
        {
            Assert.That(location, Does.StartWith($"{_mock.Prefixes.First().TrimEnd('/')}/authorize?"));
            Assert.That(location, Does.Contain("client_id=cliente-teste"));
            Assert.That(location, Does.Contain("state="));
        });
    }

    [Test]
    public async Task OAuth_Callback_StateInvalido_Retorna400()
    {
        var response = await _noRedirect.GetAsync(
            "/oauth/oidc/callback?code=abc&state=assinatura.invalida");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("State OAuth inválido"));
    }

    [Test]
    public async Task OAuth_CallbackCompleto_CriaUsuarioERedirecionaComToken()
    {
        // Dado: state válido obtido do redirect de login.
        var login = await _noRedirect.GetAsync("/oauth/oidc/login");
        var location = login.Headers.Location!.ToString();
        var state = location.Split("state=", 2)[1].Split('&')[0];

        // Quando: o provedor chama o callback com code + state válidos.
        var callback = await _noRedirect.GetAsync(
            $"/oauth/oidc/callback?code=codigo-valido&state={state}");

        // Então: usuário criado (papel padrão "user") e token na query de retorno.
        Assert.That(callback.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        var back = callback.Headers.Location!.ToString();
        Assert.That(back, Does.Contain("oauth_token="));
    }

    [Test]
    public async Task OAuth_CallbackCompleto_UsuarioExistente_ReutilizaConta()
    {
        var login = await _noRedirect.GetAsync("/oauth/oidc/login");
        var location = login.Headers.Location!.ToString();
        var state = location.Split("state=", 2)[1].Split('&')[0];

        // Mesmo subject ("oidc-sub-1") já vinculado no teste anterior.
        var callback = await _noRedirect.GetAsync(
            $"/oauth/oidc/callback?code=outro-codigo&state={state}");

        Assert.That(callback.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(callback.Headers.Location!.ToString(), Does.Contain("oauth_token="));
    }

    [Test]
    public async Task Automations_List_RetornaSomenteDoUsuario()
    {
        var owner = await SignUpAsync("AutoOwner", "autoowner@surface.local", "senha123");
        UseToken(owner.Token);

        // Lista começa vazia para o usuário novo.
        var empty = await _client.GetFromJsonAsync<List<AutomationResponse>>("/api/v1/automations");
        Assert.That(empty, Is.Not.Null.And.Empty);

        var created = await _client.PostAsJsonAsync("/api/v1/automations",
            new AutomationUpsertRequest(
                "Resumo diário", "Resuma as notícias", "fake:1",
                "interval", 60, null, null, true));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var automation = (await created.Content.ReadFromJsonAsync<AutomationResponse>())!;

        var list = await _client.GetFromJsonAsync<List<AutomationResponse>>("/api/v1/automations");
        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list![0].Id, Is.EqualTo(automation.Id));

        // Outro usuário não vê a automação alheia.
        var other = await SignUpAsync("AutoOther", "autoother@surface.local", "senha123");
        UseToken(other.Token);
        var foreign = await _client.GetFromJsonAsync<List<AutomationResponse>>("/api/v1/automations");
        Assert.That(foreign, Is.Not.Null.And.Empty);
    }
}
