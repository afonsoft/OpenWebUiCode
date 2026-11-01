using Microsoft.JSInterop;
using OpenWebUI.Client.Services;

namespace OpenWebUI.Client.Tests;

/// <summary>Testes de borda dos serviços de apresentação do cliente.</summary>
[TestFixture]
public class ClientServiceEdgeTests
{
    [Test]
    public void ToHtml_ConverteNegrito()
    {
        Assert.That(MarkdownService.ToHtml("**ola**"), Does.Contain("<strong>ola</strong>"));
    }

    [Test]
    public void ToHtml_NuloOuVazio_RetornaVazio()
    {
        Assert.That(MarkdownService.ToHtml(null), Is.EqualTo(string.Empty));
        Assert.That(MarkdownService.ToHtml("   "), Is.EqualTo(string.Empty));
    }

    [Test]
    public void ToHtml_RemoveScriptEAtributosPerigosos()
    {
        var html = MarkdownService.ToHtml("<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>");
        Assert.That(html, Does.Not.Contain("<script"));
        Assert.That(html, Does.Not.Contain("onerror"));
    }

    [Test]
    public void ToHtml_LinkJavascript_EhNeutralizado()
    {
        var html = MarkdownService.ToHtml("[x](javascript:alert(1))");
        Assert.That(html, Does.Not.Contain("javascript:"));
    }

    [Test]
    public void ToHtml_RenderizaBlocoDeCodigo()
    {
        Assert.That(MarkdownService.ToHtml("```\nvar x = 1;\n```"), Does.Contain("<code"));
    }

    [Test]
    public void ToHtml_LinkExterno_AbreEmNovaAba()
    {
        var html = MarkdownService.ToHtml("[doc](https://example.com/x)");
        Assert.Multiple(() =>
        {
            Assert.That(html, Does.Contain("target=\"_blank\""));
            Assert.That(html, Does.Contain("rel=\"noopener noreferrer\""));
            Assert.That(html, Does.Contain("href=\"https://example.com/x\""));
        });
    }

    [Test]
    public void ToHtml_AncoraInterna_PermaneceNaMesmaAba()
    {
        var html = MarkdownService.ToHtml("[topo](#secao)");
        Assert.That(html, Does.Not.Contain("target=\"_blank\""));
        Assert.That(html, Does.Contain("href=\"#secao\""));
    }

    [Test]
    public async Task Theme_SemPreferenciaSalva_CaiEmLight()
    {
        var js = new FakeJs();
        var theme = new ThemeService(js, new BrowserStorage(js));
        await theme.InitializeAsync();
        Assert.That(theme.Current, Is.EqualTo("light"));
    }

    [Test]
    public async Task Theme_ValorSalvoEInvalido_VoltaParaLight()
    {
        var js = new FakeJs { ["webui.theme"] = "roxo" };
        var theme = new ThemeService(js, new BrowserStorage(js));
        await theme.InitializeAsync();
        Assert.That(theme.Current, Is.EqualTo("light"));
    }

    [Test]
    public async Task Theme_SetAsync_PersisteAplicaEDisparaChanged()
    {
        var js = new FakeJs();
        var theme = new ThemeService(js, new BrowserStorage(js));
        var changed = 0;
        theme.Changed += () => changed++;

        await theme.SetAsync("light");

        Assert.Multiple(() =>
        {
            Assert.That(theme.Current, Is.EqualTo("light"));
            Assert.That(js["webui.theme"], Is.EqualTo("light"));
            Assert.That(changed, Is.EqualTo(1));
            Assert.That(js.ThemeCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Theme_SavedSystem_EhRespeitado()
    {
        var js = new FakeJs { ["webui.theme"] = "system" };
        var theme = new ThemeService(js, new BrowserStorage(js));
        await theme.InitializeAsync();
        Assert.That(theme.Current, Is.EqualTo("system"));
    }

    [Test]
    public void ChatListState_NotifyChanged_DisparaAssinantes()
    {
        var state = new ChatListState();
        var fired = 0;
        state.Changed += () => fired++;
        state.NotifyChanged();
        Assert.That(fired, Is.EqualTo(1));
    }

    // S7044 (SPEC-20261031-sonar-vuln-code): validação de uri em
    // CreateRequest deve decodificar antes de checar traversal — senão
    // %2e%2e e backslash contornam o guard e o Uri resolver escapa da
    // origem.
    [TestCase("/%2e%2e/admin")]
    [TestCase("/%2E%2E/admin")]
    [TestCase("/%2e%2e%2fadmin")]
    [TestCase("/a/%2e./b")]
    [TestCase("/.%2e/b")]
    [TestCase("/a\\..\\b")]
    [TestCase("/a%5c..%5cb")]
    [TestCase("//evil.example/x")]
    [TestCase("http://evil.example/x")]
    [TestCase("../admin")]
    [TestCase("/a/../b")]
    public void CreateRequest_TraversalOuAbsoluta_Rejeita(string uri)
    {
        var auth = NewAuthService();
        Assert.Throws<ArgumentException>(() => auth.CreateRequest(HttpMethod.Get, uri));
    }

    [Test]
    public void CreateRequest_PathRelativoComQuery_Passa()
    {
        var auth = NewAuthService();
        using var req = auth.CreateRequest(HttpMethod.Get, "/api/v1/files?x=1#f");
        Assert.That(req.RequestUri!.OriginalString, Is.EqualTo("/api/v1/files?x=1#f"));
    }

    private static AuthService NewAuthService()
    {
        var js = new FakeJs();
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        return new AuthService(http, new BrowserStorage(js), new LocalizationService(http, js));
    }

    private sealed class FakeJs : IJSRuntime
    {
        private readonly Dictionary<string, string?> _stored = [];

        public string? this[string key]
        {
            get => _stored.TryGetValue(key, out var v) ? v : null;
            set => _stored[key] = value;
        }

        public int ThemeCalls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "localStorage.getItem" && args?[0] is string key)
            {
                return new ValueTask<TValue>((TValue)(object?)_stored.GetValueOrDefault(key)!);
            }

            if (identifier == "localStorage.setItem" && args is [string k, string newValue])
            {
                _stored[k] = newValue;
                return new ValueTask<TValue>();
            }

            if (identifier == "openwebui.setTheme")
            {
                ThemeCalls++;
                return new ValueTask<TValue>();
            }

            return new ValueTask<TValue>();
        }
    }
}
