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
        var svc = new MarkdownService();
        Assert.That(svc.ToHtml("**ola**"), Does.Contain("<strong>ola</strong>"));
    }

    [Test]
    public void ToHtml_NuloOuVazio_RetornaVazio()
    {
        var svc = new MarkdownService();
        Assert.That(svc.ToHtml(null), Is.EqualTo(string.Empty));
        Assert.That(svc.ToHtml("   "), Is.EqualTo(string.Empty));
    }

    [Test]
    public void ToHtml_RemoveScriptEAtributosPerigosos()
    {
        var svc = new MarkdownService();
        var html = svc.ToHtml("<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>");
        Assert.That(html, Does.Not.Contain("<script"));
        Assert.That(html, Does.Not.Contain("onerror"));
    }

    [Test]
    public void ToHtml_LinkJavascript_EhNeutralizado()
    {
        var svc = new MarkdownService();
        var html = svc.ToHtml("[x](javascript:alert(1))");
        Assert.That(html, Does.Not.Contain("javascript:"));
    }

    [Test]
    public void ToHtml_RenderizaBlocoDeCodigo()
    {
        var svc = new MarkdownService();
        Assert.That(svc.ToHtml("```\nvar x = 1;\n```"), Does.Contain("<code"));
    }

    [Test]
    public void ToHtml_LinkExterno_AbreEmNovaAba()
    {
        var svc = new MarkdownService();
        var html = svc.ToHtml("[doc](https://example.com/x)");
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
        var svc = new MarkdownService();
        var html = svc.ToHtml("[topo](#secao)");
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
