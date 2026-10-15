using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Filtro declarativo de modelo extraído de <c>ModelEntry.MetaJson.filters</c>.</summary>
/// <param name="Type">Tipo: system_inject, params_override, regex_redact, max_tokens_cap.</param>
/// <param name="Config">Objeto de configuração do filtro.</param>
public sealed record ParsedModelFilter(string Type, JsonElement Config);

/// <summary>
/// Engine dos filtros declarativos por modelo (equivalente .NET dos "filters"
/// upstream): inlet transforma o payload enviado ao provider e outlet faz
/// pós-processamento do texto (redação por regex) na saída SSE.
/// Sem execução de código arbitrário — apenas os 4 tipos declarados.
/// </summary>
public static class ModelFilterService
{
    /// <summary>Tipo de filtro que injeta texto no system prompt.</summary>
    public const string SystemInject = "system_inject";

    /// <summary>Tipo de filtro que sobrescreve parâmetros de geração.</summary>
    public const string ParamsOverride = "params_override";

    /// <summary>Tipo de filtro que redige padrões via regex.</summary>
    public const string RegexRedact = "regex_redact";

    /// <summary>Tipo de filtro que limita max_tokens.</summary>
    public const string MaxTokensCap = "max_tokens_cap";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>Interpreta <c>MetaJson.filters</c> de forma tolerante — JSON inválido
    /// ou estrutura inesperada retorna lista vazia (filtros ruins nunca quebram o chat).</summary>
    public static List<ParsedModelFilter> Parse(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson))
        {
            return [];
        }
        try
        {
            using var doc = JsonDocument.Parse(metaJson);
            if (!doc.RootElement.TryGetProperty("filters", out var filters)
                || filters.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            var result = new List<ParsedModelFilter>();
            foreach (var item in filters.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("type", out var typeEl)
                    || typeEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var type = typeEl.GetString()!;
                var config = item.TryGetProperty("config", out var cfg)
                    && cfg.ValueKind == JsonValueKind.Object
                        ? cfg.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                result.Add(new ParsedModelFilter(type, config));
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Valida <c>MetaJson.filters</c> no salvamento do modelo.
    /// Retorna a mensagem de erro (400) ou null quando válido.</summary>
    public static string? Validate(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson))
        {
            return null;
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(metaJson);
        }
        catch (JsonException)
        {
            return "MetaJson inválido: JSON malformado.";
        }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("filters", out var filters))
            {
                return null;
            }
            if (filters.ValueKind != JsonValueKind.Array)
            {
                return "MetaJson inválido: \"filters\" deve ser um array.";
            }
            var i = 0;
            foreach (var item in filters.EnumerateArray())
            {
                i++;
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("type", out var typeEl)
                    || typeEl.ValueKind != JsonValueKind.String)
                {
                    return $"Filtro {i} inválido: \"type\" obrigatório.";
                }
                var type = typeEl.GetString()!;
                var hasConfig = item.TryGetProperty("config", out var cfg)
                    && cfg.ValueKind == JsonValueKind.Object;
                switch (type)
                {
                    case SystemInject:
                        if (!hasConfig
                            || !cfg.TryGetProperty("text", out var text)
                            || text.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(text.GetString()))
                        {
                            return $"Filtro {i} (system_inject) exige config.text.";
                        }
                        break;
                    case RegexRedact:
                        if (!hasConfig
                            || !cfg.TryGetProperty("pattern", out var pattern)
                            || pattern.ValueKind != JsonValueKind.String)
                        {
                            return $"Filtro {i} (regex_redact) exige config.pattern.";
                        }
                        try
                        {
                            _ = new Regex(pattern.GetString()!, RegexOptions.None, RegexDefaults.MatchTimeout);
                        }
                        catch (ArgumentException)
                        {
                            return $"Filtro {i} (regex_redact): regex inválida.";
                        }
                        break;
                    case MaxTokensCap:
                        if (!hasConfig
                            || !cfg.TryGetProperty("max_tokens", out var cap)
                            || cap.ValueKind != JsonValueKind.Number
                            || cap.GetInt32() < 1)
                        {
                            return $"Filtro {i} (max_tokens_cap) exige config.max_tokens ≥ 1.";
                        }
                        break;
                    case ParamsOverride:
                        break;
                    default:
                        return $"Filtro {i}: tipo desconhecido \"{type}\".";
                }
            }
            return null;
        }
    }

    /// <summary>Aplica os filtros de inlet em ordem estável sobre a requisição:
    /// system_inject, params_override, max_tokens_cap e regex_redact
    /// com stage inlet/both sobre o conteúdo das mensagens.</summary>
    public static ChatCompletionRequest ApplyInlet(
        ChatCompletionRequest request, IReadOnlyList<ParsedModelFilter> filters)
    {
        if (filters.Count == 0)
        {
            return request;
        }

        var prepend = new List<string>();
        var append = new List<string>();
        var parameters = request.Params?.ToDictionary(kv => kv.Key, kv => kv.Value);
        var redactRules = new List<(Regex Regex, string Replacement)>();

        foreach (var filter in filters)
        {
            switch (filter.Type)
            {
                case SystemInject:
                    var text = filter.Config.TryGetProperty("text", out var t)
                        ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        break;
                    }
                    var position = filter.Config.TryGetProperty("position", out var pos)
                        ? pos.GetString() : null;
                    (position == "prepend" ? prepend : append).Add(text);
                    break;
                case ParamsOverride:
                    parameters ??= [];
                    foreach (var prop in filter.Config.EnumerateObject())
                    {
                        parameters[prop.Name] = prop.Value;
                    }
                    break;
                case RegexRedact:
                    if (RedactRule(filter.Config, out var rule)
                        && RedactStage(filter.Config) is not "outlet")
                    {
                        redactRules.Add(rule);
                    }
                    break;
                case MaxTokensCap:
                    if (filter.Config.TryGetProperty("max_tokens", out var capEl)
                        && capEl.ValueKind == JsonValueKind.Number)
                    {
                        var cap = capEl.GetInt32();
                        parameters ??= [];
                        foreach (var key in new[] { "max_tokens", "num_predict" })
                        {
                            parameters[key] = parameters.TryGetValue(key, out var existing)
                                    && existing is JsonElement exEl
                                    && exEl.ValueKind == JsonValueKind.Number
                                ? Math.Min(exEl.GetInt32(), cap)
                                : cap;
                        }
                    }
                    break;
            }
        }

        var messages = request.Messages.ToList();
        if (redactRules.Count > 0)
        {
            for (var i = 0; i < messages.Count; i++)
            {
                var content = messages[i].Content;
                foreach (var (regex, replacement) in redactRules)
                {
                    content = regex.Replace(content, replacement);
                }
                if (content != messages[i].Content)
                {
                    messages[i] = messages[i] with { Content = content };
                }
            }
        }

        var injects = prepend.Concat(append).ToList();
        if (injects.Count > 0)
        {
            var merged = string.Join("\n\n", injects);
            var existing = messages.FindIndex(m => m.Role == "system");
            if (existing >= 0)
            {
                messages[existing] = messages[existing] with
                {
                    Content = prepend.Count > 0
                        ? $"{merged}\n\n{messages[existing].Content}"
                        : $"{messages[existing].Content}\n\n{merged}",
                };
            }
            else
            {
                messages.Insert(0, new ChatCompletionMessage("system", merged));
            }
        }

        return request with { Messages = messages, Params = parameters };
    }

    /// <summary>Regras de redação para o stage outlet (outlet/both).</summary>
    public static List<(Regex Regex, string Replacement)> OutletRules(
        IReadOnlyList<ParsedModelFilter> filters)
    {
        return filters
            .Select(TryOutletRule)
            .OfType<(Regex, string)>()
            .ToList();
    }

    private static (Regex, string)? TryOutletRule(ParsedModelFilter filter) =>
        filter.Type == RegexRedact
        && RedactStage(filter.Config) is not "inlet"
        && RedactRule(filter.Config, out var rule)
            ? rule
            : null;

    /// <summary>Aplica as regras de outlet sobre uma linha SSE <c>data: {json}</c>
    /// reescrevendo choices[].delta.content / message.content quando presente.</summary>
    public static string ProcessSseLine(
        string line, IReadOnlyList<(Regex Regex, string Replacement)> rules)
    {
        if (rules.Count == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
        {
            return line;
        }
        var payload = line["data:".Length..].TrimStart();
        if (payload.Length == 0 || payload[0] != '{')
        {
            return line;
        }
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            return line;
        }
        if (root is null)
        {
            return line;
        }

        var changed = false;
        if (root["choices"] is JsonArray choices)
        {
            foreach (var choice in choices)
            {
                foreach (var segment in new[] { "delta", "message" }
                             .Select(k => choice?[k])
                             .OfType<JsonObject>()
                             .Where(s => s["content"] is JsonValue c
                                         && c.TryGetValue<string>(out _)))
                {
                    {
                        var content = (JsonValue)segment["content"]!;
                        content.TryGetValue<string>(out var text);
                        // O Where acima garante TryGetValue — text é não-nulo aqui.
                        var redacted = text!;
                        foreach (var (regex, replacement) in rules)
                        {
                            redacted = regex.Replace(redacted, replacement);
                        }
                        if (redacted != text)
                        {
                            segment["content"] = redacted;
                            changed = true;
                        }
                    }
                }
            }
        }

        return changed ? $"data: {root.ToJsonString(JsonOptions)}" : line;
    }

    /// <summary>Compila uma regra de redação a partir do config do filtro.</summary>
    private static bool RedactRule(
        JsonElement config, out (Regex Regex, string Replacement) rule)
    {
        rule = default!;
        if (!config.TryGetProperty("pattern", out var pattern)
            || pattern.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        var replacement = config.TryGetProperty("replacement", out var r)
            && r.ValueKind == JsonValueKind.String
                ? r.GetString()! : "[redacted]";
        try
        {
            rule = (new Regex(pattern.GetString()!, RegexOptions.None, RegexDefaults.MatchTimeout), replacement);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Stage do filtro de redação: inlet, outlet ou both (default).</summary>
    private static string RedactStage(JsonElement config) =>
        config.TryGetProperty("stage", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? "both" : "both";
}
