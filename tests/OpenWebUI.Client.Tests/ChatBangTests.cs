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
/// SPEC-20261104-chat-bang-trigger RF-001/002/003: autocomplete <c>!skill</c>
/// no composer — dropdown debounced do catálogo efetivo (só habilitadas),
/// badge de origem, seleção injeta o corpo no composer, Esc fecha,
/// <c>oi!</c> não dispara.
/// </summary>
[TestFixture]
public sealed class ChatBangTests
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

    private static object[] Skills() =>
    [
        new
        {
            id = (string?)"s1", name = "write-specs", description = (string?)"escreve spec",
            source = "global", enabled = true, enabledForMe = true,
            disabledByAdmin = false, editable = false,
            content = (string?)"CORPO-WRITE-SPECS",
        },
        new
        {
            id = (string?)"s2", name = "denied-skill", description = (string?)null,
            source = "global", enabled = false, enabledForMe = false,
            disabledByAdmin = true, editable = false, content = (string?)"x",
        },
        new
        {
            id = (string?)"s3", name = "off-skill", description = (string?)null,
            source = "personal", enabled = true, enabledForMe = false,
            disabledByAdmin = false, editable = true, content = (string?)"x",
        },
        new
        {
            id = (string?)null, name = "repo-skill", description = (string?)"do repo",
            source = "repo", enabled = true, enabledForMe = true,
            disabledByAdmin = false, editable = false, content = (string?)null,
        },
    ];

    private static HttpResponseMessage Route(HttpRequestMessage req)
    {
        var path = req.RequestUri?.AbsolutePath ?? "";
        if (path == "/api/v1/customize/skills") return Json(new { data = Skills() });
        if (path.Contains("/repo/skills/repo-skill") && !path.EndsWith("/enabled"))
            return Json(new { name = "repo-skill", path = ".agents/skills/repo-skill/SKILL.md", content = "CORPO-REPO-SKILL" });
        if (path.Contains("/tree")) return Json(new { path = "", entries = Array.Empty<object>(), nextCursor = default(string), truncated = false });
        if (path.EndsWith("/workspace/repo")) return Json(new { repo = "test/repo", branch = "main", dir = "repos/x" });
        if (path.Contains("/prompts") || path.Contains("/tools") || path.Contains("/jobs")
            || path.Contains("/mcps") || path.Contains("/commands")
            || path.Contains("/references"))
        {
            return Json(Array.Empty<object>());
        }
        if (path.EndsWith("/api/models")) return Json(new { data = Array.Empty<object>() });
        if (path.EndsWith("/models/")) return Json(Array.Empty<object>());
        return Json(new { });
    }

    private static BunitContext Setup(
        Func<HttpRequestMessage, HttpResponseMessage>? route = null)
    {
        var ctx = new BunitContext();
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
    public void Bang_Sozinho_AbreDropdownSoComHabilitadas()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        textarea.Input("!");
        var options = cut.WaitForElements(".mention-list [role=option]", TimeSpan.FromSeconds(5));

        var texts = options.Select(o => o.TextContent).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(texts, Has.Some.Contains("write-specs"));
            Assert.That(texts, Has.Some.Contains("repo-skill"));
            // deny admin e tombstone nunca listam (RF-001).
            Assert.That(texts, Has.None.Contains("denied-skill"));
            Assert.That(texts, Has.None.Contains("off-skill"));
        });
    }

    [Test]
    public void Bang_FiltroQuery_FiltraPorNome()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        textarea.Input("!wri");
        var options = cut.WaitForElements(".mention-list [role=option]", TimeSpan.FromSeconds(5));

        Assert.That(options, Has.Count.EqualTo(1));
        Assert.That(options[0].TextContent, Does.Contain("write-specs"));
    }

    [Test]
    public void Bang_SelecaoDb_InjetaContentDoCatalogo()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        textarea.Input("!write");
        var option = cut.WaitForElement(".mention-list [role=option]", TimeSpan.FromSeconds(5));
        option.Click();

        cut.WaitForAssertion(() =>
            Assert.That(textarea.GetAttribute("value") ?? "",
                Does.Contain("CORPO-WRITE-SPECS")), TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Bang_SelecaoRepo_BuscaCorpoEInjeta()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        textarea.Input("!repo");
        var option = cut.WaitForElement(".mention-list [role=option]", TimeSpan.FromSeconds(5));
        option.Click();

        cut.WaitForAssertion(() =>
            Assert.That(textarea.GetAttribute("value") ?? "",
                Does.Contain("CORPO-REPO-SKILL")), TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Bang_Esc_FechaSemLimparInput()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        textarea.Input("!wri");
        cut.WaitForElement(".mention-list [role=option]", TimeSpan.FromSeconds(5));
        textarea.KeyDown("Escape");

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.FindAll(".mention-list [role=option]"), Is.Empty);
            Assert.That(textarea.GetAttribute("value") ?? "", Is.EqualTo("!wri"));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Bang_Enter_SelecionaPrimeiroItem()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        textarea.Input("!write");
        cut.WaitForElement(".mention-list [role=option]", TimeSpan.FromSeconds(5));
        textarea.KeyDown("Enter");

        cut.WaitForAssertion(() =>
            Assert.That(textarea.GetAttribute("value") ?? "",
                Does.Contain("CORPO-WRITE-SPECS")), TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task Bang_DentroDePalavra_NaoAbre()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatView>();
        var textarea = cut.Find("textarea");

        textarea.Input("oi!");

        // Sem dropdown — o regex exige boundary (^ ou espaço) antes do !.
        await Task.Delay(400);
        Assert.That(cut.FindAll(".mention-list [role=option]"), Is.Empty);
    }
}
