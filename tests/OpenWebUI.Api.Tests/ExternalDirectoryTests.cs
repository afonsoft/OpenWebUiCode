using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// RF-003 do SPEC-20261018-permissions-v2: boundary
/// <c>external_directory</c> — path fora do workdir exige liberação
/// registrada na <see cref="ExternalAccessRegistry"/> (por run) para as
/// tools file_* executarem; dentro do workspace nada muda.
/// </summary>
[TestFixture]
public class ExternalDirectoryTests
{
    private string _workspace = null!;
    private string _dbPath = null!;

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    private FileReadBuiltinTool NewFileRead(ExternalAccessRegistry? registry = null) =>
        new(new HistoryResourceService(NewDb()),
            new ToolOutputService(new ConfigService(NewDb(), TestCache.Create()),
                new StubEnvLocal(_workspace),
                NullLogger<ToolOutputService>.Instance),
            external: registry);

    [SetUp]
    public void SetUp()
    {
        _workspace = Path.Join(Path.GetTempPath(), $"owui-ws-{Guid.NewGuid():N}");
        _dbPath = Path.Join(Path.GetTempPath(), $"owui-ext-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(_workspace);
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    private static BuiltinToolContext Ctx(string workspace, string runId = "r1") =>
        new("u1", "c1", runId, workspace, "/tmp/uploads", null, null);

    [Test]
    public async Task FileRead_PathForaSemLiberacao_Nega()
    {
        var outside = Path.Join(Path.GetTempPath(), $"owui-ext-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "segredo");
        try
        {
            var res = await NewFileRead().ExecuteAsync(
                Args($"{{\"path\":\"{outside.Replace("\\", "\\\\")}\"}}"),
                Ctx(_workspace), CancellationToken.None);
            Assert.That(res.Text, Does.Contain("negad"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Test]
    public async Task FileRead_PathForaLiberado_Executa()
    {
        var outside = Path.Join(Path.GetTempPath(), $"owui-ext-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "conteudo-externo");
        var registry = new ExternalAccessRegistry();
        registry.Allow("r1", outside);
        try
        {
            var res = await NewFileRead(registry).ExecuteAsync(
                Args($"{{\"path\":\"{outside.Replace("\\", "\\\\")}\"}}"),
                Ctx(_workspace), CancellationToken.None);
            Assert.That(res.Text, Does.Contain("conteudo-externo"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Test]
    public async Task FileRead_LiberacaoDeOutraRun_NaoVaza()
    {
        var outside = Path.Join(Path.GetTempPath(), $"owui-ext-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "x");
        var registry = new ExternalAccessRegistry();
        registry.Allow("outra-run", outside);
        try
        {
            var res = await NewFileRead(registry).ExecuteAsync(
                Args($"{{\"path\":\"{outside.Replace("\\", "\\\\")}\"}}"),
                Ctx(_workspace, runId: "r1"), CancellationToken.None);
            Assert.That(res.Text, Does.Contain("negad"));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Test]
    public async Task FileRead_PathDentro_SemRegistryFunciona()
    {
        var inside = Path.Join(_workspace, "ok.txt");
        await File.WriteAllTextAsync(inside, "dentro");
        var res = await NewFileRead().ExecuteAsync(
            Args("{\"path\":\"ok.txt\"}"),
            Ctx(_workspace), CancellationToken.None);
        Assert.That(res.Text, Does.Contain("dentro"));
    }

    [Test]
    public async Task FileWrite_PathForaSemLiberacao_Nega()
    {
        var outside = Path.Join(Path.GetTempPath(), $"owui-ext-{Guid.NewGuid():N}.txt");
        var res = await new FileWriteBuiltinTool().ExecuteAsync(
            Args($"{{\"path\":\"{outside.Replace("\\", "\\\\")}\",\"content\":\"x\"}}"),
            Ctx(_workspace), CancellationToken.None);
        Assert.That(res.Text, Does.Contain("negad"));
        Assert.That(File.Exists(outside), Is.False);
    }

    [Test]
    public async Task FileWrite_PathForaLiberado_Grava()
    {
        var outside = Path.Join(Path.GetTempPath(), $"owui-ext-{Guid.NewGuid():N}.txt");
        var registry = new ExternalAccessRegistry();
        registry.Allow("r1", outside);
        try
        {
            var res = await new FileWriteBuiltinTool(external: registry).ExecuteAsync(
                Args($"{{\"path\":\"{outside.Replace("\\", "\\\\")}\",\"content\":\"gravado\"}}"),
                Ctx(_workspace), CancellationToken.None);
            Assert.That(File.Exists(outside), Is.True);
            Assert.That(await File.ReadAllTextAsync(outside), Is.EqualTo("gravado"));
        }
        finally
        {
            if (File.Exists(outside))
            {
                File.Delete(outside);
            }
        }
    }

    [Test]
    public void Registry_ClearPorRun_LimpaSoEla()
    {
        var registry = new ExternalAccessRegistry();
        registry.Allow("r1", "/tmp/a");
        registry.Allow("r2", "/tmp/b");
        registry.Clear("r1");
        Assert.Multiple(() =>
        {
            Assert.That(registry.IsAllowed("r1", "/tmp/a"), Is.False);
            Assert.That(registry.IsAllowed("r2", "/tmp/b"), Is.True);
        });
    }

    [Test]
    public void ResolveAllowed_RelativoAoWorkspace_RetornaFull()
    {
        var registry = new ExternalAccessRegistry();
        var inside = Path.GetFullPath(Path.Join(_workspace, "ok.txt"));
        registry.Allow("r1", inside);
        var resolved = registry.ResolveAllowed("r1", _workspace, "ok.txt");
        Assert.That(resolved, Is.EqualTo(inside));
    }
    /// <summary>IHostEnvironment apontando para o workspace de teste.</summary>
    private sealed class StubEnvLocal(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "OpenWebUI.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
