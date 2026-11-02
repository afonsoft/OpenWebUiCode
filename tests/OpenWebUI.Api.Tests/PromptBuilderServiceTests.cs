using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes do builder de system prompt do context-gen-wizard (RF-003).</summary>
[TestFixture]
public class PromptBuilderServiceTests
{
    private static GenerationContext Ctx() => new(
        Persona: "developer",
        PersonaCustom: null,
        Style: new GenStyle("terminal", "dark", null, "compact"),
        Projects: [new GenProject("Site", "resumo", "https://a.b", ["web"], null)],
        Integrations: ["github"],
        FreePrompt: "faça algo",
        Locale: "pt-BR");

    [Test]
    public void BuildSystemPrompt_ContemSecoesPersonaStyleProjetos()
    {
        var prompt = PromptBuilderService.BuildSystemPrompt(Ctx());
        Assert.That(prompt, Does.Contain("## Persona"));
        Assert.That(prompt, Does.Contain("## Visual style"));
        Assert.That(prompt, Does.Contain("terminal"));
        Assert.That(prompt, Does.Contain("Site"));
    }

    [Test]
    public void TryValidate_RejeitaPaletteInvalida()
    {
        var ctx = Ctx() with
        {
            Style = new GenStyle("custom", null,
                new Dictionary<string, string> { ["--bg"] = "; drop" }, null),
        };
        Assert.That(GenContextValidator.TryValidate(ctx, out var err), Is.False);
        Assert.That(err, Does.Contain("palette"));
    }

    [Test]
    public void TryValidate_RejeitaUrlNaoHttps()
    {
        var ctx = Ctx() with
        {
            Projects = [new GenProject("x", null, "http://a.b", null, null)],
        };
        Assert.That(GenContextValidator.TryValidate(ctx, out _), Is.False);
    }

    [Test]
    public void TryParse_RoundTrip_RejeitaPersonaDesconhecida()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Ctx());
        Assert.That(GenContextValidator.TryParse(json, out var back, out _), Is.True);
        Assert.That(back!.Persona, Is.EqualTo("developer"));

        var bad = json.Replace("developer", "hacker");
        Assert.That(GenContextValidator.TryParse(bad, out _, out _), Is.False);
    }
}
