using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OpenWebUI.Client.Components;
using OpenWebUI.Client.Services;

namespace OpenWebUI.Client.Tests;

/// <summary>
/// SPEC-20261104-chat-hash-collections RF-001/003/004: autocomplete
/// <c>#knowledge</c> no composer — dropdown de coleções RAG + arquivos do
/// usuário, seleção insere <c>#nome</c>, Esc fecha, <c>oi#</c> não dispara.
/// </summary>
[TestFixture]
public sealed class ChatHashTests
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

    private static HttpResponseMessage Route(HttpRequestMessage req)
    {
        var path = req.RequestUri?.AbsolutePath ?? "";
        if (path == "/api/v1/knowledge")
        {
            return Json(new object[]
            {
                new { id = "c1", name = "backend-docs", description = "docs da api", fileCount = 2, createdAt = 1L },
                new { id = "c2", name = "frontend-guide", description = (string?)null, fileCount = 1, createdAt = 1L },
            });
        }
        if (path == "/api/v1/files/")
        {
            return Json(new object[]
            {
                new { id = "f1", filename = "api-spec.md", contentType = "text/markdown", size = 10L, hasText = true, createdAt = 1L },
            });
        }
        if (path.Contains("/tree")) return Json(new { path = "", entries = Array.Empty<object>(), nextCursor = default(string), truncated = false });
        if (path.EndsWith("/workspace/repo")) return Json(new { repo = "test/repo", branch = "main", dir = "repos/x" });
        if (path.Contains("/prompts") || path.Contains("/tools") || path.Contains("/jobs")
            || path.Contains("/mcps") || path.Contains("/commands") || path.Contains("/skills")
            || path.Contains("/references"))
        {
            return Json(Array.Empty<object>());
        }
        if (path.EndsWith("/api/models")) return Json(new { data = Array.Empty<object>() });
        if (path.EndsWith("/models/")) return Json(Array.Empty<object>());
        return Json(new { });
    }

    private static BunitContext Setup()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new FakeHandler(Route)) { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(http);
        var l10n = new LocalizationService(http, ctx.JSInterop.JSRuntime);
        var storage = new BrowserStorage(ctx.JSInterop.JSRuntime);
        var auth = new AuthService(http, storage, l10n);
        var api = new ApiService(http, auth);
        ctx.Services.AddSingleton(api);
        ctx.Services.AddSingleton(l10n);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(storage);
        ctx.Services.AddSingleton(new ChatStreamService(http, auth));
        ctx.Services.AddSingleton(new ChatListState());
        ctx.Services.AddSingleton(new RealtimeService());
        ctx.Services.AddSingleton(new DialogService());
        ctx.Services.AddSingleton(new ToastService());
        ctx.Services.AddSingleton(new IdeTestRunService(api));
        ctx.Services.AddSingleton(new AttentionInboxState(api));
        return ctx;
    }

    [Test]
    public void Hash_Sozinho_ListaColecoesEArquivos()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        textarea.Input("#");
        var options = cut.WaitForElements(".mention-list [role=option]", TimeSpan.FromSeconds(5));

        var texts = options.Select(o => o.TextContent).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(texts, Has.Some.Contains("backend-docs"));
            Assert.That(texts, Has.Some.Contains("api-spec.md"));
            Assert.That(options[0].TextContent, Does.Contain("collection"));
        });
    }

    [Test]
    public void Hash_Selecao_InsereTokenNome()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        textarea.Input("#back");
        var option = cut.WaitForElement(".mention-list [role=option]", TimeSpan.FromSeconds(5));
        option.Click();

        cut.WaitForAssertion(() =>
            Assert.That(textarea.GetAttribute("value") ?? "",
                Is.EqualTo("#backend-docs ")), TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Hash_Esc_FechaSemLimparInput()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        textarea.Input("#back");
        cut.WaitForElement(".mention-list [role=option]", TimeSpan.FromSeconds(5));
        textarea.KeyDown("Escape");

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.FindAll(".mention-list [role=option]"), Is.Empty);
            Assert.That(textarea.GetAttribute("value") ?? "", Is.EqualTo("#back"));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Hash_DentroDePalavra_NaoAbre()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        textarea.Input("oi#foo");
        System.Threading.Thread.Sleep(400);
        Assert.That(cut.FindAll(".mention-list [role=option]"), Is.Empty);
    }
}
