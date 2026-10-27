using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenWebUI.Application.Contracts;

/// <summary>
/// SPEC-20261018-themes-custom: definição de tema customizado por tokens.
/// Tokens semânticos viram variáveis CSS consumidas pela UI (rampa de cinzas
/// do Tailwind + vars próprias <c>--bg</c>/<c>--surface</c>/etc.).
/// </summary>
/// <param name="Id">Slug do tema (kebab-case), usado na classe <c>theme-{id}</c>.</param>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Dark">Tokens da paleta escura (aplicada sob <c>.dark</c>).</param>
/// <param name="Light">Tokens da paleta clara (aplicada sem <c>.dark</c>).</param>
/// <param name="Vars">Overrides crus de variáveis CSS (<c>--x: valor</c>), aplicados por último.</param>
public sealed record CustomThemeDefinition(
    string Id,
    string Name,
    IReadOnlyDictionary<string, string> Dark,
    IReadOnlyDictionary<string, string> Light,
    IReadOnlyDictionary<string, string> Vars);

/// <summary>Resposta de upload/listagem de tema customizado do usuário.</summary>
public sealed record UserThemeResponse(
    string Id,
    string ThemeId,
    string Name,
    bool Active,
    CustomThemeDefinition Theme);

/// <summary>Payload de <c>PUT /api/v1/themes/active</c>.</summary>
public sealed record SetActiveThemeRequest(string ThemeId, string Mode);

/// <summary>Seleção ativa persistida do usuário (id da paleta + modo).</summary>
public sealed record ActiveThemeSelection(string ThemeId, string Mode);

/// <summary>
/// Resposta de <c>GET /api/v1/themes</c>: tema de instância
/// (<c>WEBUI_THEME_JSON</c>), temas customizados do usuário e seleção ativa.
/// </summary>
public sealed record ThemeCatalogResponse(
    CustomThemeDefinition? Instance,
    List<UserThemeResponse> Customs,
    ActiveThemeSelection Active);

/// <summary>
/// Validação/sanitização de temas JSON (SPEC-20261018-themes-custom RF-001/RF-003).
/// Aceita apenas valores de cor: hex <c>#rgb..#rrggbbaa</c>, <c>rgb()/rgba()</c>
/// e <c>var(--x[, fallback])</c>. Rejeita <c>url(</c>, <c>)</c> fora de contexto,
/// <c>;</c>, chaves, <c>&lt;&gt;</c> e javascript.
/// </summary>
public static partial class ThemeValidator
{
    /// <summary>Limite de tokens (dark+light+vars) por tema.</summary>
    public const int MaxTokens = 32;

    /// <summary>Limite do payload JSON bruto (64KB).</summary>
    public const int MaxJsonBytes = 64 * 1024;

    private const int MaxNameLength = 64;

    private static readonly HashSet<string> SemanticTokens = new(StringComparer.Ordinal)
    {
        "bg", "surface", "muted", "text", "border",
        "accent", "accent-contrast", "code-bg", "danger", "warning", "success",
    };

    /// <summary>Valida um valor de token (cor hex/rgb/var apenas).</summary>
    public static bool IsValidTokenValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        var v = value.Trim();
        if (HexColorRegex().IsMatch(v))
        {
            return true;
        }

        if (RgbColorRegex().IsMatch(v))
        {
            return true;
        }

        return VarRefRegex().IsMatch(v);
    }

    /// <summary>Valida um nome de variável CSS custom (<c>--x</c>) ou token semântico.</summary>
    public static bool IsValidTokenName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
        {
            return false;
        }

        if (SemanticTokens.Contains(name))
        {
            return true;
        }

        return CssVarNameRegex().IsMatch(name);
    }

    /// <summary>Valida slug de tema (kebab-case, começa com letra/dígito).</summary>
    public static bool IsValidThemeId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && ThemeIdRegex().IsMatch(id);

    /// <summary>Deriva um slug de tema a partir do nome.</summary>
    public static string Slugify(string name)
    {
        var slug = name.Trim().ToLowerInvariant();
        slug = NonSlugCharRegex().Replace(slug, "-");
        slug = MultiDashRegex().Replace(slug, "-").Trim('-');
        if (slug.Length == 0 || !char.IsLetterOrDigit(slug[0]))
        {
            slug = "tema-" + slug;
        }

        return slug.Length > 64 ? slug[..64].TrimEnd('-') : slug;
    }

    /// <summary>
    /// Valida e converte um payload JSON de tema em <see cref="CustomThemeDefinition"/>.
    /// Retorna erro legível em <paramref name="error"/> quando inválido.
    /// </summary>
    public static bool TryParse(
        JsonElement root, out CustomThemeDefinition? theme, out string? error)
    {
        theme = null;
        error = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "tema deve ser um objeto JSON";
            return false;
        }

        var name = root.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
        {
            error = $"campo 'name' obrigatório (máx {MaxNameLength} chars)";
            return false;
        }

        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()?.Trim()
            : null;
        id = string.IsNullOrWhiteSpace(id) ? Slugify(name) : id;
        if (!IsValidThemeId(id))
        {
            error = "campo 'id' deve ser kebab-case [a-z0-9-] (máx 64)";
            return false;
        }

        var total = 0;
        if (!TryReadPalette(root, "dark", out var dark, ref total, ref error) ||
            !TryReadPalette(root, "light", out var light, ref total, ref error) ||
            !TryReadPalette(root, "vars", out var vars, ref total, ref error))
        {
            return false;
        }

        if (total == 0)
        {
            error = "tema precisa de ao menos 1 token (dark/light/vars)";
            return false;
        }

        if (total > MaxTokens)
        {
            error = $"tema excede o máximo de {MaxTokens} tokens";
            return false;
        }

        theme = new CustomThemeDefinition(id, name, dark, light, vars);
        return true;
    }

    private static bool TryReadPalette(
        JsonElement root, string prop,
        out Dictionary<string, string> palette,
        ref int total, ref string? error)
    {
        palette = [];
        if (!root.TryGetProperty(prop, out var el))
        {
            return true;
        }

        if (el.ValueKind != JsonValueKind.Object)
        {
            error = $"seção '{prop}' deve ser um objeto de tokens";
            return false;
        }

        foreach (var item in el.EnumerateObject())
        {
            total++;
            if (!IsValidTokenName(item.Name))
            {
                error = $"token '{prop}.{item.Name}' inválido — use tokens semânticos ou --vars";
                return false;
            }

            if (item.Value.ValueKind != JsonValueKind.String ||
                !IsValidTokenValue(item.Value.GetString()))
            {
                error = $"token '{prop}.{item.Name}' com valor inválido — apenas hex/rgb()/var()";
                return false;
            }

            palette[item.Name] = item.Value.GetString()!.Trim();
        }

        return true;
    }

    [GeneratedRegex(@"^#[0-9a-fA-F]{3,8}$")]
    private static partial Regex HexColorRegex();

    [GeneratedRegex(@"^rgba?\(\s*\d{1,3}\s*[, ]\s*\d{1,3}\s*[, ]\s*\d{1,3}\s*([, /]\s*[\d.]+%?\s*)?\)$")]
    private static partial Regex RgbColorRegex();

    [GeneratedRegex(@"^var\(\s*--[A-Za-z0-9_-]+\s*(,\s*(#[0-9a-fA-F]{3,8}|rgba?\(\s*\d{1,3}\s*[, ]\s*\d{1,3}\s*[, ]\s*\d{1,3}\s*([, /]\s*[\d.]+%?\s*)?\)))?\s*\)$")]
    private static partial Regex VarRefRegex();

    [GeneratedRegex(@"^--[A-Za-z0-9_-]+$")]
    private static partial Regex CssVarNameRegex();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex ThemeIdRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlugCharRegex();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex MultiDashRegex();
}

/// <summary>
/// Gera o CSS de um tema (SPEC-20261018-themes-custom RF-001): variáveis
/// semânticas + rampa de cinzas interpolada + overrides de superfície/borda/
/// texto/acento — escopados em <c>html.theme-{id}</c> (claro) e
/// <c>html.theme-{id}.dark</c> (escuro). Sem rebuild do tailwind.css.
/// </summary>
public static partial class ThemeCssBuilder
{
    // Escala de cinzas do Tailwind (@theme --color-gray-*): 50 = mais claro,
    // 950 = mais escuro. Âncoras interpoladas: bg → surface → text.
    private static readonly string[] GrayScale =
        ["50", "100", "200", "300", "400", "500", "600", "700", "750", "800", "850", "900", "950"];

    /// <summary>Gera o conteúdo do <c>&lt;style id="user-theme"&gt;</c>.</summary>
    public static string Build(CustomThemeDefinition theme)
    {
        var css = new StringBuilder(2048);
        WritePalette(css, $"html.theme-{theme.Id}", theme.Light, theme.Vars, dark: false);
        WritePalette(css, $"html.theme-{theme.Id}.dark", theme.Dark, theme.Vars, dark: true);
        return css.ToString();
    }

    private static void WritePalette(
        StringBuilder css, string scope,
        IReadOnlyDictionary<string, string> palette,
        IReadOnlyDictionary<string, string> vars,
        bool dark)
    {
        css.Append(scope).Append(" {\n");

        foreach (var token in SemanticOrder)
        {
            if (palette.TryGetValue(token, out var value))
            {
                css.Append("  --").Append(token).Append(": ").Append(value).Append(";\n");
            }
        }

        if (TryGetColor(palette, "bg", out var bg) &&
            TryGetColor(palette, "surface", out var surface) &&
            TryGetColor(palette, "text", out var text))
        {
            WriteRamp(css, bg, surface, text, dark);
        }
        else
        {
            // Sem âncoras completas, aplica tokens diretamente nas posições-chave.
            if (TryGetColor(palette, "bg", out var b))
            {
                foreach (var step in dark ? new[] { "900", "950" } : new[] { "50" })
                {
                    WriteVar(css, $"--color-gray-{step}", b);
                }
            }

            if (TryGetColor(palette, "text", out var t))
            {
                foreach (var step in dark ? new[] { "100", "200" } : new[] { "800", "900" })
                {
                    WriteVar(css, $"--color-gray-{step}", t);
                }
            }
        }

        if (dark && TryGetColor(palette, "bg", out var deepBg))
        {
            WriteVar(css, "--color-white", deepBg); // text-white em botões escuros
            WriteVar(css, "--color-gray-925", deepBg);
        }

        if (!dark && TryGetColor(palette, "bg", out var lightBg))
        {
            WriteVar(css, "--color-white", lightBg); // bg-white = fundo da página
        }

        if (TryGetColor(palette, "accent", out var accent))
        {
            WriteVar(css, "--color-blue-400", accent);
            WriteVar(css, "--color-blue-500", accent);
            WriteVar(css, "--color-blue-600", accent);
        }

        if (TryGetColor(palette, "danger", out var danger))
        {
            WriteVar(css, "--color-red-400", danger);
            WriteVar(css, "--color-red-500", danger);
            WriteVar(css, "--color-red-600", danger);
        }

        if (TryGetColor(palette, "code-bg", out var codeBg))
        {
            WriteVar(css, "--tw-prose-pre-bg", codeBg);
        }

        foreach (var (name, value) in vars)
        {
            if (name.StartsWith("--", StringComparison.Ordinal))
            {
                WriteVar(css, name, value);
            }
        }

        css.Append("}\n");
    }

    private static void WriteRamp(StringBuilder css, Rgb bg, Rgb surface, Rgb text, bool dark)
    {
        // Claro: 50≈surface (mais claro) → 950≈text (mais escuro).
        // Escuro: 950≈bg (mais escuro) → 50≈text (mais claro); surface cobre 800/850.
        for (var i = 0; i < GrayScale.Length; i++)
        {
            var t = (double)i / (GrayScale.Length - 1);
            var color = dark
                ? Rgb.Lerp(text, bg, t)
                : Rgb.Lerp(surface, text, t);
            WriteVar(css, $"--color-gray-{GrayScale[i]}", color);
        }

        if (dark)
        {
            WriteVar(css, "--color-gray-800", surface);
            WriteVar(css, "--color-gray-850", surface);
        }
        else
        {
            WriteVar(css, "--color-gray-50", surface);
            WriteVar(css, "--color-gray-100", surface);
        }
    }

    private static void WriteVar(StringBuilder css, string name, string value) =>
        css.Append("  ").Append(name).Append(": ").Append(value).Append(";\n");

    private static void WriteVar(StringBuilder css, string name, Rgb color) =>
        WriteVar(css, name, color.ToCss());

    private static bool TryGetColor(
        IReadOnlyDictionary<string, string> palette, string token, out Rgb color)
    {
        color = default;
        return palette.TryGetValue(token, out var raw) && Rgb.TryParse(raw, out color);
    }

    private static readonly string[] SemanticOrder =
        ["bg", "surface", "muted", "text", "border", "accent", "accent-contrast",
         "code-bg", "danger", "warning", "success"];

    /// <summary>Cor RGB parseável (hex 3–8 dígitos ou rgb()/rgba()).</summary>
    private readonly partial struct Rgb
    {
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }

        private Rgb(byte r, byte g, byte b) => (R, G, B) = (r, g, b);

        public static bool TryParse(string value, out Rgb color)
        {
            color = default;
            var v = value.Trim();
            if (v.StartsWith('#'))
            {
                var hex = v[1..];
                try
                {
                    if (hex.Length is 3 or 4)
                    {
                        color = new Rgb(
                            Convert.ToByte(new string(hex[0], 2), 16),
                            Convert.ToByte(new string(hex[1], 2), 16),
                            Convert.ToByte(new string(hex[2], 2), 16));
                        return true;
                    }

                    if (hex.Length is 6 or 8)
                    {
                        color = new Rgb(
                            Convert.ToByte(hex[..2], 16),
                            Convert.ToByte(hex[2..4], 16),
                            Convert.ToByte(hex[4..6], 16));
                        return true;
                    }
                }
                catch (FormatException)
                {
                    return false;
                }

                return false;
            }

            var m = RgbFuncRegex().Match(v);
            if (m.Success)
            {
                color = new Rgb(
                    byte.Parse(m.Groups[1].Value),
                    byte.Parse(m.Groups[2].Value),
                    byte.Parse(m.Groups[3].Value));
                return true;
            }

            return false;
        }

        public static Rgb Lerp(Rgb a, Rgb b, double t) => new(
            (byte)Math.Round(a.R + ((b.R - a.R) * t)),
            (byte)Math.Round(a.G + ((b.G - a.G) * t)),
            (byte)Math.Round(a.B + ((b.B - a.B) * t)));

        public string ToCss() => $"#{R:x2}{G:x2}{B:x2}";

        [GeneratedRegex(@"^rgba?\(\s*(\d{1,3})\s*[, ]\s*(\d{1,3})\s*[, ]\s*(\d{1,3})")]
        private static partial Regex RgbFuncRegex();
    }
}
