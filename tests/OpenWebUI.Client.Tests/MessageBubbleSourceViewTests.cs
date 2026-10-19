using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Client.Components;
using OpenWebUI.Client.Services;

namespace OpenWebUI.Client.Tests;

/// <summary>
/// RF-001/RF-002 (SPEC-20261017-chat-markdown-source-view): bolha de assistente
/// alterna entre markdown renderizado (View) e fonte cru (Source), e o botão
/// copiar sempre envia o markdown original para o clipboard com feedback visual.
/// </summary>
[TestFixture]
public sealed class MessageBubbleSourceViewTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> route)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(route(request));
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(body,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            Encoding.UTF8, "application/json"),
    };

    private HttpClient? _http;

    [TearDown]
    public void TearDown() => _http?.Dispose();

    private BunitContext Setup()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        // O client vive no field e sai no TearDown (o ServiceProvider do
        // ctx também o descarta — double-dispose de HttpClient é seguro).
        _http = new HttpClient(new FakeHandler(_ => Json(new
        {
            features = new { enableCodeExecution = false, enableImageGeneration = false },
        })))
        {
            BaseAddress = new Uri("http://localhost/"),
        };
        ctx.Services.AddSingleton(_http);
        var l10n = new LocalizationService(_http, ctx.JSInterop.JSRuntime);
        var storage = new BrowserStorage(ctx.JSInterop.JSRuntime);
        var auth = new AuthService(_http, storage, l10n);
        ctx.Services.AddSingleton(new ApiService(_http, auth));
        ctx.Services.AddSingleton(l10n);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(storage);
        ctx.Services.AddSingleton(new MarkdownService());
        return ctx;
    }

    private static ChatMessageModel Assistant(string content) =>
        new("m1", "assistant", content, "model", 0);

    private static IRenderedComponent<MessageBubble> Render(
        BunitContext ctx, ChatMessageModel msg) =>
        ctx.Render<MessageBubble>(p => p
            .Add(x => x.Message, msg)
            .Add(x => x.ShowActions, true));

    [Test]
    public void Source_Toggle_MostraMarkdownCru()
    {
        using var ctx = Setup();
        var cut = Render(ctx, Assistant("**negrito** e `code`"));

        var toggle = cut.Find("button[aria-pressed]");
        Assert.That(toggle.GetAttribute("aria-pressed"), Is.EqualTo("false"));
        Assert.That(cut.FindAll("pre"), Is.Empty, "modo View não mostra fonte crua");
        Assert.That(cut.Find("strong"), Is.Not.Null, "modo View renderiza markdown");

        toggle.Click();

        var pre = cut.WaitForElement("pre");
        Assert.That(pre.TextContent, Does.Contain("**negrito**"),
            "modo Source mostra o markdown original, não o HTML");
        Assert.That(toggle.GetAttribute("aria-pressed"), Is.EqualTo("true"));
    }

    [Test]
    public void Source_Toggle_RestauraRenderizacao()
    {
        using var ctx = Setup();
        var cut = Render(ctx, Assistant("**negrito**"));
        var toggle = cut.Find("button[aria-pressed]");

        toggle.Click();
        Assert.That(cut.FindAll("pre"), Has.Count.EqualTo(1));
        toggle.Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll("pre"), Is.Empty);
            Assert.That(cut.Find("strong"), Is.Not.Null);
        });
    }

    [Test]
    public void Copy_EnviaMarkdownCruParaClipboard()
    {
        using var ctx = Setup();
        const string md = "**negrito** com `code`";
        var cut = Render(ctx, Assistant(md));

        cut.FindAll("button")
            .First(b => b.GetAttribute("aria-pressed") is null)
            .Click();

        var invoke = ctx.JSInterop.VerifyInvoke("openwebui.copyText");
        Assert.That(invoke.Arguments[0], Is.EqualTo(md),
            "copiar envia o markdown cru (DisplayContent), nunca o HTML renderizado");
    }
}
