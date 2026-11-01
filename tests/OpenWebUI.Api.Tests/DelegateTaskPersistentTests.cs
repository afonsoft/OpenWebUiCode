using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NUnit.Framework;

using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Sidekick persistente + profiles explore/general + max_nesting
/// (SPEC-20261031-sidekick-orchestration): kv de chat filho por
/// sidekick_id, serialização de dispatches, alias de frontmatter e
/// enforcement do preset fg/bg.
/// </summary>
[TestFixture]
[NonParallelizable] // muta AgentDefinitionService.HomeResolver (estático)
public class DelegateTaskPersistentTests
{
    private string _root = null!;
    private string _fakeHome = null!;
    private string _dbPath = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-sidekick-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, "test.db");
        _fakeHome = Path.Join(_root, "fake-home");
        Directory.CreateDirectory(_fakeHome);
        AgentDefinitionService.HomeResolver = () => _fakeHome;

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddHybridCache();
        services.AddSingleton<IHostEnvironment>(new SkEnv(_root));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<ConfigService>();
        services.AddScoped<AgentDefinitionService>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        AgentDefinitionService.Invalidate("u1");
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

    private AgentDefinitionService Svc()
    {
        var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AgentDefinitionService>();
    }

    // RF-005: max_nesting é alias de maxDepth (clamp 0..3, negativo→default).
    [Test]
    public async Task Parse_MaxNesting_AliasDeMaxDepth()
    {
        var svc = Svc();
        var def = svc.TryParse("""
            ---
            name: nested
            description: d
            max_nesting: 2
            ---
            corpo
            """, "x", "builtin");
        Assert.That(def, Is.Not.Null);
        Assert.That(def!.MaxDepth, Is.EqualTo(2));

        var neg = svc.TryParse("""
            ---
            name: neg
            description: d
            max_nesting: -1
            ---
            corpo
            """, "x", "builtin");
        Assert.That(neg!.MaxDepth, Is.EqualTo(0)); // clamp → 0

        var alto = svc.TryParse("""
            ---
            name: high
            description: d
            max_nesting: 99
            ---
            corpo
            """, "x", "builtin");
        Assert.That(alto!.MaxDepth, Is.EqualTo(3)); // clamp teto global
        await Task.CompletedTask;
    }

    // RF-001: profiles explore/general embutidos com tools esperadas.
    [Test]
    public async Task Profiles_ExploreReadOnly_EGeneralFull()
    {
        var svc = Svc();
        var catalog = await svc.ListAsync("u1", null, CancellationToken.None);
        Assert.That(catalog.TryGetValue("explore", out var explore), Is.True,
            "explore.agent.md builtin ausente");
        Assert.That(catalog.TryGetValue("general", out var general), Is.True,
            "general.agent.md builtin ausente");
        Assert.That(explore!.Tools, Does.Contain("file_read")
            .And.Contain("file_grep").And.Contain("web_search"));
        Assert.That(explore.Tools, Does.Not.Contain("file_write")
            .And.Not.Contain("shell_exec"));
        Assert.That(general!.Tools, Does.Contain("*"));
        Assert.That(explore.MaxDepth, Is.EqualTo(0));
        Assert.That(general.MaxDepth, Is.EqualTo(0));
    }

    // RF-002: kv do sidekick persiste o chat filho — segundo dispatch
    // com mesmo id lê o mesmo chat (a tool consulta
    // r:{parentRun}:sidekick:{id}); aqui garantimos o roundtrip no
    // ConfigService que a tool usa.
    [Test]
    public async Task SidekickKv_RoundtripDoChatFilho()
    {
        using var scope = _provider.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
        var key = "r:parent-1:sidekick:sk-test";
        Assert.That(await config.GetAsync<string?>(key, null, CancellationToken.None),
            Is.Null);
        await config.SetAsync(key, "chat-filho-1", CancellationToken.None);
        Assert.That(await config.GetAsync<string?>(key, null, CancellationToken.None),
            Is.EqualTo("chat-filho-1"));
    }

    private sealed class SkEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
