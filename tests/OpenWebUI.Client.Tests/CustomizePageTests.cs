using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OpenWebUI.Client.Pages;
using OpenWebUI.Client.Services;

namespace OpenWebUI.Client.Tests;

/// <summary>
/// SPEC-20261104-customize-hub RF-001/RF-002/RF-006/RF-009: página /customize
/// — abas Knowledge/Skills/MCPs, badges de escopo (org/pessoal), seções admin
/// gated por role e toggles no endpoint correto.
/// </summary>
[TestFixture]
public sealed class CustomizePageTests
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
        out List<string> puts, string role = "user")
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var putCalls = new List<string>();

        var http = new HttpClient(new FakeHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;

            if (req.Method == HttpMethod.Post && path == "/api/v1/auths/signin")
            {
                return Json(new
                {
                    token = "tok",
                    tokenType = "Bearer",
                    expiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    user = new
                    {
                        id = "u1", name = "U", email = "u@x",
                        role, profileImageUrl = (string?)null, timezone = (string?)null,
                    },
                });
            }
            if (path == "/api/v1/customize/knowledge")
            {
                return Json(new
                {
                    data = new object[]
                    {
                        new
                        {
                            id = "n1", title = "Convencao PR", trigger = "pr",
                            content = "corpo", pinned = true, scope = "org",
                            enabledForMe = true, editable = false, updatedAt = 1L,
                        },
                        new
                        {
                            id = "n2", title = "Minha nota", trigger = (string?)null,
                            content = "corpo2", pinned = false, scope = "personal",
                            enabledForMe = true, editable = true, updatedAt = 2L,
                        },
                    },
                });
            }
            if (path == "/api/v1/customize/skills" && req.Method == HttpMethod.Get)
            {
                return Json(new
                {
                    data = new object[]
                    {
                        new
                        {
                            id = (string?)"s1", name = "org-skill", description = (string?)"d",
                            source = "global", enabled = true, enabledForMe = true,
                            disabledByAdmin = false, editable = false, content = (string?)"c",
                        },
                        new
                        {
                            id = (string?)null, name = "repo-skill", description = (string?)null,
                            source = "repo", enabled = true, enabledForMe = false,
                            disabledByAdmin = false, editable = false, content = (string?)null,
                        },
                    },
                });
            }
            if (req.Method == HttpMethod.Put && path.StartsWith("/api/v1/customize/"))
            {
                putCalls.Add(path);
                return Json(new { status = true });
            }
            if (path == "/api/v1/admin/skills/disabled")
            {
                return Json(new { names = Array.Empty<string>() });
            }
            if (path == "/api/v1/users/user/mcp-servers/")
            {
                return Json(new[]
                {
                    new
                    {
                        server = new
                        {
                            id = "m1", name = "org-mcp", transport = "http",
                            command = (string?)null, args = Array.Empty<string>(),
                            envNames = Array.Empty<string>(), url = "https://mcp.example/mcp",
                            headers = new Dictionary<string, string>(),
                            enabled = true, lastError = (string?)null, createdAt = 1L,
                        },
                        source = "global", enabledForMe = true,
                    },
                });
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        { BaseAddress = new Uri("http://localhost/") };

        puts = putCalls;
        ctx.Services.AddSingleton(http);
        var l10n = new LocalizationService(http, ctx.JSInterop.JSRuntime);
        var auth = new AuthService(http, new BrowserStorage(ctx.JSInterop.JSRuntime), l10n);
        var api = new ApiService(http, auth);
        ctx.Services.AddSingleton(l10n);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(api);
        ctx.Services.AddSingleton<DialogService>();
        ctx.Services.AddSingleton<NavigationManager>(new TestNav());
        return ctx;
    }

    /// <summary>Autentica via mock — o handler devolve a role configurada no
    /// <see cref="Setup"/> da fixture.</summary>
    private static async Task SignInAsync(BunitContext ctx)
    {
        var auth = ctx.Services.GetRequiredService<AuthService>();
        await auth.SignInAsync("u@x", "pw");
    }

    [Test]
    public void KnowledgeTab_ListaNotas_ComBadgeDeEscopo()
    {
        using var ctx = Setup(out _);
        var cut = ctx.Render<Customize>(p => p.Add(c => c.Tab, "knowledge"));

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Convencao PR"));
            Assert.That(cut.Markup, Does.Contain("Minha nota"));
            Assert.That(cut.Markup, Does.Contain("customize.scope_org"));
            Assert.That(cut.Markup, Does.Contain("customize.scope_personal"));
        });
    }

    [Test]
    public void KnowledgeTab_NaoAdmin_NaoVeCheckboxOrgNemImport()
    {
        using var ctx = Setup(out _);
        var cut = ctx.Render<Customize>(p => p.Add(c => c.Tab, "knowledge"));

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Convencao PR")));
        // Apenas 1 checkbox (pinned) — sem o de escopo org, que é admin-only.
        Assert.That(cut.FindAll("input[type=checkbox]"), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task SkillsTab_Admin_VeImportECheckboxOrg()
    {
        using var ctx = Setup(out _, role: "admin");
        await SignInAsync(ctx);
        var cut = ctx.Render<Customize>(p => p.Add(c => c.Tab, "skills"));

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("customize.import_title"));
            Assert.That(cut.Markup, Does.Contain("org-skill"));
            Assert.That(cut.Markup, Does.Contain("customize.source_repo"));
        });
    }

    [Test]
    public void SkillsTab_Toggle_ChamaEndpointEnabled()
    {
        using var ctx = Setup(out var puts);
        var cut = ctx.Render<Customize>(p => p.Add(c => c.Tab, "skills"));

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("org-skill")));
        var toggle = cut.FindAll("button.msg-action")
            .First(b => b.TextContent.Contains("workspace.hide"));
        toggle.Click();

        cut.WaitForAssertion(() =>
            Assert.That(puts, Has.Some.Contains("/api/v1/customize/skills/s1/enabled")
                .Or.Contains("repo/skills")));
    }

    [Test]
    public void McpsTab_ListaCatalogoEfetivo_ComProvenance()
    {
        using var ctx = Setup(out _);
        var cut = ctx.Render<Customize>(p => p.Add(c => c.Tab, "mcps"));

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("org-mcp"));
            Assert.That(cut.Markup, Does.Contain("mcp.example"));
            Assert.That(cut.Markup, Does.Contain("customize.scope_org"));
        });
    }
}
