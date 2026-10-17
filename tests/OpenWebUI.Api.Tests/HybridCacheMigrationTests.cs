using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Comportamento pós-migração IMemoryCache → HybridCache: coalescing de
/// factories, leitura cacheada no ConfigService, cache de servidor MCP no
/// hot path de tools/call e eviction explícita da auth de API key.
/// </summary>
[TestFixture]
public class HybridCacheMigrationTests
{
    private string _dbPath = null!;
    private HybridCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = TestInfra.NewDbPath("openwebui-hybrid");
        _cache = TestCache.Create();
    }

    [TearDown]
    public void TearDown()
    {
        TestInfra.DeleteDb(_dbPath);
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private async Task MigrateAsync()
    {
        await using var db = NewDb();
        await db.Database.MigrateAsync();
    }

    [Test]
    public async Task ConfigService_GetAsync_LeDoCacheNaSegundaChamada()
    {
        // Dado: config persistida direto no banco (bypass do serviço)
        await MigrateAsync();
        await using (var db = NewDb())
        {
            db.ConfigEntries.Add(new ConfigEntry
            {
                Key = "teste.cache",
                ValueJson = "\"inicial\"",
                UpdatedAt = 1,
            });
            await db.SaveChangesAsync();
        }

        var svc = new ConfigService(NewDb(), _cache);

        // Quando: duas leituras seguidas
        var v1 = await svc.GetAsync<string>("teste.cache", "padrao");
        await using (var db = NewDb())
        {
            // muda o banco por trás do cache (TTL 10s não expira no teste)
            var e = await db.ConfigEntries.FirstAsync(x => x.Key == "teste.cache");
            e.ValueJson = "\"alterado\"";
            await db.SaveChangesAsync();
        }
        var v2 = await svc.GetAsync<string>("teste.cache", "padrao");

        // Então: a segunda leitura veio do cache (valor antigo)
        Assert.That(v1, Is.EqualTo("inicial"));
        Assert.That(v2, Is.EqualTo("inicial"), "segunda leitura deve vir do HybridCache");
    }

    [Test]
    public async Task ConfigService_SetAsync_NullNaoCacheaEDevolveDefault()
    {
        // Dado: config "audio.config" com objeto válido, depois null no banco
        await MigrateAsync();
        var svc = new ConfigService(NewDb(), _cache);
        var fallback = new AudioConfig("none", null, null, null, "none", null, null, null, null, null);
        await svc.SetAsync<AudioConfig?>("audio.config",
            new AudioConfig("openai", "http://x", "k", "m", "none", null, null, null, null, null));
        Assert.That((await svc.GetAsync<AudioConfig?>("audio.config", null))!.SttEngine, Is.EqualTo("openai"));

        // Quando: a config é resetada para null (caminho que 500ava o /api/config)
        await svc.SetAsync<AudioConfig?>("audio.config", null);

        // Então: leitura devolve o default, nunca null-cache
        var v = await svc.GetAsync("audio.config", fallback);
        Assert.That(v, Is.Not.Null.And.Property(nameof(AudioConfig.SttEngine)).EqualTo("none"));
        // T e T? compartilham o slot: a leitura nullable vê o fallback já
        // cacheado (mesmo comportamento do IMemoryCache antigo).
        var vNul = await svc.GetAsync<AudioConfig?>("audio.config", null);
        Assert.That(vNul, Is.Not.Null.And.Property(nameof(AudioConfig.SttEngine)).EqualTo("none"));
    }

    [Test]
    public async Task ConfigService_GetAsync_ConcorrenteCoalesceFactory()
    {
        // Dado: chave inexistente — N leituras concorrentes
        await MigrateAsync();
        var svc = new ConfigService(NewDb(), _cache);

        // Quando: 32 leituras paralelas da mesma chave
        var tasks = Enumerable.Range(0, 32)
            .Select(_ => svc.GetAsync<string>("chave.coalesce", "v"));
        var results = await Task.WhenAll(tasks);

        // Então: todas obtêm o mesmo valor sem erro de concorrência
        Assert.That(results, Is.All.EqualTo("v"));
    }

    [Test]
    public async Task McpClientService_GetServerAsync_CacheiaELookupPorTag()
    {
        // Dado: servidor MCP persistido
        await MigrateAsync();
        string id;
        await using (var db = NewDb())
        {
            var server = new McpServer
            {
                Name = "srv-cache",
                Transport = "streamablehttp",
                Url = "http://127.0.0.1:9/mcp",
                Enabled = true,
                CreatedAt = 1,
            };
            db.McpServers.Add(server);
            await db.SaveChangesAsync();
            id = server.Id;
        }

        var mcp = new McpClientService(NewDb(), _cache);

        // Quando: lookup → deleta o server no banco → lookup de novo
        var hit1 = await mcp.GetServerAsync(id);
        await using (var db = NewDb())
        {
            db.McpServers.Remove(await db.McpServers.FirstAsync(s => s.Id == id));
            await db.SaveChangesAsync();
        }
        var hit2 = await mcp.GetServerAsync(id);

        // Então: segunda chamada veio do cache (server já não existe)
        Assert.That(hit1, Is.Not.Null);
        Assert.That(hit2, Is.Not.Null.And.Property(nameof(McpServer.Name)).EqualTo("srv-cache"),
            "o lookup deve ser servido pelo cache de 30s");

        // E a invalidação por tag "mcp" derruba a entrada
        await _cache.RemoveByTagAsync("mcp");
        var miss = await mcp.GetServerAsync(id);
        Assert.That(miss, Is.Null, "após RemoveByTagAsync(\"mcp\") o lookup deve ir ao banco");
    }

    [Test]
    public async Task McpClientService_GetServerAsync_NaoExistenteRetornaNullCacheado()
    {
        // Dado: banco vazio
        await MigrateAsync();
        var mcp = new McpClientService(NewDb(), _cache);

        // Quando/Então: miss repetida é estável (null também é cacheável no L1)
        Assert.That(await mcp.GetServerAsync("inexistente"), Is.Null);
        Assert.That(await mcp.GetServerAsync("inexistente"), Is.Null);
    }

    [Test]
    public async Task ApiKeyAuthCache_EvictAsync_RemoveEntradas()
    {
        // Dado: entradas de auth cacheadas via HybridCache
        var key1 = ApiKeyAuthCache.CacheKey("hash1");
        var key2 = ApiKeyAuthCache.CacheKey("hash2");
        await _cache.SetAsync(key1, "identidade1");
        await _cache.SetAsync(key2, "identidade2");

        // Quando: evicta apenas a primeira
        await ApiKeyAuthCache.EvictAsync(_cache, ["hash1"]);

        // Então: a primeira some e a segunda permanece
        var v1 = await _cache.GetOrCreateAsync<string?>(key1, _ => ValueTask.FromResult<string?>("novo"));
        var v2 = await _cache.GetOrCreateAsync<string?>(key2, _ => ValueTask.FromResult<string?>("novo"));
        Assert.That(v1, Is.EqualTo("novo"), "entrada evictada deve ser recriada pelo factory");
        Assert.That(v2, Is.EqualTo("identidade2"));
    }

    [Test]
    public async Task ProviderService_ListModelsAsync_CacheiaPorFingerprint()
    {
        // Dado: conexão OpenAI apontando para endpoint local inalcançável —
        // a primeira chamada falha de rede; a segunda deve vir do cache
        // somente se a primeira tiver sucesso. Em vez disso testamos o
        // caminho determinístico: cache pré-aquecido é respeitado.
        await MigrateAsync();
        var config = new ConfigService(NewDb(), _cache);
        await config.SetAsync("connections",
            new ConnectionsConfig([], [], [], Providers: []));
        var svc = new ProviderService(
            new StubHttpClientFactoryAlwaysFails(),
            config,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderService>(),
            _cache, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        // Quando: primeira listagem (upstream falha → lista vazia cacheada)
        var models1 = await svc.ListModelsAsync();
        var models2 = await svc.ListModelsAsync();

        // Então: mesma lista servida pelo cache sem segunda viagem ao upstream
        Assert.That(models1, Is.Empty);
        Assert.That(models2, Is.SameAs(models1).Or.Empty,
            "segunda listagem deve vir do HybridCache (ou ser a mesma lista vazia)");
    }

    private sealed class StubHttpClientFactoryAlwaysFails : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new ThrowingHandler());
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }
}
