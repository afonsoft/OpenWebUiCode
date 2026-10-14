using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Capacidades por modelo (SPEC-20261013-model-roles RF-003): resolve
/// <c>["vision","tools","thinking",...]</c> para <c>(provider, modelId)</c>
/// na ordem override do admin (kv <c>models:capability-overrides</c>,
/// <c>{"provider:id": ["vision"]}</c>) → capacidades reportadas pelo
/// provider no fetch (ex.: <c>capabilities</c> do <c>/api/tags</c> do
/// Ollama) → catálogo estático embedded <c>model-capabilities.json</c>
/// (glob por provider, padrão mais específico vence) → <c>[]</c> —
/// desconhecido não assume capacidade (fail-safe p/ <c>vision</c>).
/// </summary>
public sealed class ModelCapabilityResolver(
    ConfigService config,
    ILogger<ModelCapabilityResolver> logger)
{
    private const string OverrideKey = "models:capability-overrides";

    private sealed record CatalogRule(string Provider, string Pattern, string[] Capabilities);

    private readonly Lazy<List<CatalogRule>> _catalog = new(() => LoadCatalog(logger));

    /// <summary>
    /// Resolve as capacidades do modelo: override admin → reportado pelo
    /// provider (<paramref name="reported"/>) → catálogo → vazio.
    /// </summary>
    /// <param name="provider">Tipo do provider (<c>ollama</c>, <c>openai</c>, ...).</param>
    /// <param name="modelId">Id do modelo como reportado pelo provider.</param>
    /// <param name="reported">Capacidades que o provider já trouxe no fetch (pode ser null).</param>
    /// <param name="ct">Token de cancelamento.</param>
    public async Task<IReadOnlyList<string>> ResolveAsync(
        string provider, string modelId, IReadOnlyList<string>? reported,
        CancellationToken ct)
    {
        var overrides = await config.GetAsync(
            OverrideKey, new Dictionary<string, string[]>(), ct);
        if (overrides.TryGetValue($"{provider}:{modelId}", out var forced)
            || overrides.TryGetValue($"*:{modelId}", out forced)
            || overrides.TryGetValue(modelId, out forced))
        {
            return Sanitize(forced);
        }
        if (reported is { Count: > 0 })
        {
            return Sanitize(reported);
        }
        var match = _catalog.Value
            .Where(r => string.Equals(r.Provider, provider, StringComparison.OrdinalIgnoreCase)
                && Matches(r.Pattern, modelId))
            .OrderByDescending(r => r.Pattern.Length)
            .FirstOrDefault();
        return match is null ? [] : match.Capabilities;
    }

    /// <summary>Enriquece uma lista de <see cref="ModelInfo"/> com capacidades.</summary>
    public async Task<List<ModelInfo>> EnrichAsync(
        List<ModelInfo> models, CancellationToken ct)
    {
        var result = new List<ModelInfo>(models.Count);
        foreach (var m in models)
        {
            var caps = await ResolveAsync(m.Provider, m.Id, m.Capabilities, ct);
            result.Add(m with { Capabilities = caps.Count == 0 ? null : caps });
        }
        return result;
    }

    private static string[] Sanitize(IReadOnlyList<string>? caps) =>
        caps is null
            ? []
            : [.. caps.Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim().ToLowerInvariant()).Distinct()];

    /// <summary>Glob simples para id de modelo: <c>*</c> casa qualquer trecho, <c>?</c> um caractere.</summary>
    private static bool Matches(string pattern, string modelId)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(modelId, regex, RegexOptions.IgnoreCase);
    }

    private static List<CatalogRule> LoadCatalog(ILogger log)
    {
        try
        {
            var asm = typeof(ModelCapabilityResolver).Assembly;
            using var stream = asm.GetManifestResourceStream(
                "OpenWebUI.Infrastructure.Services.model-capabilities.json");
            if (stream is null)
            {
                log.LogWarning("Catálogo de capacidades ausente no assembly — modelos sem capacidades.");
                return [];
            }
            using var doc = JsonDocument.Parse(stream);
            var rules = new List<CatalogRule>();
            if (doc.RootElement.TryGetProperty("rules", out var arr))
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var provider = el.GetProperty("provider").GetString() ?? string.Empty;
                    var pattern = el.GetProperty("pattern").GetString() ?? string.Empty;
                    var caps = el.GetProperty("capabilities")
                        .EnumerateArray().Select(c => c.GetString() ?? string.Empty)
                        .Where(c => c.Length > 0).ToArray();
                    if (provider.Length > 0 && pattern.Length > 0 && caps.Length > 0)
                    {
                        rules.Add(new CatalogRule(provider, pattern, caps));
                    }
                }
            }
            return rules;
        }
        catch (Exception ex)
        {
            // Catálogo corrompido: sem capacidades — nunca derruba a listagem.
            log.LogWarning(ex, "Catálogo de capacidades inválido — modelos sem capacidades.");
            return [];
        }
    }
}
