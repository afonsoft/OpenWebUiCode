using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes da slice external-agent-registry (#412): detecção via PATH walk,
/// toggle com skill ponte, execução headless para delegate ext:{id}.
/// </summary>
[TestFixture]
public class ExternalAgentTests
{
    private string _root = null!;
    private string _home = null!;
    private string _pathDir = null!;
    private string _dbPath = null!;
    private ServiceProvider _provider = null!;
    private Func<string> _homeOriginal = null!;
    private Func<string?> _pathOriginal = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-ext-{Guid.NewGuid():N}");
        _home = Path.Join(_root, "home");
        _pathDir = Path.Join(_root, "bin");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_pathDir);
        _dbPath = Path.Join(_root, "test.db");

        _homeOriginal = ExternalAgentService.HomeResolver;
        _pathOriginal = ExternalAgentService.PathResolver;
        ExternalAgentService.HomeResolver = () => _home;
        ExternalAgentService.PathResolver = () => _pathDir;
        ExternalAgentService.ResetDetectCache();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddHybridCache();
        services.AddLogging();
        services.AddScoped<ConfigService>();
        services.AddScoped<ExternalAgentService>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        ExternalAgentService.HomeResolver = _homeOriginal;
        ExternalAgentService.PathResolver = _pathOriginal;
        ExternalAgentService.ResetDetectCache();
        _provider.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ExternalAgentService Service()
    {
        var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ExternalAgentService>();
    }

    private string StubBin(string name, string body)
    {
        var path = Path.Join(_pathDir, name);
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserExecute | UnixFileMode.UserRead);
        }
        ExternalAgentService.ResetDetectCache();
        return path;
    }

    [Test]
    public async Task Detect_EncontraBinarioNoPath()
    {
        StubBin("claude", "#!/bin/sh\nexit 0\n");
        var list = await Service().DetectAsync("u1");
        var claude = list.Single(a => a.Id == "claude");
        var codex = list.Single(a => a.Id == "codex");
        Assert.Multiple(() =>
        {
            Assert.That(claude.Installed, Is.True);
            Assert.That(claude.Selected, Is.True); // ausente = todos detectados
            Assert.That(codex.Installed, Is.False);
            Assert.That(codex.Selected, Is.False);
        });
    }

    [Test]
    public async Task Detect_PathVazio_TodosFalse()
    {
        ExternalAgentService.PathResolver = () => "";
        ExternalAgentService.ResetDetectCache();
        var list = await Service().DetectAsync("u1");
        Assert.That(list.All(a => !a.Installed), Is.True);
    }

    [Test]
    public async Task Toggle_Habilita_InstalaSkillPonte_Desabilita_Remove()
    {
        StubBin("claude", "#!/bin/sh\nexit 0\n");
        var service = Service();

        var (ok, error) = await service.SetSelectedAsync("u1", "claude", true, CancellationToken.None);
        Assert.That(ok, Is.True, error);
        var skillPath = Path.Join(_home, ".claude/skills/openwebui-cli/SKILL.md");
        Assert.That(File.Exists(skillPath), Is.True);
        var content = File.ReadAllText(skillPath);
        Assert.Multiple(() =>
        {
            Assert.That(content, Does.Contain("name: openwebui-cli"));
            Assert.That(content, Does.Contain("OPENWEBUI_TOKEN"));
            // Nunca segredo embutido — token só via env no comando curl.
            Assert.That(content, Does.Not.Contain("Bearer sk-"));
        });

        var (okOff, _) = await service.SetSelectedAsync("u1", "claude", false, CancellationToken.None);
        Assert.That(okOff, Is.True);
        Assert.That(Directory.Exists(Path.GetDirectoryName(skillPath)), Is.False);
    }

    [Test]
    public async Task Toggle_SemBinario_Retorna409()
    {
        var (ok, error) = await Service().SetSelectedAsync(
            "u1", "claude", true, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("PATH"));
        });
    }

    [Test]
    public async Task Toggle_Desconhecido_Erro()
    {
        var (ok, error) = await Service().SetSelectedAsync(
            "u1", "inexistente", true, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("desconhecido"));
        });
    }

    [Test]
    public async Task Delegate_ExecutaHeadless_DevolveStdout()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("stub shell só POSIX");
        }
        StubBin("claude", "#!/bin/sh\nshift\necho \"resposta ext: $1\"\n");

        var result = await Service().RunDelegateAsync(
            "claude", "olá mundo", _root, "u1", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.Text);
            Assert.That(result.Result, Does.Contain("resposta ext: olá mundo"));
        });
    }

    [Test]
    public async Task Delegate_BinarioAusente_ErroLegivel()
    {
        var result = await Service().RunDelegateAsync(
            "claude", "x", _root, "u1", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Result, Does.Contain("indisponível"));
        });
    }

    [Test]
    public async Task Delegate_ExitNaoZero_RetornaStderr()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("stub shell só POSIX");
        }
        StubBin("codex", "#!/bin/sh\necho 'falhou feio' >&2\nexit 3\n");

        var result = await Service().RunDelegateAsync(
            "codex", "x", _root, "u1", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Result, Does.Contain("código 3"));
            Assert.That(result.Result, Does.Contain("falhou feio"));
        });
    }
}
