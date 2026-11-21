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
            + "'commit' {key, push?} (deterministic git commit), 'gate' "
            + "{key, prompt} (parks for human approval — resolved via "
            + "dev_pipeline op=answer). Steps run in order; each result "
            + "is stored under its key and prompts may interpolate "
            + "{task}, {plan} and {results:key}. Optional per step: "
            + "'when': 'passed:key'/'failed:key' runs the step only if "
            + "an earlier step's verdict matches (skipped steps cascade "
            + "as unmet), and 'optional':true records a failing verdict "
            + "as 'issues' and continues instead of killing the "
            + "workflow — e.g. validate {optional:true} then an llm fix "
            + "step {when:'failed:lint'}. 'sub' {key, workflow:'name'} "
            + "inlines a saved workflow's steps under prefixed keys "
            + "{key}.{subKey} (max 2 levels, cycles rejected). "
            + "Track/resume/answer with "
            + "dev_pipeline op=status|resume|answer on the returned "
            + "pipeline_id. op='run' (default) executes; 'list'/'delete' "
            + "manage named workflows — save with save_as='name', re-run "
            + "later with workflow='name' (steps then optional; inline "
            + "steps and other args override the stored defaults).";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "op": {
              "type": "string",
              "enum": ["run", "list", "delete"],
              "description": "'run' (default) executes a workflow; 'list' shows your saved workflows; 'delete' removes one (requires 'workflow').",
              "default": "run"
            },
            "task": {
              "type": "string",
              "description": "Overall goal — available to every step prompt as {task}; also feeds prompts that omit 'prompt'. Required for op=run."
            },
            "workflow": {
              "type": "string",
              "description": "Name of a saved workflow — supplies steps + defaults for op=run (inline args override), and the target for op=delete."
            },
            "save_as": {
              "type": "string",
              "description": "op=run: persist this workflow under this name (steps + push/models/max_parallel) for later reuse."
            },
            "steps": {
              "type": "array",
              "description": "Ordered workflow steps. Kinds: llm | parallel | tool | validate | commit | gate.",
              "items": {
                "type": "object",
                "required": ["kind", "key"],
                "properties": {
                  "kind": { "type": "string", "enum": ["llm", "parallel", "tool", "validate", "commit", "gate", "sub"] },
                  "key": { "type": "string", "description": "Unique step id — results are stored under this key." },
                  "prompt": { "type": "string", "description": "llm/gate: the step prompt (placeholders {task}, {plan}, {results:key} allowed); gate uses it as the approval question." },
                  "when": { "type": "string", "description": "Run only if an earlier step's verdict matches: 'passed:<key>' or 'failed:<key>'." },
                  "optional": { "type": "boolean", "description": "true: a failing step records verdict 'issues' and the workflow continues.", "default": false },
                  "timeout_sec": { "type": "integer", "description": "Wallclock budget for this step in seconds — the step fails with a timeout issue when exceeded (orphaned child runs are swept by the watchdog)." },
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
                  "push": { "type": "boolean", "description": "commit: also 'git push -u origin HEAD'." },
                  "workflow": { "type": "string", "description": "sub: name of a saved workflow — its steps inline under keys '{key}.{subKey}' (max 2 levels, cycles rejected)." }
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
        var op = ReadString(args, "op")?.ToLowerInvariant() ?? "run";
        return op switch
        {
            "run" => await RunAsync(args, context, ct),
            "list" => await ListAsync(context, ct),
            "delete" => await DeleteAsync(args, context, ct),
            _ => new BuiltinToolResult(
                $"op '{op}' inválido — use run, list ou delete."),
        };
    }

    /// <summary>Lista os workflows nomeados do usuário.</summary>
    private async Task<BuiltinToolResult> ListAsync(
        BuiltinToolContext context, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var names = await scope.ServiceProvider
            .GetRequiredService<DevPipelineService>()
            .ListWorkflowsAsync(context.UserId, ct);
        return new BuiltinToolResult(names.Count == 0
            ? "Nenhum workflow salvo — use save_as='nome' numa run para "
                + "persistir."
            : $"Workflows salvos ({names.Count}): "
                + string.Join(", ", names));
    }

    /// <summary>Remove um workflow nomeado (idempotente).</summary>
    private async Task<BuiltinToolResult> DeleteAsync(
        JsonElement args, BuiltinToolContext context,
        CancellationToken ct)
    {
        var name = ReadString(args, "workflow");
        if (!DevPipelineService.ValidWorkflowName(name))
        {
            return new BuiltinToolResult(
                "Parâmetro 'workflow' inválido — nome alfanumérico "
                    + "+ -_. (até 64 chars).");
        }
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DevPipelineService>()
            .DeleteWorkflowAsync(context.UserId, name!, ct);
        return new BuiltinToolResult($"Workflow '{name}' removido.");
    }

    /// <summary>Executa um workflow — steps inline ou de um workflow
    /// nomeado; <c>save_as</c> persiste para reuso antes de disparar.</summary>
    private async Task<BuiltinToolResult> RunAsync(
        JsonElement args, BuiltinToolContext context,
        CancellationToken ct)
    {
        var task = ReadString(args, "task");
        if (string.IsNullOrWhiteSpace(task))
        {
            return new BuiltinToolResult(
                "Parâmetro 'task' é obrigatório em op=run — o objetivo "
                    + "geral do workflow.");
        }

        DevPipelineService.NamedWorkflow? stored = null;
        if (ReadString(args, "workflow") is { } wfName)
        {
            if (!DevPipelineService.ValidWorkflowName(wfName))
            {
                return new BuiltinToolResult(
                    $"Nome de workflow inválido '{wfName}'.");
            }
            using var scope = scopes.CreateScope();
            stored = await scope.ServiceProvider
                .GetRequiredService<DevPipelineService>()
                .GetWorkflowAsync(context.UserId, wfName, ct);
            if (stored is null)
            {
                return new BuiltinToolResult(
                    $"Workflow '{wfName}' não encontrado — op=list mostra "
                        + "os salvos.");
            }
        }

        List<DevPipelineService.WorkflowStep>? steps = null;
        if (args.TryGetProperty("steps", out var stepsEl)
            && stepsEl.ValueKind == JsonValueKind.Array)
        {
            try
            {
                steps = JsonSerializer.Deserialize<
                    List<DevPipelineService.WorkflowStep>>(
                        stepsEl.GetRawText(), JsonOpts);
            }
            catch (JsonException ex)
            {
                return new BuiltinToolResult(
                    $"steps malformados: {ex.Message}");
            }
        }
        steps ??= stored?.Steps;
        // Steps 'sub' são expandidos inline ({key}.{subKey}) antes da
        // validação — o loop de execução fica plano e determinístico.
        var (expanded, expandErr) =
            await ExpandSubsAsync(steps ?? [], context.UserId, ct);
        if (expandErr is { } xerr)
        {
            return new BuiltinToolResult(xerr);
        }
        var validationError = Validate(expanded);
        if (validationError is { } err)
        {
            return new BuiltinToolResult(err);
        }

        // Defaults: arg inline > workflow salvo > default do serviço.
        var push = args.TryGetProperty("push", out var p)
            ? p.ValueKind == JsonValueKind.True
            : stored?.Push ?? false;
        var workerModel = ReadString(args, "worker_model")
            ?? stored?.WorkerModel;
        var fixModel = ReadString(args, "fix_model") ?? stored?.FixModel;
        int? maxParallel = args.TryGetProperty("max_parallel", out var mp)
            && mp.TryGetInt32(out var n) ? n : stored?.MaxParallel;

        if (ReadString(args, "save_as") is { } saveAs)
        {
            if (!DevPipelineService.ValidWorkflowName(saveAs))
            {
                return new BuiltinToolResult(
                    $"save_as inválido '{saveAs}' — alfanumérico + -_. "
                        + "(até 64 chars).");
            }
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<DevPipelineService>()
                .SaveWorkflowAsync(context.UserId, saveAs,
                    new DevPipelineService.NamedWorkflow(steps!, push,
                        workerModel, fixModel, maxParallel), ct);
        }

        DevPipelineService.State state;
        using (var scope = scopes.CreateScope())
        {
            var service = scope.ServiceProvider
                .GetRequiredService<DevPipelineService>();
            state = await service.StartAsync(
                context, task, ReadString(args, "context"), push,
                maxFixAttempts: 0, ct,
                workerModel: workerModel,
                fixModel: fixModel,
                steps: expanded,
                maxFanout: maxParallel);
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
                + $"{expanded.Count} passo(s) rodam no chat filho {link}; o "
                + "resultado volta neste chat ao concluir. Acompanhe com "
                + $"dev_pipeline op=status pipeline_id={state.Id}.",
            new
            {
                pipelineId = state.Id,
                childChatId = state.ChildChatId,
                steps = expanded.Count,
                status = "running",
            });
    }

    /// <summary>Sub-workflows (v2): steps <c>{kind:"sub", workflow}</c>
    /// são expandidos inline — os steps do workflow nomeado entram com
    /// keys <c>{key}.{subKey}</c> e <c>when</c> reescrito quando aponta
    /// para key interna. Máx 2 níveis; ciclo detectado por nome no
    /// caminho de expansão (diamantes permitidos).</summary>
    private async Task<(
        List<DevPipelineService.WorkflowStep> Steps, string? Error)>
        ExpandSubsAsync(List<DevPipelineService.WorkflowStep> steps,
            string userId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var service = scope.ServiceProvider
            .GetRequiredService<DevPipelineService>();
        var expanded = new List<DevPipelineService.WorkflowStep>();
        var err = await ExpandIntoAsync(steps, service, userId,
            new HashSet<string>(StringComparer.Ordinal), expanded, 0, ct);
        return err is null ? (expanded, null) : ([], err);
    }

    /// <summary>Expansão recursiva — <paramref name="seen"/> guarda o
    /// caminho (nomes ainda em expansão), não o universo: a mesma
    /// workflow pode entrar em dois slots irmãos.</summary>
    private static async Task<string?> ExpandIntoAsync(
        List<DevPipelineService.WorkflowStep> steps,
        DevPipelineService service, string userId,
        HashSet<string> seen,
        List<DevPipelineService.WorkflowStep> expanded,
        int depth, CancellationToken ct)
    {
        foreach (var s in steps)
        {
            if (s.Kind != "sub")
            {
                expanded.Add(s);
                continue;
            }
            if (depth >= 2)
            {
                return $"sub '{s.Key}': aninhamento além de 2 níveis.";
            }
            if (!DevPipelineService.ValidWorkflowName(s.Workflow))
            {
                return $"sub '{s.Key}' exige 'workflow' — nome de "
                    + "workflow salvo válido.";
            }
            if (!seen.Add(s.Workflow!))
            {
                return $"sub '{s.Key}': ciclo '{s.Workflow}' "
                    + "(workflow referencia a si própria).";
            }
            var wf = await service.GetWorkflowAsync(
                userId, s.Workflow!, ct);
            if (wf is null)
            {
                seen.Remove(s.Workflow!);
                return $"sub '{s.Key}': workflow '{s.Workflow}' não "
                    + "encontrado — op=list mostra os salvos.";
            }
            var inner = new List<DevPipelineService.WorkflowStep>();
            var err = await ExpandIntoAsync(wf.Steps ?? [], service,
                userId, seen, inner, depth + 1, ct);
            seen.Remove(s.Workflow!);
            if (err is not null)
            {
                return err;
            }
            var innerKeys = new HashSet<string>(
                inner.Select(x => x.Key), StringComparer.Ordinal);
            foreach (var sub in inner)
            {
                var when = sub.When;
                if (when is { Length: > 0 } w
                    && w.IndexOf(':', StringComparison.Ordinal) is { } c
                    && c > 0
                    && innerKeys.Contains(w[(c + 1)..]))
                {
                    when = $"{w[..c]}:{s.Key}.{w[(c + 1)..]}";
                }
                expanded.Add(sub with
                {
                    Key = $"{s.Key}.{sub.Key}", When = when,
                });
            }
        }
        return null;
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
            if (s.When is { Length: > 0 } when && when != "always")
            {
                var colon = when.IndexOf(':', StringComparison.Ordinal);
                if (colon <= 0
                    || when[..colon] is not ("passed" or "failed"))
                {
                    return $"when inválido '{when}' no step '{s.Key}' — "
                        + "use 'passed:<key>' ou 'failed:<key>'.";
                }
                if (!keys.Contains(when[(colon + 1)..]))
                {
                    return $"when '{when}' no step '{s.Key}' referencia "
                        + "key que não é de um step anterior.";
                }
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
                case "gate":
                    break;
                case "sub":
                    if (string.IsNullOrWhiteSpace(s.Workflow))
                    {
                        return $"sub '{s.Key}' exige 'workflow' "
                            + "(nome de workflow salvo).";
                    }
                    break;
                default:
                    return $"kind desconhecido '{s.Kind}' no step '{s.Key}' "
                        + "— use llm, parallel, tool, validate, commit, "
                        + "gate ou sub.";
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
