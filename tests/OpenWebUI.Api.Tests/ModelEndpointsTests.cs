using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes dos endpoints de modelos custom (CRUD, import/export, /api/models com provider mock).</summary>
[TestFixture, IsolateEnvironment]
public class ModelEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private volatile string? _lastChatBody;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-models-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@models.local", "senha123");
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);

        var baseUrl = StartMock();
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
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
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

            var path = ctx.Request.Url!.AbsolutePath;
            if (path == "/api/chat")
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                _lastChatBody = await reader.ReadToEndAsync(ct);
            }

            var (status, json) = path switch
            {
                "/api/tags" => (200, "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}"),
                "/api/chat" => (200, "{\"message\":{\"content\":\"resposta do mock\"}}"),
                _ => (404, "{}"),
            };
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
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    [Test]
    public async Task ApiModels_ComProviderMockado_ListaModeloDoOllama()
    {
        var user = await SignUpAsync("List", "list@models.local", "senha123");
        UseToken(user.Token);

        var models = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");
        Assert.That(models!.Data.Any(m => m.Id == "fake:1" && m.Provider == "ollama"), Is.True);

        var baseModels = await _client.GetFromJsonAsync<ModelListResponse>("/api/v1/models/base");
        Assert.That(baseModels!.Data.Any(m => m.Id == "fake:1"), Is.True);
    }

    [Test]
    public async Task ApiModels_SemToken_Retorna401()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var apiModels = await _client.GetAsync("/api/models");
        Assert.That(apiModels.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        var workspaceModels = await _client.GetAsync("/api/v1/models/");
        Assert.That(workspaceModels.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task ApiModels_DisabledModels_FiltraSomenteParaOUsuario()
    {
        var alice = await SignUpAsync("Alice", "alice@models.local", "senha123");
        UseToken(alice.Token);
        var save = await _client.PostAsJsonAsync("/api/v1/users/user/settings/update",
            new JsonObject { ["disabledModels"] = new JsonArray("OLLAMA:fake:1", "ollama:gone") });
        Assert.That(save.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var mine = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");
        // case-insensitive no par {provider}:{id}; entrada obsoleta ("gone") é ignorada.
        Assert.That(mine!.Data.Any(m => m.Id == "fake:1"), Is.False);

        var bob = await SignUpAsync("Bob", "bob@models.local", "senha123");
        UseToken(bob.Token);
        var theirs = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");
        Assert.That(theirs!.Data.Any(m => m.Id == "fake:1"), Is.True);
    }

    [Test]
    public async Task ModelosAll_CatalogoCompleto_ComFlagPorUsuario()
    {
        var alice = await SignUpAsync("Carol", "carol@models.local", "senha123");
        UseToken(alice.Token);
        await _client.PostAsJsonAsync("/api/v1/users/user/settings/update",
            new JsonObject { ["disabledModels"] = new JsonArray("ollama:fake:1") });

        // usuário comum (não admin) recebe 200 com a própria flag.
        var catalog = await _client.GetFromJsonAsync<CatalogModelListResponse>("/api/v1/models/all");
        var fake = catalog!.Data.Single(m => m.Id == "fake:1");
        Assert.Multiple(() =>
        {
            Assert.That(fake.Provider, Is.EqualTo("ollama"));
            Assert.That(fake.Enabled, Is.False);
        });

        var dave = await SignUpAsync("Dave", "dave@models.local", "senha123");
        UseToken(dave.Token);
        var other = await _client.GetFromJsonAsync<CatalogModelListResponse>("/api/v1/models/all");
        Assert.That(other!.Data.Single(m => m.Id == "fake:1").Enabled, Is.True);
    }

    [Test]
    public async Task Settings_DisabledModels_RoundTrip()
    {
        var user = await SignUpAsync("Erin", "erin@models.local", "senha123");
        UseToken(user.Token);
        await _client.PostAsJsonAsync("/api/v1/users/user/settings/update",
            new JsonObject { ["ui"] = new JsonObject { ["theme"] = "dark" },
                ["disabledModels"] = new JsonArray("openai:gpt-x") });

        var doc = await _client.GetFromJsonAsync<JsonObject>("/api/v1/users/user/settings");
        var disabled = doc!["disabledModels"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(disabled, Is.EqualTo(new[] { "openai:gpt-x" }));
            Assert.That(doc["ui"]!["theme"]!.GetValue<string>(), Is.EqualTo("dark"));
        });
    }

    [Test]
    public async Task Modelos_Create_SemNome_Retorna400()
    {
        var user = await SignUpAsync("Val", "val@models.local", "senha123");
        UseToken(user.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest("  ", "fake:1", null, null, null));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Modelos_CRUD_AtualizaToggleEDeleta()
    {
        var user = await SignUpAsync("Crud", "crud@models.local", "senha123");
        UseToken(user.Token);

        var created = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest("Assistente", "fake:1", "Seja direto", null, null));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var model = (await created.Content.ReadFromJsonAsync<ModelEntryResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(model.IsActive, Is.True);
            Assert.That(model.BaseModelId, Is.EqualTo("fake:1"));
        });

        // Listagem por /list (alias de /) e leitura individual por query string.
        var list = await _client.GetFromJsonAsync<List<ModelEntryResponse>>("/api/v1/models/list");
        Assert.That(list!.Any(m => m.Id == model.Id), Is.True);

        var fetched = await _client.GetFromJsonAsync<ModelEntryResponse>(
            $"/api/v1/models/model?id={model.Id}");
        Assert.That(fetched!.Name, Is.EqualTo("Assistente"));

        // UpdateModelAsync: altera nome e system prompt.
        var updated = await _client.PostAsJsonAsync("/api/v1/models/model/update",
            new { id = model.Id, name = "Assistente v2", systemPrompt = "Seja formal" });
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var updatedModel = (await updated.Content.ReadFromJsonAsync<ModelEntryResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(updatedModel.Name, Is.EqualTo("Assistente v2"));
            Assert.That(updatedModel.SystemPrompt, Is.EqualTo("Seja formal"));
            Assert.That(updatedModel.UpdatedAt, Is.GreaterThanOrEqualTo(model.UpdatedAt));
        });

        // Toggle desativa: some do seletor (/api/models agrega só ativos).
        var toggled = await _client.PostAsJsonAsync("/api/v1/models/model/toggle",
            new { id = model.Id });
        var toggledModel = (await toggled.Content.ReadFromJsonAsync<ModelEntryResponse>())!;
        Assert.That(toggledModel.IsActive, Is.False);

        var selector = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");
        Assert.That(selector!.Data.Any(m => m.Id == model.Id), Is.False);

        var reactivated = await _client.PostAsJsonAsync("/api/v1/models/model/toggle",
            new { id = model.Id });
        var reactivatedModel = (await reactivated.Content.ReadFromJsonAsync<ModelEntryResponse>())!;
        Assert.That(reactivatedModel.IsActive, Is.True);

        var selectorAfter = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");
        Assert.That(selectorAfter!.Data.Any(m => m.Id == model.Id), Is.True);

        var deleted = await _client.PostAsJsonAsync("/api/v1/models/model/delete",
            new { id = model.Id });
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var gone = await _client.GetAsync($"/api/v1/models/model?id={model.Id}");
        Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Modelos_OperacoesEmModeloAlheio_Retornam404()
    {
        var owner = await SignUpAsync("Owner", "owner@models.local", "senha123");
        UseToken(owner.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest("Privado", "fake:1", null, null, null));
        var model = (await created.Content.ReadFromJsonAsync<ModelEntryResponse>())!;

        var other = await SignUpAsync("Other", "other@models.local", "senha123");
        UseToken(other.Token);

        var get = await _client.GetAsync($"/api/v1/models/model?id={model.Id}");
        var update = await _client.PostAsJsonAsync("/api/v1/models/model/update",
            new { id = model.Id, name = "Invasão" });
        var toggle = await _client.PostAsJsonAsync("/api/v1/models/model/toggle",
            new { id = model.Id });
        var delete = await _client.PostAsJsonAsync("/api/v1/models/model/delete",
            new { id = model.Id });
        var list = await _client.GetFromJsonAsync<List<ModelEntryResponse>>("/api/v1/models/");

        Assert.Multiple(() =>
        {
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(update.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(toggle.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(list!.Any(m => m.Id == model.Id), Is.False);
        });
    }

    [Test]
    public async Task Modelos_ImportCriaEExportLista()
    {
        var user = await SignUpAsync("Imp", "imp@models.local", "senha123");
        UseToken(user.Token);

        var import = await _client.PostAsJsonAsync("/api/v1/models/import",
            new List<ModelEntryUpsertRequest>
            {
                new("Importado A", "fake:1", "Prompt A", null, null),
                new("Importado B", "fake:1", null, "{\"temperature\":0.5}", null),
            });
        Assert.That(import.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var export = await _client.GetFromJsonAsync<List<ModelEntryResponse>>(
            "/api/v1/models/export");
        Assert.That(export!.Select(m => m.Name),
            Is.SupersetOf(new[] { "Importado A", "Importado B" }));

        var imported = export!.First(m => m.Name == "Importado B");
        Assert.That(imported.ParamsJson, Is.EqualTo("{\"temperature\":0.5}"));
    }

    [Test]
    public async Task Completion_ModeloCustomizado_UsaModeloBaseESystemPrompt()
    {
        var user = await SignUpAsync("Comp", "comp@models.local", "senha123");
        UseToken(user.Token);

        var created = await _client.PostAsJsonAsync("/api/v1/models/create",
            new ModelEntryUpsertRequest("Custom", "fake:1", "Responda em pt-BR", null, null));
        var model = (await created.Content.ReadFromJsonAsync<ModelEntryResponse>())!;

        var request = new ChatCompletionRequest(
            model.Id,
            [new ChatCompletionMessage("user", "oi")]);
        var response = await _client.PostAsJsonAsync("/api/chat/completions", request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("data:"));

        // O provedor recebeu o modelo base, não o id custom, e o system prompt aplicado.
        Assert.That(_lastChatBody, Is.Not.Null);
        var payload = JsonNode.Parse(_lastChatBody!)!;
        Assert.That(payload["model"]!.GetValue<string>(), Is.EqualTo("fake:1"));
        // SPEC-20261103-agent-prompt-cache RF-001: system vem em dois
        // blocos — o system do modelo custom é tier Dynamic (último bloco).
        var system = string.Join("\n", payload["messages"]!.AsArray()
            .Where(m => m?["role"]?.GetValue<string>() == "system")
            .Select(m => m!["content"]!.GetValue<string>()));
        Assert.That(system, Does.Contain("Responda em pt-BR"));
    }
}
