using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenWebUI.Application.Contracts;

/// <summary>
/// Contexto estruturado capturado pelo wizard de geração
/// (SPEC-20261027-context-gen-wizard RF-001 — JSON Schema version="1").
/// </summary>
public sealed record GenerationContext(
    string Persona,
    string? PersonaCustom,
    GenStyle Style,
    List<GenProject> Projects,
    List<string> Integrations,
    string? FreePrompt,
    string? Locale);

/// <summary>Estilo visual escolhido no passo 3 do wizard.</summary>
public sealed record GenStyle(
    string Preset,
    string? Mode,
    Dictionary<string, string>? Palette,
    string? Density);

/// <summary>Projeto informado no passo 2 do wizard.</summary>
public sealed record GenProject(
    string Title,
    string? Summary,
    string? Url,
    List<string>? Tags,
    string? Ref);

/// <summary>Payload do POST <c>/api/v1/gen-context/generate</c>.</summary>
public sealed record GenContextGenerateRequest(
    string? Model,
    string? Content,
    IReadOnlyDictionary<string, object>? Params);

/// <summary>Resposta do generate: chat criado + run enfileirada.</summary>
public sealed record GenContextGenerateResponse(string ChatId, string RunId);

/// <summary>
/// Validação server-side do <see cref="GenerationContext"/> (RF-001):
/// enums de persona/preset/mode/densidade, limites de itens e tamanho,
/// URLs apenas https (ou <c>ref://</c> ligando a references) e tokens de
/// paleta sanitizados pelo mesmo guard do themes-custom.
/// </summary>
public static class GenContextValidator
{
    /// <summary>Versão do schema do payload.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Personas aceitas (catálogo de presets extensível).</summary>
    public static readonly string[] Personas =
        ["developer", "designer", "photographer", "writer", "custom"];

    /// <summary>Presets de estilo aceitos.</summary>
    public static readonly string[] StylePresets =
        ["terminal", "minimal", "editorial", "bold", "custom"];

    /// <summary>Modos de tema aceitos.</summary>
    public static readonly string[] Modes = ["system", "light", "dark"];

    /// <summary>Densidades aceitas.</summary>
    public static readonly string[] Densities =
        ["compact", "comfortable", "spacious"];

    /// <summary>Máximo de projetos por contexto.</summary>
    public const int MaxProjects = 20;

    /// <summary>Máximo de integrações declaradas.</summary>
    public const int MaxIntegrations = 12;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Deserializa e valida o JSON bruto do contexto.</summary>
    public static bool TryParse(
        string json, out GenerationContext? context, out string? error)
    {
        context = null;
        error = null;
        GenerationContext? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<GenerationContext>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"JSON inválido: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = "contexto deve ser um objeto JSON";
            return false;
        }

        if (!TryValidate(parsed, out error))
        {
            return false;
        }

        context = parsed;
        return true;
    }

    /// <summary>Valida um contexto já tipado (wizard ou JSON).</summary>
    public static bool TryValidate(GenerationContext ctx, out string? error)
    {
        error = null;
        if (!Personas.Contains(ctx.Persona))
        {
            error = $"campo 'persona' deve ser um de: {string.Join(", ", Personas)}";
            return false;
        }

        if (ctx.Persona == "custom"
            && string.IsNullOrWhiteSpace(ctx.PersonaCustom))
        {
            error = "campo 'personaCustom' obrigatório quando persona = custom";
            return false;
        }

        if (ctx.PersonaCustom is { Length: > 200 })
        {
            error = "campo 'personaCustom' excede 200 chars";
            return false;
        }

        if (ctx.Style is null)
        {
            error = "campo 'style' obrigatório";
            return false;
        }

        return TryValidateStyle(ctx.Style, ref error)
            && TryValidateProjects(ctx.Projects, ref error)
            && TryValidateIntegrations(ctx.Integrations, ref error)
            && TryValidateFreePrompt(ctx.FreePrompt, ref error);
    }

    private static bool TryValidateStyle(GenStyle style, ref string? error)
    {
        if (!StylePresets.Contains(style.Preset))
        {
            error = $"campo 'style.preset' deve ser um de: {string.Join(", ", StylePresets)}";
            return false;
        }

        if (style.Mode is not null && !Modes.Contains(style.Mode))
        {
            error = $"campo 'style.mode' deve ser um de: {string.Join(", ", Modes)}";
            return false;
        }

        if (style.Density is not null && !Densities.Contains(style.Density))
        {
            error = $"campo 'style.density' deve ser um de: {string.Join(", ", Densities)}";
            return false;
        }

        if (style.Palette is { } palette)
        {
            foreach (var (name, value) in palette)
            {
                if (!ThemeValidator.IsValidTokenName(name)
                    || !ThemeValidator.IsValidTokenValue(value))
                {
                    error = $"token 'style.palette.{name}' inválido — apenas hex/rgb()/var()";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryValidateProjects(
        List<GenProject>? projects, ref string? error)
    {
        if (projects is null)
        {
            return true;
        }

        if (projects.Count > MaxProjects)
        {
            error = $"campo 'projects' excede o máximo de {MaxProjects} itens";
            return false;
        }

        for (var i = 0; i < projects.Count; i++)
        {
            var p = projects[i];
            if (string.IsNullOrWhiteSpace(p.Title) || p.Title.Length > 120)
            {
                error = $"campo 'projects[{i}].title' obrigatório (máx 120 chars)";
                return false;
            }

            if (p.Summary is { Length: > 500 })
            {
                error = $"campo 'projects[{i}].summary' excede 500 chars";
                return false;
            }

            if (p.Url is { } url)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    error = $"campo 'projects[{i}].url' deve ser uma URI absoluta";
                    return false;
                }

                if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"campo 'projects[{i}].url' aceita apenas https://";
                    return false;
                }
            }

            if (p.Ref is { } r
                && !r.StartsWith("ref://", StringComparison.Ordinal))
            {
                error = $"campo 'projects[{i}].ref' deve começar com ref://";
                return false;
            }

            if (p.Tags is { Count: > 8 })
            {
                error = $"campo 'projects[{i}].tags' excede 8 itens";
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateIntegrations(
        List<string>? integrations, ref string? error)
    {
        if (integrations is null)
        {
            return true;
        }

        if (integrations.Count > MaxIntegrations)
        {
            error = $"campo 'integrations' excede o máximo de {MaxIntegrations} itens";
            return false;
        }

        foreach (var item in integrations)
        {
            if (string.IsNullOrWhiteSpace(item) || item.Length > 40)
            {
                error = "campo 'integrations[]' aceita ids de até 40 chars";
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateFreePrompt(
        string? freePrompt, ref string? error)
    {
        if (freePrompt is { Length: > 4000 })
        {
            error = "campo 'freePrompt' excede 4000 chars";
            return false;
        }

        return true;
    }
}
