using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261018-formatters-catalog: seleção por extensão (binding →
/// custom global → builtin detectado), agrupamento uma-vez-por-formatter,
/// disabled, timeout e warnings agregados.
/// </summary>
[TestFixture, IsolateEnvironment]
public class FormatterCatalogTests
{
    private string _root = null!;
    private string _workdir = null!;
    private string _bin = null!;
    private AppDbContext _db = null!;
    private WorkspaceRepoService _repos = null!;
    private string? _origPath;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-fmt-{Guid.NewGuid():N}");
        _workdir = Path.Join(_root, "ws");
        _bin = Path.Join(_root, "bin");
        Directory.CreateDirectory(_workdir);
        Directory.CreateDirectory(_bin);
        _origPath = Environment.GetEnvironmentVariable("PATH");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Join(_root, "t.db")}").Options);
        DatabaseMigrator.MigrateAsync(_db).GetAwaiter().GetResult();
        _repos = new WorkspaceRepoService(
            new ConfigService(_db, TestCache.Create()), new StubEnv(_root));
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("PATH", _origPath);
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>Cria um binário fake que registra seus argv num log.</summary>
    private string FakeBin(string name, string script)
    {
        var path = Path.Join(_bin, name);
        File.WriteAllText(path, "#!/bin/sh\n" + script + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return path;
    }

    private void UseFakePath() =>
        Environment.SetEnvironmentVariable(
            "PATH", _bin + ":" + _origPath);

    private FormatHookService Hook(Dictionary<string, string?>? config = null,
        Func<string, CancellationToken, Task<bool>>? probe = null) =>
        new(_repos,
            new ConfigurationBuilder().AddInMemoryCollection(
                config ?? []).Build(),
            NullLogger<FormatHookService>.Instance,
            probe);

    private static Task<bool> ProbeAll(string _, CancellationToken __) =>
        Task.FromResult(true);

    private static Task<bool> ProbeNone(string _, CancellationToken __) =>
        Task.FromResult(false);

    // ---- catálogo puro ----

    [Test]
    public void GroupByExtension_AgrupaPorExtLower()
    {
        var groups = FormatterCatalog.GroupByExtension(
            ["a.PY", "b.py", "c.ts", "README"]);
        Assert.Multiple(() =>
        {
            Assert.That(groups[".py"], Is.EquivalentTo(new[] { "a.PY", "b.py" }));
            Assert.That(groups[".ts"], Is.EquivalentTo(new[] { "c.ts" }));
            Assert.That(groups["."], Is.EquivalentTo(new[] { "README" }));
        });
    }

    [Test]
    public async Task Resolve_BindingGanhaDeTudo()
    {
        var r = await FormatterCatalog.ResolveAsync(
            ".py", "make format {files}", "ruff format {files}", ProbeAll);
        Assert.Multiple(() =>
        {
            Assert.That(r.Source, Is.EqualTo("custom-binding"));
            Assert.That(r.Entry!.Command, Is.EqualTo("make format {files}"));
        });
    }

    [Test]
    public async Task Resolve_CustomGlobalGanhaDeBuiltin()
    {
        var r = await FormatterCatalog.ResolveAsync(
            ".py", null, "myfmt {files}", ProbeAll);
        Assert.Multiple(() =>
        {
            Assert.That(r.Source, Is.EqualTo("custom-user"));
            Assert.That(r.Entry!.Command, Is.EqualTo("myfmt {files}"));
        });
    }

    [Test]
    public async Task Resolve_BuiltinQuandoBinarioExiste()
    {
        var r = await FormatterCatalog.ResolveAsync(
            ".py", null, null, (b, ct) => Task.FromResult(b == "ruff"));
        Assert.Multiple(() =>
        {
            Assert.That(r.Source, Is.EqualTo("builtin"));
            Assert.That(r.Entry!.Name, Is.EqualTo("ruff"));
        });
    }

    [Test]
    public async Task Resolve_SemMatchViraNone()
    {
        var r = await FormatterCatalog.ResolveAsync(
            ".xyz", null, null, ProbeNone);
        Assert.Multiple(() =>
        {
            Assert.That(r.Source, Is.EqualTo("none"));
            Assert.That(r.Entry, Is.Null);
        });
    }

    [Test]
    public async Task Resolve_BuiltinRespeitaOrdemDoCatalogo()
    {
        // ruff vem antes de black para .py — só o primeiro disponível.
        var r = await FormatterCatalog.ResolveAsync(
            ".py", null, null, ProbeAll);
        Assert.That(r.Entry!.Name, Is.EqualTo("ruff"));
    }

    // ---- end-to-end ----

    [Test]
    public async Task Run_MultiExt_RodaCadaFormatterUmaVez()
    {
        UseFakePath();
        FakeBin("ruff", $"echo \"$@\" >> '{_root}/ruff.log'; exit 0");
        FakeBin("prettier", $"echo \"$@\" >> '{_root}/prettier.log'; exit 0");
        var warning = await Hook().RunForUserAsync("u1", _workdir,
            ["src/a.py", "src/b.py", "web/c.ts"], default);

        Assert.Multiple(() =>
        {
            Assert.That(warning, Is.Null);
            Assert.That(File.ReadAllText($"{_root}/ruff.log").Trim(),
                Is.EqualTo("format src/a.py src/b.py"));
            Assert.That(File.ReadAllText($"{_root}/prettier.log").Trim(),
                Is.EqualTo("--write web/c.ts"));
        });
    }

    [Test]
    public async Task Run_CustomPorExt_SobrescreveBuiltin()
    {
        UseFakePath();
        FakeBin("ruff", $"echo \"called\" >> '{_root}/ruff.log'; exit 0");
        var warning = await Hook(new()
        {
            ["Format:Commands:.py"] = $"echo done >> '{_root}/custom.log'",
        }).RunForUserAsync("u1", _workdir, ["a.py"], default);

        Assert.Multiple(() =>
        {
            Assert.That(warning, Is.Null);
            Assert.That(File.ReadAllText($"{_root}/custom.log").Trim(),
                Is.EqualTo("done a.py"));
            Assert.That(File.Exists($"{_root}/ruff.log"), Is.False);
        });
    }

    [Test]
    public async Task Run_FormatterFalha_WarningAgregadoSemQuebrar()
    {
        UseFakePath();
        FakeBin("ruff", "exit 3");
        FakeBin("prettier", $"touch '{_root}/prettier-ran'; exit 0");
        var warning = await Hook().RunForUserAsync("u1", _workdir,
            ["a.py", "b.ts"], default);

        Assert.Multiple(() =>
        {
            Assert.That(warning, Does.Contain("ruff").And.Contain("exit 3"));
            Assert.That(File.Exists($"{_root}/prettier-ran"), Is.True);
        });
    }

    [Test]
    public async Task Run_SemFormatter_NoOp()
    {
        var warning = await Hook(probe: ProbeNone).RunForUserAsync(
            "u1", _workdir, ["a.xyz", "b.uvw"], default);
        Assert.That(warning, Is.Null);
    }

    [Test]
    public async Task Run_Disabled_PulaFormatter()
    {
        UseFakePath();
        FakeBin("ruff", $"touch '{_root}/ruff-ran'; exit 0");
        var warning = await Hook(new()
        {
            ["Format:Disabled"] = "ruff",
        }).RunForUserAsync("u1", _workdir, ["a.py"], default);

        Assert.Multiple(() =>
        {
            Assert.That(warning, Is.Null);
            Assert.That(File.Exists($"{_root}/ruff-ran"), Is.False);
        });
    }

    [Test]
    public async Task Run_FormatCommandGlobal_CatchAll()
    {
        UseFakePath();
        var warning = await Hook(new()
        {
            ["Format:Command"] = $"echo \"$@\" >> '{_root}/all.log'",
        }).RunForUserAsync("u1", _workdir, ["a.py", "b.ts"], default);

        Assert.Multiple(() =>
        {
            Assert.That(warning, Is.Null);
            Assert.That(File.ReadAllText($"{_root}/all.log").Trim(),
                Does.Contain("a.py").And.Contain("b.ts"));
        });
    }

    [Test]
    public async Task Run_PlaceholderFile_RodaUmaVezPorArquivo()
    {
        UseFakePath();
        var warning = await Hook(new()
        {
            ["Format:Commands:.py"] = $"echo fmt >> '{_root}/per.log'; echo {{file}} >> '{_root}/per.log'",
        }).RunForUserAsync("u1", _workdir, ["a.py", "b.py"], default);

        Assert.Multiple(() =>
        {
            Assert.That(warning, Is.Null);
            Assert.That(File.ReadAllLines($"{_root}/per.log"),
                Is.EqualTo(new[] { "fmt", "a.py", "fmt", "b.py" }));
        });
    }

    /// <summary>IHostEnvironment apontando para a raiz temporária do teste.</summary>
    private sealed class StubEnv(string contentRoot)
        : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider
        { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
