using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OpenWebUI.Client.Components;
using OpenWebUI.Client.Services;

namespace OpenWebUI.Client.Tests;

/// <summary>
/// SPEC-20261010-model-visibility-settings: aba Models do SettingsModal —
/// grupos por provider, toggles role="switch", busca, enable/disable-all e
/// persistência de disabledModels via settings do usuário.
/// </summary>
[TestFixture]
public sealed class ModelVisibilityTests
{
    private sealed class TestNav : NavigationManager
    {
        public TestNav() => Initialize("http://localhost/", "http://localhost/");
    }

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

    private static readonly object Catalog = new
    {
        data = new object[]
        {
            new { id = "fake:1", name = "fake:1", provider = "ollama", ownedBy = "ollama", enabled = true },
            new { id = "gpt-x", name = "GPT X", provider = "openai", ownedBy = "openai", enabled = false },
        },
    };

    private static BunitContext Setup(out List<string> settingsPosts)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var posts = new List<string>();

        var http = new HttpClient(new FakeHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path == "/api/v1/models/all")
            {
                return Json(Catalog);
            }
            if (req.Method == HttpMethod.Get && path == "/api/v1/users/user/settings")
            {
                return Json(new { disabledModels = new[] { "openai:gpt-x" } });
            }
            if (req.Method == HttpMethod.Post && path == "/api/v1/users/user/settings/update")
            {
                posts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return Json(new { });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        { BaseAddress = new Uri("http://localhost/") };

        settingsPosts = posts;
        ctx.Services.AddSingleton(http);
        var l10n = new LocalizationService(http, ctx.JSInterop.JSRuntime);
        var auth = new AuthService(http, new BrowserStorage(ctx.JSInterop.JSRuntime), l10n);
        var api = new ApiService(http, auth);
        var toast = new ToastService();
        var realtime = new RealtimeService();
        var nav = new TestNav();
        ctx.Services.AddSingleton(l10n);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(api);
        ctx.Services.AddSingleton(new ThemeService(ctx.JSInterop.JSRuntime,
            new BrowserStorage(ctx.JSInterop.JSRuntime)));
        ctx.Services.AddSingleton(new ChatNotificationsService(
            realtime, nav, ctx.JSInterop.JSRuntime, api, auth, toast));
        return ctx;
    }

    private static IRenderedComponent<SettingsModal> OpenModelsTab(BunitContext ctx)
    {
        var cut = ctx.Render<SettingsModal>(p => p.Add(c => c.IsOpen, true));
        var tab = cut.FindAll("button.modal-tab")
            .Single(b => b.TextContent.Contains("settings.models"));
        tab.Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("button[role='switch']"), Has.Count.EqualTo(2)));
        return cut;
    }

    [Test]
    public void ModelsTab_RenderizaGruposComToggles()
    {
        using var ctx = Setup(out _);
        var cut = OpenModelsTab(ctx);

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Ollama"));
            Assert.That(cut.Markup, Does.Contain("OpenAI"));
            Assert.That(cut.Markup, Does.Contain("1/1"));
            var switches = cut.FindAll("button[role='switch']");
            Assert.That(switches.Single(s => s.GetAttribute("aria-label") == "fake:1")
                .GetAttribute("aria-checked"), Is.EqualTo("true"));
            Assert.That(switches.Single(s => s.GetAttribute("aria-label") == "GPT X")
                .GetAttribute("aria-checked"), Is.EqualTo("false"));
        });
    }

    [Test]
    public void ModelsTab_Toggle_PersisteDisabledModels()
    {
        using var ctx = Setup(out var posts);
        var cut = OpenModelsTab(ctx);

        cut.Find("button[role='switch'][aria-label='fake:1']").Click();
        cut.WaitForAssertion(() => Assert.That(posts, Has.Count.EqualTo(1)));

        using var doc = JsonDocument.Parse(posts[0]);
        var disabled = doc.RootElement.GetProperty("disabledModels")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(disabled, Does.Contain("ollama:fake:1"));
            Assert.That(disabled, Does.Contain("openai:gpt-x"));
            Assert.That(cut.Find("button[role='switch'][aria-label='fake:1']")
                .GetAttribute("aria-checked"), Is.EqualTo("false"));
        });
    }

    [Test]
    public void ModelsTab_Busca_FiltraSomenteMatches()
    {
        using var ctx = Setup(out _);
        var cut = OpenModelsTab(ctx);

        cut.Find("input.input").Input("gpt");
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("button[role='switch']"), Has.Count.EqualTo(1)));

        // A seção de papéis (SPEC-20261013-model-roles) lista todos os
        // modelos em <option> sem filtro — "fake:1" segue no markup dos
        // selects de papel. A asserção de filtro mira a lista de
        // visibilidade (switches), não o markup inteiro.
        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("OpenAI"));
            Assert.That(cut.FindAll("button[role='switch']")
                .Select(b => b.GetAttribute("aria-label")),
                Is.All.Not.EqualTo("fake:1").And.Not.Empty);
        });
    }

    [Test]
    public void ModelsTab_DisableAll_DesabilitaGrupo()
    {
        using var ctx = Setup(out var posts);
        var cut = OpenModelsTab(ctx);

        // Primeiro grupo do catálogo = ollama — o botão desliga fake:1.
        var disableAll = cut.FindAll("button.button-sm")
            .First(b => b.TextContent.Contains("models.disable_all"));
        disableAll.Click();
        cut.WaitForAssertion(() => Assert.That(posts, Has.Count.EqualTo(1)));

        using var doc = JsonDocument.Parse(posts[0]);
        var disabled = doc.RootElement.GetProperty("disabledModels")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(disabled, Does.Contain("ollama:fake:1"));
            Assert.That(disabled, Does.Contain("openai:gpt-x"));
        });
    }
}
