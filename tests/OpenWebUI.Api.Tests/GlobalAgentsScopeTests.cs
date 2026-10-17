using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura de SPEC-20261016-global-agents-scope: scan multi-escopo
/// (projeto + <c>~/.agents</c>), precedência último-vence, escopo global
/// sem workdir e parse de <c>.agents/mcp.json</c>.
/// </summary>
[TestFixture]
public class GlobalAgentsScopeTests
{
    private string _dir = null!;
    private string _home = null!;
    private SkillDiscoveryService _svc = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Join(Path.GetTempPath(), $"scope-proj-{Guid.NewGuid():N}");
        _home = Path.Join(Path.GetTempPath(), $"scope-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_home);
        _svc = new SkillDiscoveryService();
    }

    [TearDown]
    public void TearDown()
    {
        SkillDiscoveryService.Invalidate(_dir);
        foreach (var d in new[] { _dir, _home })
        {
            try { Directory.Delete(d, recursive: true); }
            catch (IOException) { }
        }
    }

    private static string WriteFile(string root, string rel, string content)
    {
        var full = Path.Join(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Test]
    // RF-001/AC-01: skill global aparece com scope=global junto às do projeto.
    public void Scan_IncluiSkillGlobal_ComScopeGlobal()
    {
        WriteFile(_home, ".agents/skills/minha-skill/SKILL.md",
            "---\nname: minha-skill\ndescription: global\n---\ncorpo global");
        WriteFile(_dir, ".agents/skills/proj-skill/SKILL.md",
            "---\nname: proj-skill\n---\ncorpo projeto");

        var scan = _svc.Scan(_dir, home: _home);

        var global = scan.Skills.Single(s => s.Name == "minha-skill");
        Assert.Multiple(() =>
        {
            Assert.That(global.Scope, Is.EqualTo("global"));
            Assert.That(global.Body, Is.EqualTo("corpo global"));
            Assert.That(global.Path, Does.EndWith("SKILL.md"));
            Assert.That(scan.Skills.Single(s => s.Name == "proj-skill").Scope,
                Is.EqualTo("project"));
        });
    }

    [Test]
    // RF-002/AC-02: mesmo nome — a do projeto vence a global (último vence).
    public void Scan_MesmoNome_ProjetoSobrescreveGlobal()
    {
        WriteFile(_home, ".agents/skills/foo/SKILL.md", "---\nname: foo\n---\nGLOBAL");
        WriteFile(_dir, ".agents/skills/foo/SKILL.md", "---\nname: foo\n---\nPROJETO");

        var scan = _svc.Scan(_dir, home: _home);

        var foo = scan.Skills.Single(s => s.Name == "foo");
        Assert.Multiple(() =>
        {
            Assert.That(foo.Scope, Is.EqualTo("project"));
            Assert.That(foo.Body, Is.EqualTo("PROJETO"));
        });
    }

    [Test]
    // RF-002b: dentro do escopo global, ~/.agents vence ~/.claude (canônico).
    public void Scan_MesmoNomeGlobal_AgentsVenceClaude()
    {
        WriteFile(_home, ".claude/skills/foo/SKILL.md", "---\nname: foo\n---\nCLAUDE");
        WriteFile(_home, ".agents/skills/foo/SKILL.md", "---\nname: foo\n---\nAGENTS");

        var scan = _svc.Scan(_dir, home: _home);

        Assert.That(scan.Skills.Single(s => s.Name == "foo").Body, Is.EqualTo("AGENTS"));
    }

    [Test]
    // RF-001: commands globais em ~/.agents/commands descobertos com scope.
    public void Scan_IncluiCommandsGlobais()
    {
        WriteFile(_home, ".agents/commands/deploy.md",
            "---\ndescription: deploy remoto\n---\nrode deploy $1");
        WriteFile(_dir, ".opencode/command/local.md", "corpo local");

        var scan = _svc.Scan(_dir, home: _home);

        Assert.Multiple(() =>
        {
            Assert.That(scan.Commands.Single(c => c.Name == "deploy").Scope,
                Is.EqualTo("global"));
            Assert.That(scan.Commands.Single(c => c.Name == "local").Scope,
                Is.EqualTo("project"));
        });
    }

    [Test]
    // RF-007/AC-05: sem workdir o scan retorna apenas o escopo global.
    public void Scan_SemWorkdir_RetornaSoGlobal()
    {
        WriteFile(_home, ".agents/skills/g/SKILL.md", "---\nname: g\n---\nglobal");
        WriteFile(_dir, ".agents/skills/p/SKILL.md", "---\nname: p\n---\nprojeto");

        var scan = _svc.Scan(workdir: null, home: _home);

        Assert.Multiple(() =>
        {
            Assert.That(scan.Skills.Select(s => s.Name), Is.EqualTo(new[] { "g" }));
            Assert.That(scan.Skills[0].Scope, Is.EqualTo("global"));
        });
    }

    [Test]
    // Edge: ~/.agents inexistente — scan do projeto sem erro.
    public void Scan_SemDirGlobal_SoProjetoSemErro()
    {
        WriteFile(_dir, ".agents/skills/p/SKILL.md", "---\nname: p\n---\nprojeto");

        var scan = _svc.Scan(_dir, home: Path.Join(_home, "nao-existe"));

        Assert.That(scan.Skills.Select(s => s.Name), Is.EqualTo(new[] { "p" }));
    }

    [Test]
    // RF-001: includeGlobal=false preserva o comportamento antigo.
    public void Scan_SemGlobal_NaoLeHome()
    {
        WriteFile(_home, ".agents/skills/g/SKILL.md", "---\nname: g\n---\nglobal");
        WriteFile(_dir, ".agents/skills/p/SKILL.md", "---\nname: p\n---\nprojeto");

        var scan = _svc.Scan(_dir, includeGlobal: false, home: _home);

        Assert.That(scan.Skills.Select(s => s.Name), Is.EqualTo(new[] { "p" }));
    }

    [Test]
    // RF-006/AC-04: mcp.json stdio + http do global e do projeto mesclados.
    public void LoadMcpServers_MesclaGlobalEProjeto()
    {
        WriteFile(_home, ".agents/mcp.json", """
            {"mcpServers":{
              "srv-a":{"command":"npx","args":["-y","srv"],"env":{"K":"${PATH}","L":"lit"}},
              "srv-b":{"url":"https://mcp.example.com","headers":{"Authorization":"Bearer x"}},
              "dup":{"command":"global-cmd"}}}
            """);
        WriteFile(_dir, ".agents/mcp.json", """
            {"mcpServers":{"dup":{"url":"https://proj.example.com"}}}
            """);

        var config = _svc.LoadMcpServers(_dir, _home);

        Assert.Multiple(() =>
        {
            Assert.That(config.Warnings, Is.Empty);
            Assert.That(config.Servers.Select(s => s.Id),
                Is.EquivalentTo(new[] { "srv-a", "srv-b", "dup" }));
            var a = config.Servers.Single(s => s.Id == "srv-a");
            Assert.That(a.Transport, Is.EqualTo("stdio"));
            Assert.That(a.Args, Is.EqualTo(new[] { "-y", "srv" }));
            Assert.That(a.Env, Has.Count.EqualTo(2));
            var b = config.Servers.Single(s => s.Id == "srv-b");
            Assert.That(b.Transport, Is.EqualTo("http"));
            Assert.That(b.Headers["Authorization"], Is.EqualTo("Bearer x"));
            // projeto sobrescreve global por id
            Assert.That(config.Servers.Single(s => s.Id == "dup").Transport,
                Is.EqualTo("http"));
        });
    }

    [Test]
    // Edge: mcp.json malformado → warning, sem exceção, demais servers ok.
    public void LoadMcpServers_Malformado_ViraWarning()
    {
        WriteFile(_home, ".agents/mcp.json", "{broken");
        WriteFile(_dir, ".agents/mcp.json",
            """{"mcpServers":{"ok":{"url":"https://x.example.com"}}}""");

        var config = _svc.LoadMcpServers(_dir, _home);

        Assert.Multiple(() =>
        {
            Assert.That(config.Warnings, Has.Count.EqualTo(1));
            Assert.That(config.Servers.Select(s => s.Id), Is.EqualTo(new[] { "ok" }));
        });
    }

    [Test]
    // RF-006: servidor sem command nem url é ignorado com warning.
    public void LoadMcpServers_SemCommandNemUrl_Ignorado()
    {
        WriteFile(_home, ".agents/mcp.json",
            """{"mcpServers":{"vazio":{"foo":1}}}""");

        var config = _svc.LoadMcpServers(null, _home);

        Assert.Multiple(() =>
        {
            Assert.That(config.Servers, Is.Empty);
            Assert.That(config.Warnings, Has.Count.EqualTo(1));
        });
    }

    [Test]
    // RF-006: ${VAR}/{env:VAR}/$VAR resolvem do ambiente; literal passa direto.
    public void ResolveEnvRef_FormasSuportadas()
    {
        Environment.SetEnvironmentVariable("SCOPE_TEST_VAR", "resolvido");
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(McpClientService.ResolveEnvRef("${SCOPE_TEST_VAR}"),
                    Is.EqualTo("resolvido"));
                Assert.That(McpClientService.ResolveEnvRef("{env:SCOPE_TEST_VAR}"),
                    Is.EqualTo("resolvido"));
                Assert.That(McpClientService.ResolveEnvRef("$SCOPE_TEST_VAR"),
                    Is.EqualTo("resolvido"));
                Assert.That(McpClientService.ResolveEnvRef("literal"),
                    Is.EqualTo("literal"));
                Assert.That(McpClientService.ResolveEnvRef("${SCOPE_VAR_AUSENTE}"),
                    Is.Null);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCOPE_TEST_VAR", null);
        }
    }
}
