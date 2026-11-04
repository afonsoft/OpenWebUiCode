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
/// SPEC-20261104-session-insights-v2 RF-004: painel de insights no chat —
/// chip de tamanho, categoria, issues/timeline e botão gerar análise.
/// </summary>
[TestFixture]
public sealed class InsightsPanelTests
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

    private static object InsightPayload(string size = "XL") => new
    {
        sizeClass = size,
        category = "bugfix",
        metrics = new { tokensIn = 400000L, tokensOut = 400000L, costUsd = 0.5, runs = 3, userMessages = 5 },
        issues = new object[]
        {
            new { label = "run_failed", impact = "high", description = "3 runs com erro", count = 3 },
        },
        timeline = new object[]
        {
            new { at = 1L, kind = "error", title = "tool falhou: boom", @ref = "r1" },
        },
        analysis = (object?)null,
    };

    private static BunitContext Setup(
        Func<HttpRequestMessage, HttpResponseMessage> route)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new FakeHandler(route)) { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(http);
        var l10n = new LocalizationService(http, ctx.JSInterop.JSRuntime);
        var auth = new AuthService(http, new BrowserStorage(ctx.JSInterop.JSRuntime), l10n);
        ctx.Services.AddSingleton(new ApiService(http, auth));
        ctx.Services.AddSingleton(l10n);
        return ctx;
    }

    [Test]
    public void Panel_XlChat_ChipAlertaEErrosNaTimeline()
    {
        using var ctx = Setup(req => Json(InsightPayload()));
        var cut = ctx.Render<InsightsPanel>(p => p.Add(c => c.ChatId, "c1"));

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("XL"));
            Assert.That(cut.Markup, Does.Contain("bugfix"));
            Assert.That(cut.Markup, Does.Contain("insights.unhealthy"));
            Assert.That(cut.Markup, Does.Contain("tool falhou"));
            Assert.That(cut.Markup, Does.Contain("run_failed"));
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Panel_Gerar_ChamadaPostEExibeAnalise()
    {
        var calls = new List<string>();
        using var ctx = Setup(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post)
            {
                calls.Add(path);
                return Json(new
                {
                    sizeClass = "M", category = "feature",
                    metrics = new { tokensIn = 1L, tokensOut = 1L, costUsd = 0, runs = 1, userMessages = 1 },
                    issues = Array.Empty<object>(), timeline = Array.Empty<object>(),
                    analysis = new
                    {
                        summary = "resumo da análise",
                        issues = new[] { "retry" },
                        improvedPrompt = "prompt melhor",
                        changes = Array.Empty<object>(),
                        actionItems = new[] { new { kind = "skill", text = "criar skill" } },
                        knowledgeUsed = Array.Empty<string>(),
                        modelId = "adm-a", generatedAt = 1L,
                    },
                });
            }
            return Json(InsightPayload("M"));
        });
        var cut = ctx.Render<InsightsPanel>(p => p.Add(c => c.ChatId, "c1"));

        cut.WaitForElement("button.msg-action", TimeSpan.FromSeconds(5)).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("resumo da análise"));
            Assert.That(cut.Markup, Does.Contain("prompt melhor"));
            Assert.That(calls, Has.Some.Contains("/insights/generate"));
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Panel_SemChat_NaoCarrega()
    {
        var called = false;
        using var ctx = Setup(_ =>
        {
            called = true;
            return Json(InsightPayload());
        });
        ctx.Render<InsightsPanel>(p => p.Add(c => c.ChatId, (string?)null));
        Assert.That(called, Is.False);
    }
}
