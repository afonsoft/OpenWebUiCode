using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes de hosting PWA (slice pwa-offline): documento revalida e assets do
/// manifest/service worker são servidos.
/// </summary>
[TestFixture, IsolateEnvironment]
public class PwaTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-pwa-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    [Test]
    public async Task Index_ServeNoCache()
    {
        var response = await _client.GetAsync("/");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.CacheControl?.ToString(), Does.Contain("no-cache"));
    }

    [Test]
    public async Task RotaDaSpa_ServeDocumentoComNoCache()
    {
        var response = await _client.GetAsync("/admin");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.CacheControl?.ToString(), Does.Contain("no-cache"));
    }

    [Test]
    public async Task Index_EmiteContentSecurityPolicyDoBootWasm()
    {
        // AC SPEC-20261010-app-csp-header: GET / declara um CSP real —
        // worker-src 'self' para SW/py-worker, connect-src cobrindo
        // API/SSE/ws, e wasm-unsafe-eval para o boot do dotnet.wasm.
        var response = await _client.GetAsync("/");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(
            response.Headers.TryGetValues("Content-Security-Policy", out var values),
            Is.True, "GET / não emitiu Content-Security-Policy");
        var csp = string.Join(' ', values!);
        Assert.Multiple(() =>
        {
            Assert.That(csp, Does.Contain("default-src 'self'"));
            Assert.That(csp, Does.Contain("worker-src 'self'"));
            Assert.That(csp, Does.Contain("connect-src 'self'"));
            Assert.That(csp, Does.Contain("wss:"));
            Assert.That(csp, Does.Contain("wasm-unsafe-eval"));
        });
    }

    [Test]
    public async Task Index_CspNaoContemUnsafeEval()
    {
        // S7039 (SPEC-20261031-sonar-vuln-code): 'unsafe-eval' não é
        // necessário ao boot WASM — 'wasm-unsafe-eval' (CSP3) basta e não
        // abre eval() de JS arbitrário.
        var response = await _client.GetAsync("/");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var csp = response.Headers.TryGetValues("Content-Security-Policy", out var v)
            ? string.Join(' ', v) : string.Empty;
        Assert.Multiple(() =>
        {
            Assert.That(csp, Does.Contain("wasm-unsafe-eval"));
            Assert.That(csp, Does.Not.Contain("'unsafe-eval'"));
        });
    }

    [Test]
    public async Task AssetsNaoHtml_NaoEmitemContentSecurityPolicy()
    {
        // O CSP é política de documento: estampar em todo response (ex.: o
        // proxy /preview/{port}) imporia nossa política a apps terceiros.
        var manifest = await _client.GetAsync("/manifest.webmanifest");

        Assert.That(manifest.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(
            manifest.Headers.Contains("Content-Security-Policy")
                || (manifest.Content.Headers.Contains("Content-Security-Policy")),
            Is.False);
    }

    [Test]
    public async Task Manifest_ServiceWorker_EIcones_SaoServidos()
    {
        var manifest = await _client.GetAsync("/manifest.webmanifest");
        Assert.That(manifest.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await manifest.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("\"standalone\"").And.Contain("icon-192.png"));

        var sw = await _client.GetAsync("/service-worker.js");
        Assert.That(sw.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(sw.Headers.CacheControl?.ToString(), Does.Contain("no-cache"));

        var icon = await _client.GetAsync("/assets/icons/icon-512.png");
        Assert.That(icon.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Index_RegistraServiceWorker_ComCatchNoErro()
    {
        // Covers AC: SPEC-20261010-sw-register-observability — o register do SW
        // não pode ser fire-and-forget: falha de registro precisa aparecer no
        // console (.catch) e sucesso em debug (.then) para diagnóstico.
        var html = await _client.GetStringAsync("/");

        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("navigator.serviceWorker.register('service-worker.js')"));
            Assert.That(html, Does.Contain(".catch("));
            Assert.That(html, Does.Contain("SW registration failed"));
        });
    }

    [Test]
    public async Task ServiceWorker_SeparaCacheImutavelDoShellMutavel()
    {
        // Regressão do bug pós-E16: um único cache-first servia bundle WASM
        // velho entre deploys. O SW precisa isolar o cache de framework
        // (fingerprinted, cache-first) do shell mutável (network-first).
        var sw = await _client.GetStringAsync("/service-worker.js");

        Assert.Multiple(() =>
        {
            Assert.That(sw, Does.Contain("openwebui-shell-v"));
            Assert.That(sw, Does.Contain("openwebui-fw-v"));
            Assert.That(sw, Does.Contain("networkFirst"));
            Assert.That(sw, Does.Contain("/framework-assets/"));
            Assert.That(sw, Does.Contain("/_framework/blazor.webassembly.js"));
            // cache.put consome o body e clone() falha depois que o browser
            // leu — cada put precisa de um clone criado síncrono (E2E pós-#269).
            Assert.That(sw, Does.Contain("cache.put('/', shellCopy)"));
        });
    }
}
