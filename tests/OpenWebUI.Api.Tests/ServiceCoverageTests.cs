using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.Services.Image;
using OpenWebUI.Infrastructure.Terminal;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura direta de serviços: web loader (incl. YouTube), engines de web
/// search, motor de imagem OpenAI-compatible, embeddings, provider de modelos
/// e edges do PTY/session manager.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ServiceCoverageTests
{
    private readonly List<IDisposable> _owned = [];
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-svccov-{Guid.NewGuid():N}.db");
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var d in _owned)
        {
            d.Dispose();
        }

        _owned.Clear();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private ConfigService NewConfig()
    {
        return new ConfigService(NewDb(), TestCache.Create());
    }

    private async Task SeedConfigAsync(string key, object value)
    {
        await using var db = NewDb();
        var json = JsonSerializer.Serialize(value);
        var existing = await db.ConfigEntries.FirstOrDefaultAsync(c => c.Key == key);
        if (existing is null)
        {
            db.ConfigEntries.Add(new ConfigEntry
            {
                Key = key,
                ValueJson = json,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }
        else
        {
            existing.ValueJson = json;
            existing.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        await db.SaveChangesAsync();
    }

    // ---------------- WebLoaderService ----------------

    [Test]
    public async Task WebLoader_UrlInvalida_LancaExcecao()
    {
        var loader = new WebLoaderService(new StubFactory());
        await Assert.ThatAsync(
            () => loader.LoadTextAsync("ftp://ex.com/arquivo"),
            Throws.TypeOf<WebLoaderException>());
        await Assert.ThatAsync(
            () => loader.LoadYoutubeTranscriptAsync("https://ex.com/nao-e-video"),
            Throws.TypeOf<WebLoaderException>());
    }

    [Test]
    public async Task WebLoader_FalhaHttp_LancaExcecao()
    {
        var loader = new WebLoaderService(new StubFactory(
            new FakeHandler(HttpStatusCode.InternalServerError, "boom", "text/plain")));
        await Assert.ThatAsync(
            () => loader.LoadTextAsync("http://93.184.216.34/pagina"),
            Throws.TypeOf<WebLoaderException>().With.Message.Contains("500"));
    }

    [Test]
    public async Task WebLoader_ConteudoMaiorQueMax_RejeitaPeloHeader()
    {
        var loader = new WebLoaderService(new StubFactory(
            new FakeHandler(HttpStatusCode.OK, "0123456789", "text/plain")))
        { MaxBytes = 5 };
        await Assert.ThatAsync(
            () => loader.LoadTextAsync("http://93.184.216.34/grande"),
            Throws.TypeOf<WebLoaderException>().With.Message.Contains("tamanho"));
    }

    [Test]
    public async Task WebLoader_StreamingAcimaDoMax_RejeitaNoLoop()
    {
        // Sem Content-Length → o limite só é detectado no loop de leitura.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 100)))
        { Position = 0 };
        var handler = new RouteHandler(_ =>
        {
            var content = new StreamContent(new NoLengthStream(stream));
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var loader = new WebLoaderService(new StubFactory(handler)) { MaxBytes = 10 };
        await Assert.ThatAsync(
            () => loader.LoadTextAsync("http://93.184.216.34/stream"),
            Throws.TypeOf<WebLoaderException>());
    }

    [Test]
    public async Task WebLoader_TextoEHtml_ConverteExtrai()
    {
        var html = "<html><head><script>var x=1;</script><style>.a{}</style></head>"
            + "<body><h1>Título</h1><p>parágrafo</p></body></html>";
        var loaderHtml = new WebLoaderService(new StubFactory(
            new FakeHandler(HttpStatusCode.OK, html, "text/html")));
        var text = await loaderHtml.LoadTextAsync("http://93.184.216.34/doc");
        Assert.That(text, Does.Contain("Título").And.Contain("parágrafo"));
        Assert.That(text, Does.Not.Contain("var x=1"));

        var loaderTxt = new WebLoaderService(new StubFactory(
            new FakeHandler(HttpStatusCode.OK, "texto puro", "text/plain")));
        Assert.That(await loaderTxt.LoadTextAsync("http://93.184.216.34/txt"),
            Is.EqualTo("texto puro"));
    }

    [Test]
    public async Task WebLoader_Youtube_FluxoCompletoESemLegenda()
    {
        var timedtext = "<transcript><text>Oi</text><text>mundo</text></transcript>";
        var page = "<html>player \"captionTracks\":[{\"baseUrl\":\"http://mock/timedtext\"}]"
            + " resto</html>";
        var handler = new RouteHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("timedtext")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(timedtext) }
                : new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(page) });
        var loader = new WebLoaderService(new StubFactory(handler));

        var transcript = await loader.LoadYoutubeTranscriptAsync(
            "https://www.youtube.com/watch?v=abc123xyz");
        Assert.That(transcript, Is.EqualTo("Oi mundo"));

        var semLegenda = new WebLoaderService(new StubFactory(
            new FakeHandler(HttpStatusCode.OK, "<html>sem tracks</html>", "text/html")));
        await Assert.ThatAsync(
            () => semLegenda.LoadYoutubeTranscriptAsync("https://youtu.be/abc123xyz"),
            Throws.TypeOf<WebLoaderException>().With.Message.Contains("transcrição"));
    }

    // ---------------- WebSearchService — engines ----------------

    private async Task<List<WebSearchResult>?> SearchWithAsync(
        object config, HttpMessageHandler handler)
    {
        await SeedConfigAsync("retrieval.config", config);
        var svc = new WebSearchService(new StubFactory(handler), NewConfig());
        return await svc.SearchAsync("devin", 3, default);
    }

    [Test]
    public async Task WebSearch_EngineNone_RetornaNull()
    {
        var r = await SearchWithAsync(new { engine = "none" }, new FakeHandler(
            HttpStatusCode.OK, "{}", "application/json"));
        Assert.That(r, Is.Null);
    }

    [Test]
    public async Task WebSearch_SemApiKey_RetornaListaVazia()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}", "application/json");
        foreach (var cfg in new object[]
        {
            new { engine = "tavily" },
            new { engine = "brave" },
            new { engine = "searxng" },
            new { engine = "jina" },
            new { engine = "exa" },
            new { engine = "kagi" },
            new { engine = "perplexity" },
            new { engine = "google_pse" },
        })
        {
            var r = await SearchWithAsync(cfg, handler);
            Assert.That(r, Is.Not.Null.And.Empty);
        }
    }

    [Test]
    public async Task WebSearch_Tavily_FormataResultados()
    {
        var json = "{\"results\":[{\"title\":\"T1\",\"url\":\"https://a.io\",\"content\":\"snip\"}]}";
        var r = await SearchWithAsync(
            new { engine = "tavily", tavilyApiKey = "k" },
            new FakeHandler(HttpStatusCode.OK, json, "application/json"));
        Assert.That(r, Has.Count.EqualTo(1));
        Assert.That(r![0].Title, Is.EqualTo("T1"));
    }

    [Test]
    public async Task WebSearch_Brave_FormataResultados()
    {
        var json = "{\"web\":{\"results\":[{\"title\":\"B1\",\"url\":\"https://b.io\",\"description\":\"d\"}]}}";
        var r = await SearchWithAsync(
            new { engine = "brave", braveApiKey = "k" },
            new FakeHandler(HttpStatusCode.OK, json, "application/json"));
        Assert.That(r, Has.Count.EqualTo(1));
        Assert.That(r![0].Url, Is.EqualTo("https://b.io"));
    }

    [Test]
    public async Task WebSearch_Searxng_FormataResultados()
    {
        var json = "{\"results\":[{\"title\":\"S1\",\"url\":\"https://s.io\",\"content\":\"c\"}]}";
        var r = await SearchWithAsync(
            new { engine = "searxng", searxngBaseUrl = "http://searx.local" },
            new FakeHandler(HttpStatusCode.OK, json, "application/json"));
        Assert.That(r, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task WebSearch_DuckDuckGo_AbstractERelated()
    {
        var json = "{\"AbstractText\":\"texto\",\"Heading\":\"H\",\"AbstractURL\":\"https://d.io\"," +
            "\"RelatedTopics\":[{\"Text\":\"rel\",\"FirstURL\":\"https://r.io\"}]}";
        var r = await SearchWithAsync(
            new { engine = "duckduckgo" },
            new FakeHandler(HttpStatusCode.OK, json, "application/json"));
        Assert.That(r, Is.Not.Null.And.Not.Empty);
        Assert.That(r![0].Title, Is.EqualTo("H"));
    }

    [Test]
    public async Task WebSearch_JinaExaKagiPerplexity_ParsesRespostas()
    {
        var rJina = await SearchWithAsync(
            new { engine = "jina", jinaApiKey = "k", jinaBaseUrl = "http://j.local" },
            new FakeHandler(HttpStatusCode.OK,
                "{\"data\":[{\"title\":\"J\",\"url\":\"https://j.io\",\"description\":\"d\"}]}",
                "application/json"));
        Assert.That(rJina, Has.Count.EqualTo(1));

        var rExa = await SearchWithAsync(
            new { engine = "exa", exaApiKey = "k", exaBaseUrl = "http://e.local" },
            new FakeHandler(HttpStatusCode.OK,
                "{\"results\":[{\"title\":\"E\",\"url\":\"https://e.io\",\"text\":\"t\"}]}",
                "application/json"));
        Assert.That(rExa, Has.Count.EqualTo(1));

        var rKagi = await SearchWithAsync(
            new { engine = "kagi", kagiApiKey = "k", kagiBaseUrl = "http://k.local" },
            new FakeHandler(HttpStatusCode.OK,
                "{\"data\":[{\"t\":0,\"title\":\"K\",\"url\":\"https://k.io\",\"snippet\":\"s\"}," +
                "{\"t\":1,\"title\":\"rel\",\"url\":\"https://r.io\",\"snippet\":\"x\"}]}",
                "application/json"));
        Assert.That(rKagi, Has.Count.EqualTo(1));

        var rPpx = await SearchWithAsync(
            new { engine = "perplexity", perplexityApiKey = "k",
                  perplexityBaseUrl = "http://p.local" },
            new FakeHandler(HttpStatusCode.OK,
                "{\"citations\":[\"https://p.io/artigo\",\"https://q.io/outro\"]}",
                "application/json"));
        Assert.That(rPpx, Is.Not.Null.And.Not.Empty);
    }

    // ---------------- OpenAiImageEngine ----------------

    private static ImagesConfig ImgCfg() =>
        new(true, "openai", "http://img.local", "k", "dall-e-3", "1024x1024", 30);

    [Test]
    public async Task ImageEngine_Generate_B64EUrl()
    {
        var b64 = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var pngBytes = new byte[] { 9, 9, 9 };
        var handler = new RouteHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("/images/generations"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{{\"data\":[{{\"b64_json\":\"{b64}\"}},{{\"url\":\"http://img.local/a.png\"}}]}}",
                        Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(pngBytes),
            };
        });
        var engine = new OpenAiImageEngine(new StubFactory(handler));
        var images = await engine.GenerateAsync(ImgCfg(), "um gato", 2, null, default);
        Assert.That(images, Has.Count.EqualTo(2));
        Assert.That(images[0], Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(images[1], Is.EqualTo(pngBytes));
    }

    [Test]
    public async Task ImageEngine_Generate_SemData_Lanca()
    {
        var engine = new OpenAiImageEngine(new StubFactory(
            new FakeHandler(HttpStatusCode.OK, "{}", "application/json")));
        await Assert.ThatAsync(
            () => engine.GenerateAsync(ImgCfg(), "x", 1, null, default),
            Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task ImageEngine_EditETest()
    {
        var b64 = Convert.ToBase64String(new byte[] { 7 });
        var handler = new RouteHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("/images/edits"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{{\"data\":[{{\"b64_json\":\"{b64}\"}}]}}",
                        Encoding.UTF8, "application/json"),
                };
            }
            return url.EndsWith("/models")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var engine = new OpenAiImageEngine(new StubFactory(handler));

        var edited = await engine.EditAsync(ImgCfg(), new byte[] { 1 }, "edit", null, default);
        Assert.That(edited, Is.EqualTo(new byte[] { 7 }));

        Assert.That((await engine.TestAsync(ImgCfg(), default)).Ok, Is.True);

        var engineDown = new OpenAiImageEngine(new StubFactory(new ThrowingHandler()));
        var (ok, _) = await engineDown.TestAsync(ImgCfg(), default);
        Assert.That(ok, Is.False);
    }

    // ---------------- EmbeddingService / ProviderService ----------------

    private async Task SeedConnectionsAsync() =>
        await SeedConfigAsync("connections", new
        {
            ollamaBaseUrls = new[] { "http://ollama.local" },
            ollamaApiKeys = new string[0],
            openAiBaseUrls = new[] { "http://oai.local" },
            openAiApiKeys = new[] { "key" },
        });

    [Test]
    public async Task Embedding_OllamaPrimeiro_OpenAiFallback_EIndisponivel()
    {
        await SeedConnectionsAsync();

        // Ollama responde embeddings no formato /api/embed.
        var svc = new EmbeddingService(new StubFactory(
            new FakeHandler(HttpStatusCode.OK,
                "{\"embeddings\":[[0.1,0.2,0.3]]}", "application/json")), NewConfig());
        var v = await svc.EmbedAsync("texto");
        Assert.That(v, Is.EqualTo(new[] { 0.1f, 0.2f, 0.3f }));

        // Tudo falha → null.
        var svcDown = new EmbeddingService(
            new StubFactory(new ThrowingHandler()), NewConfig());
        Assert.That(await svcDown.EmbedAsync("texto"), Is.Null);
        Assert.That(await svcDown.EmbedBatchAsync(["a", "b"]), Is.Null);
    }

    [Test]
    public async Task Embedding_OpenAiQuandoOllamaFalha()
    {
        await SeedConnectionsAsync();
        var handler = new RouteHandler(req =>
            req.RequestUri!.AbsoluteUri.Contains("/api/embed")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"data\":[{\"embedding\":[1.0,2.0]}]}",
                        Encoding.UTF8, "application/json"),
                });
        var svc = new EmbeddingService(new StubFactory(handler), NewConfig());
        var v = await svc.EmbedAsync("texto");
        Assert.That(v, Is.EqualTo(new[] { 1.0f, 2.0f }));
    }

    [Test]
    public async Task ProviderService_ListModels_PorTipoEIndice()
    {
        await SeedConnectionsAsync();
        var handler = new RouteHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("/api/tags"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"models\":[{\"model\":\"llama3\",\"name\":\"llama3\"}]}",
                        Encoding.UTF8, "application/json"),
                };
            }
            if (url.Contains("/models"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"data\":[{\"id\":\"gpt-x\"}]}", Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var mc1 = TestCache.Create();
        var svc = new ProviderService(new StubFactory(handler), NewConfig(),
            NullLogger<ProviderService>.Instance, mc1,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        var ollama = await svc.ListModelsForConnectionAsync("ollama", 0);
        Assert.That(ollama.Select(m => m.Id), Does.Contain("llama3"));

        var openai = await svc.ListModelsForConnectionAsync("openai", 0);
        Assert.That(openai.Select(m => m.Id), Does.Contain("gpt-x"));

        Assert.That(await svc.ListModelsForConnectionAsync("openai", 9), Is.Empty);
        Assert.That(await svc.ListModelsForConnectionAsync("outro", 0), Is.Not.Null);
    }

    // ---------------- PTY / TerminalSessionManager edges ----------------

    [Test]
    public void PtySession_ShellQuote_EscapaAspas()
    {
        Assert.That(PtySession.ShellQuote("a b"), Is.EqualTo("'a b'"));
        Assert.That(PtySession.ShellQuote("o'k"), Is.EqualTo("'o'\"'\"'k'"));
    }

    [Test]
    public async Task PtySession_ResizeInvalido_Noop()
    {
        var pty = new PtySession(Path.GetTempPath(), NullLogger<PtySession>.Instance);
        await pty.ResizeAsync(0, 10);
        await pty.ResizeAsync(1000, 1000);
        await pty.DisposeAsync();
    }

    [Test]
    public async Task PtySession_ExecutavelInexistente_FalhaNoStart()
    {
        var pty = new PtySession(Path.GetTempPath(), NullLogger<PtySession>.Instance,
            executableLocator: _ => null);
        Assert.Throws<InvalidOperationException>(() => pty.Start());
        await pty.DisposeAsync();
    }

    // ---------------- helpers ----------------

    private sealed class StubFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler ?? new FakeHandler(HttpStatusCode.OK, "{}", "application/json"));
    }

    private sealed class FakeHandler(HttpStatusCode status, string body, string mediaType)
        : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _pending = [];

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var response in _pending)
                {
                    response.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) 
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, mediaType),
            };
            _pending.Add(response);
            return Task.FromResult(response);
        }
    }

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(route(request));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    /// <summary>Stream que não reporta Length — força o path de leitura incremental.</summary>
    private sealed class NoLengthStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
