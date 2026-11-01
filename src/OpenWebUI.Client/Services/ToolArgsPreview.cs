using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenWebUI.Client.Services;

/// <summary>
/// Extrai campos-chave do <c>argsPreview</c> de uma tool call
/// (SPEC-20261007-chat-agent-ux RF-001/RF-003). O preview pode vir truncado
/// a 2KB — o JSON pode não fechar — então primeiro tenta
/// <see cref="JsonDocument"/> e, se falhar, cai num regex tolerante por
/// campo (<c>"key": "value"</c>).
/// </summary>
public static class ToolArgsPreview
{
    /// <summary>Campo-chave usado como resumo por tool (sufixo após ':').</summary>
    private static readonly (string Tool, string Field)[] SummaryFields =
    [
        ("shell_exec", "command"),
        ("code_interpreter", "language"),
        ("fetch_url", "url"),
        ("web_search", "query"),
        ("generate_image", "prompt"),
        ("job_output", "job_id"),
        ("job_kill", "job_id"),
        ("file_read", "path"),
        ("file_write", "path"),
        ("file_edit", "path"),
        ("file_list", "path"),
        ("file_grep", "pattern"),
        ("file_glob", "pattern"),
    ];

    /// <summary>
    /// Resumo de uma linha para o cabeçalho do card/prompt — o campo
    /// principal da tool conhecida (ex.: comando do shell_exec).
    /// </summary>
    public static string? Summary(string toolName, string? argsJson)
    {
        var suffix = toolName.Split(':', '/').Last();
        var field = SummaryFields.FirstOrDefault(f => f.Tool == suffix).Field;
        return field is null ? null : TryGetField(argsJson, field);
    }

    /// <summary>
    /// Valor de um campo string de primeiro nível do JSON de args;
    /// tolera preview truncado via regex quando o parse falha.
    /// </summary>
    public static string? TryGetField(string? argsJson, string field)
    {
        if (string.IsNullOrEmpty(argsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(field, out var el))
            {
                return el.ValueKind == JsonValueKind.String
                    ? el.GetString()
                    : el.GetRawText();
            }
            return null;
        }
        catch (JsonException)
        {
            // Preview truncado no meio do JSON — regex tolerante.
        }

        var match = FieldRegex(field).Match(argsJson);
        return match.Success ? Unescape(match.Groups[1].Value) : null;
    }

    private static string Unescape(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<string>($"\"{raw}\"") ?? raw;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static Regex FieldRegex(string field) =>
        new($"\"{Regex.Escape(field)}\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
            RegexOptions.Compiled, RegexDefaults.MatchTimeout);
}
