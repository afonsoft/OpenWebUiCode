using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Reparo determinístico de <c>function.arguments</c> malformados
/// (SPEC-20261123-tool-arg-repair — portado do Devin CLI,
/// <c>agent-ext/src/tool_arg_leak_recovery.rs</c>): JSON truncado, vírgula
/// sobrando, fence de markdown e campos <c>required</c> ausentes com
/// <c>default</c> no schema são consertados antes de falhar a call. Nunca
/// inventa valor para campo required sem default — nesses casos a call
/// falha normalmente.
/// </summary>
public static class ToolArgRepair
{
    /// <summary>Acima deste tamanho não vale tentar reparar (SPEC: 256KB).</summary>
    private const int MaxRepairBytes = 256 * 1024;

    /// <summary>
    /// Tenta devolver um JSON de argumentos parseável. Retorna true quando o
    /// texto saiu diferente do original (reparo aplicado) — false quando já
    /// era válido ou quando não há reparo seguro.
    /// </summary>
    public static bool TryRepair(string? argumentsJson, out string repaired)
    {
        repaired = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
        if (IsParseable(repaired))
        {
            return false;
        }
        if (repaired.Length > MaxRepairBytes)
        {
            return false;
        }

        var candidate = StripFence(repaired.Trim());
        candidate = RemoveTrailingCommas(candidate);
        candidate = CloseStructure(candidate);
        if (IsParseable(candidate))
        {
            repaired = candidate;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Reparo por schema: preenche propriedades <c>required</c> ausentes que
    /// declarem <c>default</c> no <paramref name="parametersJson"/> da tool.
    /// Retorna false se algum required continuar ausente (a call deve falhar
    /// em vez de executar com valor inventado).
    /// </summary>
    public static bool ApplySchemaDefaults(
        string argumentsJson, string? parametersJson, out string result)
    {
        result = argumentsJson;
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return true;
        }
        try
        {
            var args = JsonNode.Parse(result) as JsonObject ?? new JsonObject();
            var schema = JsonNode.Parse(parametersJson) as JsonObject;
            // Aceita o parameters.json puro ou o spec completo da tool
            // ({"function":{"parameters":{…}}} ou {"parameters":{…}}).
            schema = (schema?["function"]?["parameters"] as JsonObject)
                ?? (schema?["parameters"] as JsonObject)
                ?? schema;
            if (schema is null)
            {
                return true;
            }
            var required = schema["required"] as JsonArray;
            var properties = schema["properties"] as JsonObject;
            if (required is not null && properties is not null)
            {
                foreach (var req in required)
                {
                    var name = req?.GetValue<string>();
                    if (name is null || args.ContainsKey(name))
                    {
                        continue;
                    }
                    if (properties[name]?["default"] is { } def)
                    {
                        args[name] = def.DeepClone();
                    }
                    else
                    {
                        return false; // required sem default → falha normal.
                    }
                }
            }
            result = args.ToJsonString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsParseable(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Remove fence <c>```json … ```</c> que alguns modelos emitem.</summary>
    private static string StripFence(string json)
    {
        if (!json.StartsWith("```", StringComparison.Ordinal))
        {
            return json;
        }
        var start = json.IndexOf('\n');
        var end = json.LastIndexOf("```", StringComparison.Ordinal);
        return start > 0 && end > start
            ? json[(start + 1)..end].Trim()
            : json;
    }

    /// <summary>Remove vírgulas imediatamente antes de <c>}</c>/<c>]</c>.</summary>
    private static string RemoveTrailingCommas(string json)
    {
        var sb = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];
            if (escaped) { sb.Append(c); escaped = false; continue; }
            if (inString && c == '\\') { sb.Append(c); escaped = true; continue; }
            if (c == '"') { inString = !inString; sb.Append(c); continue; }
            if (!inString && c == ',')
            {
                var j = i + 1;
                while (j < json.Length && char.IsWhiteSpace(json[j])) { j++; }
                if (j < json.Length && (json[j] == '}' || json[j] == ']'))
                {
                    continue; // vírgula sobrando — pula.
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Fecha aspas e estruturas pendentes no fim (JSON truncado pelo
    /// output-limit do modelo). Escape/control chars dentro de strings são
    /// normalizados pelo parser depois — aqui só balanceamos a estrutura.
    /// </summary>
    private static string CloseStructure(string json)
    {
        var stack = new Stack<char>(16);
        var inString = false;
        var escaped = false;
        foreach (var c in json)
        {
            if (escaped) { escaped = false; continue; }
            if (inString)
            {
                if (c == '\\') { escaped = true; }
                else if (c == '"') { inString = false; }
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}' or ']':
                    if (stack.Count > 0 && stack.Peek() == c) { stack.Pop(); }
                    break;
            }
        }
        var sb = new StringBuilder(json);
        if (inString) { sb.Append('"'); }
        while (stack.Count > 0) { sb.Append(stack.Pop()); }
        return sb.ToString();
    }
}
