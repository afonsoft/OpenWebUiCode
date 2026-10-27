using System.Text.Json;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261018-themes-custom: testes unitários de <see cref="ThemeValidator"/>
/// e <see cref="ThemeCssBuilder"/> — erros de schema, slugificação e geração de
/// CSS (rampa de cinzas, âncoras parciais, overrides de vars e paleta clara).
/// </summary>
[TestFixture]
public class ThemeContractsTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // Covers RF-003: 'name' obrigatório (ausente → erro legível).
    [Test]
    public void TryParse_SemName_RetornaErro()
    {
        var ok = ThemeValidator.TryParse(
            Parse("""{"dark":{"bg":"#000"}}"""), out var theme, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(theme, Is.Null);
            Assert.That(error, Does.Contain("name"));
        });
    }

    // Covers RF-003: 'name' limitado a 64 chars.
    [Test]
    public void TryParse_NameMaiorQue64_RetornaErro()
    {
        var name = new string('n', 65);
        var ok = ThemeValidator.TryParse(
            Parse("{\"name\":\"" + name + "\",\"dark\":{\"bg\":\"#000\"}}"),
            out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("name"));
        });
    }

    // Covers RF-003: id explícito deve ser kebab-case válido.
    [Test]
    public void TryParse_IdInvalido_RetornaErro()
    {
        var ok = ThemeValidator.TryParse(
            Parse("""{"id":"Tema_X!","name":"T","dark":{"bg":"#000"}}"""),
            out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("id"));
        });
    }

    // Covers RF-003: id omitido deriva do nome via Slugify.
    [Test]
    public void TryParse_SemId_GeraSlugDoNome()
    {
        var ok = ThemeValidator.TryParse(
            Parse("""{"name":"Meu Tema!","dark":{"bg":"#000000"}}"""),
            out var theme, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True, error);
            Assert.That(theme!.Id, Is.EqualTo("meu-tema"));
        });
    }

    // Covers RF-003: seções dark/light/vars devem ser objetos de tokens.
    [Test]
    public void TryParse_SecaoNaoObjeto_RetornaErro()
    {
        var ok = ThemeValidator.TryParse(
            Parse("""{"name":"T","dark":"nope"}"""), out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("dark"));
        });
    }

    // Covers RF-003: nome de token só aceita semântico conhecido ou --var.
    [Test]
    public void TryParse_TokenNameInvalido_RetornaErro()
    {
        var ok = ThemeValidator.TryParse(
            Parse("""{"name":"T","dark":{"cor-fundo":"#000"}}"""),
            out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("cor-fundo"));
        });
    }

    // Covers RF-003: valor de token deve ser string cor (não número/bool).
    [Test]
    public void TryParse_ValorNaoString_RetornaErro()
    {
        var ok = ThemeValidator.TryParse(
            Parse("""{"name":"T","dark":{"bg":123}}"""), out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("bg"));
        });
    }

    // Covers RF-003: tema sem nenhum token é rejeitado.
    [Test]
    public void TryParse_SemTokens_RetornaErro()
    {
        var ok = ThemeValidator.TryParse(
            Parse("""{"name":"T","dark":{},"light":{},"vars":{}}"""),
            out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("token"));
        });
    }

    // Covers RF-001: sanitização de nomes — semânticos, --vars e rejeições.
    [TestCase("bg", true)]
    [TestCase("accent-contrast", true)]
    [TestCase("--x", true)]
    [TestCase("--cor-1", true)]
    [TestCase("--", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    [TestCase("cor", false)]
    [TestCase("--a b", false)]
    public void IsValidTokenName_AceitaApenasSemanticoOuVar(string? name, bool expected) =>
        Assert.That(ThemeValidator.IsValidTokenName(name), Is.EqualTo(expected));

    // Covers RF-001: slug kebab-case, começa alfanumérico, máx 64.
    [TestCase("meu-tema", true)]
    [TestCase("t1", true)]
    [TestCase("Tema", false)]
    [TestCase("tema_x", false)]
    [TestCase("-tema", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsValidThemeId_ApenasKebabCase(string? id, bool expected) =>
        Assert.That(ThemeValidator.IsValidThemeId(id), Is.EqualTo(expected));

    // Covers RF-001: Slugify normaliza maiúsculas, especiais e colapsa traços.
    [Test]
    public void Slugify_NormalizaNome()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ThemeValidator.Slugify("Meu Tema!"), Is.EqualTo("meu-tema"));
            Assert.That(ThemeValidator.Slugify("  --Espaco--  "), Is.EqualTo("espaco"));
            Assert.That(ThemeValidator.Slugify("!!!"), Is.EqualTo("tema-"));
            Assert.That(ThemeValidator.Slugify("a__b__c"), Is.EqualTo("a-b-c"));
            Assert.That(ThemeValidator.Slugify(new string('x', 100)).Length, Is.LessThanOrEqualTo(64));
        });
    }

    // Covers RF-001/AC: âncoras completas interpolam a rampa de cinzas.
    [Test]
    public void CssBuilder_AncorasCompletas_GeraRampaInterpolada()
    {
        var theme = new CustomThemeDefinition(
            "corp", "Corp",
            new Dictionary<string, string>
            {
                ["bg"] = "#101418", ["surface"] = "#182028", ["text"] = "#e8edf2",
            },
            new Dictionary<string, string>
            {
                ["bg"] = "#ffffff", ["surface"] = "#f0f0f0", ["text"] = "#202020",
            },
            new Dictionary<string, string>());

        var css = ThemeCssBuilder.Build(theme);

        Assert.Multiple(() =>
        {
            // Escuros: 950 ≈ bg; a rampa inteira deve existir.
            Assert.That(css, Does.Contain("--color-gray-950"));
            Assert.That(css, Does.Contain("--color-gray-500"));
            Assert.That(css, Does.Contain("--color-gray-50"));
            // dark: --color-white recebe o bg profundo; light: o bg claro.
            Assert.That(css, Does.Contain("--color-white: #101418"));
            Assert.That(css, Does.Contain("--color-white: #ffffff"));
        });
    }

    // Covers RF-001: tokens rgb() também viram âncoras de rampa.
    [Test]
    public void CssBuilder_TokensRgbFunc_ParseiaRampas()
    {
        var theme = new CustomThemeDefinition(
            "rgb", "Rgb",
            new Dictionary<string, string>
            {
                ["bg"] = "rgb(10 20 30)",
                ["surface"] = "rgba(20, 30, 40, 0.9)",
                ["text"] = "rgb(200 210 220)",
            },
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var css = ThemeCssBuilder.Build(theme);

        Assert.Multiple(() =>
        {
            Assert.That(css, Does.Contain("--color-gray-950"));
            Assert.That(css, Does.Contain("--bg: rgb(10 20 30)"));
        });
    }

    // Covers RF-001: âncoras parciais aplicam tokens nas posições-chave.
    [Test]
    public void CssBuilder_SemSurface_AplicaBgETextDiretos()
    {
        var theme = new CustomThemeDefinition(
            "parcial", "Parcial",
            new Dictionary<string, string>
            {
                ["bg"] = "#0b0f14", ["text"] = "#e6edf3",
            },
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var css = ThemeCssBuilder.Build(theme);

        Assert.Multiple(() =>
        {
            Assert.That(css, Does.Contain("--color-gray-900: #0b0f14"));
            Assert.That(css, Does.Contain("--color-gray-100: #e6edf3"));
            Assert.That(css, Does.Contain("--color-gray-925: #0b0f14"));
        });
    }

    // Covers RF-001: accent/danger/code-bg mapeiam para azul/vermelho/prose.
    [Test]
    public void CssBuilder_AccentDangerCodebg_MapeiaUtilities()
    {
        var theme = new CustomThemeDefinition(
            "util", "Util",
            new Dictionary<string, string>
            {
                ["accent"] = "#ff8800", ["danger"] = "#ff0000", ["code-bg"] = "#101010",
            },
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        var css = ThemeCssBuilder.Build(theme);

        Assert.Multiple(() =>
        {
            Assert.That(css, Does.Contain("--color-blue-500: #ff8800"));
            Assert.That(css, Does.Contain("--color-red-500: #ff0000"));
            Assert.That(css, Does.Contain("--tw-prose-pre-bg: #101010"));
        });
    }

    // Covers RF-001: vars (--x) sobrescrevem por último na cascata do bloco.
    [Test]
    public void CssBuilder_Vars_EscreveOverrides()
    {
        var theme = new CustomThemeDefinition(
            "vars", "Vars",
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>
            {
                ["--radius"] = "#aabbcc", ["nao-var"] = "#ffffff",
            });

        var css = ThemeCssBuilder.Build(theme);

        Assert.Multiple(() =>
        {
            Assert.That(css, Does.Contain("--radius: #aabbcc"));
            Assert.That(css, Does.Not.Contain("nao-var"));
        });
    }

    // Covers RF-001: paleta clara gera bloco sem .dark e rampa clara.
    [Test]
    public void CssBuilder_LightPalette_GeraBlocoSemDark()
    {
        var theme = new CustomThemeDefinition(
            "claro", "Claro",
            new Dictionary<string, string>(),
            new Dictionary<string, string>
            {
                ["bg"] = "#fafafa", ["surface"] = "#eeeeee", ["text"] = "#111111",
            },
            new Dictionary<string, string>());

        var css = ThemeCssBuilder.Build(theme);

        Assert.Multiple(() =>
        {
            Assert.That(css, Does.Contain("html.theme-claro {"));
            Assert.That(css, Does.Contain("html.theme-claro.dark {"));
            // Claro: 50/100 = surface clara.
            Assert.That(css, Does.Contain("--color-gray-50: #eeeeee"));
        });
    }

    // Covers RF-003: contrato de resposta serializa seleção ativa.
    [Test]
    public void ThemeCatalogResponse_SerializaCampos()
    {
        var catalog = new ThemeCatalogResponse(
            null, [], new ActiveThemeSelection("default", "system"));

        var json = JsonSerializer.Serialize(catalog);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Contain("\"ThemeId\":\"default\"").Or.Contain("\"themeId\":\"default\""));
            Assert.That(catalog.Instance, Is.Null);
            Assert.That(catalog.Customs, Is.Empty);
        });
    }
}
