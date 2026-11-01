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
/// RF-001 (SPEC-20261009-ide-mentions-tests): autocomplete <c>@path</c> no
/// composer — dropdown debounced com top-20 dirs-primeiro, seleção vira
/// chip atômico, Backspace no input vazio remove o chip inteiro, Esc fecha.
/// </summary>
[TestFixture]
public sealed class ChatMentionTests
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

    private static object Tree() => new
    {
        path = "",
        entries = new object[]
        {
            new { name = "src", path = "src", type = "dir", size = default(long?), collapsed = false },
            new { name = "README.md", path = "README.md", type = "file", size = 10L, collapsed = false },
            new { name = "render.cs", path = "src/render.cs", type = "file", size = 10L, collapsed = false },
            new { name = "zzz.txt", path = "zzz.txt", type = "file", size = 1L, collapsed = false },
        },
        nextCursor = default(string),
        truncated = false,
    };

    private static HttpResponseMessage Route(HttpRequestMessage req)
    {
        var path = req.RequestUri?.AbsolutePath ?? "";
        if (path.Contains("/tree")) return Json(Tree());
        if (path.EndsWith("/workspace/repo")) return Json(new { repo = "test/repo", branch = "main", dir = "repos/x" });
        if (path.Contains("/prompts") || path.Contains("/tools") || path.Contains("/jobs")
            || path.Contains("/mcps") || path.Contains("/skills") || path.Contains("/commands"))
        {
            return Json(Array.Empty<object>());
        }
        if (path.EndsWith("/api/models")) return Json(new { data = Array.Empty<object>() });
        if (path.EndsWith("/models/")) return Json(Array.Empty<object>());
        return Json(new { });
    }

    private static Bunit.BunitContext Setup(
        Func<HttpRequestMessage, HttpResponseMessage>? route = null)
    {
        var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new FakeHandler(route ?? Route)) { BaseAddress = new Uri("http://localhost/") };
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
    public void Mention_AtQuery_MostraDropdownDirsPrimeiro()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        textarea.Input("explain @re");
        var options = cut.WaitForElements(".mention-list [role=option]", TimeSpan.FromSeconds(5));

        Assert.That(options, Has.Count.GreaterThan(0));
        Assert.That(options.Select(o => o.TextContent),
            Has.Some.Contains("README.md").And.Some.Contains("render.cs"));
    }

    [Test]
    public void Mention_Selecao_InsereChipELimpaToken()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        textarea.Input("explain @read");
        var option = cut.WaitForElement(".mention-list [role=option]", TimeSpan.FromSeconds(5));
        option.Click();

        var chip = cut.WaitForElement(".mention-chip", TimeSpan.FromSeconds(3));
        Assert.Multiple(() =>
        {
            Assert.That(chip.TextContent, Does.Contain("README.md"));
            Assert.That(textarea.GetAttribute("value") ?? "",
                Does.Not.Contain("@read"), "token @query deve sair do input");
        });
    }

    [Test]
    public void Mention_BackspaceInputVazio_RemoveChipInteiro()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        textarea.Input("@read");
        var option = cut.WaitForElement(".mention-list [role=option]", TimeSpan.FromSeconds(5));
        option.Click();
        cut.WaitForElement(".mention-chip", TimeSpan.FromSeconds(3));

        textarea.KeyDown("Backspace");
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".mention-chip"), Is.Empty), TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Mention_Esc_FechaDropdown()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        textarea.Input("@re");
        cut.WaitForElement(".mention-list [role=option]", TimeSpan.FromSeconds(5));

        textarea.KeyDown("Escape");
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".mention-list"), Is.Empty), TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Mention_Refs_SaoSugeridasECacheadas()
    {
        var refsCalls = 0;
        HttpResponseMessage route(HttpRequestMessage req)
        {
            if ((req.RequestUri?.AbsolutePath ?? "").Contains("/references"))
            {
                refsCalls++;
                return Json(new object[]
                {
                    new
                    {
                        id = "r1", alias = "myref", source = "https://git.example/r",
                        branch = (string?)null, description = "minha ref",
                        hidden = false, lastFetchedAt = 0L, updatedAt = 0L,
                    },
                });
            }
            return Route(req);
        }

        using var ctx = Setup(route);
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        // 1ª lookup: refs entram na lista como "ref:alias" (badge ref)
        textarea.Input("@my");
        var option = cut.WaitForElement(".mention-list [role=option]", TimeSpan.FromSeconds(5));
        Assert.That(option.TextContent, Does.Contain("myref"));

        // 2ª lookup ("@zzz" casa só arquivo): refs vêm do cache — sem novo fetch
        textarea.Input("@zzz");
        cut.WaitForState(() =>
            cut.FindAll(".mention-list [role=option]")
                .Any(o => o.TextContent.Contains("zzz.txt")),
            TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(
                cut.FindAll(".mention-list [role=option]")
                    .Any(o => o.TextContent.Contains("myref")),
                Is.False, "ref filtrada pela query @zzz não deve aparecer");
            Assert.That(refsCalls, Is.EqualTo(1), "2ª lookup deve reusar o cache de refs");
        });
    }
}
