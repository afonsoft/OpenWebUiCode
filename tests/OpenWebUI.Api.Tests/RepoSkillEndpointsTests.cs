using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura do catálogo de skills/commands do repo
/// (SPEC-20261009-repo-skills-slash-commands / E16 S3+S4): endpoints
/// <c>/api/v1/workspace/repo/{skills,commands}</c> + scan do
/// <see cref="SkillDiscoveryService"/> (dedup, frontmatter, caps) e
/// <see cref="SkillDiscoveryService.LoadProjectInstructions"/>.
/// </summary>
[TestFixture, IsolateEnvironment]
public class RepoSkillEndpointsTests
{
    private string _apiDir = null!;

    [SetUp]
    public void SetUp() => _apiDir = FindApiDir();

    // ---------- endpoints ----------

    [Test]
    public async Task Endpoints_SemAuth_401()
    {
        using var dbScope = TestInfra.UseDb();
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var res = await client.GetAsync("/api/v1/workspace/repo/skills");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Endpoints_SemRepo_404BoundFalse()
    {
        using var ctx = await NewAppAsync(bound: false);
        var res = await ctx.Client.GetAsync("/api/v1/workspace/repo/skills");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("bound").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Skills_ListaEDetalhe_ComFrontmatter()
    {
        using var ctx = await NewAppAsync(bound: true);
        var dir = Path.Join(ctx.Workdir!, ".claude", "skills", "review");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "SKILL.md"),
            "---\nname: review\ndescription: Revisa PRs\n---\nCorpo da skill de review.\n");

        var list = await ctx.Client.GetAsync("/api/v1/workspace/repo/skills");
        Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var items = (await list.Content
            .ReadFromJsonAsync<List<RepoSkillItemResponse>>())!;
        Assert.That(items, Has.Count.EqualTo(1));
        Assert.That(items[0].Name, Is.EqualTo("review"));
        Assert.That(items[0].Description, Is.EqualTo("Revisa PRs"));
        Assert.That(items[0].Path, Does.Contain(".claude/skills/review/SKILL.md"));

        var detail = await ctx.Client.GetAsync("/api/v1/workspace/repo/skills/review");
        var det = (await detail.Content
            .ReadFromJsonAsync<RepoSkillDetailResponse>())!;
        Assert.That(det.Content, Does.Contain("Corpo da skill de review"));

        var missing = await ctx.Client.GetAsync("/api/v1/workspace/repo/skills/nope");
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Commands_ListaEDetalhe()
    {
        using var ctx = await NewAppAsync(bound: true);
        var dir = Path.Join(ctx.Workdir!, ".opencode", "command");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "deploy.md"),
            "---\ndescription: Faz deploy\nsubtask: true\n---\nDeploy $1 em $ARGUMENTS\n");

        var list = await ctx.Client.GetAsync("/api/v1/workspace/repo/commands");
        var items = (await list.Content
            .ReadFromJsonAsync<List<RepoCommandItemResponse>>())!;
        Assert.That(items, Has.Count.EqualTo(1));
        Assert.That(items[0].Name, Is.EqualTo("deploy"));
        Assert.That(items[0].Subtask, Is.True);

        var detail = await ctx.Client.GetAsync("/api/v1/workspace/repo/commands/deploy");
        var det = (await detail.Content
            .ReadFromJsonAsync<RepoCommandDetailResponse>())!;
        Assert.That(det.Content, Does.Contain("$ARGUMENTS"));
    }

    [Test]
    public async Task Skills_FiltroScope_ProjetoVsGlobal()
    {
        // Skill global no home isolado da suíte + skill de projeto no workdir.
        var skillDir = Path.Join(TestHomeIsolation.Home, ".agents", "skills", "glob-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Join(skillDir, "SKILL.md"),
            "---\nname: glob-skill\ndescription: global\n---\ncorpo global\n");
        SkillDiscoveryService.Invalidate("");
        try
        {
            using var ctx = await NewAppAsync(bound: true);
            var dir = Path.Join(ctx.Workdir!, ".claude", "skills", "proj-skill");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Join(dir, "SKILL.md"),
                "---\nname: proj-skill\ndescription: projeto\n---\ncorpo projeto\n");

            var global = await ctx.Client.GetAsync("/api/v1/workspace/repo/skills?scope=global");
            var gItems = (await global.Content
                .ReadFromJsonAsync<List<RepoSkillItemResponse>>())!;
            Assert.Multiple(() =>
            {
                Assert.That(gItems.Any(s => s.Name == "glob-skill"), Is.True);
                Assert.That(gItems.Any(s => s.Name == "proj-skill"), Is.False);
                Assert.That(gItems.All(s => s.Scope == "global"), Is.True);
            });

            var proj = await ctx.Client.GetAsync("/api/v1/workspace/repo/skills?scope=project");
            var pItems = (await proj.Content
                .ReadFromJsonAsync<List<RepoSkillItemResponse>>())!;
            Assert.Multiple(() =>
            {
                Assert.That(pItems.Any(s => s.Name == "proj-skill"), Is.True);
                Assert.That(pItems.Any(s => s.Name == "glob-skill"), Is.False);
            });

            // Detalhe devolve o Scope.
            var detail = await ctx.Client.GetAsync("/api/v1/workspace/repo/skills/glob-skill");
            var det = (await detail.Content
                .ReadFromJsonAsync<RepoSkillDetailResponse>())!;
            Assert.That(det.Scope, Is.EqualTo("global"));
        }
        finally
        {
            try { Directory.Delete(skillDir, recursive: true); } catch (IOException) { }
            SkillDiscoveryService.Invalidate("");
        }
    }

    [Test]
    public async Task Commands_FiltroScope_DetalheComScope()
    {
        var cmdDir = Path.Join(TestHomeIsolation.Home, ".agents", "commands");
        Directory.CreateDirectory(cmdDir);
        File.WriteAllText(Path.Join(cmdDir, "glob-cmd.md"),
            "---\ndescription: comando global\n---\nBody $ARGUMENTS\n");
        SkillDiscoveryService.Invalidate("");
        try
        {
            using var ctx = await NewAppAsync(bound: true);
            var dir = Path.Join(ctx.Workdir!, ".opencode", "command");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Join(dir, "proj-cmd.md"),
                "---\ndescription: comando projeto\n---\nBody\n");

            var global = await ctx.Client.GetAsync("/api/v1/workspace/repo/commands?scope=global");
            var gItems = (await global.Content
                .ReadFromJsonAsync<List<RepoCommandItemResponse>>())!;
            Assert.Multiple(() =>
            {
                Assert.That(gItems.Any(c => c.Name == "glob-cmd"), Is.True);
                Assert.That(gItems.Any(c => c.Name == "proj-cmd"), Is.False);
            });

            var detail = await ctx.Client.GetAsync("/api/v1/workspace/repo/commands/glob-cmd");
            var det = (await detail.Content
                .ReadFromJsonAsync<RepoCommandDetailResponse>())!;
            Assert.That(det.Scope, Is.EqualTo("global"));
        }
        finally
        {
            try { File.Delete(Path.Join(cmdDir, "glob-cmd.md")); } catch (IOException) { }
            SkillDiscoveryService.Invalidate("");
        }
    }

    [Test]
    public async Task SetSkillEnabled_Toggle_AdminOff_404()
    {
        using var ctx = await NewAppAsync(bound: true);
        var dir = Path.Join(ctx.Workdir!, ".claude", "skills", "toggleme");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "SKILL.md"),
            "---\nname: toggleme\ndescription: t\n---\ncorpo\n");

        // Skill inexistente → 404.
        var missing = await ctx.Client.PutAsJsonAsync(
            "/api/v1/workspace/repo/skills/nope/enabled",
            new McpServerEnabledRequest(false));
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Desliga para o usuário.
        var off = await ctx.Client.PutAsJsonAsync(
            "/api/v1/workspace/repo/skills/toggleme/enabled",
            new McpServerEnabledRequest(false));
        Assert.That(off.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var list = (await ctx.Client.GetFromJsonAsync<List<RepoSkillItemResponse>>(
            "/api/v1/workspace/repo/skills"))!;
        Assert.That(list.Single(s => s.Name == "toggleme").Enabled, Is.False);

        // Religa — passa pelo ramo adminOff.Contains (falso) e SetSkillDisabledAsync.
        var on = await ctx.Client.PutAsJsonAsync(
            "/api/v1/workspace/repo/skills/toggleme/enabled",
            new McpServerEnabledRequest(true));
        Assert.That(on.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        list = (await ctx.Client.GetFromJsonAsync<List<RepoSkillItemResponse>>(
            "/api/v1/workspace/repo/skills"))!;
        Assert.That(list.Single(s => s.Name == "toggleme").Enabled, Is.True);
    }

    // ---------- discovery service (unitário sobre workdir fake) ----------

    [Test]
    public void Discovery_Dedup_PrimeiroPrecedenciaVence()
    {
        var dir = NewWorkdir();
        try
        {
            WriteSkill(dir, ".claude/skills/foo", "claude");
            WriteSkill(dir, ".agents/skills/foo", "agents");
            WriteSkill(dir, "skills/foo", "raiz");

            var scan = new SkillDiscoveryService().Scan(dir);
            Assert.That(scan.Skills, Has.Count.EqualTo(1));
            Assert.That(scan.Skills[0].Body, Does.Contain("claude"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Discovery_SkillSemName_UsaDiretorioPai()
    {
        var dir = NewWorkdir();
        try
        {
            var skillDir = Path.Join(dir, "skills", "lint-fix");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Join(skillDir, "SKILL.md"), "Roda o lint e corrige.\n");

            var scan = new SkillDiscoveryService().Scan(dir);
            Assert.That(scan.Skills[0].Name, Is.EqualTo("lint-fix"));
            Assert.That(scan.Skills[0].Description, Does.Contain("lint"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Discovery_CapEArquivoGrande_Ignorado()
    {
        var dir = NewWorkdir();
        try
        {
            var big = Path.Join(dir, "skills", "big");
            Directory.CreateDirectory(big);
            File.WriteAllText(Path.Join(big, "SKILL.md"),
                new string('x', SkillDiscoveryService.MaxFileBytes + 10));

            var scan = new SkillDiscoveryService().Scan(dir);
            Assert.That(scan.Skills, Is.Empty);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Instrucoes_AgentsMdEClaudeMd_EntramNaOrdem()
    {
        var dir = NewWorkdir();
        try
        {
            File.WriteAllText(Path.Join(dir, "CLAUDE.md"), "regras claude");
            File.WriteAllText(Path.Join(dir, "AGENTS.md"), "regras agents");

            var text = SkillDiscoveryService.LoadProjectInstructions(dir);
            Assert.That(text, Is.Not.Null);
            Assert.That(text!.IndexOf("AGENTS.md"), Is.LessThan(text.IndexOf("CLAUDE.md")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    public void Instrucoes_SemArquivos_Null()
    {
        var dir = NewWorkdir();
        try
        {
            Assert.That(SkillDiscoveryService.LoadProjectInstructions(dir), Is.Null);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---------- helpers ----------

    private static void WriteSkill(string workdir, string relDir, string marker)
    {
        var dir = Path.Join(workdir, relDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "SKILL.md"), $"---\nname: foo\n---\n{marker}\n");
    }

    private static string NewWorkdir()
    {
        var dir = Path.Join(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class Ctx : IDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;

        public Ctx(WebApplicationFactory<Program> factory, HttpClient client,
            string? workdir, string userId)
        {
            _factory = factory;
            Client = client;
            Workdir = workdir;
            UserId = userId;
        }

        public HttpClient Client { get; }
        public string? Workdir { get; }
        public string UserId { get; }

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
            try
            {
                var dir = Path.Join(FindApiDir(), "data", "workspaces", UserId);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<Ctx> NewAppAsync(bool bound)
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"openwebui-sk-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={dbPath}");
        var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var signup = await client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest($"SK{tag}", $"sk{tag}@sk.local", "senha123"));
        Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await signup.Content.ReadAsStringAsync());
        var auth = (await signup.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.Token);

        var uid = auth.User.Id;
        string? workdir = null;
        if (bound)
        {
            const string dir = "repos/test__repo";
            workdir = Path.Join(_apiDir, "data", "workspaces", uid, dir);
            Directory.CreateDirectory(Path.Join(workdir, ".git"));
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath}").Options);
            db.ConfigEntries.Add(new ConfigEntry
            {
                Key = $"u:{uid}:workspace.repo",
                ValueJson = JsonSerializer.Serialize(
                    new WorkspaceRepoBinding("test/repo", "main", dir)),
            });
            await db.SaveChangesAsync();
        }

        return new Ctx(factory, client, workdir, uid);
    }

    private static string FindApiDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Join(dir, "src", "OpenWebUI.Api");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("src/OpenWebUI.Api não encontrado");
    }
}
