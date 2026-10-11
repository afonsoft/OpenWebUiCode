using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OpenWebUI.Client.Components;
using OpenWebUI.Client.Services;
using OpenWebUI.Client.Pages;

namespace OpenWebUI.Client.Tests;

/// <summary>
/// SPEC-20261010-offline-init-resilience: chamadas de API no init não podem
/// propagar HttpRequestException até a blazor-error-ui. O componente renderiza
/// estado degradado (badge vazio / casca / retry) e recupera sem reload quando
/// a rede volta.
/// </summary>
[TestFixture]
public sealed class OfflineInitResilienceTests
{
    /// <summary>Handler cujo modo pode ser trocado: offline lança
    /// HttpRequestException; online delega para uma rota normal.</summary>
    private sealed class SwitchableHandler : HttpMessageHandler
    {
        public bool Offline { get; set; } = true;

        /// <summary>Quando seta, lança essa exceção no lugar do
        /// HttpRequestException padrão (timeout/abort/parse de proxy).</summary>
        public Exception? OfflineException { get; set; }

        public Func<HttpRequestMessage, HttpResponseMessage>? Route { get; set; }
        public List<(string Method, string Path)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method.Method, request.RequestUri?.PathAndQuery ?? ""));
            if (Offline)
            {
                throw OfflineException ?? new HttpRequestException("Failed to fetch");
            }
            return Task.FromResult(
                Route?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                });
        }
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(body,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            Encoding.UTF8, "application/json"),
    };

    private sealed class Fixture : IDisposable
    {
        public required BunitContext Ctx { get; init; }
        public required SwitchableHandler Handler { get; init; }
        public required HttpClient Http { get; init; }

        public void Dispose()
        {
            Ctx.Dispose();
            Http.Dispose();
        }
    }

    /// <summary>Fixture mínima (NotificationBell: Api + L10n + Nav).</summary>
    private static Fixture SetupBell()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var handler = new SwitchableHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var l10n = new LocalizationService(http, ctx.JSInterop.JSRuntime);
        var auth = new AuthService(http,
            new BrowserStorage(ctx.JSInterop.JSRuntime), l10n);
        ctx.Services.AddSingleton(new ApiService(http, auth));
        ctx.Services.AddSingleton(l10n);
        return new Fixture { Ctx = ctx, Handler = handler, Http = http };
    }

    /// <summary>Fixture completa da sidebar/páginas (mesmos serviços de
    /// AttentionInboxTests + IdeTestRunService).</summary>
    private static Fixture SetupFull()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var handler = new SwitchableHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var l10n = new LocalizationService(http, ctx.JSInterop.JSRuntime);
        var auth = new AuthService(http,
            new BrowserStorage(ctx.JSInterop.JSRuntime), l10n);
        var api = new ApiService(http, auth);
        ctx.Services.AddSingleton(api);
        ctx.Services.AddSingleton(l10n);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(new DialogService());
        ctx.Services.AddSingleton(new ThemeService(
            ctx.JSInterop.JSRuntime, new BrowserStorage(ctx.JSInterop.JSRuntime)));
        ctx.Services.AddSingleton(new ChatListState());
        ctx.Services.AddSingleton(new AttentionInboxState(api));
        ctx.Services.AddSingleton(new IdeTestRunService(api));
        return new Fixture { Ctx = ctx, Handler = handler, Http = http };
    }

    [Test]
    public void NotificationBell_Offline_NaoPropagaExcecaoEBadgeFicaVazio()
    {
        using var fx = SetupBell();
        using var cut = fx.Ctx.Render<NotificationBell>();

        // Sem exceção propagada e sem badge de não-lidas.
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("[aria-label*='notifications.unread_aria']"), Is.Empty));
    }

    [Test]
    public void NotificationBell_Offline_DropdownMostraEstadoDegradado()
    {
        using var fx = SetupBell();
        using var cut = fx.Ctx.Render<NotificationBell>();
        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("common.offline_load")));
    }

    [Test]
    public void Sidebar_Offline_RenderizaCascaComFallback()
    {
        using var fx = SetupFull();
        using var cut = fx.Ctx.Render<Sidebar>();

        // Nav/casca continua renderizando com o aviso offline + retry.
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("[data-offline-fallback]"), Is.Not.Empty));
    }

    [Test]
    public void NotificationBell_Timeout_NaoPropagaExcecao()
    {
        // TaskCanceledException do timeout do HttpClient: antes caía na
        // blazor-error-ui porque só HttpRequestException era tratada.
        using var fx = SetupBell();
        fx.Handler.OfflineException = new TaskCanceledException("timeout");
        using var cut = fx.Ctx.Render<NotificationBell>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("[aria-label*='notifications.unread_aria']"), Is.Empty));
    }

    [Test]
    public void Sidebar_Timeout_RenderizaCascaComFallback()
    {
        using var fx = SetupFull();
        fx.Handler.OfflineException = new TaskCanceledException("timeout");
        using var cut = fx.Ctx.Render<Sidebar>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("[data-offline-fallback]"), Is.Not.Empty));
    }

    [Test]
    public void Ide_RespostaNaoJson_NaoPropagaExcecao()
    {
        // Proxy/CDN devolvendo página HTML com 200: ReadFromJsonAsync lança
        // JsonException — também é falha transitória, não erro fatal da UI.
        using var fx = SetupFull();
        fx.Handler.Offline = false;
        fx.Handler.Route = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>bad gateway</html>", Encoding.UTF8, "text/html"),
        };
        using var cut = fx.Ctx.Render<Ide>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("[data-offline-fallback]"), Is.Not.Empty));
    }

    [Test]
    public void Sidebar_QuandoRedeVolta_RetryRecarregaSemReload()
    {
        using var fx = SetupFull();
        fx.Handler.Route = _ => Json((object)new object[] { });
        using var cut = fx.Ctx.Render<Sidebar>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("[data-offline-fallback]"), Is.Not.Empty));

        // Rede volta: clique no retry roda LoadAsync de novo.
        fx.Handler.Offline = false;
        cut.Find("[data-offline-fallback] button").Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("[data-offline-fallback]"), Is.Empty));
    }

    [Test]
    public void Ide_Offline_RenderizaPainelDegradadoComRetry()
    {
        using var fx = SetupFull();
        using var cut = fx.Ctx.Render<Ide>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("[data-offline-fallback]"), Is.Not.Empty));
    }

    [Test]
    public void Ide_QuandoRedeVolta_RetryCarregaConteudo()
    {
        using var fx = SetupFull();
        fx.Handler.Route = req => req.RequestUri?.AbsolutePath switch
        {
            "/api/v1/ide/config" => Json(new { enabled = true }),
            "/api/v1/workspace/repo/" => Json(new { repo = (object?)null }),
            "/api/v1/workspace/repo/git" => Json(new { }),
            "/api/v1/github/config" => Json(new { configured = false }),
            // Listas (jobs/checkpoints/pulls) vêm vazias.
            _ => Json((object)new object[] { }),
        };
        using var cut = fx.Ctx.Render<Ide>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("[data-offline-fallback]"), Is.Not.Empty));

        fx.Handler.Offline = false;
        cut.Find("[data-offline-fallback] button").Click();

        // Sem repo vinculado o IDE mostra o painel de bind — não o fallback.
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("[data-offline-fallback]"), Is.Empty));
    }
}
