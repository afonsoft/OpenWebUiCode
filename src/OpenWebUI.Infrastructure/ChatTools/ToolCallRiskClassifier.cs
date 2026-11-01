using System.Text.Json;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>Risco de uma tool call para o preset <c>auto</c> de aprovação.</summary>
public enum ToolCallRisk
{
    /// <summary>Somente leitura — aprova sem aviso.</summary>
    Low = 0,

    /// <summary>Efeito confinado ao workspace — aprova com aviso visível.</summary>
    Medium = 1,

    /// <summary>Ação arbitrária/externa — pergunta ao dono.</summary>
    High = 2,
}

/// <summary>
/// Classificador de risco por tool call (SPEC-20261007-chat-agent-parity
/// RF-012, padrão do security analyzer do OpenHands): usado apenas pelo
/// preset <c>auto</c> — LOW aprova, MEDIUM aprova com notice no stream,
/// HIGH cai no fluxo de pergunta. Tools não-mutáveis são LOW; builtins
/// confinadas ao workspace são MEDIUM; MCP/HTTP/python arbitrários e o
/// shell_exec <c>background</c> são HIGH; fail-safe = HIGH.
/// </summary>
public static class ToolCallRiskClassifier
{
    /// <summary>Classifica a call pelo Tool carregado + JSON de argumentos.</summary>
    public static ToolCallRisk Classify(Tool tool, string argumentsJson, string workspacePath)
    {
        if (!ToolExecutor.IsMutable(tool))
        {
            return ToolCallRisk.Low;
        }

        var url = tool.Url ?? string.Empty;
        if (url.StartsWith(BuiltinToolRegistry.UrlPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ClassifyBuiltin(url[BuiltinToolRegistry.UrlPrefix.Length..],
                argumentsJson, workspacePath);
        }

        // MCP e tools custom (HTTP/python) executam ação arbitrária.
        return ToolCallRisk.High;
    }

    /// <summary>
    /// Diz se a call mutável toca o filesystem do workspace — usado pelo
    /// preset <c>smart</c>: LOW/MEDIUM executam direto, mas alterações de
    /// arquivos (write/edit, imagem gerada, code interpreter, shell que
    /// escreve no workspace) caem no fluxo de pergunta como HIGH.
    /// </summary>
    public static bool IsFileMutation(Tool tool, string argumentsJson, string workspacePath)
    {
        if (!ToolExecutor.IsMutable(tool))
        {
            return false;
        }

        var url = tool.Url ?? string.Empty;
        if (!url.StartsWith(BuiltinToolRegistry.UrlPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return url[BuiltinToolRegistry.UrlPrefix.Length..] switch
        {
            "file_write" or "file_edit" or "generate_image" or "code_interpreter" => true,
            "shell_exec" => CommandRiskClassifier.Classify(
                TryGetString(argumentsJson, "command") ?? string.Empty,
                workspacePath).Level == CommandRiskLevel.WorkspaceWrite,
            _ => false,
        };
    }

    private static ToolCallRisk ClassifyBuiltin(
        string name, string argumentsJson, string workspacePath) => name switch
    {
        "generate_image" => ToolCallRisk.Medium,
        "code_interpreter" => ToolCallRisk.Medium,
        "file_write" or "file_edit" => ToolCallRisk.Medium,
        "job_kill" => ToolCallRisk.Medium,
        "shell_exec" => ClassifyShell(argumentsJson, workspacePath),
        _ => ToolCallRisk.High,
    };

    private static ToolCallRisk ClassifyShell(string argumentsJson, string workspacePath)
    {
        if (TryGetBool(argumentsJson, "background"))
        {
            return ToolCallRisk.High;
        }

        var command = TryGetString(argumentsJson, "command");
        if (command is null)
        {
            return ToolCallRisk.High;
        }

        return CommandRiskClassifier.Classify(command, workspacePath).Level switch
        {
            CommandRiskLevel.Safe => ToolCallRisk.Low,
            CommandRiskLevel.WorkspaceWrite => ToolCallRisk.Medium,
            _ => ToolCallRisk.High,
        };
    }

    private static string? TryGetString(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
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
            return null;
        }
    }

    private static bool TryGetBool(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(field, out var el)
                && el.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
