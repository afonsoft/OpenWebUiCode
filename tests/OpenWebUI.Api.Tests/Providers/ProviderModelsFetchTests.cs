using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests.Providers;

/// <summary>
/// Listagem de modelos por conexão tipada (combo da UI admin):
/// <c>FetchGoogleModelsAsync</c> (x-goog-api-key, strip do prefixo
/// <c>models/</c>), <c>FetchAnthropicModelsAsync</c> (x-api-key +
/// anthropic-version) e <c>FetchOllamaModelsAsync</c> (capabilities do
/// /api/tags). Cobertura das branches de fallback/erro (issue #706).
/// </summary>
[TestFixture, IsolateEnvironment]
public class ProviderModelsFetchTests
{
    private string _dbPath = null!;
    private AppDbContext _db = null!;
    private ConfigService _config = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-pmf-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        await DatabaseMigrator.MigrateAsync(_db);
        _config = new ConfigService(_db, TestCache.Create());
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private ProviderService NewService(StubHttpHandler handler) =>
        new(new StubHttpFactory(handler), _config,
            NullLogger<ProviderService>.Instance,
            TestCache.Create(), new ConfigurationBuilder().Build());

    private Task SetProvidersAsync(params ProviderConnection[] providers) =>
        _config.SetAsync("connections",
            new ConnectionsConfig([], [], [], Providers: providers));

    /// <summary>Google: GET {base}/models com x-goog-api-key; id sem o
    /// prefixo "models/", displayName preservado; entrada sem nome é
    /// descartada.</summary>
    [Test]
    public async Task ListModels_Google_StripPrefixoEHeader()
    {
        await SetProvidersAsync(new ProviderConnection(
            "google", "https://goog.test", "gk"));
        var http = new StubHttpHandler();
        http.Enqueue(HttpStatusCode.OK,
            "{\"models\":[" +
            "{\"name\":\"models/gemini-3\",\"displayName\":\"Gemini 3\"}," +
            "{\"name\":\"models/gemini-2\"}," +
            "{\"displayName\":\"sem nome\"}]}");

        var models = await NewService(http)
            .ListModelsForConnectionAsync("google", 0);

        Assert.Multiple(() =>
        {
            Assert.That(models.Select(m => m.Id),
                Is.EqualTo(new[] { "gemini-3", "gemini-2" }));
            Assert.That(models[0].Name, Is.EqualTo("Gemini 3"));
            Assert.That(models[1].Name, Is.EqualTo("gemini-2"),
                "displayName ausente cai no id");
            Assert.That(http.Requests[0].Url,
                Is.EqualTo("https://goog.test/models"));
            Assert.That(http.Requests[0].Headers.Any(h =>
                h.Key == "x-goog-api-key" && h.Value == "gk"), Is.True);
        });
    }

    /// <summary>Anthropic: GET {base}/v1/models com x-api-key +
    /// anthropic-version; display_name vira nome de exibição.</summary>
    [Test]
    public async Task ListModels_Anthropic_HeadersEDisplay()
    {
        await SetProvidersAsync(new ProviderConnection(
            "anthropic", "https://anth.test", "ak"));
        var http = new StubHttpHandler();
        http.Enqueue(HttpStatusCode.OK,
            "{\"data\":[{\"id\":\"claude-4\",\"display_name\":\"Claude 4\"}]}");

        var models = await NewService(http)
            .ListModelsForConnectionAsync("anthropic", 0);

        Assert.Multiple(() =>
        {
            Assert.That(models.Select(m => m.Id),
                Is.EqualTo(new[] { "claude-4" }));
            Assert.That(models[0].Name, Is.EqualTo("Claude 4"));
            Assert.That(http.Requests[0].Url,
                Is.EqualTo("https://anth.test/v1/models"));
            Assert.That(http.Requests[0].Headers.Any(h =>
                h.Key == "x-api-key" && h.Value == "ak"), Is.True);
            Assert.That(http.Requests[0].Headers.Any(h =>
                h.Key == "anthropic-version"), Is.True);
        });
    }

    /// <summary>Falha HTTP → lista vazia (provider indisponível não derruba
    /// o restante); sem api key → nem chama a rede; índice fora → vazio.</summary>
    [Test]
    public async Task ListModels_FalhasEBordas()
    {
        await SetProvidersAsync(new ProviderConnection(
            "google", "https://goog.test", "gk"));
        var http500 = new StubHttpHandler();
        http500.Enqueue(HttpStatusCode.InternalServerError, "{}");
        Assert.That(await NewService(http500)
                .ListModelsForConnectionAsync("google", 0), Is.Empty);

        // Sem api key: retorna [] sem request.
        await SetProvidersAsync(new ProviderConnection(
            "anthropic", "https://anth.test", ""));
        var httpNop = new StubHttpHandler();
        Assert.That(await NewService(httpNop)
                .ListModelsForConnectionAsync("anthropic", 0), Is.Empty);
        Assert.That(httpNop.Requests, Is.Empty);

        // Índice fora do range → [].
        Assert.That(await NewService(httpNop)
                .ListModelsForConnectionAsync("google", 9), Is.Empty);
    }

    /// <summary>Ollama /api/tags: capabilities viram o campo Caps do
    /// ModelInfo; resposta sem "models" → vazio (shape defensivo).</summary>
    [Test]
    public async Task ListModels_Ollama_CapabilitiesEShape()
    {
        await _config.SetAsync("connections", new ConnectionsConfig(
            ["http://oll.test"], [], []));
        var http = new StubHttpHandler();
        http.Enqueue(HttpStatusCode.OK,
            "{\"models\":[{\"model\":\"llama3\",\"name\":\"Llama 3\"," +
            "\"capabilities\":[\"tools\",\"vision\"]}," +
            "{\"model\":\"sem-caps\"}]}");
        var models = await NewService(http)
            .ListModelsForConnectionAsync("ollama", 0);
        Assert.Multiple(() =>
        {
            Assert.That(models.Select(m => m.Id),
                Is.EqualTo(new[] { "llama3", "sem-caps" }));
            Assert.That(models[0].Capabilities,
                Is.EqualTo(new[] { "tools", "vision" }));
            Assert.That(models[1].Capabilities, Is.Null);
        });

        var httpShape = new StubHttpHandler();
        httpShape.Enqueue(HttpStatusCode.OK, "{\"erro\":\"shape inesperado\"}");
        Assert.That(await NewService(httpShape)
                .ListModelsForConnectionAsync("ollama", 0), Is.Empty);
    }
}
