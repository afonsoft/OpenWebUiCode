using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Erro de resolução <c>model#variant</c> (RF-001): variante inexistente.
/// Não é engolida pelo catch tolerante do pipeline — vira HTTP 400.
/// </summary>
public sealed class ModelVariantException(string message) : Exception(message);

/// <summary>Variante resolvida de <c>ModelEntry.MetaJson</c>.</summary>
/// <param name="Settings">Settings mesclados por cima dos params do modelo.</param>
/// <param name="Headers">Headers extras aplicados ao request HTTP do provider.</param>
/// <param name="Body">Overlay profundo aplicado ao payload final.</param>
/// <param name="ReasoningField">Nome do campo de reasoning no payload (compat).</param>
/// <param name="CacheFormat"><c>anthropic</c> → injeta cache_control ephemeral.</param>
public sealed record ModelVariant(
    JsonObject? Settings,
    JsonObject? Headers,
    JsonObject? Body,
    string? ReasoningField,
    string? CacheFormat);

/// <summary>
/// SPEC-20261018-model-variants: resolução de <c>model#variant</c> a
/// partir de <c>MetaJson</c> — <c>variants[]</c> declaradas, variants
/// sintéticas de <c>capabilities.reasoning.effort[]</c> quando
/// <c>variants</c> vazio, aliases e bloco <c>compatibility</c>.
/// A resolução é pura (sem IO) para teste direto.
/// </summary>
public static class ModelVariantService
{
    private static readonly Regex VariantId =
        new("^[a-z0-9-]+$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>Separa <c>model#variant</c> — variante só em id válido.</summary>
    public static (string BaseModel, string? VariantId) Split(string model)
    {
        var hash = model.IndexOf('#');
        if (hash <= 0 || hash == model.Length - 1)
        {
            return (model, null);
        }
        var variant = model[(hash + 1)..];
        return VariantId.IsMatch(variant) ? (model[..hash], variant) : (model, null);
    }

    /// <summary>
    /// Resolve a variante no MetaJson. Devolve a entry ou null quando a
    /// variante não existe (declarada nem sintética).
    /// </summary>
    public static ModelVariant? Resolve(string? metaJson, string variantId)
    {
        if (!TryParse(metaJson, out var meta))
        {
            return null;
        }
        var compat = meta?["compatibility"] as JsonObject;
        var reasoningField = compat?["reasoningField"]?.GetValue<string>();
        // anthropic_cache:false desliga o cache_format (RF-003 — ex.: quando
        // o gateway OmniRoute já injeta cache_control próprio).
        var cacheDisabled = compat?["anthropic_cache"] is JsonValue ac
            && ac.TryGetValue<bool>(out var flag) && !flag;
        var cacheFormat = cacheDisabled
            ? null
            : compat?["cacheFormat"]?.GetValue<string>();
        var variants = meta?["variants"] as JsonArray;
        if (variants is not null)
        {
            foreach (var node in variants)
            {
                if (node is JsonObject v
                    && string.Equals(v["id"]?.GetValue<string>(), variantId,
                        StringComparison.Ordinal))
                {
                    return new ModelVariant(
                        v["settings"] as JsonObject,
                        v["headers"] as JsonObject,
                        v["body"] as JsonObject,
                        reasoningField, cacheFormat);
                }
            }
        }

        // Variants sintéticas (RF-002): capabilities.reasoning.effort[]
        // gera #{effort} → settings[reasoningField ?? "reasoning_effort"].
        if ((variants is null || variants.Count == 0)
            && meta?["capabilities"]?["reasoning"]?["effort"] is JsonArray efforts
            && efforts.Any(e => string.Equals(
                e?.GetValue<string>(), variantId, StringComparison.Ordinal)))
        {
            return new ModelVariant(
                new JsonObject
                {
                    [reasoningField ?? "reasoning_effort"] = variantId,
                },
                null, null, reasoningField, cacheFormat);
        }
        return null;
    }

    /// <summary>
    /// Ids de variant para listagem (seletor): declaradas em
    /// <c>variants[]</c> ou sintéticas de <c>capabilities.reasoning.effort[]</c>.
    /// </summary>
    public static IReadOnlyList<string> ListVariantIds(string? metaJson)
    {
        if (!TryParse(metaJson, out var meta))
        {
            return [];
        }
        if (meta?["variants"] is JsonArray declared)
        {
            return declared
                .Select(v => (v as JsonObject)?["id"]?.GetValue<string>())
                .Where(id => id is not null && VariantId.IsMatch(id))
                .Cast<string>()
                .ToList();
        }
        if (meta?["capabilities"]?["reasoning"]?["effort"] is JsonArray efforts)
        {
            return efforts
                .Select(e => e?.GetValue<string>())
                .Where(id => id is not null && VariantId.IsMatch(id))
                .Cast<string>()
                .ToList();
        }
        return [];
    }

    /// <summary>Aliases declaradas em <c>MetaJson.aliases[]</c> para listagem.</summary>
    public static IReadOnlyList<string> ListAliases(string? metaJson) =>
        TryParse(metaJson, out var meta) && meta?["aliases"] is JsonArray aliases
            ? aliases.Select(a => a?.GetValue<string>())
                .Where(a => !string.IsNullOrWhiteSpace(a)).Cast<string>().ToList()
            : [];

    /// <summary>
    /// Devolve o id real do modelo quando <paramref name="name"/> casa com
    /// um alias em <c>MetaJson.aliases[]</c>; senão null.
    /// </summary>
    public static bool MatchesAlias(string? metaJson, string name)
    {
        return TryParse(metaJson, out var meta)
            && meta?["aliases"] is JsonArray aliases
            && aliases.Any(a => string.Equals(
                a?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Merge profundo de <paramref name="overlay"/> sobre <paramref name="target"/>.</summary>
    public static void DeepMerge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay)
        {
            if (value is JsonObject child
                && target[key] is JsonObject existing)
            {
                DeepMerge(existing, child);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private static bool TryParse(string? metaJson, out JsonObject? meta)
    {
        meta = null;
        if (string.IsNullOrWhiteSpace(metaJson))
        {
            return false;
        }
        try
        {
            meta = JsonNode.Parse(metaJson) as JsonObject;
            return meta is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
