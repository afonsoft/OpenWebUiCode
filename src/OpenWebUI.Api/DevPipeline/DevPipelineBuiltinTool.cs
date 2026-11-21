using System.Text.Json;
using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Api.DevPipeline;

/// <summary>
/// <c>builtin:dev_pipeline</c> — dispara o pipeline orquestrado de
/// desenvolvimento (SPEC-20261121-dev-pipeline RF-003): o servidor
/// encadeia <c>implement → test → validate → commit</c> em chat filho —
/// validação e commit são determinísticos (não dependem do modelo). Ops:
/// <c>start</c> cria e dispara em background; <c>status</c> lê o kv;
/// <c>resume</c> re-dá marcha numa pipeline interrompida por restart.
/// </summary>
public sealed class DevPipelineBuiltinTool(
    IServiceScopeFactory scopes) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "dev_pipeline";

    /// <inheritdoc />
    public string Description =>
        "Orchestrate a development pipeline on this repo — the server runs "
            + "child-agent phases implement → test, then validates "
            + "(deterministic test suite) and commits (deterministic git) "
            + "for you, reporting back to this chat when done. op='start' "
            + "launches in background; 'status'/'resume' inspect or "
            + "re-drive a pipeline by pipeline_id.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "op": {
              "type": "string",
              "enum": ["start", "status", "resume"],
              "description": "'start' (default) launches a new pipeline; 'status' reads its state; 'resume' re-drives an interrupted one."
            },
            "task": {
              "type": "string",
              "description": "What to implement — self-contained (files, behavior, constraints); required for op=start."
            },
            "context": {
              "type": "string",
              "description": "Optional extra context appended to the implement-phase prompt."
            },
            "push": {
              "type": "boolean",
              "description": "true: after committing, also 'git push -u origin HEAD'. Default false (commit only).",
              "default": false
            },
            "max_fix_attempts": {
              "type": "integer",
              "description": "LLM fix runs allowed after a failed validation or verify issues (default 1).",
              "default": 1
            },
            "plan": {
              "type": "boolean",
              "description": "true (default): a plan phase decomposes the task before implement — planner→worker split.",
              "default": true
            },
            "worker_model": {
              "type": "string",
              "description": "Optional model ref for the plan/implement/test/verify phases — typically the cheaper worker model. chain:/fusion: refs are accepted. When omitted and this run is fusion:{leader}>{sidekick}, workers default to the sidekick chain and plan to the leader."
            },
            "fix_model": {
              "type": "string",
              "description": "Optional model ref for the fix phase — typically the stronger diagnoser. When omitted and this run is fusion, fix defaults to the leader."
            },
            "pipeline_id": {
              "type": "string",
              "description": "Pipeline id (returned by op=start) — required for status/resume."
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
        var op = ReadString(args, "op")?.ToLowerInvariant() ?? "start";
        return op switch
        {
            "start" => await StartAsync(args, context, ct),
            "status" => await StatusAsync(args, ct),
            "resume" => await ResumeAsync(args, ct),
            _ => new BuiltinToolResult(
                $"op '{op}' inválido — use start, status ou resume."),
        };
    }

    /// <summary>Cria kv + chat filho e dispara o runner em background.</summary>
    private async Task<BuiltinToolResult> StartAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var task = ReadString(args, "task");
        if (string.IsNullOrWhiteSpace(task))
        {
            return new BuiltinToolResult(
                "Parâmetro 'task' é obrigatório em op=start — descreva o que "
                    + "implementar de forma auto-contida.");
        }
        var push = args.TryGetProperty("push", out var p)
            && p.ValueKind == JsonValueKind.True;
        var maxFix = args.TryGetProperty("max_fix_attempts", out var m)
            && m.TryGetInt32(out var n) ? n : 1;
        var plan = !args.TryGetProperty("plan", out var pl)
            || pl.ValueKind != JsonValueKind.False;

        DevPipelineService.State state;
        using (var scope = scopes.CreateScope())
        {
            var service = scope.ServiceProvider
                .GetRequiredService<DevPipelineService>();
            state = await service.StartAsync(
                context, task, ReadString(args, "context"), push, maxFix, ct,
                enablePlan: plan,
                workerModel: ReadString(args, "worker_model"),
                fixModel: ReadString(args, "fix_model"));
        }

        // Runner em background: escopos próprios por fase — seguro para
        // sobreviver ao retorno da tool e ao fim da run pai.
        _ = Task.Run(async () =>
        {
            using var s = scopes.CreateScope();
            await s.ServiceProvider.GetRequiredService<DevPipelineService>()
                .RunAsync(state.Id);
        });

        var link = $"/c/{state.ChildChatId}";
        return new BuiltinToolResult(
            $"Pipeline {state.Id} iniciada em background — fases "
                + "implement → test → validate → commit rodam no chat filho "
                + $"{link}; o resultado volta neste chat ao concluir. "
                + $"Acompanhe com dev_pipeline op=status pipeline_id={state.Id}.",
            new
            {
                pipelineId = state.Id,
                childChatId = state.ChildChatId,
                status = "running",
            });
    }

    /// <summary>Lê o kv e devolve o estado corrente da pipeline.</summary>
    private async Task<BuiltinToolResult> StatusAsync(
        JsonElement args, CancellationToken ct)
    {
        var id = ReadString(args, "pipeline_id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return new BuiltinToolResult(
                "Parâmetro 'pipeline_id' é obrigatório em op=status.");
        }
        using var scope = scopes.CreateScope();
        var state = await scope.ServiceProvider
            .GetRequiredService<DevPipelineService>().GetAsync(id, ct);
        return state is null
            ? new BuiltinToolResult($"Pipeline '{id}' não encontrada.")
            : new BuiltinToolResult(
                $"Pipeline {id}: status={state.Status}, fase={state.Phase}, "
                    + $"runs=[{string.Join(", ", state.Runs.Keys)}]"
                    + (state.ValidationOutput is { } v ? $", validação={v}" : "")
                    + (state.CommitSha is { } s ? $", commit={s}" : "")
                    + (state.Error is { } e ? $", erro={e}" : "")
                    + $" — chat /c/{state.ChildChatId}",
                new
                {
                    pipelineId = id,
                    state.Status,
                    state.Phase,
                    runs = state.Runs,
                    validation = state.ValidationOutput,
                    commit = state.CommitSha,
                    error = state.Error,
                });
    }

    /// <summary>Re-dispara pipeline 'running' sem run ativa (restart do
    /// app matou o runner — o kv sobrevive).</summary>
    private async Task<BuiltinToolResult> ResumeAsync(
        JsonElement args, CancellationToken ct)
    {
        var id = ReadString(args, "pipeline_id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return new BuiltinToolResult(
                "Parâmetro 'pipeline_id' é obrigatório em op=resume.");
        }
        using var scope = scopes.CreateScope();
        var service = scope.ServiceProvider
            .GetRequiredService<DevPipelineService>();
        var state = await service.GetAsync(id, ct);
        if (state is null)
        {
            return new BuiltinToolResult($"Pipeline '{id}' não encontrada.");
        }
        if (state.Status != "running")
        {
            return new BuiltinToolResult(
                $"Pipeline {id} está '{state.Status}' — nada a retomar.",
                new { pipelineId = id, state.Status });
        }

        _ = Task.Run(async () =>
        {
            using var s = scopes.CreateScope();
            await s.ServiceProvider.GetRequiredService<DevPipelineService>()
                .RunAsync(state.Id);
        });
        return new BuiltinToolResult(
            $"Pipeline {id} re-disparada a partir da fase '{state.Phase}'.",
            new { pipelineId = id, resumed = true, state.Phase });
    }

    private static string? ReadString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.String
                ? el.GetString()?.Trim()
                : null;
}
