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

/// <summary>SPEC-20261128-ux-polish RF-001: feed da run promovido ao topo
/// do painel quando usado como drawer mobile.</summary>
[TestFixture]
public sealed class WorkspacePanelUxTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> route)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(route(request));
    }

    private static BunitContext Setup()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        })) { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(http);
        var l10n = new LocalizationService(http, ctx.JSInterop.JSRuntime);
        var auth = new AuthService(http, new BrowserStorage(ctx.JSInterop.JSRuntime), l10n);
        var api = new ApiService(http, auth);
        ctx.Services.AddSingleton(api);
        ctx.Services.AddSingleton(l10n);
        ctx.Services.AddSingleton(new IdeTestRunService(api));
        ctx.Services.AddSingleton(new DialogService());
        return ctx;
    }

    [Test]
    public void PainelComActivityFeed_RenderizaAcimaDasTabs()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatWorkspacePanel>(p => p
            .Add(c => c.ChatId, "c1")
            .Add(c => c.ActivityFeed, builder => builder.AddContent(0, "activity-feed-marker")));

        cut.WaitForAssertion(() =>
        {
            var feed = cut.Markup.IndexOf("activity-feed-marker", StringComparison.Ordinal);
            var tabs = cut.Markup.IndexOf("role=\"tablist\"", StringComparison.Ordinal);
            Assert.That(feed, Is.GreaterThanOrEqualTo(0));
            Assert.That(tabs, Is.GreaterThanOrEqualTo(0));
            Assert.That(feed, Is.LessThan(tabs));
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void PainelSemActivityFeed_NaoReservaBloco()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ChatWorkspacePanel>(p => p.Add(c => c.ChatId, "c1"));
        Assert.That(cut.Markup, Does.Not.Contain("activity-feed-marker"));
    }
}
