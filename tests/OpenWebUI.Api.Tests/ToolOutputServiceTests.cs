using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Bound genérico de outputs de tool (SPEC-20261016-tool-output-bounding):
/// pass-through sob o limite, overflow → arquivo gerenciado + head/tail,
/// allowlist de leitura, scrub antes de gravar e cleanup &gt;7d.
/// </summary>
[TestFixture]
public class ToolOutputServiceTests
{
    private string _root = null!;
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-tout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, "test.db");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private ToolOutputService NewService()
    {
        var db = NewDb();
        db.Database.EnsureCreated();
        return new ToolOutputService(
            new ConfigService(db, TestCache.Create()),
            new StubEnv(_root),
            NullLogger<ToolOutputService>.Instance);
    }

    private string OutputDir => Path.Join(_root, "data", "tool-output");

    [Test]
    public async Task Bound_OutputPequeno_PassaIntacto()
    {
        var service = NewService();
        var text = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"linha {i}"));

        var bound = await service.BoundAsync(text, "file_grep");

        Assert.Multiple(() =>
        {
            Assert.That(bound.Truncated, Is.False);
            Assert.That(bound.Text, Is.EqualTo(text));
            Assert.That(bound.OutputPath, Is.Null);
        });
        Assert.That(Directory.Exists(OutputDir), Is.False);
    }

    [Test]
    public async Task Bound_OutputGrande_GravaArquivo_EDevolveHeadTail()
    {
        var service = NewService();
        var text = string.Join('\n',
            Enumerable.Range(1, 5000).Select(i => $"linha-{i:D5}"));

        var bound = await service.BoundAsync(text, "file_grep");

        Assert.Multiple(() =>
        {
            Assert.That(bound.Truncated, Is.True);
            Assert.That(bound.OutputPath, Is.Not.Null);
            Assert.That(bound.TotalLines, Is.EqualTo(5000));
            Assert.That(bound.Text, Does.Contain("linha-00001"));          // head
            Assert.That(bound.Text, Does.Contain("linha-05000"));          // tail
            Assert.That(bound.Text, Does.Not.Contain("linha-03000"));      // meio omitido
            Assert.That(bound.Text, Does.Contain("output completo:"));
            Assert.That(bound.Text.Split('\n').Length, Is.LessThan(2100));
        });

        var full = await File.ReadAllTextAsync(bound.OutputPath!);
        Assert.That(full, Does.Contain("linha-03000"));
        Assert.That(full.Split('\n'), Has.Length.EqualTo(5000));
    }

    [Test]
    public async Task Bound_LimiteBytes_TruncaPorBytes()
    {
        await using var db = NewDb();
        db.Database.EnsureCreated();
        var config = new ConfigService(db, TestCache.Create());
        // Limite de bytes apertado — 2000 linhas mas ~4KB.
        await config.SetAsync("builtinTools.outputMaxBytes", 4 * 1024);
        var service = new ToolOutputService(
            config, new StubEnv(_root), NullLogger<ToolOutputService>.Instance);

        var text = string.Join('\n',
            Enumerable.Range(1, 200).Select(i => new string('x', 100)));
        var bound = await service.BoundAsync(text, "shell_exec");

        Assert.Multiple(() =>
        {
            Assert.That(bound.Truncated, Is.True);
            Assert.That(bound.Text.Length, Is.LessThan(6000));
        });
    }

    [Test]
    public async Task Bound_Secret_ScrubAntesDeGravar()
    {
        var service = NewService();
        var secret = "sk-abcdefghij0123456789abcdefghij";
        var text = string.Join('\n',
            Enumerable.Range(1, 3000).Select(i => $"linha-{i:D5} {secret}"));

        var bound = await service.BoundAsync(text, "shell_exec");

        Assert.That(bound.OutputPath, Is.Not.Null);
        var full = await File.ReadAllTextAsync(bound.OutputPath!);
        Assert.Multiple(() =>
        {
            Assert.That(full, Does.Not.Contain(secret));
            Assert.That(full, Does.Contain("[REDACTED_SECRET]"));
        });
    }

    [Test]
    public async Task Cleanup_RemoveMais7Dias_PreservaRecentes()
    {
        var service = NewService();
        Directory.CreateDirectory(OutputDir);
        var velho = Path.Join(OutputDir, "tool_velho.txt");
        var novo = Path.Join(OutputDir, "tool_novo.txt");
        var estranho = Path.Join(OutputDir, "not-tool.txt");
        await File.WriteAllTextAsync(velho, "velho");
        await File.WriteAllTextAsync(novo, "novo");
        await File.WriteAllTextAsync(estranho, "não é tool");
        File.SetLastWriteTimeUtc(velho, DateTime.UtcNow.AddDays(-8));

        var removed = await service.CleanupAsync();

        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(File.Exists(velho), Is.False);
            Assert.That(File.Exists(novo), Is.True);
            Assert.That(File.Exists(estranho), Is.True); // fora do padrão
        });
    }

    [Test]
    public async Task Cleanup_DirInexistente_ZeroSemErro()
    {
        var service = NewService();
        Assert.That(await service.CleanupAsync(), Is.EqualTo(0));
    }

    [Test]
    public void TryResolveManaged_AceitaToolTxt_NegaResto()
    {
        var service = NewService();
        var dir = Path.Join(_root, "data", "tool-output");

        Assert.Multiple(() =>
        {
            Assert.That(
                service.TryResolveManaged(Path.Join(dir, "tool_abc.txt")),
                Is.EqualTo(Path.Join(dir, "tool_abc.txt")));
            Assert.That(service.TryResolveManaged(Path.Join(dir, "evil.md")), Is.Null);
            Assert.That(service.TryResolveManaged("/etc/passwd"), Is.Null);
            Assert.That(
                service.TryResolveManaged(Path.Join(dir, "..", "tool_x.txt")),
                Is.Null);
            Assert.That(
                service.TryResolveManaged(Path.Join(dir, "sub", "tool_x.txt")),
                Is.Null);
            Assert.That(service.TryResolveManaged("relativo.txt"), Is.Null);
            Assert.That(service.TryResolveManaged(null), Is.Null);
        });
    }

    private sealed class StubEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
