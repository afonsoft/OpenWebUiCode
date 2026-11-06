using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NUnit.Framework;

using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Modo Adaptive/Fusion (SPEC-20261106): ref composta
/// <c>fusion:{leader}&gt;{sidekick}</c> — parse/format do contrato,
/// resolução do leader para caminhos sem orquestração e o kv
/// <c>r:{run}:fusion.{leader,sidekick}</c> que o <c>delegate_task</c> lê
/// para rotear os sub-agentes ao Sidekick do par.
/// </summary>
[TestFixture]
public class FusionModeTests
{
    private string _root = null!;
    private string _dbPath = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-fusion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, "test.db");

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddHybridCache();
        services.AddSingleton<IHostEnvironment>(new FusionEnv(_root));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<ConfigService>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }

    // Contrato: ref válida round-trips via Format/TryParse (ids com ':' e
    // '/' — padrão openai/ollama — só o '>' é o separador do par).
    [Test]
    public void TryParse_RefValida_RoundTrip()
    {
        var value = FusionModelRef.Format("openai/gpt-5", "ollama:qwen3:8b");
        Assert.That(value, Is.EqualTo("fusion:openai/gpt-5>ollama:qwen3:8b"));
        Assert.That(FusionModelRef.TryParse(value, out var leader, out var sidekick),
            Is.True);
        Assert.That(leader, Is.EqualTo("openai/gpt-5"));
        Assert.That(sidekick, Is.EqualTo("ollama:qwen3:8b"));
    }

    // Refs malformadas não são fusion: sem prefixo, sem '>', lado vazio.
    [Test]
    public void TryParse_RefsInvalidas_Falham()
    {
        Assert.That(FusionModelRef.TryParse(null, out _, out _), Is.False);
        Assert.That(FusionModelRef.TryParse("gpt-5", out _, out _), Is.False);
        Assert.That(FusionModelRef.TryParse("fusion:", out _, out _), Is.False);
        Assert.That(FusionModelRef.TryParse("fusion:a>", out _, out _), Is.False);
        Assert.That(FusionModelRef.TryParse("fusion:>b", out _, out _), Is.False);
        Assert.That(FusionModelRef.TryParse("arena:gpt-5>mini", out _, out _), Is.False);
    }

    // IsFusion/LeaderOrSelf: caminhos sem orquestração (tasks, playground)
    // resolvem ao leader; modelos comuns passam intocados.
    [Test]
    public void LeaderOrSelf_ResolveAoLeader_QuandoFusion()
    {
        Assert.That(FusionModelRef.IsFusion("fusion:a>b"), Is.True);
        Assert.That(FusionModelRef.IsFusion("FUSION:a>b"), Is.True);
        Assert.That(FusionModelRef.IsFusion("gpt-5"), Is.False);
        Assert.That(FusionModelRef.LeaderOrSelf("fusion:a>b"), Is.EqualTo("a"));
        Assert.That(FusionModelRef.LeaderOrSelf("gpt-5"), Is.EqualTo("gpt-5"));
        // Espaços nas pontas do par são tolerados no parse.
        Assert.That(FusionModelRef.TryParse("fusion: a > b ", out var l, out var s),
            Is.True);
        Assert.That((l, s), Is.EqualTo(("a", "b")));
    }

    // RF-003: kv do par — o executor grava r:{run}:fusion.sidekick e o
    // delegate_task a lê como modelo default dos sub-agentes; aqui o
    // roundtrip no ConfigService garante o contrato persistido.
    [Test]
    public async Task FusionKv_RoundtripDoPar()
    {
        using var scope = _provider.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
        var leaderKey = "r:run-1:fusion.leader";
        var sidekickKey = "r:run-1:fusion.sidekick";

        Assert.That(await config.GetAsync<string?>(
            sidekickKey, null, CancellationToken.None), Is.Null);

        await config.SetAsync(leaderKey, "openai/gpt-5", CancellationToken.None);
        await config.SetAsync(sidekickKey, "ollama:qwen3:8b", CancellationToken.None);

        Assert.That(await config.GetAsync<string?>(
            leaderKey, null, CancellationToken.None), Is.EqualTo("openai/gpt-5"));
        Assert.That(await config.GetAsync<string?>(
            sidekickKey, null, CancellationToken.None),
            Is.EqualTo("ollama:qwen3:8b"));
    }

    private sealed class FusionEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
