using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:delegate_task</c> — delega uma subtarefa a uma run filha
/// (SPEC-20261007-chat-agent-parity RF-016, padrão <c>task</c> do opencode /
/// child session do Devin): cria um chat próprio do usuário com o prompt
/// delegado, enfileira a run (histórico isolado herdando modelo, tools —
/// menos o próprio delegate, para impedir recursão — e preset do chat pai)
/// e aguarda o término até <see cref="TimeoutSeconds"/>; o resultado volta
/// ao transcript do pai como tool_result com o link do chat filho.
/// Um chat separado é necessário: o dispatcher serializa runs por chat —
/// um filho no mesmo chat só executaria depois que o pai terminasse,
/// enquanto o pai o aguarda (deadlock).
/// <para>
/// SPEC-20261010-async-delegate: <c>wait=false</c> retorna imediatamente
/// com <c>childRunId</c> — a run filha continua server-side e o pai
/// colhe o resultado depois via <c>builtin:run_result</c> (paralelismo
/// real entre subtarefas). A profundidade é derivada da cadeia
/// <see cref="ChatRun.ParentRunId"/>: runs com profundidade ≥
/// <see cref="MaxDepth"/> perdem a tool <c>delegate_task</c> (sub-agents
/// encadeados até 3 níveis, estilo opencode-web).
/// </para>
/// </summary>
public sealed class DelegateTaskBuiltinTool(
    IServiceScopeFactory scopeFactory,
    IChatRunDispatcher dispatcher) : IBuiltinChatTool
{
    /// <summary>Tempo máximo de espera pela run filha (SPEC: 5min).</summary>
    private const int TimeoutSeconds = 300;

    /// <summary>Intervalo entre polls de status da run filha.</summary>
    private const int PollMs = 1500;

    /// <summary>
    /// Profundidade máxima da cadeia de delegação (SPEC-20261010-async-delegate):
    /// depth 0 = run raiz; filhos a partir de <see cref="MaxDepth"/> não herdam
    /// a tool <c>delegate_task</c>. Derivada caminhando <see cref="ChatRun.ParentRunId"/>
    /// — sem coluna nova no schema.
    /// </summary>
    private const int MaxDepth = 3;

    /// <summary>Caps de entrada/saída.</summary>
    private const int MaxPromptChars = 4000;
    private const int MaxContextChars = 2000;
    private const int MaxResultChars = 6000;
    private const int MaxTitleChars = 60;

    /// <inheritdoc />
    public string Name => "delegate_task";

    /// <inheritdoc />
    public string Description =>
        "Delegate to a child agent — call for parallel work. "
            + "Set wait=false to run in background and collect later via builtin_run_result.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "prompt": {
              "type": "string",
              "description": "Complete, self-contained instruction for the subtask (the child cannot see this conversation)."
            },
            "context": {
              "type": "string",
              "description": "Optional extra context (paths, decisions, constraints) appended to the prompt."
            },
            "wait": {
              "type": "boolean",
              "description": "true (default): block until the child finishes. false: return childRunId immediately — collect later with builtin_run_result."
            },
            "repo": {
              "type": "string",
              "description": "Optional 'owner/repo' slug — bind the child chat to this repo instead of inheriting the parent's binding (sub-agent on another repository)."
            },
            "branch": {
              "type": "string",
              "description": "Optional branch for 'repo' (default 'main')."
            },
            "clone_url": {
              "type": "string",
              "description": "Optional clone URL override for 'repo' (default https://github.com/<repo>.git)."
            },
            "persona": {
              "type": "string",
              "description": "Optional persona: name or id of an active workspace Skill whose instructions become the sub-agent's system prompt (e.g. 'reviewer', 'tester')."
            },
            "merge": {
              "type": "boolean",
              "description": "true: auto-merge the child's worktree into the workspace when it completes (wait=true only; with wait=false the parent reviews via builtin_worktree_diff and merges via builtin_worktree_merge)."
            }
          },
          "required": ["prompt"]
        }
        """;

    /// <inheritdoc />
    public bool RequiresApproval => true;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var prompt = ReadString(args, "prompt", MaxPromptChars);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new BuiltinToolResult("Parâmetro 'prompt' é obrigatório.");
        }

        var extra = ReadString(args, "context", MaxContextChars);
        var wait = !args.TryGetProperty("wait", out var waitEl)
            || waitEl.ValueKind is not JsonValueKind.False;
        var autoMerge = args.TryGetProperty("merge", out var mergeEl)
            && mergeEl.ValueKind == JsonValueKind.True;
        var fullPrompt = string.IsNullOrWhiteSpace(extra)
            ? prompt
            : $"{prompt}\n\nContexto adicional:\n{extra}";

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // SPEC-20261010-delegate-persona: persona = slug (Name ou Id) de uma
        // Skill ativa do usuário — o conteúdo vira system prompt do filho.
        var (personaSkill, personaError) = await ResolvePersonaAsync(db, context, args, ct);
        if (personaError is not null)
        {
            return personaError;
        }

        var parentRun = context.RunId is { } parentId
            ? await db.ChatRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == parentId, ct)
            : null;
        if (parentRun is null)
        {
            return new BuiltinToolResult(
                "delegate_task só pode ser chamada dentro de uma run de chat.");
        }

        // Profundidade da cadeia pai (depth 0 = raiz). Runs de profundidade
        // ≥ MaxDepth perdem a tool delegate_task — o filho não pode delegar
        // além do limite (SPEC-20261010-async-delegate).
        var childDepth = await DepthOfAsync(db, parentRun, ct) + 1;

        // Herda modelo + tools do pai; exclui o próprio delegate apenas
        // quando o filho atingiria o teto de profundidade.
        IReadOnlyList<string>? childToolIds = null;
        try
        {
            var parentRequest = JsonSerializer.Deserialize<ChatCompletionRequest>(
                parentRun.RequestJson, JsonOptions);
            childToolIds = parentRequest?.ToolIds
                ?.Where(id => childDepth < MaxDepth
                    || !id.Equals(
                        $"{BuiltinToolRegistry.IdPrefix}{Name}",
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (JsonException)
        {
            // RequestJson corrompido: delega sem tools, nunca falha por isso.
        }

        var preset = context.ChatId is { } chatId
            ? await db.Chats.AsNoTracking()
                .Where(c => c.Id == chatId)
                .Select(c => c.ApprovalPreset)
                .FirstOrDefaultAsync(ct) ?? "approve-mutations"
            : "approve-mutations";

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var title = prompt.Length > MaxTitleChars
            ? prompt[..MaxTitleChars] + "…"
            : prompt;
        var childChat = new Chat
        {
            UserId = context.UserId,
            Title = $"delegado: {title}",
            ParentChatId = context.ChatId,
            ModelsJson = JsonSerializer.Serialize(new[] { parentRun.Model }),
            ToolIdsJson = JsonSerializer.Serialize(childToolIds ?? []),
            ApprovalPreset = preset,
            // Subtarefa é workspace-capable por natureza — o modo efetivo
            // cai para chat se o filho ficar sem binding (SPEC-15).
            Mode = PermissionRuleset.BuildMode,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (personaSkill is not null)
        {
            childChat.Messages.Add(new ChatMessage
            {
                ChatId = childChat.Id,
                Role = "system",
                Content = personaSkill.Content,
                Position = 0,
                Timestamp = now,
            });
        }
        childChat.Messages.Add(new ChatMessage
        {
            ChatId = childChat.Id,
            Role = "user",
            Content = fullPrompt,
            Position = personaSkill is null ? 0 : 1,
            Timestamp = now,
        });
        db.Chats.Add(childChat);

        var childRequest = new ChatCompletionRequest(
            parentRun.Model,
            [
                .. personaSkill is null
                    ? Array.Empty<ChatCompletionMessage>()
                    : new[] { new ChatCompletionMessage("system", personaSkill.Content) },
                new ChatCompletionMessage("user", fullPrompt),
            ],
            Stream: true,
            ToolIds: childToolIds);
        var childRun = new ChatRun
        {
            ChatId = childChat.Id,
            UserId = context.UserId,
            Model = parentRun.Model,
            RequestJson = JsonSerializer.Serialize(childRequest, JsonOptions),
            ParentRunId = parentRun.Id,
            CreatedAt = now,
        };
        db.ChatRuns.Add(childRun);
        await db.SaveChangesAsync(ct);

        // SPEC-20261010-chat-repo-binding: o filho herda o binding POR CHAT
        // do pai (não o global). SPEC-15: herda repo/branch mas com
        // worktree próprio — subtarefa não escreve no worktree do pai.
        var repos = scope.ServiceProvider.GetRequiredService<WorkspaceRepoService>();
        if (context.ChatId is { } boundParent
            && await repos.GetChatBindingAsync(boundParent, ct) is { } parentBinding)
        {
            await repos.SetChatBindingAsync(
                childChat.Id, repos.InheritChatBinding(childChat.Id, parentBinding), ct);
        }

        var repoError = await BindTargetRepoAsync(scope, context, args, childChat, ct);
        if (repoError is not null)
        {
            childRun.Status = ChatRunStatus.Failed;
            childRun.Error = repoError;
            await db.SaveChangesAsync(ct);
            return new BuiltinToolResult(repoError);
        }

        dispatcher.Enqueue(childRun.Id);

        var link = $"/c/{childChat.Id}";
        if (!wait)
        {
            // wait=false: devolve já — o pai segue o turno e colhe o
            // resultado depois via builtin:run_result (paralelismo real).
            return new BuiltinToolResult(
                $"Subtarefa delegada em background — run filha {childRun.Id} "
                    + $"(chat filho: {link}). Chame builtin_run_result com "
                    + $"run_id={childRun.Id} para colher o resultado.",
                new
                {
                    childChatId = childChat.Id,
                    childRunId = childRun.Id,
                    status = "queued",
                });
        }

        return await WaitForChildAsync(
            db, scope, context, childChat, childRun, autoMerge, link, ct);
    }

    /// <summary>
    /// Persona opcional (SPEC-20261010-delegate-persona): slug (Name ou Id)
    /// de uma Skill ativa do usuário. Erro listando as ativas quando o
    /// slug não existe.
    /// </summary>
    private static async Task<(Skill? Skill, BuiltinToolResult? Error)> ResolvePersonaAsync(
        AppDbContext db, BuiltinToolContext context, JsonElement args, CancellationToken ct)
    {
        if (ReadString(args, "persona", 200) is not { Length: > 0 } persona)
        {
            return (null, null);
        }

        var skill = await db.Skills.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == context.UserId && s.IsActive
                && (s.Name == persona || s.Id == persona), ct);
        if (skill is not null)
        {
            return (skill, null);
        }

        var available = await db.Skills.AsNoTracking()
            .Where(s => s.UserId == context.UserId && s.IsActive)
            .Select(s => s.Name).ToListAsync(ct);
        return (null, new BuiltinToolResult(
            $"Persona '{persona}' não encontrada. Skills ativas: "
            + (available.Count == 0 ? "(nenhuma)" : string.Join(", ", available))));
    }

    /// <summary>
    /// SPEC-20261010-delegate-repo-target: `repo` explícito sobrescreve a
    /// herança — o chat filho é vinculado ao repo alvo (checkout
    /// compartilhado por slug). Retorna mensagem de erro pronta ou null.
    /// </summary>
    private static async Task<string?> BindTargetRepoAsync(
        IServiceScope scope, BuiltinToolContext context, JsonElement args,
        Chat childChat, CancellationToken ct)
    {
        if (ReadString(args, "repo", 200) is not { Length: > 0 } targetRepo)
        {
            return null;
        }

        var targetBranch = ReadString(args, "branch", 200);
        if (string.IsNullOrWhiteSpace(targetBranch))
        {
            targetBranch = "main";
        }
        var cloneUrl = ReadString(args, "clone_url", 2000);
        if (string.IsNullOrWhiteSpace(cloneUrl))
        {
            cloneUrl = $"https://github.com/{targetRepo.Trim()}.git";
        }
        var repos = scope.ServiceProvider.GetRequiredService<WorkspaceRepoService>();
        var token = await scope.ServiceProvider
            .GetRequiredService<GitHubService>()
            .GetTokenAsync(context.UserId, ct);
        var (repoBinding, repoError) = await repos.OpenChatAsync(
            context.UserId, childChat.Id, targetRepo.Trim(),
            targetBranch.Trim(), cloneUrl, token, ct);
        return repoBinding is null
            ? $"Falha ao preparar o repo '{targetRepo}' para a subtarefa: {repoError}"
            : null;
    }

    /// <summary>
    /// wait=true: faz polling da run filha até status terminal ou o
    /// timeout de <see cref="TimeoutSeconds"/> — o resultado inclui a
    /// nota de auto-merge quando habilitado.
    /// </summary>
    private static async Task<BuiltinToolResult> WaitForChildAsync(
        AppDbContext db, IServiceScope scope, BuiltinToolContext context,
        Chat childChat, ChatRun childRun, bool autoMerge, string link,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(TimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(PollMs, ct);
            var status = await db.ChatRuns.AsNoTracking()
                .Where(r => r.Id == childRun.Id)
                .Select(r => new { r.Status, r.PartialContent, r.Error })
                .FirstOrDefaultAsync(ct);
            if (status is null || status.Status
                    is ChatRunStatus.Queued or ChatRunStatus.Running or ChatRunStatus.Paused)
            {
                continue;
            }

            var result = new
            {
                childChatId = childChat.Id,
                childRunId = childRun.Id,
                status = status.Status,
            };
            return status.Status switch
            {
                ChatRunStatus.Completed => new BuiltinToolResult(
                    $"{Truncate(status.PartialContent ?? "(sem conteúdo)", MaxResultChars)}\n\n"
                        + $"(subtarefa concluída — chat filho: {link})"
                        + await AutoMergeNoteAsync(
                            autoMerge, scope, context, childChat, childRun, ct),
                    result),
                _ => new BuiltinToolResult(
                    $"Subtarefa terminou como '{status.Status}'"
                        + (status.Error is { } e ? $": {Truncate(e, 500)}" : string.Empty)
                        + $" — chat filho: {link}",
                    result),
            };
        }

        return new BuiltinToolResult(
            $"Subtarefa ainda em andamento após {TimeoutSeconds}s — continua em "
                + $"background; acompanhe o resultado no chat filho: {link}",
            new { childChatId = childChat.Id, childRunId = childRun.Id, status = "running" });
    }

    /// <summary>
    /// Profundidade da run na cadeia <see cref="ChatRun.ParentRunId"/>
    /// (0 = raiz). Limita o walk a <see cref="MaxDepth"/> — ciclos ou
    /// correntes longas não custam mais que MaxDepth lookups.
    /// </summary>
    private static async Task<int> DepthOfAsync(
        AppDbContext db, ChatRun run, CancellationToken ct)
    {
        var depth = 0;
        var cursor = run.ParentRunId;
        while (cursor is not null && depth < MaxDepth)
        {
            depth++;
            cursor = await db.ChatRuns.AsNoTracking()
                .Where(r => r.Id == cursor)
                .Select(r => r.ParentRunId)
                .FirstOrDefaultAsync(ct);
        }
        return depth;
    }

    /// <summary>
    /// Auto-merge opt-in (SPEC-20261010-worktree-review, <c>merge=true</c>
    /// + <c>wait=true</c>): depois da run filha completar, aplica o diff do
    /// worktree dela no workdir do chat filho. Conflitos nunca são
    /// forçados — voltam listados na nota e o worktree permanece.
    /// </summary>
    private static async Task<string> AutoMergeNoteAsync(
        bool enabled,
        IServiceScope scope,
        BuiltinToolContext context,
        Chat childChat,
        ChatRun childRun,
        CancellationToken ct)
    {
        if (!enabled)
        {
            return string.Empty;
        }
        try
        {
            var worktrees = scope.ServiceProvider
                .GetRequiredService<WorktreeService>();
            var worktree = worktrees.ResolveIsolated(context.UserId, childRun.Id);
            if (worktree is null)
            {
                return "\n\n(merge: a run filha não isolou worktree — nada a aplicar)";
            }
            var repos = scope.ServiceProvider
                .GetRequiredService<WorkspaceRepoService>();
            var mainWorkdir = await repos.ResolveWorkdirAsync(
                context.UserId, childChat.Id, ct);
            var merge = await worktrees.MergeAsync(mainWorkdir, worktree, ct);
            return merge.Merged
                ? $"\n\n(merge: {merge.Applied.Count} arquivo(s) aplicados no workspace)"
                : $"\n\n(merge: PARCIAL — {merge.Conflicts.Count} conflito(s) não "
                    + "aplicados; worktree preservado p/ correção manual: "
                    + string.Join("; ", merge.Conflicts) + ")";
        }
        catch (Exception ex)
        {
            return $"\n\n(merge: falhou — {ex.Message}; worktree preservado)";
        }
    }

    private static string? ReadString(JsonElement args, string field, int max)
    {
        if (!args.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.String
            || el.GetString() is not { } s)
        {
            return null;
        }
        return s.Length > max ? s[..max] : s;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
}
