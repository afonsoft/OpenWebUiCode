using System.Text.Json;
using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Api.DevPipeline;

/// <summary>
/// <c>builtin:run_workflow</c> — orquestração declarativa
/// (SPEC-20261124-run-workflow): o autor (modelo ou usuário) escreve os
/// passos em JSON e o servidor executa determinístico, reutilizando a
/// maquinaria do dev_pipeline (chat filho, worktrees, validação e commit
/// determinísticos, report no chat pai). O estado vive no mesmo kv
/// <c>devpipeline:{id}</c> — <c>status</c>/<c>resume</c> usam a tool
/// <c>dev_pipeline</c> normalmente.
/// </summary>
public sealed class RunWorkflowBuiltinTool(
    IServiceScopeFactory scopes) : IBuiltinChatTool
{
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public string Name => "run_workflow";

    /// <inheritdoc />
    public string Description =>
        "Run a declarative workflow on this repo — you write the steps, "
            + "the server executes them deterministically in a child chat "
            + "(isolated worktrees for parallel steps, real test runs for "
            + "validate, real git for commit — nothing depends on the "
            + "model remembering). Kinds: 'llm' {key, prompt}, 'parallel' "
            + "{key, items:[{id, prompt, files?}]} (one worker chat + "
            + "worktree per item, merged after each wave), 'tool' "
            + "{key, tool, args} (any builtin, e.g. agent_validate), "
            + "'validate' {key} (deterministic ProjectValidation), "
            + "'commit' {key, push?} (deterministic git commit). Steps "
            + "run in order; each result is stored under its key and "
            + "prompts may interpolate {task}, {plan} and {results:key}. "
            + "A failed step stops the workflow — chain an 'llm' fix step "
            + "that reads {results:validate-key} if you want retries. "
            + "Track/resume with dev_pipeline op=status|resume on the "
            + "returned pipeline_id.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "required": ["task", "steps"],
          "properties": {
            "task": {
              "type": "string",
              "description": "Overall goal — available to every step prompt as {task}; also feeds prompts that omit 'prompt'."
            },
            "steps": {
              "type": "array",
              "description": "Ordered workflow steps. Kinds: llm | parallel | tool | validate | commit.",
              "items": {
                "type": "object",
                "required": ["kind", "key"],
                "properties": {
                  "kind": { "type": "string", "enum": ["llm", "parallel", "tool", "validate", "commit"] },
                  "key": { "type": "string", "description": "Unique step id — results are stored under this key." },
                  "prompt": { "type": "string", "description": "llm: the step prompt (placeholders {task}, {plan}, {results:key} allowed)." },
                  "items": {
                    "type": "array",
                    "description": "parallel: one worker chat + isolated worktree per item.",
                    "items": {
                      "type": "object",
                      "required": ["id", "prompt"],
                      "properties": {
                        "id": { "type": "string" },
                        "prompt": { "type": "string" },
                        "files": { "type": "array", "items": { "type": "string" }, "description": "File scope hint." }
                      }
                    }
                  },
                  "tool": { "type": "string", "description": "tool: builtin name, e.g. 'agent_validate'." },
                  "args": { "description": "tool: args — JSON object or JSON-encoded string." },
                  "push": { "type": "boolean", "description": "commit: also 'git push -u origin HEAD'." }
                }
              }
            },
            "context": {
              "type": "string",
              "description": "Optional extra context appended to llm-step prompts via the task body."
            },
            "push": {
              "type": "boolean",
              "description": "Default push for commit steps that don't set their own. Default false.",
              "default": false
            },
            "worker_model": {
              "type": "string",
              "description": "Model ref for llm/parallel steps (chain:/fusion:/adaptive: accepted). Omitting inside a fusion run defaults to the sidekick chain."
            },
            "fix_model": {
              "type": "string",
              "description": "Model ref stored under 'fix' — use a step key equal to 'fix' or override per-step via worker_model; omitting inside fusion defaults to the leader."
            },
            "max_parallel": {
              "type": "integer",
              "description": "Cap on concurrent parallel workers per wave (default 4).",
              "default": 4
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
        var task = ReadString(args, "task");
        if (string.IsNullOrWhiteSpace(task))
        {
            return new BuiltinToolResult(
                "Parâmetro 'task' é obrigatório — o objetivo geral do workflow.");
        }
        if (!args.TryGetProperty("steps", out var stepsEl)
            || stepsEl.ValueKind != JsonValueKind.Array)
        {
            return new BuiltinToolResult(
                "Parâmetro 'steps' é obrigatório — array de passos "
                    + "{kind, key, …}.");
        }
        List<DevPipelineService.WorkflowStep>? steps;
        try
        {
            steps = JsonSerializer.Deserialize<List<DevPipelineService.WorkflowStep>>(
                stepsEl.GetRawText(), JsonOpts);
        }
        catch (JsonException ex)
        {
            return new BuiltinToolResult(
                $"steps malformados: {ex.Message}");
        }
        var validationError = Validate(steps);
        if (validationError is { } err)
        {
            return new BuiltinToolResult(err);
        }

        var push = args.TryGetProperty("push", out var p)
            && p.ValueKind == JsonValueKind.True;
        DevPipelineService.State state;
        using (var scope = scopes.CreateScope())
        {
            var service = scope.ServiceProvider
                .GetRequiredService<DevPipelineService>();
            state = await service.StartAsync(
                context, task, ReadString(args, "context"), push,
                maxFixAttempts: 0, ct,
                workerModel: ReadString(args, "worker_model"),
                fixModel: ReadString(args, "fix_model"),
                steps: steps,
                maxFanout: args.TryGetProperty("max_parallel", out var mp)
                    && mp.TryGetInt32(out var n) ? n : null);
        }

        _ = Task.Run(async () =>
        {
            using var s = scopes.CreateScope();
            await s.ServiceProvider.GetRequiredService<DevPipelineService>()
                .RunAsync(state.Id);
        });

        var link = $"/c/{state.ChildChatId}";
        return new BuiltinToolResult(
            $"Workflow {state.Id} iniciado em background — "
                + $"{steps!.Count} passo(s) rodam no chat filho {link}; o "
                + "resultado volta neste chat ao concluir. Acompanhe com "
                + $"dev_pipeline op=status pipeline_id={state.Id}.",
            new
            {
                pipelineId = state.Id,
                childChatId = state.ChildChatId,
                steps = steps!.Count,
                status = "running",
            });
    }

    /// <summary>Validação estrutural dos steps — kind conhecido, key
    /// única, campos obrigatórios por kind.</summary>
    internal static string? Validate(
        List<DevPipelineService.WorkflowStep>? steps)
    {
        if (steps is not { Count: > 0 })
        {
            return "steps vazio — declare ao menos um passo.";
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in steps)
        {
            if (string.IsNullOrWhiteSpace(s.Key))
            {
                return "todo step exige 'key' não-vazia.";
            }
            if (!keys.Add(s.Key))
            {
                return $"key duplicada '{s.Key}' — keys são únicas.";
            }
            switch (s.Kind)
            {
                case "llm":
                    break; // prompt opcional — {task} é o default
                case "parallel":
                    if (s.Items is not { Count: > 0 })
                    {
                        return $"parallel '{s.Key}' exige 'items' não-vazio.";
                    }
                    if (s.Items.Any(i => string.IsNullOrWhiteSpace(i.Id)))
                    {
                        return $"parallel '{s.Key}': todo item exige 'id'.";
                    }
                    break;
                case "tool":
                    if (string.IsNullOrWhiteSpace(s.Tool))
                    {
                        return $"tool '{s.Key}' exige 'tool' (nome builtin).";
                    }
                    break;
                case "validate":
                case "commit":
                    break;
                default:
                    return $"kind desconhecido '{s.Kind}' no step '{s.Key}' "
                        + "— use llm, parallel, tool, validate ou commit.";
            }
        }
        return null;
    }

    private static string? ReadString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.String
                ? el.GetString()?.Trim()
                : null;
}
