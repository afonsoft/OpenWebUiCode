using NUnit.Framework;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261027-context-gen-wizard RF-001: validação server-side do
/// <see cref="GenerationContext"/> — enums de persona/preset/mode/
/// densidade, limites de itens e tamanho, URLs apenas https e
/// <c>ref://</c>, paleta sanitizada pelo <see cref="ThemeValidator"/>.
/// Cobertura das branches de rejeição (issue #706).
/// </summary>
[TestFixture]
public class GenContextValidatorTests
{
    private static GenStyle Estilo(
        string preset = "terminal", string? mode = null,
        Dictionary<string, string>? palette = null, string? density = null) =>
        new(preset, mode, palette, density);

    private static GenerationContext Ctx(
        string persona = "developer", string? personaCustom = null,
        GenStyle? style = null, List<GenProject>? projects = null,
        List<string>? integrations = null, string? freePrompt = null) =>
        new(persona, personaCustom, style ?? Estilo(),
            projects ?? [], integrations ?? [], freePrompt, "pt-BR");

    /// <summary>Caminho feliz: contexto mínimo válido e contexto completo
    /// (custom + palette + projeto com ref/tags + integrações).</summary>
    [Test]
    public void TryValidate_ContextoValido_Ok()
    {
        Assert.That(GenContextValidator.TryValidate(Ctx(), out var err),
            Is.True, err);
        Assert.That(err, Is.Null);

        var full = Ctx(
            persona: "custom", personaCustom: "pessoa devops",
            style: Estilo("custom", "dark",
                new() { ["accent"] = "#ff00aa", ["--x"] = "rgb(1,2,3)" },
                "compact"),
            projects: [new("t", "s", "https://ok.dev/p",
                ["a", "b"], "ref://x")],
            integrations: ["github"], freePrompt: "livre");
        Assert.That(GenContextValidator.TryValidate(full, out _), Is.True);
    }

    /// <summary>Persona fora do catálogo e custom sem personaCustom.</summary>
    [Test]
    public void TryValidate_Persona_Rejeicoes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GenContextValidator.TryValidate(
                Ctx(persona: "alien"), out var e1), Is.False);
            Assert.That(e1, Does.Contain("persona"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(persona: "custom"), out var e2), Is.False);
            Assert.That(e2, Does.Contain("personaCustom"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(personaCustom: new string('x', 201)), out var e3), Is.False);
            Assert.That(e3, Does.Contain("200 chars"));
        });
    }

    /// <summary>Style nulo, preset/mode/density inválidos e paleta com
    /// nome ou valor fora do allowlist de tokens.</summary>
    [Test]
    public void TryValidate_Style_Rejeicoes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GenContextValidator.TryValidate(
                new GenerationContext("developer", null, null!,
                    [], [], null, null), out var e1), Is.False);
            Assert.That(e1, Does.Contain("'style'"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(style: Estilo(preset: "psychedelic")), out var e2), Is.False);
            Assert.That(e2, Does.Contain("preset"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(style: Estilo(mode: "sepia")), out var e3), Is.False);
            Assert.That(e3, Does.Contain("mode"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(style: Estilo(density: "dense")), out var e4), Is.False);
            Assert.That(e4, Does.Contain("density"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(style: Estilo(palette: new() { ["não é token"] = "#fff" })),
                out var e5), Is.False);
            Assert.That(e5, Does.Contain("palette"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(style: Estilo(palette: new() { ["accent"] = "url(evil)" })),
                out var e6), Is.False);
            Assert.That(e6, Does.Contain("palette"));
        });
    }

    /// <summary>Projetos: máximo, título, summary, url não-https/absoluta,
    /// ref sem prefixo e tags demais.</summary>
    [Test]
    public void TryValidate_Projects_Rejeicoes()
    {
        var demais = Enumerable.Range(0, 21)
            .Select(i => new GenProject($"p{i}", null, null, null, null))
            .ToList();
        Assert.Multiple(() =>
        {
            Assert.That(GenContextValidator.TryValidate(
                Ctx(projects: demais), out var e1), Is.False);
            Assert.That(e1, Does.Contain("máximo"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(projects: [new("", null, null, null, null)]),
                out var e2), Is.False);
            Assert.That(e2, Does.Contain("title"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(projects: [new("t", new string('s', 501), null, null, null)]),
                out var e3), Is.False);
            Assert.That(e3, Does.Contain("summary"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(projects: [new("t", null, "não-uri", null, null)]),
                out var e4), Is.False);
            Assert.That(e4, Does.Contain("URI absoluta"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(projects: [new("t", null, "http://inseguro.dev", null, null)]),
                out var e5), Is.False);
            Assert.That(e5, Does.Contain("https://"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(projects: [new("t", null, null, null, "arquivo.txt")]),
                out var e6), Is.False);
            Assert.That(e6, Does.Contain("ref://"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(projects: [new("t", null, null,
                    Enumerable.Range(0, 9).Select(i => $"t{i}").ToList(), null)]),
                out var e7), Is.False);
            Assert.That(e7, Does.Contain("tags"));
        });
    }

    /// <summary>Integrações: máximo, id vazio ou >40 chars; freePrompt
    /// acima de 4000.</summary>
    [Test]
    public void TryValidate_IntegrationsEFreePrompt_Rejeicoes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GenContextValidator.TryValidate(
                Ctx(integrations: Enumerable.Range(0, 13)
                    .Select(i => $"i{i}").ToList()), out var e1), Is.False);
            Assert.That(e1, Does.Contain("integrations"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(integrations: [" "]), out var e2), Is.False);
            Assert.That(e2, Does.Contain("40 chars"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(integrations: [new string('x', 41)]), out var e3), Is.False);
            Assert.That(e3, Does.Contain("40 chars"));
            Assert.That(GenContextValidator.TryValidate(
                Ctx(freePrompt: new string('f', 4001)), out var e4), Is.False);
            Assert.That(e4, Does.Contain("freePrompt"));
        });
    }

    /// <summary>TryParse: JSON inválido, null literal, objeto com campo
    /// ruim e objeto válido (case-insensitive).</summary>
    [Test]
    public void TryParse_JsonBruto()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GenContextValidator.TryParse(
                "{quebrado", out _, out var e1), Is.False);
            Assert.That(e1, Does.Contain("JSON inválido"));
            Assert.That(GenContextValidator.TryParse(
                "null", out _, out var e2), Is.False);
            Assert.That(e2, Does.Contain("objeto JSON"));
            Assert.That(GenContextValidator.TryParse(
                "{\"persona\":\"alien\",\"style\":{\"preset\":\"terminal\"}}",
                out _, out var e3), Is.False);
            Assert.That(e3, Does.Contain("persona"));
            Assert.That(GenContextValidator.TryParse(
                "{\"PERSONA\":\"developer\",\"STYLE\":{\"PRESET\":\"minimal\"}}",
                out var ok, out _), Is.True);
            Assert.That(ok!.Persona, Is.EqualTo("developer"));
        });
    }
}
