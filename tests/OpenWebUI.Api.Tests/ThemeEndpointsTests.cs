using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261018-themes-custom: endpoints <c>/api/v1/themes</c> — listagem
/// (instance via <c>WEBUI_THEME_JSON</c> + customs do usuário + seleção ativa),
/// upload validado, delete próprio e persistência da seleção.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ThemeEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-themes-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("Admin", "admin@themes.local", "senha123");
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
        TestInfra.DeleteDb(_dbPath);
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

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private const string ValidTheme = """
        {
          "id": "meu-tema",
          "name": "Meu Tema",
          "dark": { "bg": "#0b0f14", "surface": "#141b24", "text": "#dbe4ee", "accent": "#58a6ff" },
          "light": { "bg": "#ffffff", "surface": "#f5f5f5", "text": "#1a1a2e" }
        }
        """;

    // Covers RF-003: upload JSON validado e salvo por usuário.
    [Test]
    public async Task UploadTema_Valido_CriaCustom()
    {
        var user = await SignUpAsync("Up1", "up1@themes.local", "senha123");
        UseToken(user.Token);

        var created = await _client.PostAsync("/api/v1/themes", Json(ValidTheme));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());

        var doc = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(doc.GetProperty("themeId").GetString(), Is.EqualTo("meu-tema"));

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/v1/themes");
        var customs = list.GetProperty("customs");
        Assert.That(customs.GetArrayLength(), Is.EqualTo(1));
        Assert.That(customs[0].GetProperty("name").GetString(), Is.EqualTo("Meu Tema"));
    }

    // Covers RF-001/AC: token com url( é rejeitado → 400 com erro claro.
    [Test]
    public async Task UploadTema_TokenComUrl_Retorna400()
    {
        var user = await SignUpAsync("Up2", "up2@themes.local", "senha123");
        UseToken(user.Token);

        var malicious = """
            { "name": "evil", "dark": { "bg": "url(javascript:alert(1))" } }
            """;
        var response = await _client.PostAsync("/api/v1/themes", Json(malicious));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("bg").IgnoreCase);
    }

    // Covers RF-001: valor inválido (não hex/rgb/var) → 400.
    [Test]
    public async Task UploadTema_ValorInvalido_Retorna400()
    {
        var user = await SignUpAsync("Up3", "up3@themes.local", "senha123");
        UseToken(user.Token);

        var invalid = """
            { "name": "bad", "dark": { "bg": "red; } </style><script>alert(1)</script>" } }
            """;
        var response = await _client.PostAsync("/api/v1/themes", Json(invalid));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // Covers RF-003: máx 32 tokens.
    [Test]
    public async Task UploadTema_Excede32Tokens_Retorna400()
    {
        var user = await SignUpAsync("Up4", "up4@themes.local", "senha123");
        UseToken(user.Token);

        var tokens = string.Join(",",
            Enumerable.Range(0, 33).Select(i => $"\"--tok-{i}\": \"#ffffff\""));
        var theme = $"{{ \"name\": \"gordo\", \"vars\": {{ {tokens} }} }}";

        var response = await _client.PostAsync("/api/v1/themes", Json(theme));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // Covers RF-003: máx 64KB de payload.
    [Test]
    public async Task UploadTema_Excede64Kb_Retorna400()
    {
        var user = await SignUpAsync("Up5", "up5@themes.local", "senha123");
        UseToken(user.Token);

        var name = new string('x', 70 * 1024);
        var theme = $"{{ \"name\": \"{name}\" }}";

        var response = await _client.PostAsync("/api/v1/themes", Json(theme));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // Covers RF-003: WEBUI_THEME_JSON marca tema da instância para todos.
    [Test]
    public async Task ListarTemas_ComWebuiThemeJson_ExpoeInstancia()
    {
        Environment.SetEnvironmentVariable("WEBUI_THEME_JSON",
            """{"id":"corp","name":"Corp","dark":{"bg":"#101418","text":"#e8edf2"}}""");
        try
        {
            var user = await SignUpAsync("Inst", "inst@themes.local", "senha123");
            UseToken(user.Token);

            var list = await _client.GetFromJsonAsync<JsonElement>("/api/v1/themes");

            var instance = list.GetProperty("instance");
            Assert.That(instance.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(instance.GetProperty("id").GetString(), Is.EqualTo("corp"));
            Assert.That(instance.GetProperty("name").GetString(), Is.EqualTo("Corp"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBUI_THEME_JSON", null);
        }
    }

    // Covers RF-003: WEBUI_THEME_JSON malformado → instance null (não quebra).
    [Test]
    public async Task ListarTemas_WebuiThemeJsonInvalido_InstanceNull()
    {
        Environment.SetEnvironmentVariable("WEBUI_THEME_JSON", "{not json");
        try
        {
            var user = await SignUpAsync("Inv1", "inv1@themes.local", "senha123");
            UseToken(user.Token);

            var list = await _client.GetFromJsonAsync<JsonElement>("/api/v1/themes");
            Assert.That(list.GetProperty("instance").ValueKind,
                Is.EqualTo(JsonValueKind.Null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBUI_THEME_JSON", null);
        }
    }

    // Covers RF-003: WEBUI_THEME_JSON acima de 64KB → instance null.
    [Test]
    public async Task ListarTemas_WebuiThemeJsonGigante_InstanceNull()
    {
        Environment.SetEnvironmentVariable("WEBUI_THEME_JSON",
            new string('x', 70 * 1024));
        try
        {
            var user = await SignUpAsync("Inv2", "inv2@themes.local", "senha123");
            UseToken(user.Token);

            var list = await _client.GetFromJsonAsync<JsonElement>("/api/v1/themes");
            Assert.That(list.GetProperty("instance").ValueKind,
                Is.EqualTo(JsonValueKind.Null));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WEBUI_THEME_JSON", null);
        }
    }

    // Covers RF-002: escolha persistida por usuário.
    [Test]
    public async Task DefinirAtivo_TemaBuiltin_PersisteSelecao()
    {
        var user = await SignUpAsync("Sel1", "sel1@themes.local", "senha123");
        UseToken(user.Token);

        var put = await _client.PutAsJsonAsync("/api/v1/themes/active",
            new { themeId = "midnight", mode = "dark" });
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await put.Content.ReadAsStringAsync());

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/v1/themes");
        var active = list.GetProperty("active");
        Assert.Multiple(() =>
        {
            Assert.That(active.GetProperty("themeId").GetString(), Is.EqualTo("midnight"));
            Assert.That(active.GetProperty("mode").GetString(), Is.EqualTo("dark"));
        });
    }

    // Covers RF-002: modo inválido → 400.
    [Test]
    public async Task DefinirAtivo_ModoInvalido_Retorna400()
    {
        var user = await SignUpAsync("Sel2", "sel2@themes.local", "senha123");
        UseToken(user.Token);

        var put = await _client.PutAsJsonAsync("/api/v1/themes/active",
            new { themeId = "midnight", mode = "neon" });
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // Covers RF-003: tema custom ativo marca a linha do usuário.
    [Test]
    public async Task DefinirAtivo_TemaCustom_MarcaActive()
    {
        var user = await SignUpAsync("Sel3", "sel3@themes.local", "senha123");
        UseToken(user.Token);

        var created = await _client.PostAsync("/api/v1/themes", Json(ValidTheme));
        var doc = await created.Content.ReadFromJsonAsync<JsonElement>();
        var themeId = doc.GetProperty("themeId").GetString()!;

        var put = await _client.PutAsJsonAsync("/api/v1/themes/active",
            new { themeId, mode = "system" });
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/v1/themes");
        var custom = list.GetProperty("customs").EnumerateArray().Single();
        Assert.That(custom.GetProperty("active").GetBoolean(), Is.True);
    }

    // Covers RF-003: delete remove o tema do próprio usuário.
    [Test]
    public async Task DeletarTema_Proprio_Remove()
    {
        var user = await SignUpAsync("Del1", "del1@themes.local", "senha123");
        UseToken(user.Token);

        var created = await _client.PostAsync("/api/v1/themes", Json(ValidTheme));
        var doc = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = doc.GetProperty("id").GetString()!;

        var del = await _client.DeleteAsync($"/api/v1/themes/{id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/v1/themes");
        Assert.That(list.GetProperty("customs").GetArrayLength(), Is.EqualTo(0));
    }

    // Covers RF-002/RF-003: deletar o tema ativo reverte a seleção a default.
    [Test]
    public async Task DeletarTema_Ativo_ReverteSelecaoParaDefault()
    {
        var user = await SignUpAsync("Del2", "del2@themes.local", "senha123");
        UseToken(user.Token);

        var created = await _client.PostAsync("/api/v1/themes", Json(ValidTheme));
        var doc = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = doc.GetProperty("id").GetString()!;
        var themeId = doc.GetProperty("themeId").GetString()!;

        var put = await _client.PutAsJsonAsync("/api/v1/themes/active",
            new { themeId, mode = "dark" });
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var del = await _client.DeleteAsync($"/api/v1/themes/{id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/v1/themes");
        var active = list.GetProperty("active");
        Assert.That(active.GetProperty("themeId").GetString(), Is.EqualTo("default"));
    }

    // Covers RF-002: themeId fora do formato (slug kebab-case ou "default") → 400.
    [TestCase("EVIL!")]
    [TestCase("  ")]
    [TestCase("tema_com_underscore")]
    public async Task DefinirAtivo_ThemeIdInvalido_Retorna400(string themeId)
    {
        var user = await SignUpAsync("Sel4", $"sel4-{Guid.NewGuid():N}@themes.local", "senha123");
        UseToken(user.Token);

        var put = await _client.PutAsJsonAsync("/api/v1/themes/active",
            new { themeId, mode = "system" });
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // Covers RF-002: mode "light" persiste como "light" (ramo de fallback).
    [Test]
    public async Task DefinirAtivo_ModoLight_PersisteLight()
    {
        var user = await SignUpAsync("Sel5", "sel5@themes.local", "senha123");
        UseToken(user.Token);

        var put = await _client.PutAsJsonAsync("/api/v1/themes/active",
            new { themeId = "default", mode = "light" });
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/v1/themes");
        Assert.That(list.GetProperty("active").GetProperty("mode").GetString(),
            Is.EqualTo("light"));
    }

    // Covers RF-003: tema de outro usuário não é deletável → 404.
    [Test]
    public async Task DeletarTema_DeOutroUsuario_Retorna404()
    {
        var owner = await SignUpAsync("Own", "own@themes.local", "senha123");
        UseToken(owner.Token);
        var created = await _client.PostAsync("/api/v1/themes", Json(ValidTheme));
        var doc = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = doc.GetProperty("id").GetString()!;

        var other = await SignUpAsync("Otr", "otr@themes.local", "senha123");
        UseToken(other.Token);
        var del = await _client.DeleteAsync($"/api/v1/themes/{id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // Covers RF-001: sanitização aceita hex, rgb(), rgba() e var().
    [TestCase("#fff", true)]
    [TestCase("#a1b2c3", true)]
    [TestCase("#a1b2c3d4", true)]
    [TestCase("rgb(10 20 30)", true)]
    [TestCase("rgba(10, 20, 30, 0.5)", true)]
    [TestCase("var(--color-gray-900)", true)]
    [TestCase("var(--x, #fff)", true)]
    [TestCase("url(https://evil)", false)]
    [TestCase("expression(alert(1))", false)]
    [TestCase("red; } </style>", false)]
    [TestCase("javascript:alert(1)", false)]
    [TestCase("#zzz", false)]
    public void Sanitizacao_AceitaApenasHexRgbVar(string value, bool expected) =>
        Assert.That(ThemeValidator.IsValidTokenValue(value), Is.EqualTo(expected));

    // Covers AC: CSS gerado escopa por classe do tema e cobre rampa de cinzas.
    [Test]
    public void CssBuilder_GeraVarsSemanticasERampa()
    {
        var theme = new CustomThemeDefinition(
            "midnight", "Midnight",
            new Dictionary<string, string> { ["bg"] = "#0b0f14", ["text"] = "#e6edf3" },
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var css = ThemeCssBuilder.Build(theme);

        Assert.Multiple(() =>
        {
            Assert.That(css, Does.Contain("html.theme-midnight"));
            Assert.That(css, Does.Contain("--bg: #0b0f14"));
            Assert.That(css, Does.Contain("--color-gray-950"));
            Assert.That(css, Does.Contain("html.theme-midnight.dark"));
        });
    }
}
