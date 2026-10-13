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
/// SPEC-20261009-agent-modes-plan-build RF-004 + SPEC-15: o seletor do
/// composer renderiza com rótulo/ícone e abre menu chat/plan/build —
/// build fica travado sem repositório vinculado; o chip do header mostra
/// o modo (badge no plan).
/// </summary>
[TestFixture]
public sealed class AgentModeTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
    }

    private static Bunit.BunitContext Setup()
    {
        var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new FakeHandler())
        {
            BaseAddress = new Uri("http://localhost/"),
        };
        ctx.Services.AddSingleton(new LocalizationService(http, ctx.JSInterop.JSRuntime));
        return ctx;
    }

    [Test]
    public void Toggle_Chat_RenderizaComLabelEIcone()
    {
        var ctx = Setup();
        using var cut = ctx.Render<AgentModeToggle>(p => p.Add(c => c.Mode, "chat"));
        var button = cut.Find("button");
        Assert.Multiple(() =>
        {
            Assert.That(button.GetAttribute("aria-haspopup"), Is.EqualTo("menu"));
            Assert.That(button.GetAttribute("aria-expanded"), Is.EqualTo("false"));
            Assert.That(button.TextContent, Does.Contain("chat.mode_chat"));
            Assert.That(button.GetAttribute("class"), Does.Contain("sky"));
        });
    }

    [Test]
    public void Toggle_Menu_AbreTresOpcoes_MarcandoAtiva()
    {
        var ctx = Setup();
        using var cut = ctx.Render<AgentModeToggle>(p => p.Add(c => c.Mode, "plan"));
        cut.Find("button").Click();

        var items = cut.FindAll("[role=menuitemradio]");
        Assert.That(items, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(items[0].TextContent, Does.Contain("chat.mode_chat"));
            Assert.That(items[1].TextContent, Does.Contain("chat.mode_plan"));
            Assert.That(items[2].TextContent, Does.Contain("chat.mode_build"));
            // plan é a opção ativa.
            Assert.That(items[0].GetAttribute("aria-checked"), Is.EqualTo("false"));
            Assert.That(items[1].GetAttribute("aria-checked"), Is.EqualTo("true"));
            Assert.That(items[2].GetAttribute("aria-checked"), Is.EqualTo("false"));
        });
    }

    [Test]
    public void Toggle_Build_SemRepo_FicaDesabilitadoComHint()
    {
        var ctx = Setup();
        using var cut = ctx.Render<AgentModeToggle>(p => p
            .Add(c => c.Mode, "chat")
            .Add(c => c.HasRepo, false));
        cut.Find("button").Click();

        var items = cut.FindAll("[role=menuitemradio]");
        Assert.Multiple(() =>
        {
            Assert.That(items[0].HasAttribute("disabled"), Is.False, "chat livre");
            Assert.That(items[1].HasAttribute("disabled"), Is.False, "plan livre");
            Assert.That(items[2].HasAttribute("disabled"), Is.True, "build travado sem repo");
            Assert.That(items[2].TextContent, Does.Contain("chat.mode_build_locked"));
        });
    }

    [Test]
    public void Toggle_Build_ComRepo_Habilitado()
    {
        var ctx = Setup();
        using var cut = ctx.Render<AgentModeToggle>(p => p
            .Add(c => c.Mode, "chat")
            .Add(c => c.HasRepo, true));
        cut.Find("button").Click();

        var build = cut.FindAll("[role=menuitemradio]")[^1];
        Assert.Multiple(() =>
        {
            Assert.That(build.HasAttribute("disabled"), Is.False);
            Assert.That(build.TextContent, Does.Not.Contain("chat.mode_build_locked"));
        });
    }

    [Test]
    public void Toggle_SelecionarOpcao_DisparaCallback()
    {
        var ctx = Setup();
        var selected = string.Empty;
        using var cut = ctx.Render<AgentModeToggle>(p => p
            .Add(c => c.Mode, "chat")
            .Add(c => c.OnSelect, (string m) => selected = m));
        cut.Find("button").Click();
        cut.FindAll("[role=menuitemradio]")[1].Click();
        Assert.That(selected, Is.EqualTo("plan"));
    }

    [Test]
    public void Chip_Plan_MostraModo()
    {
        var ctx = Setup();
        using var cut = ctx.Render<AgentModeBadge>(p => p.Add(c => c.Mode, "plan"));
        var chip = cut.Find("[role=status]");
        Assert.Multiple(() =>
        {
            Assert.That(chip.TextContent, Does.Contain("chat.mode_plan"), "chip mostra 'Modo plano'");
            Assert.That(chip.GetAttribute("class"), Does.Contain("violet"));
        });
    }

    [Test]
    public void Chip_Chat_MostraChat()
    {
        var ctx = Setup();
        using var cut = ctx.Render<AgentModeBadge>(p => p.Add(c => c.Mode, "chat"));
        var chip = cut.Find("[role=status]");
        Assert.Multiple(() =>
        {
            Assert.That(chip.TextContent, Does.Contain("chat.mode_chat"));
            Assert.That(chip.GetAttribute("class"), Does.Contain("sky"));
        });
    }

    [Test]
    public void Chip_Build_MostraBuild()
    {
        var ctx = Setup();
        using var cut = ctx.Render<AgentModeBadge>(p => p.Add(c => c.Mode, "build"));
        var chip = cut.Find("[role=status]");
        Assert.Multiple(() =>
        {
            Assert.That(chip.TextContent, Does.Contain("chat.mode_build"));
            Assert.That(chip.GetAttribute("class"), Does.Not.Contain("violet"));
        });
    }

    [Test]
    public void Normalize_Desconhecido_ViraBuild()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AgentModeVisuals.Normalize("chat"), Is.EqualTo("chat"));
            Assert.That(AgentModeVisuals.Normalize("plan"), Is.EqualTo("plan"));
            Assert.That(AgentModeVisuals.Normalize("build"), Is.EqualTo("build"));
            Assert.That(AgentModeVisuals.Normalize("yolo"), Is.EqualTo("build"));
            Assert.That(AgentModeVisuals.Normalize(null), Is.EqualTo("build"));
        });
    }
}
