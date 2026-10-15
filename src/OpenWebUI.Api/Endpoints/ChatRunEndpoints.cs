using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Runs;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Rotas de runs de chat desacopladas (SPEC-20261007-chat-detached-runs),
/// mapeadas sob o grupo <c>/api/v1/chats</c>:
/// envio de mensagem → run em background; attach SSE com replay por
/// <c>lastSeq</c>; stop; listagem. A geração sobrevive a reload/fechamento
/// da aba — o cliente anexa ao stream quando volta.
/// </summary>
public static class ChatRunEndpoints
{
    /// <summary>Mapeia as rotas de runs dentro do grupo de chats.</summary>
    public static void MapChatRunEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/runs", ListAllRunsAsync);
        group.MapPost("/{id}/messages", EnqueueMessageAsync);
        group.MapGet("/{id}/runs", ListRunsAsync);
        group.MapGet("/{id}/runs/active", GetActiveRunAsync);
        group.MapGet("/{id}/runs/{runId}", GetRunAsync);
        group.MapGet("/{id}/runs/{runId}/stream", StreamRunAsync);
        group.MapPost("/{id}/runs/{runId}/stop", StopRunAsync);
        group.MapPost("/{id}/runs/{runId}/pause", PauseRunAsync);
        group.MapPost("/{id}/runs/{runId}/resume", ResumeRunAsync);
        group.MapPost("/{id}/runs/{runId}/approvals/{callId}", DecideApprovalAsync);
        group.MapPost("/{id}/runs/{runId}/steer", SteerRunAsync);
        group.MapGet("/{id}/runs/{runId}/diff", GetRunDiffAsync);
    }

    /// <summary>
    /// Persiste a mensagem do usuário, cria a run queued e enfileira no
    /// dispatcher. Responde 202 + snapshot da run (o cliente abre o attach).
    /// </summary>
    private static async Task<IResult> EnqueueMessageAsync(
        string id,
        EnqueueChatRunRequest request,
        HttpContext http,
        AppDbContext db,
        ChatRunDispatcher dispatcher,
        ContextBoundaryService boundaries,
        BranchService branches,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            return Results.BadRequest(new { detail = "model é obrigatório." });
        }

        var chat = await db.Chats.Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == user.Id, ct);
        if (chat is null)
        {
            return Results.NotFound(new { detail = "Chat não encontrado." });
        }

        // Ramificação atômica (RF-004): ativa o alvo como leaf — resumindo o
        // ramo abandonado — antes de pendurar a nova mensagem nele.
        if (request.ForkFromMessageId is not null
            && !await branches.ActivateLeafAsync(
                chat, request.ForkFromMessageId, request.Model, ct))
        {
            return Results.NotFound(new { detail = "Mensagem de origem não encontrada." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // SPEC-20261013-chat-media-attachments RF-003: metadados dos anexos
        // do envio (somente arquivos do dono) viram FilesJson da mensagem.
        List<ChatMessageFileModel>? attached = null;
        if (request.FileIds is { Count: > 0 })
        {
            var owned = await db.Files.AsNoTracking()
                .Where(f => request.FileIds.Contains(f.Id) && f.UserId == user.Id)
                .ToListAsync(ct);
            var byId = owned.ToDictionary(f => f.Id);
            attached = request.FileIds
                .Where(byId.ContainsKey)
                .Select(fid =>
                {
                    var f = byId[fid];
                    return ChatMessageFileModel.Of(
                        f.Id, f.Filename, f.ContentType, f.Size);
                })
                .ToList();
            if (attached.Count == 0)
            {
                attached = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Content))
        {
            // Persiste a mensagem do usuário já no servidor (o cliente não precisa reenviar o chat inteiro).
            var nextPosition = chat.Messages.Count == 0
                ? 0
                : chat.Messages.Max(m => m.Position) + 1;
            var message = new ChatMessage
            {
                ChatId = chat.Id,
                Role = "user",
                Content = request.Content,
                Position = nextPosition,
                Timestamp = now,
                FilesJson = attached is null
                    ? null
                    : JsonSerializer.Serialize(attached, JsonOptions),
                ParentId = BranchService.ResolveLeaf(chat)?.Id,
            };
            chat.Messages.Add(message);
            chat.LeafMessageId = message.Id;
            chat.UpdatedAt = now;
        }
        else if (BranchService.WalkPath(chat).LastOrDefault()?.Role != "user")
        {
            // Sem conteúdo novo, a run regenera sobre o histórico — que precisa terminar em msg do usuário.
            return Results.BadRequest(new { detail = "Histórico não termina em mensagem do usuário." });
        }

        // Histórico reconstruído no servidor — a fonte da verdade é o banco.
        // O caminho ativo (leaf→raiz, RF-002) substitui a ordenação global por
        // Position: fora dele, mensagens de outros ramos nunca chegam ao modelo.
        // Boundaries fechadas (SPEC-20261013-agent-context-management RF-002)
        // filtram o contexto: mensagens cobertas permanecem persistidas, mas
        // não voltam ao modelo — o marcador "compaction" carrega o report.
        // FileIds por mensagem (do FilesJson) alimentam a resolução
        // preguiçosa de imagens na pipeline (RF-006 media-attachments).
        var path = BranchService.WalkPath(chat);
        var visible = await boundaries.FilterAsync(chat.Id, path, ct);
        var history = visible
            .Select(m => new ChatCompletionMessage(
                m.Role, m.Content, m.ToolCallId, m.ToolCallsJson,
                FileIds: ParseFileIds(m.FilesJson)))
            .ToList();
        var completionRequest = new ChatCompletionRequest(
            request.Model, history, Stream: true,
            FileIds: request.FileIds, Params: request.Params,
            ToolIds: request.ToolIds, WebSearch: request.WebSearch,
            MentionPaths: request.MentionPaths);

        var run = new ChatRun
        {
            ChatId = chat.Id,
            UserId = user.Id,
            Model = request.Model,
            RequestJson = JsonSerializer.Serialize(completionRequest, JsonOptions),
            CreatedAt = now,
        };
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync(ct);

        dispatcher.Enqueue(run.Id);
        return Results.Accepted(value: ToResponse(run));
    }

    /// <summary>Lista as runs mais recentes do chat (máx. 20).</summary>
    private static async Task<IResult> ListRunsAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var runs = await db.ChatRuns.AsNoTracking()
            .Where(r => r.ChatId == id && r.UserId == user.Id)
            .OrderByDescending(r => r.CreatedAt)
            .Take(20)
            .ToListAsync(ct);
        return Results.Ok(runs.Select(ToResponse));
    }

    /// <summary>
    /// Lista consolidada das runs do usuário (SPEC-20261010-parallel-runs-console):
    /// todas as chats em uma visão só — status, chat, vínculo pai/filha
    /// (sub-agents) — pro console de runs paralelas da sidebar.
    /// </summary>
    private static async Task<IResult> ListAllRunsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var rows = await db.ChatRuns.AsNoTracking()
            .Where(r => r.UserId == user.Id)
            .OrderByDescending(r => r.CreatedAt)
            .Take(50)
            .Join(db.Chats.AsNoTracking(),
                r => r.ChatId, c => c.Id,
                (r, c) => new ParallelRunResponse(
                    r.Id, r.ChatId, c.Title, r.Status, r.Model,
                    r.CreatedAt, r.StartedAt, r.CompletedAt,
                    r.ParentRunId, c.ParentChatId))
            .ToListAsync(ct);
        return Results.Ok(rows);
    }

    /// <summary>Retorna a run ativa (queued/running) do chat, ou null.</summary>
    private static async Task<IResult> GetActiveRunAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var run = await db.ChatRuns.AsNoTracking()
            .Where(r => r.ChatId == id && r.UserId == user.Id
                && (r.Status == ChatRunStatus.Queued || r.Status == ChatRunStatus.Running
                    || r.Status == ChatRunStatus.Paused))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return run is null ? Results.Ok() : Results.Ok(ToResponse(run));
    }

    /// <summary>Snapshot de uma run (status + conteúdo parcial).</summary>
    private static async Task<IResult> GetRunAsync(
        string id, string runId, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var run = await db.ChatRuns.AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Id == runId && r.ChatId == id && r.UserId == user.Id, ct);
        return run is null
            ? Results.NotFound(new { detail = "Run não encontrada." })
            : Results.Ok(ToResponse(run));
    }

    /// <summary>
    /// Attach SSE: replay do backlog com seq &gt; <c>lastSeq</c> e depois o
    /// stream vivo até a run fechar. Cada evento sai como
    /// <c>id: {seq}\n{payload}\n\n</c> — o cliente resume por Last-Event-ID.
    /// </summary>
    private static async Task StreamRunAsync(
        string id,
        string runId,
        HttpContext http,
        AppDbContext db,
        ChatRunBroadcaster broadcaster,
        int? lastSeq,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            http.Response.StatusCode = 401;
            return;
        }

        var run = await db.ChatRuns.AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Id == runId && r.ChatId == id && r.UserId == user.Id, ct);
        if (run is null)
        {
            http.Response.StatusCode = 404;
            return;
        }

        // Last-Event-ID do SSE também conta como checkpoint de replay.
        var since = lastSeq ?? 0;
        if (since == 0
            && http.Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId)
            && int.TryParse(lastEventId, out var parsed))
        {
            since = parsed;
        }

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Connection = "keep-alive";
        // Sem isso nginx/traefik bufferizam o SSE — eventos de tool só chegam
        // no fim da run (o usuário "só vê depois de refresh").
        http.Response.Headers["X-Accel-Buffering"] = "no";

        // UTF8Encoding(false): sem BOM — o BOM saía como caractere
        // invisível na primeira linha do SSE.
        await using var writer = new StreamWriter(
            http.Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            // Heartbeat `: hb` a cada 15s sem evento: proxies/balanceadores
            // derrubam ou bufferizam SSE ocioso — o comentário mantém o pipe
            // vivo e o cliente re-arma o watchdog de stall. Sem heartbeat a
            // UI congela no meio da run e "só atualiza no refresh".
            await using var enumerator = broadcaster
                .SubscribeAsync(runId, since, ct)
                .GetAsyncEnumerator(ct);
            // ValueTask é consumo único — materializa em Task antes do WhenAny.
            // PeriodicTimer aceita um único wait pendente: o tick é criado
            // uma vez e só re-armado depois de completar.
            var moveNext = enumerator.MoveNextAsync().AsTask();
            using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
            var tick = heartbeat.WaitForNextTickAsync(ct).AsTask();
            while (true)
            {
                var winner = await Task.WhenAny(moveNext, tick);
                if (winner == tick)
                {
                    await tick; // relança OperationCanceledException no disconnect
                    await writer.WriteLineAsync(": hb");
                    await writer.WriteLineAsync();
                    await writer.FlushAsync(ct);
                    tick = heartbeat.WaitForNextTickAsync(ct).AsTask();
                    continue;
                }

                if (!await moveNext)
                {
                    break;
                }
                var evt = enumerator.Current;
                await writer.WriteLineAsync($"id: {evt.Seq}");
                await writer.WriteLineAsync(evt.Payload);
                await writer.WriteLineAsync();
                await writer.FlushAsync(ct);
                moveNext = enumerator.MoveNextAsync().AsTask();
            }
        }
        catch (OperationCanceledException)
        {
            // Cliente desconectou — normal: a run continua no dispatcher.
        }
        catch (IOException)
        {
            // Disconnect no meio de um write (cliente matou o socket) —
            // idem: a run segue no dispatcher, só o attach morreu.
        }
    }

    /// <summary>Pede a interrupção de uma run ativa.</summary>
    private static async Task<IResult> StopRunAsync(
        string id,
        string runId,
        HttpContext http,
        AppDbContext db,
        ChatRunDispatcher dispatcher,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var run = await db.ChatRuns
            .FirstOrDefaultAsync(
                r => r.Id == runId && r.ChatId == id && r.UserId == user.Id, ct);
        if (run is null)
        {
            return Results.NotFound(new { detail = "Run não encontrada." });
        }

        if (run.Status == ChatRunStatus.Queued
            || (run.Status == ChatRunStatus.Paused && run.StartedAt is null))
        {
            // Ainda não iniciou: cancela direto (o dispatcher pula status != queued).
            run.Status = ChatRunStatus.Stopped;
            run.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(run));
        }

        if (run.Status is ChatRunStatus.Running or ChatRunStatus.Paused)
        {
            dispatcher.TryStop(run.Id);
            return Results.Ok(ToResponse(run));
        }

        return Results.Conflict(new { detail = $"Run já finalizada ({run.Status})." });
    }

    /// <summary>
    /// Injeta uma mensagem durável na run viva (SPEC-20261010-steer-queue):
    /// <c>mode=steer</c> (default) é promovida na próxima fronteira de rodada
    /// do tool loop; <c>mode=queue</c> só quando a run ficaria ociosa. 202 —
    /// a promoção é assíncrona pelo executor.
    /// </summary>
    private static async Task<IResult> SteerRunAsync(
        string id,
        string runId,
        SteerRunRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Results.BadRequest(new { detail = "Mensagem vazia." });
        }
        var mode = request.Mode is "queue" ? "queue" : "steer";

        var run = await db.ChatRuns
            .FirstOrDefaultAsync(
                r => r.Id == runId && r.ChatId == id && r.UserId == user.Id, ct);
        if (run is null)
        {
            return Results.NotFound(new { detail = "Run não encontrada." });
        }
        if (run.Status is not (ChatRunStatus.Queued or ChatRunStatus.Running
            or ChatRunStatus.Paused))
        {
            return Results.Conflict(new { detail = $"Run já finalizada ({run.Status})." });
        }

        db.ChatRunSteers.Add(new ChatRunSteer
        {
            RunId = run.Id, ChatId = id, Content = request.Message.Trim(),
            Mode = mode, Status = "pending",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        await db.SaveChangesAsync(ct);
        return Results.Accepted();
    }

    /// <summary>
    /// Suspende uma run ativa (RF-013 chat-agent-parity): queued → vira
    /// paused e o dispatcher pula; running → drena o gate de pausa e o
    /// executor bloqueia no próximo checkpoint. A transição usa
    /// ExecuteUpdate condicional para não correr com o flip queued→running.
    /// </summary>
    private static async Task<IResult> PauseRunAsync(
        string id,
        string runId,
        HttpContext http,
        AppDbContext db,
        ChatRunPauses pauses,
        ChatRunBroadcaster broadcaster,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var run = await db.ChatRuns
            .FirstOrDefaultAsync(
                r => r.Id == runId && r.ChatId == id && r.UserId == user.Id, ct);
        if (run is null)
        {
            return Results.NotFound(new { detail = "Run não encontrada." });
        }
        if (run.Status is not (ChatRunStatus.Queued or ChatRunStatus.Running))
        {
            return Results.Conflict(
                new { detail = $"Run não está ativa ({run.Status})." });
        }

        var flipped = await db.ChatRuns
            .Where(r => r.Id == run.Id && r.Status == run.Status)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, ChatRunStatus.Paused), ct);
        if (flipped == 0)
        {
            return Results.Conflict(
                new { detail = "Run mudou de estado durante a pausa." });
        }

        if (run.Status == ChatRunStatus.Running)
        {
            pauses.Pause(run.Id);
        }
        run.Status = ChatRunStatus.Paused;
        broadcaster.Publish(run.Id,
            $"event: status\ndata: {JsonSerializer.Serialize(new RunPhaseEvent("paused", null), JsonOptions)}");
        return Results.Ok(ToResponse(run));
    }

    /// <summary>
    /// Retoma uma run pausada (RF-013): nunca iniciada (StartedAt null)
    /// volta pra fila do dispatcher; já em execução libera o gate — o
    /// executor sai do checkpoint e continua de onde parou.
    /// </summary>
    private static async Task<IResult> ResumeRunAsync(
        string id,
        string runId,
        HttpContext http,
        AppDbContext db,
        ChatRunDispatcher dispatcher,
        ChatRunPauses pauses,
        ChatRunBroadcaster broadcaster,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var run = await db.ChatRuns
            .FirstOrDefaultAsync(
                r => r.Id == runId && r.ChatId == id && r.UserId == user.Id, ct);
        if (run is null)
        {
            return Results.NotFound(new { detail = "Run não encontrada." });
        }
        if (run.Status != ChatRunStatus.Paused)
        {
            return Results.Conflict(
                new { detail = $"Run não está pausada ({run.Status})." });
        }

        var wasQueued = run.StartedAt is null;
        var next = wasQueued ? ChatRunStatus.Queued : ChatRunStatus.Running;
        var flipped = await db.ChatRuns
            .Where(r => r.Id == run.Id && r.Status == ChatRunStatus.Paused)
            .ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, next), ct);
        if (flipped == 0)
        {
            return Results.Conflict(
                new { detail = "Run mudou de estado durante o resume." });
        }

        run.Status = next;
        if (wasQueued)
        {
            dispatcher.Enqueue(run.Id);
        }
        else
        {
            pauses.Resume(run.Id);
            broadcaster.Publish(run.Id,
                $"event: status\ndata: {JsonSerializer.Serialize(new RunPhaseEvent("resumed", null), JsonOptions)}");
        }
        return Results.Ok(ToResponse(run));
    }

    /// <summary>
    /// Decisão do dono sobre uma aprovação de tool pendente
    /// (SPEC-20261007-chat-tool-streaming RF-003): <c>approve</c> | <c>deny</c>
    /// + <c>remember</c> opcional (válido só nesta conversa, em memória).
    /// 404 quando a call não está pendente; 410 quando a run já finalizou.
    /// </summary>
    private static async Task<IResult> DecideApprovalAsync(
        string id,
        string runId,
        string callId,
        RunApprovalDecisionRequest request,
        HttpContext http,
        AppDbContext db,
        ChatRunApprovals approvals,
        ToolApprovalOverrideService toolOverrides,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (request.Decision is not ("approve" or "deny"))
        {
            return Results.BadRequest(new { detail = "decision deve ser approve|deny." });
        }

        var run = await db.ChatRuns.AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Id == runId && r.ChatId == id && r.UserId == user.Id, ct);
        if (run is null)
        {
            return Results.NotFound(new { detail = "Run não encontrada." });
        }
        if (run.Status is not (ChatRunStatus.Queued or ChatRunStatus.Running
            or ChatRunStatus.Paused))
        {
            return Results.Json(
                new { detail = $"Run já finalizada ({run.Status})." },
                statusCode: StatusCodes.Status410Gone);
        }

        if (request.Message is { Length: > 2048 })
        {
            return Results.BadRequest(new { detail = "message deve ter até 2KB." });
        }

        var approved = request.Decision == "approve";
        // "Sempre para esta tool" (SPEC-20261013-approval-tiers RF-004):
        // grava override persistente allow para o usuário, além do
        // remember da sessão resolvido abaixo.
        if (approved && request.RememberPersist)
        {
            var pendingTool = await approvals.PendingToolNameAsync(run.Id, callId);
            if (pendingTool is not null)
            {
                await toolOverrides.MergeAsync(user.Id,
                    new Dictionary<string, string?>
                    { [PermissionRuleset.BuiltinName(pendingTool)] = "allow" }, ct);
            }
        }
        // A mensagem vai mesmo em approve: no ask_user a resposta do dono
        // é justamente decision=approve + message (RF-005); numa aprovação
        // comum, message em approve é ignorada pelo gate.
        return approvals.Resolve(
            run.Id, run.ChatId, callId, approved, request.Remember,
            request.Message)
            ? Results.Ok(new StatusResponse(true))
            : Results.NotFound(new { detail = "Aprovação não está pendente." });
    }

    /// <summary>
    /// Snapshot git do workspace do chat (RF-018): branch, numstat
    /// agregado e unified diff de <c>HEAD</c> — alimenta a git bar do
    /// header e a aba Changes do painel. <c>Git=false</c> quando o
    /// workdir não é repo (ou git ausente) — o cliente cai pro agregado
    /// de diffs das tools <c>file_*</c>.
    /// </summary>
    private static async Task<IResult> GetRunDiffAsync(
        string id,
        string runId,
        HttpContext http,
        AppDbContext db,
        WorkspaceRepoService repos,
        WorkspaceGitService git,
        WorktreeService worktrees,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var exists = await db.ChatRuns.AsNoTracking()
            .AnyAsync(r => r.Id == runId && r.ChatId == id && r.UserId == user.Id, ct);
        if (!exists)
        {
            return Results.NotFound(new { detail = "Run não encontrada." });
        }

        // Com repo vinculado o snapshot sai do checkout do repo; numa run
        // isolada (Workspace:RunIsolation=worktree, E16 S9) sai do worktree.
        var mainWorkdir = await repos.ResolveWorkdirAsync(user.Id, id, ct);
        var isolated = worktrees.ResolveIsolated(user.Id, runId);
        var workdir = isolated ?? mainWorkdir;
        var info = await git.GetInfoAsync(workdir, ct);
        return Results.Ok(new WorkspaceGitResponse(
            info.IsRepo, info.Branch, info.Added, info.Removed,
            info.Files.Select(f => new WorkspaceGitFileResponse(
                f.Path, f.Added, f.Removed, f.Status)).ToList(),
            info.Diff, info.DiffTruncated,
            Worktree: isolated is not null));
    }

    private static ChatRunResponse ToResponse(ChatRun run) => new(
        run.Id, run.ChatId, run.Status, run.Model,
        run.PartialContent, run.Error,
        run.CreatedAt, run.StartedAt, run.CompletedAt, run.ParentRunId);

    /// <summary>Ids de anexo persistidos na mensagem (FilesJson → FileIds; null/ausente → null).</summary>
    private static List<string>? ParseFileIds(string? filesJson)
    {
        if (string.IsNullOrEmpty(filesJson))
        {
            return null;
        }
        try
        {
            var files = JsonSerializer.Deserialize<List<ChatMessageFileModel>>(
                filesJson, JsonOptions);
            return files is { Count: > 0 } ? files.Select(f => f.Id).ToList() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
