using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do allowed-tools de skills + plugins
/// (SPEC-20261104-skills-allowed-tools-plugins): parse de frontmatter
/// (CSV e lista), persistência CSV na entidade, interseção de skills
/// restritivas ativas, fallback essencial em interseção vazia e match
/// de nomes de tool (builtin/DB/MCP).
/// </summary>
[TestFixture]
public class SkillsAllowedToolsTests
{
    private AppDbContext _db = null!;
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"allowed-tools-{Guid.NewGuid():N}")
            .Options;
        _db = new AppDbContext(opts);
        _dir = Path.Combine(Path.GetTempPath(), $"skills-at-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ---------------- Frontmatter parse ----------------

    [Test]
    public void Frontmatter_AllowedTools_Csv_E_Lista()
    {
        var csv = SkillDiscoveryService.ParseEntryContent(
            "---\nname: s1\nallowed-tools: file_read, shell_exec\n---\ncorpo",
            "s1", "p", "project");
        Assert.That(csv!.AllowedTools, Is.EqualTo(new[] { "file_read", "shell_exec" }));

        var lista = SkillDiscoveryService.ParseEntryContent(
            "---\nname: s2\nallowed-tools: [file_read, \"file_write\"]\n---\ncorpo",
            "s2", "p", "project");
        Assert.That(lista!.AllowedTools, Is.EqualTo(new[] { "file_read", "file_write" }));
    }

    [Test]
    public void Frontmatter_SemAllowedTools_Null()
    {
        var entry = SkillDiscoveryService.ParseEntryContent(
            "---\nname: s3\n---\ncorpo", "s3", "p", "project");
        Assert.That(entry!.AllowedTools, Is.Null);
    }

    [Test]
    public void ImportParse_PreservaAllowedTools()
    {
        var (name, _, _, allowed) = SkillImportService.ParseSkillMarkdown(
            "---\nname: skill-x\ndescription: d\nallowed-tools: file_read, todo_write\n---\nbody");
        Assert.Multiple(() =>
        {
            Assert.That(name, Is.EqualTo("skill-x"));
            Assert.That(allowed, Is.EqualTo("file_read,todo_write"));
        });
    }

    // ---------------- Permits (match de nomes) ----------------

    [Test]
    public void Permits_CasaFormasBuiltinEDb()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "file_read", "mcp-search" };
        var builtin = new Tool
        {
            Name = "builtin/file_read",
            Url = "builtin://file_read",
            SpecJson = """{"type":"function","function":{"name":"builtin_file_read"}}""",
        };
        var mcp = new Tool
        {
            Name = "mcp-search", Url = "http://mcp.local/call",
            SpecJson = """{"type":"function","function":{"name":"mcp-search"}}""",
        };
        var outra = new Tool
        {
            Name = "builtin/shell_exec",
            Url = "builtin://shell_exec",
            SpecJson = """{"type":"function","function":{"name":"builtin_shell_exec"}}""",
        };
        Assert.Multiple(() =>
        {
            Assert.That(SkillToolRestrictionService.Permits(allowed, builtin), Is.True);
            Assert.That(SkillToolRestrictionService.Permits(allowed, mcp), Is.True);
            Assert.That(SkillToolRestrictionService.Permits(allowed, outra), Is.False);
        });
    }

    // ---------------- ResolveAsync (interseção) ----------------

    [Test]
    public async Task Resolve_SemRestricoes_RetornaNull()
    {
        var svc = BuildService();
        var allowed = await svc.ResolveAsync("u1", "modelo-x", null, CancellationToken.None);
        Assert.That(allowed, Is.Null);
    }

    [Test]
    public async Task Resolve_SkillDoModelo_Restringe()
    {
        var skill = SeedSkill("s1", "file_read,file_write");
        SeedModel("m1", $$"""{"skill_ids":["{{skill.Id}}"]}""");
        var svc = BuildService();

        var allowed = await svc.ResolveAsync("u1", "m1", null, CancellationToken.None);
        Assert.That(allowed, Is.Not.Null);
        Assert.That(allowed, Is.EquivalentTo(new[] { "file_read", "file_write" }));
    }

    [Test]
    public async Task Resolve_DuasRestritivas_Intersecao()
    {
        var a = SeedSkill("a", "file_read,file_write,shell_exec");
        var b = SeedSkill("b", "file_read,file_edit");
        SeedModel("m2", $$"""{"skill_ids":["{{a.Id}}","{{b.Id}}"]}""");
        var svc = BuildService();

        var allowed = await svc.ResolveAsync("u1", "m2", null, CancellationToken.None);
        Assert.That(allowed, Is.EquivalentTo(new[] { "file_read" }));
    }

    [Test]
    public async Task Resolve_IntersecaoVazia_Essenciais()
    {
        var a = SeedSkill("a", "shell_exec");
        var b = SeedSkill("b", "browser_navigate");
        SeedModel("m3", $$"""{"skill_ids":["{{a.Id}}","{{b.Id}}"]}""");
        var svc = BuildService();

        var allowed = await svc.ResolveAsync("u1", "m3", null, CancellationToken.None);
        Assert.That(allowed, Is.EquivalentTo(SkillToolRestrictionService.EssentialBuiltins));
    }

    [Test]
    public async Task Resolve_SkillSemAllowedTools_NaoRestringe()
    {
        SeedSkill("livre", allowedTools: null);
        var a = SeedSkill("a", "file_read");
        SeedModel("m4", $$"""{"skill_ids":["{{a.Id}}"]}""");
        var svc = BuildService();

        // Modelo sem a skill "livre" — só "a" restringe.
        var allowed = await svc.ResolveAsync("u1", "m4", null, CancellationToken.None);
        Assert.That(allowed, Is.EquivalentTo(new[] { "file_read" }));
    }

    [Test]
    public async Task Resolve_Autoinvoke_Repo_AplicaRestricao()
    {
        File.WriteAllText(
            Path.Combine(Directory.CreateDirectory(
                Path.Combine(_dir, ".agents", "skills", "restricted")).FullName,
                "SKILL.md"),
            "---\nname: restricted\nautoinvoke: true\nallowed-tools: file_read\n---\ncorpo");
        var svc = BuildService();

        var allowed = await svc.ResolveAsync("u1", "provider-model", _dir, CancellationToken.None);
        Assert.That(allowed, Is.EquivalentTo(new[] { "file_read" }));
    }

    // ---------------- helpers ----------------

    private Skill SeedSkill(string name, string? allowedTools)
    {
        var s = new Skill
        {
            UserId = null, Name = name, Content = "corpo",
            AllowedTools = allowedTools, IsActive = true,
        };
        _db.Skills.Add(s);
        _db.SaveChanges();
        return s;
    }

    private void SeedModel(string id, string metaJson)
    {
        _db.ModelEntries.Add(new ModelEntry
        {
            Id = id, Name = id, UserId = "u1",
            IsActive = true, MetaJson = metaJson,
        });
        _db.SaveChanges();
    }

    /// <summary>Monta o serviço com deps mínimos (sem remote roots → sem HTTP).</summary>
    private SkillToolRestrictionService BuildService()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        services.AddHttpClient();
        var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<HybridCache>();
        var config = new ConfigService(_db, cache);
        var overlay = new UserConfigOverlayService(_db, config);
        var remote = new RemoteSkillCatalog(
            provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<RemoteSkillCatalog>.Instance);
        var discovery = new SkillDiscoveryService(remote);
        return new SkillToolRestrictionService(_db, discovery, remote, config, overlay);
    }
}
