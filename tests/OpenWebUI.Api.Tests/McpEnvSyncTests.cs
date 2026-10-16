using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// <c>SyncEnvServersAsync</c> (SPEC-20261016-global-agents-scope RF-006):
/// materializa servers de <c>.agents/mcp.json</c> como linhas
/// <c>env:{id}</c>, resolve env literais/<c>${REF}</c>, desativa os
/// obsoletos e transforma falha de refresh em <c>LastError</c> sem quebrar.
/// </summary>
[TestFixture]
[NonParallelizable] // EnvSyncAt/EnvLiterals são estáticos por workdir
public class McpEnvSyncTests
{
    private string _root = null!;
    private string _workdir = null!;
    private string _dbPath = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-mcpenv-{Guid.NewGuid():N}");
        _workdir = Path.Join(_root, "repo");
        Directory.CreateDirectory(_workdir);
        _dbPath = Path.Join(_root, "test.db");

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddHybridCache();
        services.AddLogging();
        services.AddScoped<SkillDiscoveryService>();
        services.AddScoped<McpClientService>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private McpClientService Service()
    {
        var scope = _provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<McpClientService>();
    }

    private void WriteMcpJson(string json)
    {
        var dir = Path.Join(_workdir, ".agents");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "mcp.json"), json);
        SkillDiscoveryService.Invalidate(_workdir);
    }

    [Test]
    public async Task Sync_MaterializaEnvServers_EDesativaObsoletos()
    {
        Environment.SetEnvironmentVariable("OWUI_TEST_ENV", "segredo-ref");
        try
        {
            WriteMcpJson("""
                {"mcpServers":{
                  "stuby":{"command":"bin-que-nao-existe-xyz","args":["-y"],
                           "env":{"LITERAL":"abc","REF":"${OWUI_TEST_ENV}"}},
                  "site":{"url":"https://api.exemplo.com/mcp"}
                }}
                """);

            var total = await Service().SyncEnvServersAsync(_workdir);
            Assert.That(total, Is.EqualTo(0)); // stdio falha → 0 tools, mas linha upsertada

            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stuby = await db.McpServers.SingleAsync(s => s.Id == "env:stuby");
            Assert.Multiple(() =>
            {
                Assert.That(stuby.Enabled, Is.True);
                Assert.That(stuby.UserId, Is.Null); // global
                Assert.That(stuby.LastError, Is.Not.Null.And.Not.Empty); // refresh falhou
                Assert.That(stuby.EnvJson, Does.Contain("LITERAL").And.Contain("REF"));
            });
            var site = await db.McpServers.SingleAsync(s => s.Id == "env:site");
            Assert.Multiple(() =>
            {
                Assert.That(site.Url, Is.EqualTo("https://api.exemplo.com/mcp"));
                Assert.That(site.LastError, Is.Not.Null); // http inalcançável → erro registrado
            });

            // Re-sync dentro do throttle devolve 0 sem tocar nas linhas.
            Assert.That(await Service().SyncEnvServersAsync(_workdir), Is.EqualTo(0));

            // Removendo o mcp.json e usando outro workdir (burla throttle por
            // chave), o server "stuby" vira stale → desabilitado.
            var workdir2 = Path.Join(_root, "repo2");
            Directory.CreateDirectory(workdir2);
            await Service().SyncEnvServersAsync(workdir2);
            await db.Entry(stuby).ReloadAsync(); // entidade estava trackeada
            Assert.That(stuby.Enabled, Is.False);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OWUI_TEST_ENV", null);
        }
    }

    [Test]
    public async Task Sync_SemSkills_RetornaZero()
    {
        var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<HybridCache>();
        var svc = new McpClientService(db, cache, skills: null);
        Assert.That(await svc.SyncEnvServersAsync(_workdir), Is.EqualTo(0));
    }
}
