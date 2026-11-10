using System.Net;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using OpenWebUI.Api.Tests.Providers;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261104-customize-hub RF-008 + SPEC-20261104-skills-allowed-
/// tools-plugins RF-003: import de skills/plugins de repos git públicos —
/// SSRF restrito a github.com, SKILL.md varridos da árvore, manifesto
/// plugin.json (skills/commands/mcps), upsert idempotente por nome e
/// MCPs importados desabilitados sem headers.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ImportServicesTests
{
    private string _dbPath = null!;
    private AppDbContext _db = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-imp-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        await DatabaseMigrator.MigrateAsync(_db);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    [SetUp]
    public async Task SetUp()
    {
        _db.Skills.RemoveRange(_db.Skills);
        _db.McpServers.RemoveRange(_db.McpServers);
        await _db.SaveChangesAsync();
    }

    private const string RepoUrl = "https://github.com/acme/toolbox";

    private static readonly string[] TreeDois = [
        "skills/alfa/SKILL.md", "skills/beta/SKILL.md", "docs/readme.md"];

    private static string TreeJson(IEnumerable<string> paths) =>
        "{\"tree\":[" + string.Join(',', paths.Select(p =>
            $"{{\"path\":\"{p}\",\"type\":\"blob\"}}")) + "]}";

    private const string SkillAlfa =
        "---\nname: alfa-skill\ndescription: faz alfa\n"
        + "allowed-tools: [file_read, shell_exec]\n---\n# Alfa\nconteúdo";

    private const string SkillBeta = "# Beta sem frontmatter";

    private void EnqueueRepoInfo(StubHttpHandler http, string branch = "main") =>
        http.Enqueue(HttpStatusCode.OK, $"{{\"default_branch\":\"{branch}\"}}");

    // ---------- SkillImportService ----------

    /// <summary>URL fora do padrão github.com/{owner}/{repo} → "invalid:"
    /// sem chamada de rede.</summary>
    [Test]
    public async Task SkillImport_UrlInvalida_ThrowsInvalid()
    {
        var service = new SkillImportService(_db,
            new StubHttpFactory(new StubHttpHandler()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.ImportAsync("https://evil.com/x",
                null, null, CancellationToken.None));

        Assert.That(ex!.Message, Does.StartWith("invalid:"));
    }

    /// <summary>Fluxo feliz: repo info → árvore → SKILL.md por pasta;
    /// skills viram globais (UserId null) com frontmatter parseado.</summary>
    [Test]
    public async Task SkillImport_FluxoFeliz_ImportaEUpserta()
    {
        var http = new StubHttpHandler();
        EnqueueRepoInfo(http);
        http.Enqueue(HttpStatusCode.OK, TreeJson(TreeDois));
        http.Enqueue(HttpStatusCode.OK, SkillAlfa);
        http.Enqueue(HttpStatusCode.OK, SkillBeta);
        var service = new SkillImportService(_db, new StubHttpFactory(http));

        var result = await service.ImportAsync(RepoUrl, "skills", null,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Imported, Is.EquivalentTo(
                new[] { "alfa-skill", "beta" }));
            Assert.That(result.Skipped, Is.Empty);
            var alfa = _db.Skills.Single(s => s.Name == "alfa-skill");
            Assert.That(alfa.UserId, Is.Null);
            Assert.That(alfa.Description, Is.EqualTo("faz alfa"));
            Assert.That(alfa.AllowedTools, Is.EqualTo("file_read,shell_exec"));
            Assert.That(alfa.IsActive, Is.True);
            var beta = _db.Skills.Single(s => s.Name == "beta");
            Assert.That(beta.Description, Is.Null,
                "sem frontmatter → nome cai no dirName");
            // urls no wire: api.github.com + raw.githubusercontent.com.
            Assert.That(http.Requests[0].Url,
                Does.Contain("api.github.com/repos/acme/toolbox"));
            Assert.That(http.Requests[1].Url,
                Does.Contain("git/trees/main?recursive=1"));
            Assert.That(http.Requests[2].Url,
                Does.Contain("raw.githubusercontent.com/acme/toolbox/main/"));
        });

        // Idempotente: reimport atualiza sem duplicar.
        var http2 = new StubHttpHandler();
        EnqueueRepoInfo(http2);
        http2.Enqueue(HttpStatusCode.OK, TreeJson(TreeDois));
        http2.Enqueue(HttpStatusCode.OK, SkillAlfa);
        http2.Enqueue(HttpStatusCode.OK, SkillBeta);
        var result2 = await new SkillImportService(_db,
            new StubHttpFactory(http2)).ImportAsync(
                RepoUrl, "skills", null, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(result2.Imported, Has.Count.EqualTo(2));
            Assert.That(_db.Skills.Count(), Is.EqualTo(2));
        });
    }

    /// <summary><paramref name="names"/> filtra por dirName: skills fora
    /// da seleção viram skipped "não selecionado".</summary>
    [Test]
    public async Task SkillImport_FiltroPorNome_Skips()
    {
        var http = new StubHttpHandler();
        EnqueueRepoInfo(http);
        http.Enqueue(HttpStatusCode.OK, TreeJson(TreeDois));
        http.Enqueue(HttpStatusCode.OK, SkillAlfa);
        var service = new SkillImportService(_db, new StubHttpFactory(http));

        var result = await service.ImportAsync(RepoUrl, "skills",
            ["alfa"], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Imported, Is.EqualTo(new[] { "alfa-skill" }));
            Assert.That(result.Skipped, Has.Count.EqualTo(1));
            Assert.That(result.Skipped[0].Name, Is.EqualTo("beta"));
            Assert.That(result.Skipped[0].Reason,
                Does.Contain("não selecionado"));
        });
    }

    /// <summary>Falha do GitHub na primeira chamada → "github:" (502-level
    /// no endpoint). Download individual falho → skip, não aborta.</summary>
    [Test]
    public async Task SkillImport_ErrosGithub_EDownload()
    {
        var httpFail = new StubHttpHandler();
        httpFail.Enqueue(HttpStatusCode.InternalServerError, "{}");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SkillImportService(_db,
                new StubHttpFactory(httpFail)).ImportAsync(
                    RepoUrl, "skills", null, CancellationToken.None));
        Assert.That(ex!.Message, Does.StartWith("github:"));

        var http = new StubHttpHandler();
        EnqueueRepoInfo(http);
        http.Enqueue(HttpStatusCode.OK, TreeJson(TreeDois));
        http.Enqueue(HttpStatusCode.OK, SkillAlfa);
        http.Enqueue(HttpStatusCode.NotFound, "nope");
        var result = await new SkillImportService(_db,
            new StubHttpFactory(http)).ImportAsync(
                RepoUrl, "skills", null, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(result.Imported, Is.EqualTo(new[] { "alfa-skill" }));
            Assert.That(result.Skipped.Single().Name, Is.EqualTo("beta"));
            Assert.That(result.Skipped[0].Reason,
                Does.Contain("falha no download"));
        });
    }

    /// <summary>ParseSkillMarkdown: frontmatter (BOM tolerado),
    /// allowed-tools normalizado lowercase, sem frontmatter → nulls.</summary>
    [Test]
    public void ParseSkillMarkdown_FrontmatterEVazias()
    {
        var (name, desc, content, tools) =
            SkillImportService.ParseSkillMarkdown(
                "\uFEFF---\nname: \"quoted\"\ndescription: 'd'\n"
                + "allowed_tools: [B, a, a]\n---\nbody");
        Assert.Multiple(() =>
        {
            Assert.That(name, Is.EqualTo("quoted"));
            Assert.That(desc, Is.EqualTo("d"));
            Assert.That(tools, Is.EqualTo("b,a"));
            Assert.That(content, Does.Contain("body"));
        });

        var (n2, d2, _, t2) = SkillImportService.ParseSkillMarkdown(
            "# sem frontmatter");
        Assert.Multiple(() =>
        {
            Assert.That(n2, Is.Null);
            Assert.That(d2, Is.Null);
            Assert.That(t2, Is.Null);
        });
    }

    // ---------- PluginImportService ----------

    private void EnqueueManifest(StubHttpHandler http, string manifest,
        string branch = "main", IEnumerable<string>? files = null)
    {
        EnqueueRepoInfo(http, branch);
        http.Enqueue(HttpStatusCode.OK, manifest);
        http.Enqueue(HttpStatusCode.OK,
            TreeJson(files ?? ["skills/foo/SKILL.md", "commands/deploy.md"]));
    }

    /// <summary>URL inválida → "invalid:"; plugin.json 404 →
    /// "invalid: plugin.json não encontrado"; JSON malformado →
    /// "invalid: malformado".</summary>
    [Test]
    public async Task PluginImport_ErrosDeEntrada()
    {
        var svc = new PluginImportService(_db,
            new StubHttpFactory(new StubHttpHandler()));
        var exUrl = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.ImportAsync("não-é-url", CancellationToken.None));
        Assert.That(exUrl!.Message, Does.StartWith("invalid:"));

        var http404 = new StubHttpHandler();
        EnqueueRepoInfo(http404);
        http404.Enqueue(HttpStatusCode.NotFound, "");
        var ex404 = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new PluginImportService(_db,
                new StubHttpFactory(http404)).ImportAsync(
                    RepoUrl, CancellationToken.None));
        Assert.That(ex404!.Message, Does.Contain("plugin.json"));

        var httpBad = new StubHttpHandler();
        EnqueueManifest(httpBad, "{isto não é json");
        var exBad = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new PluginImportService(_db,
                new StubHttpFactory(httpBad)).ImportAsync(
                    RepoUrl, CancellationToken.None));
        Assert.That(exBad!.Message, Does.Contain("malformado"));
    }

    /// <summary>Fluxo feliz completo: skills do manifest + command vira
    /// skill + MCP http nasce desabilitado e sem headers; entries ruins
    /// viram warnings/skipped.</summary>
    [Test]
    public async Task PluginImport_FluxoFeliz_SkillsCommandsEMcps()
    {
        var manifest =
            "{\"skills\":[\"skills/foo\",\"skills/falta\"]," +
            "\"commands\":[\"commands/deploy.md\",\"commands/ausente.md\"]," +
            "\"mcps\":[" +
            "{\"name\":\"web\",\"transport\":\"http\",\"url\":\"https://mcp.test\"}," +
            "{\"name\":\"local\",\"transport\":\"stdio\",\"command\":\"npx\",\"args\":[\"-y\",\"srv\"],\"env\":[\"KEY\"]}," +
            "{\"name\":\"sem-url\",\"transport\":\"http\"}," +
            "{\"command\":\"x\"}," +
            "{\"name\":\"com-headers\",\"transport\":\"http\",\"url\":\"https://h.test\",\"headers\":{\"A\":\"b\"}}" +
            "]}";
        var http = new StubHttpHandler();
        EnqueueManifest(http, manifest,
            files: ["skills/foo/SKILL.md", "commands/deploy.md"]);
        http.Enqueue(HttpStatusCode.OK, SkillAlfa);        // skills/foo
        http.Enqueue(HttpStatusCode.OK, "# deploy cmd");   // commands/deploy.md

        var result = await new PluginImportService(_db,
            new StubHttpFactory(http)).ImportAsync(RepoUrl, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ImportedSkills, Is.EquivalentTo(
                new[] { "alfa-skill", "deploy" }));
            Assert.That(result.ImportedMcps, Is.EquivalentTo(
                new[] { "web", "local", "com-headers" }));
            var skippedNames = result.Skipped.Select(s => s.Name).ToList();
            Assert.That(skippedNames, Does.Contain("skills:skills/falta"));
            Assert.That(skippedNames, Does.Contain("commands:commands/ausente.md"));
            Assert.That(result.Warnings.Any(w => w.Contains("sem-url")), Is.True);
            Assert.That(result.Warnings.Any(w => w.Contains("sem 'name'")), Is.True);
            Assert.That(result.Warnings.Any(w => w.Contains("headers")), Is.True);

            var web = _db.McpServers.Single(m => m.Name == "web");
            Assert.That(web.Enabled, Is.False,
                "MCP importado nasce desabilitado (guardrail)");
            Assert.That(web.HeadersJson, Is.EqualTo("{}"),
                "headers nunca vêm do manifest");
            Assert.That(web.Url, Is.EqualTo("https://mcp.test"));
            var local = _db.McpServers.Single(m => m.Name == "local");
            Assert.That(local.Transport, Is.EqualTo("stdio"));
            Assert.That(local.Command, Is.EqualTo("npx"));
            Assert.That(local.ArgsJson, Does.Contain("-y"));
            var deploy = _db.Skills.Single(s => s.Name == "deploy");
            Assert.That(deploy.Content, Does.Contain("deploy cmd"));
        });
    }

    /// <summary>Entrada de skill apontando direto para SKILL.md também
    /// resolve; reimport atualiza o MCP existente (idempotente).</summary>
    [Test]
    public async Task PluginImport_SkillPathDiretoEUpsertMcp()
    {
        var manifest =
            "{\"skills\":[\"skills/foo/SKILL.md\"]," +
            "\"mcps\":[{\"name\":\"web\",\"transport\":\"http\",\"url\":\"https://v2.test\"}]}";
        var http = new StubHttpHandler();
        EnqueueManifest(http, manifest,
            files: ["skills/foo/SKILL.md"]);
        http.Enqueue(HttpStatusCode.OK, SkillAlfa);
        // MCP "web" pré-existente — idempotência cobre o update.
        _db.McpServers.Add(new McpServer
        {
            UserId = null, Name = "web", Enabled = true,
            Url = "https://v1.test",
        });
        await _db.SaveChangesAsync();

        var result = await new PluginImportService(_db,
            new StubHttpFactory(http)).ImportAsync(RepoUrl, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ImportedSkills, Is.EqualTo(new[] { "alfa-skill" }));
            var web = _db.McpServers.Single(m => m.Name == "web");
            Assert.That(web.Url, Is.EqualTo("https://v2.test"),
                "update troca a URL do existente");
            Assert.That(web.Enabled, Is.True,
                "enabled do existente é preservado (só novos nascem off)");
            Assert.That(_db.McpServers.Count(), Is.EqualTo(1));
        });
    }
}
