using System.Text;
using System.Text.Json;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Constrói o system prompt restritivo do wizard de geração
/// (SPEC-20261027-context-gen-wizard RF-003): papel fixo de expert
/// frontend architect + persona + estilo + features obrigatórias +
/// projetos serializados + regras de output. O template é versionado via
/// <see cref="PromptVersion"/> (presente no próprio prompt, verificável
/// em teste/auditoria).
/// </summary>
public static class PromptBuilderService
{
    /// <summary>Versão do template — trafega como prompt_version.</summary>
    public const int PromptVersion = 1;

    /// <summary>Teto do system prompt construído (~8k tokens a 4 chars).</summary>
    public const int MaxPromptChars = 32 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>Descrição de persona por preset (persona=custom usa o
    /// texto livre do usuário).</summary>
    private static readonly Dictionary<string, string> PersonaPresets = new()
    {
        ["developer"] =
            "Terminal-style developer portfolio — dark, monospaced, "
            + "dense, keyboard-first.",
        ["designer"] =
            "Minimal grid designer portfolio — generous whitespace, "
            + "precise typography, case-study driven.",
        ["photographer"] =
            "Photographer portfolio — full-bleed imagery, lightbox "
            + "galleries, minimal chrome.",
        ["writer"] =
            "Editorial writer portfolio — long-form reading experience, "
            + "serif headlines, calm palette.",
    };

    /// <summary>Features obrigatórias por preset de estilo.</summary>
    private static readonly Dictionary<string, string> StyleDirectives = new()
    {
        ["terminal"] =
            "dark theme default, monospace type, command-palette feel",
        ["minimal"] =
            "light-leaning theme, strict grid, restrained color use",
        ["editorial"] =
            "magazine rhythm, serif/display headlines, strong hierarchy",
        ["bold"] =
            "large display type, high-contrast accent, oversized spacing",
        ["custom"] =
            "honor the declared palette tokens verbatim",
    };

    /// <summary>
    /// Monta o system prompt a partir do contexto validado. Projetos são
    /// serializados em JSON e truncados por prioridade (ordem de
    /// declaração) quando o total passa de <see cref="MaxPromptChars"/>.
    /// </summary>
    public static string BuildSystemPrompt(GenerationContext ctx)
    {
        var sb = new StringBuilder(4096);
        sb.Append("You are an expert frontend architect generating a "
            + "production-ready artifact (site/portfolio). ");
        sb.Append("prompt_version=").Append(PromptVersion).Append('\n');

        sb.Append("\n## Persona\n");
        AppendPersona(sb, ctx);

        sb.Append("\n## Visual style\n");
        AppendStyle(sb, ctx.Style);

        AppendProjects(sb, ctx);
        AppendIntegrations(sb, ctx);

        sb.Append("\n## Output rules\n")
            .Append("- Semantic, accessible HTML; production quality, "
                + "no placeholders or lorem ipsum.\n")
            .Append("- Iterate on the generated artifact when the user "
                + "asks for refinements — keep the context above as the "
                + "source of truth for persona and style.\n");

        var prompt = sb.ToString();
        return prompt.Length <= MaxPromptChars ? prompt : prompt[..MaxPromptChars];
    }

    private static void AppendPersona(StringBuilder sb, GenerationContext ctx)
    {
        if (ctx.Persona == "custom")
        {
            sb.Append(ctx.PersonaCustom).Append('\n');
        }
        else if (PersonaPresets.TryGetValue(ctx.Persona, out var persona))
        {
            sb.Append(persona).Append('\n');
        }
    }

    private static void AppendStyle(StringBuilder sb, GenStyle style)
    {
        if (StyleDirectives.TryGetValue(style.Preset, out var directive))
        {
            sb.Append("- preset ").Append(style.Preset)
                .Append(": ").Append(directive).Append('\n');
        }
        if (style.Mode is { } mode)
        {
            sb.Append("- mode: ").Append(mode).Append('\n');
        }
        if (style.Density is { } density)
        {
            sb.Append("- density: ").Append(density).Append('\n');
        }
        if (style.Palette is { Count: > 0 } palette)
        {
            sb.Append("- palette tokens:\n");
            foreach (var (name, value) in palette)
            {
                sb.Append("  - ").Append(name).Append(" = ")
                    .Append(value).Append('\n');
            }
        }
    }

    private static void AppendProjects(StringBuilder sb, GenerationContext ctx)
    {
        if (ctx.Projects is not { Count: > 0 } projects)
        {
            return;
        }

        sb.Append("\n## Projects (ordered by priority)\n");
        var baseLen = sb.Length + 512;
        var included = 0;
        foreach (var p in projects)
        {
            var line = JsonSerializer.Serialize(p, JsonOptions);
            if (baseLen + sb.Length + line.Length > MaxPromptChars)
            {
                break;
            }
            sb.Append("- ").Append(line).Append('\n');
            included++;
        }
        if (included < projects.Count)
        {
            sb.Append("- ... ").Append(projects.Count - included)
                .Append(" more project(s) truncated.\n");
        }
    }

    private static void AppendIntegrations(StringBuilder sb, GenerationContext ctx)
    {
        if (ctx.Integrations is not { Count: > 0 } integrations)
        {
            return;
        }

        sb.Append("\n## Declared integrations (metadata only — no "
            + "connector in v1)\n");
        foreach (var item in integrations)
        {
            sb.Append("- ").Append(item).Append('\n');
        }
    }
}
