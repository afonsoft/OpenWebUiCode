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

    // SPEC-20261110 fusion-fallback-chain: cada papel aceita cadeia
    // ordenada separada por vírgula — TryParseChain extrai os dois lados.
    [Test]
    public void TryParseChain_CadeiaCompleta_ExtraiOsDoisPapeis()
    {
        Assert.That(FusionModelRef.TryParseChain(
            "fusion:auto/claude-opus,auto/claude-sonnet>auto/glm,auto/kimi",
            out var leaders, out var sidekicks), Is.True);
        Assert.That(leaders, Is.EqualTo(
            new[] { "auto/claude-opus", "auto/claude-sonnet" }));
        Assert.That(sidekicks, Is.EqualTo(
            new[] { "auto/glm", "auto/kimi" }));
    }

    // O par legado fusion:{L}>{S} é a cadeia de 1 — TryParse continua
    // devolvendo a primeira posição de cada lado (backward-compat).
    [Test]
    public void TryParseChain_ParLegado_CadeiaDeUm()
    {
        Assert.That(FusionModelRef.TryParseChain("fusion:a>b",
            out var leaders, out var sidekicks), Is.True);
        Assert.That(leaders, Is.EqualTo(new[] { "a" }));
        Assert.That(sidekicks, Is.EqualTo(new[] { "b" }));
        Assert.That(FusionModelRef.TryParse(
            "fusion:L1,L2>S1,S2", out var l, out var s), Is.True);
        Assert.That((l, s), Is.EqualTo(("L1", "S1")));
    }

    // Slots vazios e ref malformada são rejeitados.
    [Test]
    public void TryParseChain_SlotVazio_Falha()
    {
        Assert.That(FusionModelRef.TryParseChain(
            "fusion:a,>b", out _, out _), Is.False);
        Assert.That(FusionModelRef.TryParseChain(
            "fusion:a>b,", out _, out _), Is.False);
        Assert.That(FusionModelRef.TryParseChain(
            "fusion:a,,b>c", out _, out _), Is.False);
        Assert.That(FusionModelRef.TryParseChain(
            "chain:a>b", out _, out _), Is.False);
        Assert.That(FusionModelRef.TryParseChain(null, out _, out _), Is.False);
    }

    // Cadeia efetiva = leaders primeiro, depois sidekicks, dedup
    // case-insensitive preservando a primeira posição.
    [Test]
    public void EffectiveChain_LeadersPrimeiro_Dedup()
    {
        var chain = FusionModelRef.EffectiveChain(
            ["L1", "L2"], ["S1", "L2", "S2", "l1"]);
        Assert.That(chain, Is.EqualTo(new[] { "L1", "L2", "S1", "S2" }));
    }

    // chain:{m1,m2,…} — ref de fallback dos filhos, sem semântica fusion.
    [Test]
    public void ChainModelRef_RoundTrip_EInvalidas()
    {
        var value = ChainModelRef.Format(["auto/glm", "auto/kimi", "auto/opus"]);
        Assert.That(value, Is.EqualTo("chain:auto/glm,auto/kimi,auto/opus"));
        Assert.That(ChainModelRef.TryParse(value, out var chain), Is.True);
        Assert.That(chain, Is.EqualTo(
            new[] { "auto/glm", "auto/kimi", "auto/opus" }));

        Assert.That(ChainModelRef.TryParse("chain:", out _), Is.False);
        Assert.That(ChainModelRef.TryParse("chain:a,,b", out _), Is.False);
        Assert.That(ChainModelRef.TryParse("gpt-5", out _), Is.False);
        Assert.That(ChainModelRef.TryParse(null, out _), Is.False);
        Assert.That(ChainModelRef.IsChain("CHAIN:a,b"), Is.True);
    }

    // LeaderOrSelf com ref de cadeia: devolve a primeira posição —
    // caminhos sem orquestração (tasks/playground) usam o leader 1.
    [Test]
    public void LeaderOrSelf_Cadeia_DevolvePrimeiroLeader()
    {
        Assert.That(FusionModelRef.LeaderOrSelf("fusion:L1,L2>S1,S2"),
            Is.EqualTo("L1"));
        Assert.That(FusionModelRef.LeaderOrSelf("chain:a,b"),
            Is.EqualTo("chain:a,b"));
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
