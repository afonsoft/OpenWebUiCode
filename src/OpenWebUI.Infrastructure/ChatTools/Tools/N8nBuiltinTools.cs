using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:n8n_list_workflows</c> — lista os workflows do n8n
/// configurado (SPEC-20261007-chat-agent-parity RF-020). Leitura —
/// não passa pelo gate de aprovação.
/// </summary>
public sealed class N8nListWorkflowsBuiltinTool(N8nService n8n) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "n8n_list_workflows";

    /// <inheritdoc />
    public string Description =>
        "List n8n workflows — call before n8n_trigger.";

    /// <inheritdoc />
    public string ParametersJson => """{"type":"object","properties":{}}""";

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;
    /// <inheritdoc />
    public bool SafeInParallel => true;


    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        IReadOnlyList<N8nWorkflowItem> items;
        try
        {
            items = await n8n.ListWorkflowsAsync(ct);
        }
        catch (InvalidOperationException ex)
        {
            return new BuiltinToolResult($"Erro: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return new BuiltinToolResult($"Erro: falha ao contatar o n8n — {ex.Message}");
        }

        if (items.Count == 0)
        {
            return new BuiltinToolResult("O n8n não tem workflows cadastrados.");
        }

        var lines = items.Select(w =>
            $"- {w.Name} (id: {w.Id}){(w.Active ? " [ativo]" : string.Empty)}");
        return new BuiltinToolResult(
            $"{items.Count} workflow(s) no n8n:\n{string.Join('\n', lines)}",
            new { workflows = items });
    }
}

/// <summary>
/// <c>builtin:n8n_trigger</c> — dispara um workflow do n8n por id
/// (API pública) ou por webhook de produção
/// (SPEC-20261007-chat-agent-parity RF-020). Efeito externo → passa
/// pelo gate de aprovação.
/// </summary>
public sealed class N8nTriggerBuiltinTool(N8nService n8n) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "n8n_trigger";

    /// <inheritdoc />
    public string Description =>
        "Run an n8n workflow — call to trigger automation.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "workflow_id": {
              "type": "string",
              "description": "Workflow id in n8n (executes via public API)."
            },
            "webhook_path": {
              "type": "string",
              "description": "Production webhook path of the workflow (without leading /webhook/)."
            },
            "payload": {
              "type": "object",
              "description": "Workflow input data (optional JSON)."
            }
          }
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Exec;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var workflowId = ReadString(args, "workflow_id");
        var webhookPath = ReadString(args, "webhook_path");
        JsonElement? payload = args.TryGetProperty("payload", out var p)
                               && p.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? p.Clone()
            : null;

        if (string.IsNullOrWhiteSpace(workflowId) == string.IsNullOrWhiteSpace(webhookPath))
        {
            return new BuiltinToolResult(
                "Informe exatamente um de 'workflow_id' ou 'webhook_path'.");
        }

        N8nTriggerResult result;
        try
        {
            result = await n8n.TriggerAsync(workflowId, webhookPath, payload, ct);
        }
        catch (InvalidOperationException ex)
        {
            return new BuiltinToolResult($"Erro: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return new BuiltinToolResult($"Erro: falha ao contatar o n8n — {ex.Message}");
        }

        var alvo = webhookPath is not null ? $"webhook {webhookPath}" : $"workflow {workflowId}";
        return result.Success
            ? new BuiltinToolResult(
                $"Workflow disparado ({alvo}) — HTTP {result.StatusCode}: {result.Body}",
                new { workflowId, webhookPath, result.StatusCode })
            : new BuiltinToolResult(
                $"Erro: disparo de {alvo} falhou — HTTP {result.StatusCode}: {result.Body}");
    }

    private static string? ReadString(JsonElement args, string field) =>
        args.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
