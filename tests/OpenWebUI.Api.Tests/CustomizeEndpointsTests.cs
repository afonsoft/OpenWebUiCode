using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do hub Customize (SPEC-20261104-customize-hub): notas de
/// conhecimento com escopo org/pessoal + tombstone, catálogo unificado de
/// skills com deny global admin, e import restrito a admin.
/// </summary>
[TestFixture, IsolateEnvironment]
public class CustomizeEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;
    private string _userToken = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = TestInfra.NewDbPath("openwebui-customize");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@custom.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        Assert.That(
            (await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config))
                .StatusCode,
            Is.EqualTo(HttpStatusCode.OK));

        var user = await SignUpAsync("User", "user@custom.local", "senha123");
        _userToken = user.Token;
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
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    // ---------------- Knowledge (RF-003) ----------------

    [Test]
    public async Task Knowledge_AdminCriaGlobal_UsuarioVeComoOrg()
    {
        UseToken(_adminToken);
        var create = await _client.PostAsJsonAsync("/api/v1/customize/knowledge",
            new KnowledgeNoteRequest("Convencao PR", "pull request", "Use conventional commits.", false, Global: true));
        Assert.That(create.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        UseToken(_userToken);
        var list = await _client.GetFromJsonAsync<JsonWrapper<KnowledgeNoteResponse>>(
            "/api/v1/customize/knowledge");
        var note = list!.Data.Single(n => n.Title == "Convencao PR");
        Assert.Multiple(() =>
        {
            Assert.That(note.Scope, Is.EqualTo("org"));
            Assert.That(note.EnabledForMe, Is.True);
            Assert.That(note.Editable, Is.False);
        });
    }

    [Test]
    public async Task Knowledge_UsuarioNaoCriaGlobal_403()
    {
        UseToken(_userToken);
        var response = await _client.PostAsJsonAsync("/api/v1/customize/knowledge",
            new KnowledgeNoteRequest("GlobalFake", null, "x", true, Global: true));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task Knowledge_SemTriggerNaoPinada_400_ETituloDuplicado_409()
    {
        UseToken(_userToken);
        var bad = await _client.PostAsJsonAsync("/api/v1/customize/knowledge",
            new KnowledgeNoteRequest("SemTrigger", null, "x", false));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var ok = await _client.PostAsJsonAsync("/api/v1/customize/knowledge",
            new KnowledgeNoteRequest("Minha", "t", "corpo", false));
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        var dup = await _client.PostAsJsonAsync("/api/v1/customize/knowledge",
            new KnowledgeNoteRequest("Minha", "t", "corpo", false));
        Assert.That(dup.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task Knowledge_TombstoneGlobal_NaoAfetaOutros()
    {
        UseToken(_adminToken);
        var created = await (await _client.PostAsJsonAsync("/api/v1/customize/knowledge",
            new KnowledgeNoteRequest("NotaToggle", "x", "corpo", true, Global: true)))
            .Content.ReadFromJsonAsync<KnowledgeNoteResponse>();

        UseToken(_userToken);
        Assert.That((await _client.PutAsJsonAsync(
            $"/api/v1/customize/knowledge/{created!.Id}/enabled",
            new CustomizeEnabledRequest(false))).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var mine = await _client.GetFromJsonAsync<JsonWrapper<KnowledgeNoteResponse>>(
            "/api/v1/customize/knowledge");
        Assert.That(mine!.Data.Single(n => n.Id == created.Id).EnabledForMe, Is.False);

        UseToken(_adminToken);
        var admins = await _client.GetFromJsonAsync<JsonWrapper<KnowledgeNoteResponse>>(
            "/api/v1/customize/knowledge");
        Assert.That(admins!.Data.Single(n => n.Id == created.Id).EnabledForMe, Is.True);
    }

    [Test]
    public async Task Knowledge_NotaDeOutroUsuario_404()
    {
        UseToken(_userToken);
        var created = await (await _client.PostAsJsonAsync("/api/v1/customize/knowledge",
            new KnowledgeNoteRequest("Privada", "x", "corpo", false)))
            .Content.ReadFromJsonAsync<KnowledgeNoteResponse>();

        var other = await SignUpAsync("Other", "other@custom.local", "senha123");
        UseToken(other.Token);
        Assert.That((await _client.PutAsJsonAsync(
            $"/api/v1/customize/knowledge/{created!.Id}",
            new KnowledgeNoteRequest("Hackeada", "x", "corpo", false))).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That((await _client.DeleteAsync(
            $"/api/v1/customize/knowledge/{created.Id}")).StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---------------- Skills catalog + deny (RF-006/RF-007) ----------------

    [Test]
    public async Task Skills_CatalogoUnificado_ComFonteEEscopo()
    {
        UseToken(_adminToken);
        await _client.PostAsJsonAsync("/api/v1/customize/skills",
            new SkillUpsertRequest("org-skill", "da org", "corpo org", Global: true));

        UseToken(_userToken);
        await _client.PostAsJsonAsync("/api/v1/customize/skills",
            new SkillUpsertRequest("my-skill", "minha", "corpo minha"));

        var list = await _client.GetFromJsonAsync<JsonWrapper<SkillCatalogItem>>(
            "/api/v1/customize/skills");
        Assert.Multiple(() =>
        {
            var org = list!.Data.Single(s => s.Name == "org-skill");
            Assert.That(org.Source, Is.EqualTo("global"));
            Assert.That(org.EnabledForMe, Is.True);
            Assert.That(org.Content, Is.EqualTo("corpo org"));

            var own = list.Data.Single(s => s.Name == "my-skill");
            Assert.That(own.Source, Is.EqualTo("personal"));
            Assert.That(own.Editable, Is.True);
        });
    }

    [Test]
    public async Task Skills_UsuarioNaoCriaGlobal_403_EDenyAdminAbsoluto()
    {
        UseToken(_userToken);
        var forbid = await _client.PostAsJsonAsync("/api/v1/customize/skills",
            new SkillUpsertRequest("g", null, "x", Global: true));
        Assert.That(forbid.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_adminToken);
        var created = await (await _client.PostAsJsonAsync("/api/v1/customize/skills",
            new SkillUpsertRequest("denied-skill", null, "corpo", Global: true)))
            .Content.ReadFromJsonAsync<SkillCatalogItem>();
        Assert.That((await _client.PutAsJsonAsync("/api/v1/admin/skills/disabled",
            new SkillsDisabledRequest(["denied-skill"]))).StatusCode,
            Is.EqualTo(HttpStatusCode.OK));

        UseToken(_userToken);
        var reEnable = await _client.PutAsJsonAsync(
            $"/api/v1/customize/skills/{created!.Id}/enabled",
            new CustomizeEnabledRequest(true));
        Assert.That(reEnable.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var list = await _client.GetFromJsonAsync<JsonWrapper<SkillCatalogItem>>(
            "/api/v1/customize/skills");
        var item = list!.Data.Single(s => s.Name == "denied-skill");
        Assert.Multiple(() =>
        {
            Assert.That(item.DisabledByAdmin, Is.True);
            Assert.That(item.EnabledForMe, Is.False);
        });
    }

    [Test]
    public async Task Skills_TogglePessoal_IsActive()
    {
        UseToken(_userToken);
        var created = await (await _client.PostAsJsonAsync("/api/v1/customize/skills",
            new SkillUpsertRequest("toggle-me", null, "corpo")))
            .Content.ReadFromJsonAsync<SkillCatalogItem>();

        Assert.That((await _client.PutAsJsonAsync(
            $"/api/v1/customize/skills/{created!.Id}/enabled",
            new CustomizeEnabledRequest(false))).StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = await _client.GetFromJsonAsync<JsonWrapper<SkillCatalogItem>>(
            "/api/v1/customize/skills");
        Assert.That(list!.Data.Single(s => s.Id == created.Id).EnabledForMe, Is.False);
    }

    // ---------------- Import (RF-008) ----------------

    [Test]
    public async Task Import_NaoAdmin_403_EUrlInvalida_400()
    {
        UseToken(_userToken);
        Assert.That((await _client.PostAsJsonAsync("/api/v1/admin/skills/import",
            new SkillImportRequest("https://github.com/afonsoft/skills"))).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_adminToken);
        var bad = await _client.PostAsJsonAsync("/api/v1/admin/skills/import",
            new SkillImportRequest("https://evil.example.com/repo"));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---------------- allowed-tools + plugins (SPEC-20261104) ----------------

    [Test]
    public async Task Skill_AllowedTools_Persiste_EVoltaNoCatalogo()
    {
        UseToken(_adminToken);
        var created = await (await _client.PostAsJsonAsync("/api/v1/customize/skills",
            new SkillUpsertRequest("Restrita", null, "corpo", Global: true,
                AllowedTools: "File_Read, shell_exec , file_read")))
            .Content.ReadFromJsonAsync<SkillCatalogItem>();
        Assert.That(created, Is.Not.Null);
        // Normalizado: trim + lower + dedupe.
        Assert.That(created!.AllowedTools, Is.EqualTo("file_read,shell_exec"));

        var list = await _client.GetFromJsonAsync<JsonWrapper<SkillCatalogItem>>(
            "/api/v1/customize/skills");
        Assert.That(list!.Data.Single(s => s.Name == "Restrita").AllowedTools,
            Is.EqualTo("file_read,shell_exec"));

        // Update limpa a restrição.
        Assert.That((await _client.PutAsJsonAsync(
            $"/api/v1/customize/skills/{created.Id}",
            new SkillUpsertRequest("Restrita", null, "corpo2", Global: true)))
            .StatusCode, Is.EqualTo(HttpStatusCode.OK));
        list = await _client.GetFromJsonAsync<JsonWrapper<SkillCatalogItem>>(
            "/api/v1/customize/skills");
        Assert.That(list!.Data.Single(s => s.Name == "Restrita").AllowedTools, Is.Null);
    }

    [Test]
    public async Task PluginImport_NaoAdmin_403_EUrlInvalida_400()
    {
        UseToken(_userToken);
        Assert.That((await _client.PostAsJsonAsync("/api/v1/admin/plugins/import",
            new PluginImportRequest("https://github.com/afonsoft/skills"))).StatusCode,
            Is.EqualTo(HttpStatusCode.Forbidden));

        UseToken(_adminToken);
        var bad = await _client.PostAsJsonAsync("/api/v1/admin/plugins/import",
            new PluginImportRequest("https://evil.example.com/repo"));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>Envelope <c>{ data: [...] }</c> das listagens customize.</summary>
    private sealed record JsonWrapper<T>(List<T> Data);
}
