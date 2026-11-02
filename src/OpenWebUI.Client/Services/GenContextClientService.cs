using System.Net.Http.Json;
using System.Text.Json;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Estado e chamadas do wizard de geração
/// (SPEC-20261027-context-gen-wizard RF-002): carrega o catálogo de
/// presets (<c>wwwroot/gen-context/presets.json</c>), persiste rascunho
/// em localStorage e chama <c>/api/v1/gen-context</c> via
/// <see cref="ApiService"/>.
/// </summary>
public class GenContextClientService(
    ApiService api, HttpClient http, BrowserStorage storage)
{
    private const string DraftKey = "gen-context-draft";

    /// <summary>Preset de persona do catálogo.</summary>
    public sealed record PersonaPreset(
        string Id, string Label, string Description, PresetStyle Style);

    /// <summary>Estilo sugerido por um preset de persona.</summary>
    public sealed record PresetStyle(
        string Preset, string Mode, string Density);

    /// <summary>Rascunho editável do wizard (passa de volta ao servidor
    /// validado pelo <see cref="GenContextValidator"/>).</summary>
    public sealed class Draft
    {
        /// <summary>Persona escolhida (enum do schema v1).</summary>
        public string Persona { get; set; } = "developer";

        /// <summary>Texto livre quando persona = custom.</summary>
        public string? PersonaCustom { get; set; }

        /// <summary>Preset de estilo.</summary>
        public string StylePreset { get; set; } = "terminal";

        /// <summary>Modo claro/escuro/sistema.</summary>
        public string Mode { get; set; } = "dark";

        /// <summary>Densidade do layout.</summary>
        public string Density { get; set; } = "compact";

        /// <summary>Tokens de paleta opcionais (nome → valor).</summary>
        public Dictionary<string, string>? Palette { get; set; }

        /// <summary>Projetos capturados (título/summary/url/tags/ref).</summary>
        public List<DraftProject> Projects { get; set; } = [];

        /// <summary>Integrações declaradas (ids).</summary>
        public List<string> Integrations { get; set; } = [];

        /// <summary>Prompt livre do passo 4.</summary>
        public string? FreePrompt { get; set; }
    }

    /// <summary>Projeto editável do passo 2.</summary>
    public sealed class DraftProject
    {
        /// <summary>Título obrigatório.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Resumo curto.</summary>
        public string? Summary { get; set; }

        /// <summary>URL https do projeto.</summary>
        public string? Url { get; set; }

        /// <summary>Tags separadas por vírgula no input.</summary>
        public string? TagsCsv { get; set; }
    }

    /// <summary>Carrega o catálogo de personas (estático).</summary>
    public async Task<IReadOnlyList<PersonaPreset>> LoadPresetsAsync(
        CancellationToken ct = default)
    {
        using var doc = await http
            .GetFromJsonAsync<JsonDocument>("gen-context/presets.json", ct)
            ?? JsonDocument.Parse("{}");
        var list = new List<PersonaPreset>();
        if (doc.RootElement.TryGetProperty("personas", out var personas))
        {
            foreach (var p in personas.EnumerateArray())
            {
                var style = p.GetProperty("style");
                list.Add(new PersonaPreset(
                    p.GetProperty("id").GetString()!,
                    p.GetProperty("label").GetString()!,
                    p.GetProperty("description").GetString()!,
                    new PresetStyle(
                        style.GetProperty("preset").GetString()!,
                        style.GetProperty("mode").GetString()!,
                        style.GetProperty("density").GetString()!)));
            }
        }
        return list;
    }

    /// <summary>Restaura o rascunho do localStorage (anônimo/reload).</summary>
    public async Task<Draft> LoadDraftAsync()
    {
        var raw = await storage.GetAsync(DraftKey).AsTask();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Draft();
        }
        try
        {
            return JsonSerializer.Deserialize<Draft>(raw) ?? new Draft();
        }
        catch (JsonException)
        {
            return new Draft();
        }
    }

    /// <summary>Persiste o rascunho no localStorage.</summary>
    public Task SaveDraftAsync(Draft draft) =>
        storage.SetAsync(DraftKey, JsonSerializer.Serialize(draft)).AsTask();

    /// <summary>Limpa o rascunho após o generate.</summary>
    public Task ClearDraftAsync() => storage.RemoveAsync(DraftKey).AsTask();

    /// <summary>Converte o rascunho no payload do schema v1.</summary>
    public static object ToPayload(Draft d) => new
    {
        persona = d.Persona,
        personaCustom = d.PersonaCustom,
        style = new
        {
            preset = d.StylePreset,
            mode = d.Mode,
            density = d.Density,
            palette = d.Palette is { Count: > 0 } ? d.Palette : null,
        },
        projects = d.Projects
            .Where(p => !string.IsNullOrWhiteSpace(p.Title))
            .Select(p => new
            {
                title = p.Title.Trim(),
                summary = string.IsNullOrWhiteSpace(p.Summary) ? null : p.Summary.Trim(),
                url = string.IsNullOrWhiteSpace(p.Url) ? null : p.Url.Trim(),
                tags = SplitTags(p.TagsCsv),
            })
            .ToList(),
        integrations = d.Integrations,
        freePrompt = d.FreePrompt,
    };

    private static List<string>? SplitTags(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return null;
        }
        var tags = csv.Split(',', StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries).ToList();
        return tags.Count > 0 ? tags : null;
    }
}
