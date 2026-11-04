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
/// SPEC-20261104-model-visibility-gate RF-006: aba Conexões do SettingsModal —
/// carrega e grava APENAS as conexões do próprio usuário
/// (<c>user:{id}:connections</c>), nunca o endpoint admin
/// <c>/api/v1/configs/connections</c>; flag <c>features.userConnections</c>
/// desligada → estado vazio explicativo em vez de "Carregando…" infinito.
/// </summary>
[TestFixture]
public sealed class UserConnectionsTests
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

    private static BunitContext Setup(
        out List<string> adminHits, out List<string> userPosts,
        bool flagOn = true)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var admin = new List<string>();
        var posts = new List<string>();

        var http = new HttpClient(new FakeHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;

            // Endpoint admin global — RF-006 proíbe a aba de tocá-lo.
            if (path == "/api/v1/configs/connections")
            {
                admin.Add($"{req.Method} {path}");
                return Json(new
                {
                    ollamaBaseUrls = new[] { "http://ADMIN-NAO-DEVE-VAZAR:11434" },
                    openAiBaseUrls = Array.Empty<string>(),
                    openAiKeyConfigured = Array.Empty<bool>(),
                });
            }
            if (req.Method == HttpMethod.Get && path == "/api/v1/configs/user_connections")
            {
                return Json(new { enabled = flagOn });
            }
            if (req.Method == HttpMethod.Get && path == "/api/v1/configs/user/connections")
            {
                if (!flagOn)
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }
                return Json(new
                {
                    ollamaBaseUrls = new[] { "http://user-ollama:11434" },
                    openAiBaseUrls = new[] { "https://meu-openai.example/v1" },
                    openAiKeyConfigured = new[] { true },
                });
            }
            if (req.Method == HttpMethod.Post && path == "/api/v1/configs/user/connections")
            {
                posts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return Json(new
                {
                    ollamaBaseUrls = new[] { "http://user-ollama:11434" },
                    openAiBaseUrls = new[] { "https://meu-openai.example/v1" },
                    openAiKeyConfigured = new[] { true },
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        { BaseAddress = new Uri("http://localhost/") };

        adminHits = admin;
        userPosts = posts;
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

    private static IRenderedComponent<SettingsModal> OpenConnectionsTab(BunitContext ctx)
    {
        var cut = ctx.Render<SettingsModal>(p => p.Add(c => c.IsOpen, true));
        var tab = cut.FindAll("button.modal-tab")
            .Single(b => b.TextContent.Contains("settings.connections"));
        tab.Click();
        return cut;
    }

    [Test]
    public void ConnectionsTab_FlagOff_EstadoVazio_SemChamadaAdmin()
    {
        using var ctx = Setup(out var adminHits, out _, flagOn: false);
        var cut = OpenConnectionsTab(ctx);

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("settings.connections_disabled")));
        Assert.That(adminHits, Is.Empty);
    }

    [Test]
    public void ConnectionsTab_FlagOn_RenderizaConexoesDoUsuario_SemChamadaAdmin()
    {
        using var ctx = Setup(out var adminHits, out _);
        var cut = OpenConnectionsTab(ctx);

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("user-ollama")));
        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("meu-openai.example"));
            // A config global do admin nunca é lida nem exibida.
            Assert.That(cut.Markup, Does.Not.Contain("ADMIN-NAO-DEVE-VAZAR"));
            Assert.That(adminHits, Is.Empty);
        });
    }

    [Test]
    public void ConnectionsTab_Salvar_PostEmUserConnections()
    {
        using var ctx = Setup(out var adminHits, out var posts);
        var cut = OpenConnectionsTab(ctx);

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("user-ollama")));

        cut.Find("button.primary-btn").Click();
        cut.WaitForAssertion(() => Assert.That(posts, Has.Count.EqualTo(1)));

        using var doc = JsonDocument.Parse(posts[0]);
        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("ollamaBaseUrls")[0].GetString(),
                Is.EqualTo("http://user-ollama:11434"));
            Assert.That(adminHits, Is.Empty);
        });
    }
}
