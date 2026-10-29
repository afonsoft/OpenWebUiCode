using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Hubs;
using OpenWebUI.Api.Runs;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de chats, espelhando <c>/api/v1/chats</c> do Open WebUI.</summary>
public static class ChatEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia as rotas de chats.</summary>
    public static RouteGroupBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/chats").RequireAuthorization();

        group.MapGet("/", ListChatsAsync);
        group.MapGet("/list", ListChatsAsync);
        group.MapGet("/search", ListChatsAsync);
        group.MapPost("/", CreateChatAsync);
        group.MapPost("/new", CreateChatAsync);
        group.MapGet("/pinned", ListPinnedAsync);
        group.MapGet("/archived", ListArchivedAsync);
        group.MapGet("/archived/count", CountArchivedAsync);
        group.MapGet("/attention/count", CountAttentionAsync);
        group.MapGet("/all/tags", ListAllTagsAsync);
        group.MapGet("/all/db", ExportAllAsync);
        group.MapGet("/shared", ListSharedAsync);
        group.MapGet("/share/{shareId}", GetSharedChatAsync).AllowAnonymous();
        group.MapPost("/import", ImportChatsAsync);
        group.MapPost("/archive/all", ArchiveAllAsync);
        group.MapPost("/unarchive/all", UnarchiveAllAsync);
        group.MapDelete("/", DeleteAllChatsAsync);
        group.MapGet("/folder/{folderId}", ListChatsAsync);
        group.MapPost("/tags", ListChatsByTagAsync);

        group.MapGet("/all", ListAllChatsAdminAsync);
        group.MapGet("/{id}", GetChatAsync);
        group.MapGet("/{id}/messages", GetChatMessagesAsync);
        group.MapGet("/{id}/children", ListChildrenAsync);
        group.MapGet("/{id}/workspace-repo", GetWorkspaceRepoAsync);
        group.MapPut("/{id}/workspace-repo", PutWorkspaceRepoAsync);
        group.MapGet("/{id}/workspace/scratch", GetScratchStatusAsync);
        group.MapPost("/{id}/workspace/publish", PublishWorkspaceAsync);
        group.MapPost("/{id}", UpdateChatAsync);
        group.MapPatch("/{id}", PatchChatAsync);
        group.MapDelete("/{id}", DeleteChatAsync);
        group.MapPost("/{id}/pin", TogglePinAsync);
        group.MapGet("/{id}/pinned", GetPinnedAsync);
        group.MapPost("/{id}/archive", ToggleArchiveAsync);
        group.MapPost("/{id}/share", ShareChatAsync);
        group.MapDelete("/{id}/share", UnshareChatAsync);
        group.MapPost("/{id}/clone", CloneChatAsync);
        group.MapPost("/{id}/folder", SetFolderAsync);
        group.MapGet("/{id}/tags", GetTagsAsync);
        group.MapPost("/{id}/tags", SetTagsAsync);
        group.MapDelete("/{id}/tags", ClearTagsAsync);
        group.MapPost("/{id}/messages/{messageId}", UpdateMessageAsync);
        group.MapDelete("/{id}/messages/{messageId}", DeleteMessageAsync);
        group.MapGet("/{id}/messages/{messageId}/versions", GetMessageVersionsAsync);
        group.MapGet("/{id}/branches", ListBranchesAsync);
        group.MapPost("/{id}/branches/activate", ActivateBranchAsync);
        group.MapPost("/{id}/compact", CompactChatAsync);

        // Runs desacopladas (SPEC-20261007-chat-detached-runs).
        ChatRunEndpoints.MapChatRunEndpoints(group);

        return group;
    }

    private static async Task<IResult> ListChatsAsync(
        string? folderId,
        HttpContext http,
        AppDbContext db,
        ChatRunApprovals approvals,
        CancellationToken ct,
        string? query = null,
        bool includeFolders = false,
        int skip = 0,
        int limit = 0,
        string? attention = null)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        // D2 (attention-inbox): chats com run bloqueada esperando decisão do
        // dono — aprovação de tool ou pergunta ask_user/plan_exit. A pendência
        // vive em memória no gate (uma run parada esperando o usuário), não no
        // banco: um snapshot do HashSet vira filtro/flag sem N+1.
        var awaiting = approvals.PendingChatIds();

        // Chats sinalizados = aprovações pendentes ∪ chats com itens
        // persistidos do inbox de atenção (run concluída/falha, terminal
        // encerrado) — mesma definição do /chats/attention/count. Sem a
        // união o badge contava itens que o filtro "aguardando" não listava.
        var attentionChatIds = await db.UserAttentions.AsNoTracking()
            .Where(a => a.UserId == user.Id && a.ChatId != null)
            .Select(a => a.ChatId!)
            .Distinct()
            .ToListAsync(ct);
        var flagged = new HashSet<string>(awaiting);
        flagged.UnionWith(attentionChatIds);

        var chats = db.Chats.AsNoTracking().Where(c => c.UserId == user.Id && !c.Archived && !c.IsEphemeral);

        if (!string.IsNullOrEmpty(folderId))
        {
            chats = chats.Where(c => c.FolderId == folderId);
        }
        else if (!includeFolders)
        {
            chats = chats.Where(c => c.FolderId == null);
        }

        if (attention is "1" or "true")
        {
            chats = chats.Where(c => flagged.Contains(c.Id));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLower();
            chats = chats.Where(c =>
                c.Title.ToLower().Contains(term)
                || c.Messages.Any(m => m.Content.ToLower().Contains(term)));
        }

        var ordered = chats
            .OrderByDescending(c => c.Pinned)
            .ThenByDescending(c => c.UpdatedAt);

        // skip/limit opcionais — sem eles o comportamento original (lista completa)
        // é preservado para o sidebar, que faz busca/pin client-side.
        var list = limit > 0
            ? await ordered.Skip(Math.Max(0, skip)).Take(limit).ToListAsync(ct)
            : await ordered.ToListAsync(ct);

        // Contagem de filhos delegados por pai — uma query agrupada, sem N+1.
        var childrenCounts = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id && c.ParentChatId != null)
            .GroupBy(c => c.ParentChatId!)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ParentId, x => x.Count, ct);

        return Results.Ok(list.Select(c => ToSummary(
                c, flagged.Contains(c.Id), childrenCounts.GetValueOrDefault(c.Id)))
            .ToList());
    }

    /// <summary>
    /// Contagem leve de chats aguardando o dono — poll do badge da sidebar
    /// (D2) sem baixar a lista inteira a cada tick.
    /// </summary>
    private static async Task<IResult> CountAttentionAsync(
        HttpContext http, AppDbContext db,
        ChatRunApprovals approvals, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        // Badge = chats que o filtro "aguardando" da sidebar listaria
        // (aprovações pendentes ∪ chats com itens persistidos do inbox) +
        // itens órfãos sem chat — nunca a soma de itens, senão o número
        // diverge da lista exibida.
        var flagged = approvals.PendingChatIds();
        var count = await db.Chats.CountAsync(
            c => c.UserId == user.Id && !c.Archived && !c.IsEphemeral
                && (flagged.Contains(c.Id)
                    || db.UserAttentions.Any(a => a.UserId == user.Id && a.ChatId == c.Id)),
            ct);
        count += await db.UserAttentions.CountAsync(
            a => a.UserId == user.Id && a.ChatId == null, ct);
        return Results.Ok(new AttentionCountResponse(count));
    }

    private static async Task<IResult> ListPinnedAsync(
        HttpContext http, AppDbContext db,
        ChatRunApprovals approvals, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var awaiting = approvals.PendingChatIds();
        var list = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id && c.Pinned && !c.Archived && !c.IsEphemeral)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        return Results.Ok(list.Select(c => ToSummary(c, awaiting.Contains(c.Id))).ToList());
    }

    private static async Task<IResult> ListArchivedAsync(
        HttpContext http, AppDbContext db,
        ChatRunApprovals approvals, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var awaiting = approvals.PendingChatIds();
        var list = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id && c.Archived)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        return Results.Ok(list.Select(c => ToSummary(c, awaiting.Contains(c.Id))).ToList());
    }

    private static async Task<IResult> CountArchivedAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var count = await db.Chats.CountAsync(c => c.UserId == user.Id && c.Archived, ct);
        return Results.Ok(new { count });
    }

    private static async Task<IResult> ListAllTagsAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var tagJsons = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id)
            .Select(c => c.TagsJson)
            .ToListAsync(ct);

        var tags = tagJsons
            .SelectMany(t => JsonSerializer.Deserialize<List<string>>(t, JsonOptions) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t)
            .ToList();

        return Results.Ok(tags);
    }

    private static async Task<IResult> ListChatsByTagAsync(
        TagQueryRequest request,
        HttpContext http,
        AppDbContext db,
        ChatRunApprovals approvals,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var chats = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id && !c.Archived)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        var awaiting = approvals.PendingChatIds();
        var filtered = chats
            .Where(c => (JsonSerializer.Deserialize<List<string>>(c.TagsJson, JsonOptions) ?? [])
                .Any(t => request.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            .Select(c => ToSummary(c, awaiting.Contains(c.Id)))
            .ToList();

        return Results.Ok(filtered);
    }

    private static async Task<IResult> ListSharedAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var list = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id && c.ShareId != null)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        return Results.Ok(list.Select(c => ToSummary(c)).ToList());
    }

    private static async Task<IResult> GetSharedChatAsync(
        string shareId, AppDbContext db, CancellationToken ct)
    {
        var chat = await db.Chats.AsNoTracking()
            .Include(c => c.Messages.OrderBy(m => m.Position))
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.ShareId == shareId, ct);

        if (chat is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new
        {
            chat.Id,
            chat.Title,
            User = new { chat.User?.Name },
            Models = JsonSerializer.Deserialize<List<string>>(chat.ModelsJson, JsonOptions) ?? [],
            Messages = BranchService.WalkPath(chat)
                .Select(m => new ChatMessageModel(
                m.Id, m.Role, m.Content, m.Model, m.Timestamp,
                JsonSerializer.Deserialize<List<ChatMessageVersionModel>>(m.VersionsJson, JsonOptions),
                m.ToolCallId, m.ToolCallsJson,
                ParseFiles(m.FilesJson), m.ParentId))
                .ToList(),
            chat.CreatedAt,
            chat.UpdatedAt,
        });
    }

    private static async Task<IResult> ExportAllAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var chats = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id)
            .Include(c => c.Messages)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            chats = chats.Select(c => ToResponse(c)).ToList(),
            exportedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
    }

    private static async Task<IResult> ImportChatsAsync(
        List<ChatUpsertRequest> request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var item in request)
        {
            var chat = new Chat
            {
                UserId = user.Id,
                Title = string.IsNullOrWhiteSpace(item.Title) ? "New Chat" : item.Title.Trim(),
                ModelsJson = JsonSerializer.Serialize(item.Models ?? [], JsonOptions),
                CreatedAt = now,
                UpdatedAt = now,
            };
            chat.Messages = MapMessages(item.Messages, chat.Id, now);
            db.Chats.Add(chat);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> CreateChatAsync(
        ChatUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        ConfigService config,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var chat = new Chat
        {
            UserId = user.Id,
            Title = string.IsNullOrWhiteSpace(request.Title) ? "New Chat" : request.Title.Trim(),
            ModelsJson = JsonSerializer.Serialize(request.Models ?? [], JsonOptions),
            ToolIdsJson = JsonSerializer.Serialize(request.ToolIds ?? [], JsonOptions),
            // SPEC-20261013-run-advisor RF-005: default admin do revisor.
            AdvisorEnabled = await config.GetAsync<bool?>(
                "advisor.default", null, ct) ?? false,
            CreatedAt = now,
            UpdatedAt = now,
        };

        chat.Messages = MapMessages(request.Messages, chat.Id, now);

        db.Chats.Add(chat);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> GetChatAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct, int? tail = null)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct);
        if (chat is null)
        {
            return Results.NotFound();
        }

        // Abrir o chat marca os itens de atenção dele como vistos — o badge
        // da sidebar não fica preso contando pendências já lidas.
        if (user is not null && chat.UserId == user.Id)
        {
            await AttentionService.MarkChatSeenAsync(db, user.Id, chat.Id, ct);
        }

        var parentTitle = chat.ParentChatId is { } pid
            ? await db.Chats.AsNoTracking()
                .Where(c => c.Id == pid && c.UserId == user!.Id)
                .Select(c => c.Title)
                .FirstOrDefaultAsync(ct)
            : null;
        return Results.Ok(ToResponse(chat, parentTitle, tail));
    }

    /// <summary>Página de mensagens anteriores a <paramref name="before"/> no caminho
    /// ativo (SPEC-20261019-chat-window-perf RF-001). <paramref name="take"/> é
    /// limitado a 1–200. A janela nunca quebra um grupo de tool messages no topo.</summary>
    private static async Task<IResult> GetChatMessagesAsync(
        string id, string? before, int take, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct);
        if (chat is null)
        {
            return Results.NotFound();
        }

        var path = BranchService.WalkPath(chat);
        var end = before is null
            ? path.Count
            : path.FindIndex(m => m.Id == before) is { } i && i >= 0 ? i : path.Count;
        take = Math.Clamp(take <= 0 ? 50 : take, 1, 200);
        var start = Math.Max(0, end - take);
        // Snap de fronteira: nunca abrir a página no meio de um grupo "tool"
        // consecutivo — a UI agrupa tools seguidas num único feed.
        while (start > 0 && path[start].Role == "tool" && path[start - 1].Role == "tool")
        {
            start--;
        }
        var messages = path.Skip(start).Take(end - start)
            .Select(m => new ChatMessageModel(
                m.Id, m.Role, m.Content, m.Model, m.Timestamp,
                JsonSerializer.Deserialize<List<ChatMessageVersionModel>>(m.VersionsJson, JsonOptions),
                m.ToolCallId, m.ToolCallsJson,
                ParseFiles(m.FilesJson), m.ParentId))
            .ToList();
        return Results.Ok(new ChatMessagesPage(messages, start > 0, path.Count));
    }

    /// <summary>
    /// Filhos delegados do chat (SPEC-20261010-runs-hierarchy): criados por
    /// <c>delegate_task</c> — árvore pai↔filho da sidebar/chat.
    /// </summary>
    private static async Task<IResult> ListChildrenAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var parent = await db.Chats.AsNoTracking()
            .AnyAsync(c => c.Id == id && c.UserId == user.Id, ct);
        if (!parent)
        {
            return Results.NotFound();
        }

        var children = await db.Chats.AsNoTracking()
            .Where(c => c.ParentChatId == id && c.UserId == user.Id)
            .OrderBy(c => c.CreatedAt)
            .Select(c => new { c.Id, c.Title, c.CreatedAt })
            .ToListAsync(ct);

        // Última run de cada filho — uma query agrupada.
        var childIds = children.Select(c => c.Id).ToList();
        var lastRuns = await db.ChatRuns.AsNoTracking()
            .Where(r => childIds.Contains(r.ChatId))
            .GroupBy(r => r.ChatId)
            .Select(g => new
            {
                ChatId = g.Key,
                Status = g.OrderByDescending(r => r.CreatedAt).Select(r => r.Status).FirstOrDefault(),
            })
            .ToDictionaryAsync(x => x.ChatId, x => x.Status, ct);

        return Results.Ok(children
            .Select(c => new ChatChildSummaryResponse(
                c.Id, c.Title, lastRuns.GetValueOrDefault(c.Id), c.CreatedAt))
            .ToList());
    }

    /// <summary>
    /// Binding workspace↔repo DO CHAT (SPEC-20261010-chat-repo-binding):
    /// devolve o binding efetivo com <c>source</c> = chat|user|none —
    /// "chat" quando o chat tem repo próprio, "user" no fallback global.
    /// </summary>
    private static async Task<IResult> GetWorkspaceRepoAsync(
        string id, HttpContext http, AppDbContext db,
        WorkspaceRepoService repos, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (!await db.Chats.AsNoTracking()
                .AnyAsync(c => c.Id == id && c.UserId == user.Id, ct))
        {
            return Results.NotFound();
        }

        var (binding, source) = await repos.ResolveBindingAsync(user.Id, id, ct);
        return Results.Ok(new ChatWorkspaceRepoResponse(
            binding is null
                ? null
                : new WorkspaceRepoResponse(
                    binding.Repo, binding.Branch, binding.Dir,
                    binding.TestCommand, binding.FormatCommand),
            source));
    }

    /// <summary>
    /// Abre/atualiza o repo por chat criando um worktree exclusivo da
    /// conversa (SPEC-15) ou limpa o binding de chat (body vazio/repo nulo
    /// → worktree removido; o checkout base sai de disco se nenhuma outra
    /// conversa/global o referenciar).
    /// </summary>
    private static async Task<IResult> PutWorkspaceRepoAsync(
        string id,
        WorkspaceRepoOpenRequest? request,
        HttpContext http,
        AppDbContext db,
        WorkspaceRepoService repos, GitHubService github, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (!await db.Chats.AsNoTracking()
                .AnyAsync(c => c.Id == id && c.UserId == user.Id, ct))
        {
            return Results.NotFound();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Repo))
        {
            // SPEC-15: desvincular libera o worktree da conversa e, se o
            // repo ficar sem referências, o checkout base sai de disco.
            // O chat segue trabalhando — o scratch o atende (SPEC-20261027)
            // e NÃO é apagado aqui: desvincular não é deletar a conversa.
            await repos.ReleaseChatWorkspaceAsync(
                user.Id, id, keepBinding: false, ct, dropScratch: false);
            return Results.Ok(new ChatWorkspaceRepoResponse(null, "none"));
        }

        if (string.IsNullOrWhiteSpace(request.Branch))
        {
            return Results.BadRequest(new { detail = "branch é obrigatória." });
        }

        var slug = request.Repo.Trim();
        var token = await github.GetTokenAsync(user.Id, ct);
        var (binding, error) = await repos.OpenChatAsync(
            user.Id, id, slug, request.Branch.Trim(),
            $"https://github.com/{slug}.git", token, ct);
        if (binding is null)
        {
            return Results.BadRequest(new { detail = error });
        }
        return Results.Ok(new ChatWorkspaceRepoResponse(
            new WorkspaceRepoResponse(
                binding.Repo, binding.Branch, binding.Dir,
                binding.TestCommand, binding.FormatCommand),
            "chat"));
    }

    /// <summary>
    /// Status do workspace scratch da conversa (SPEC-20261027 RF-007) —
    /// a UI usa para decidir se mostra o botão Publicar.
    /// </summary>
    private static async Task<IResult> GetScratchStatusAsync(
        string id, HttpContext http, AppDbContext db,
        WorkspaceRepoService repos, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (!await db.Chats.AsNoTracking()
                .AnyAsync(c => c.Id == id && c.UserId == user.Id, ct))
        {
            return Results.NotFound();
        }
        return Results.Ok(await repos.GetScratchStatusAsync(user.Id, id, ct));
    }

    /// <summary>
    /// Publica o scratch da conversa (SPEC-20261027 RF-006): commita o
    /// rascunho e faz push para <c>repo</c> (slug existente → branch
    /// <c>chat/{id}</c> + compare; nome novo/inexistente → cria via API e
    /// push em <c>main</c>), <c>remoteUrl</c> com credencial embutida ou
    /// PAT ad-hoc em <c>token</c>. O token nunca é gravado no remote.
    /// </summary>
    private static async Task<IResult> PublishWorkspaceAsync(
        string id,
        PublishWorkspaceRequest? request,
        HttpContext http,
        AppDbContext db,
        WorkspacePublishService publish,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (!await db.Chats.AsNoTracking()
                .AnyAsync(c => c.Id == id && c.UserId == user.Id, ct))
        {
            return Results.NotFound();
        }
        if (request is null)
        {
            return Results.BadRequest(new { detail = "informe repo ou remoteUrl." });
        }

        var (result, status, error) = await publish.PublishAsync(
            user.Id, id, user.Name, user.Email, request, ct);
        return result is not null
            ? Results.Ok(result)
            : status switch
            {
                404 => Results.NotFound(new { detail = error }),
                502 => Results.Json(new { detail = error }, statusCode: 502),
                _ => Results.BadRequest(new { detail = error }),
            };
    }

    private static async Task<IResult> UpdateChatAsync(
        string id,
        ChatUpsertRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        chat.Title = string.IsNullOrWhiteSpace(request.Title) ? chat.Title : request.Title.Trim();
        chat.ModelsJson = JsonSerializer.Serialize(request.Models ?? [], JsonOptions);
        if (request.ToolIds is not null)
        {
            chat.ToolIdsJson = JsonSerializer.Serialize(request.ToolIds, JsonOptions);
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        chat.UpdatedAt = now;

        // Merge por caminho (SPEC-20261013-chat-branching): o cliente envia só
        // o caminho ativo — mensagens fora dele (ramos irmãos) são preservadas.
        // Deleta-se o que estava no caminho antigo e sumiu do request; lista
        // vazia mantém o comportamento antigo de limpar tudo.
        var previous = chat.Messages
            .ToDictionary(m => m.Id, m => (m.Content, m.Model, m.VersionsJson, m.FilesJson));
        if (request.Messages.Count == 0)
        {
            db.ChatMessages.RemoveRange(chat.Messages);
            chat.Messages = [];
            chat.LeafMessageId = null;
        }
        else
        {
            var oldPathIds = BranchService.WalkPath(chat)
                .Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            var incomingIds = request.Messages
                .Where(m => !string.IsNullOrEmpty(m.Id))
                .Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            var toRemove = chat.Messages
                .Where(m => oldPathIds.Contains(m.Id) && !incomingIds.Contains(m.Id))
                .ToList();
            if (toRemove.Count > 0)
            {
                db.ChatMessages.RemoveRange(toRemove);
                chat.Messages.RemoveAll(m => toRemove.Contains(m));
            }

            var mapped = MapMessages(request.Messages, chat.Id, now);
            var byId = chat.Messages.ToDictionary(m => m.Id, StringComparer.Ordinal);
            chat.Messages = mapped.Select(m =>
            {
                if (!byId.TryGetValue(m.Id, out var existing))
                {
                    return m;
                }
                existing.Content = m.Content;
                existing.Model = m.Model;
                existing.ToolCallId = m.ToolCallId;
                existing.ToolCallsJson = m.ToolCallsJson;
                // Cliente legado não conhece ParentId — omite na volta;
                // preserva o armazenado em vez de apagar a árvore.
                existing.ParentId = m.ParentId ?? existing.ParentId;
                existing.Position = m.Position;
                existing.Timestamp = m.Timestamp;
                return existing;
            }).Concat(chat.Messages.Where(m => !incomingIds.Contains(m.Id))).ToList();
            // O leaf é a ponta do caminho que o cliente descreveu.
            chat.LeafMessageId = mapped[^1].Id;
        }
        foreach (var m in chat.Messages.Where(m => previous.ContainsKey(m.Id)))
        {
            var old = previous[m.Id];
            m.VersionsJson = old.Content == m.Content
                ? old.VersionsJson
                : PushVersion(old.VersionsJson, old.Content, old.Model, now);
            // Anexos (SPEC-20261013-chat-media-attachments): cliente que não
            // conhece Files não apaga os anexos existentes num upsert.
            m.FilesJson ??= old.FilesJson;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(chat));
    }

    /// <summary>
    /// Atualização parcial do chat (SPEC-20261007-chat-tool-streaming
    /// RF-004): preset de aprovação de tools —
    /// <c>allow-readonly</c> | <c>approve-mutations</c> | <c>smart</c> | <c>always-allow</c> | <c>auto</c> —
    /// e/ou título (rename leve, sem enviar o chat inteiro).
    /// </summary>
    private static async Task<IResult> PatchChatAsync(
        string id,
        ChatPatchRequest request,
        HttpContext http,
        AppDbContext db,
        WorkspaceRepoService repos,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        var changed = false;
        if (request.ApprovalPreset is not null)
        {
            if (request.ApprovalPreset is not ("allow-readonly"
                or "approve-mutations" or "smart" or "always-allow" or "auto"))
            {
                return Results.BadRequest(
                    new { detail = "approvalPreset inválido." });
            }
            chat.ApprovalPreset = request.ApprovalPreset;
            changed = true;
        }
        if (request.Mode is not null)
        {
            // SPEC-20261009 RF-001 + SPEC-15: chat | plan | build.
            // SPEC-20261027: build sem repo trabalha na pasta scratch da
            // conversa — a exigência de binding foi removida.
            if (request.Mode is not ("chat" or "build" or "plan"))
            {
                return Results.BadRequest(new { detail = "mode inválido." });
            }
            chat.Mode = request.Mode;
            changed = true;
        }
        if (request.AdvisorEnabled is { } advisor)
        {
            // Revisor de run (SPEC-20261013-run-advisor RF-005) — relido
            // por gatilho; desligar mid-run vale já na próxima fronteira.
            chat.AdvisorEnabled = advisor;
            changed = true;
        }
        if (request.Title is { } rawTitle && !string.IsNullOrWhiteSpace(rawTitle))
        {
            chat.Title = rawTitle.Trim();
            changed = true;
        }
        if (changed)
        {
            chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> DeleteChatAsync(
        string id, HttpContext http, AppDbContext db,
        WorkspaceRepoService repos, WorktreeService worktrees,
        CheckpointService checkpoints,
        OpenWebUI.Infrastructure.Terminal.TerminalSessionManager terminals,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        // Libera TODOS os artefatos do chat (sessões PTY bindadas, worktree
        // do binding, worktrees isolados por run e snapshots de checkpoint)
        // — liberação de espaço imediata, sem esperar sweeper de retenção.
        await ReleaseChatDiskAsync(
            db, repos, worktrees, checkpoints, terminals, user!.Id, id, ct);
        await AttentionService.RemoveForChatAsync(db, id, ct);

        db.Chats.Remove(chat);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> DeleteAllChatsAsync(
        HttpContext http, AppDbContext db,
        WorkspaceRepoService repos, WorktreeService worktrees,
        CheckpointService checkpoints,
        OpenWebUI.Infrastructure.Terminal.TerminalSessionManager terminals,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var chatIds = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id)
            .Select(c => c.Id)
            .ToListAsync(ct);
        foreach (var chatId in chatIds)
        {
            await ReleaseChatDiskAsync(
                db, repos, worktrees, checkpoints, terminals,
                user.Id, chatId, ct);
            await AttentionService.RemoveForChatAsync(db, chatId, ct);
        }

        await db.Chats.Where(c => c.UserId == user.Id).ExecuteDeleteAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    /// <summary>
    /// Libera o disco do chat antes de apagar a entidade (SPEC-15 +
    /// liberação de espaço): worktrees isolados de cada run
    /// (<c>data/worktrees/{u}/{run}</c>), snapshots de checkpoint de cada
    /// workdir (<c>data/checkpoints/{repo|files}/{hash}</c>) e o worktree
    /// do binding via <see cref="WorkspaceRepoService.ReleaseChatWorkspaceAsync"/>.
    /// A ordem importa: worktrees de run primeiro — o
    /// <c>git worktree remove</c> precisa do repo base ainda vivo.
    /// </summary>
    private static async Task ReleaseChatDiskAsync(
        AppDbContext db, WorkspaceRepoService repos,
        WorktreeService worktrees, CheckpointService checkpoints,
        OpenWebUI.Infrastructure.Terminal.TerminalSessionManager terminals,
        string userId, string chatId, CancellationToken ct)
    {
        // PTYs bindadas primeiro — shell órfão em cwd deletado ficaria
        // preso a inode morto (SPEC-20261022).
        await terminals.KillForChatAsync(userId, chatId);
        var mainWorkdir = await repos.ResolveWorkdirAsync(userId, chatId, ct);
        var runIds = await db.ChatRuns.AsNoTracking()
            .Where(r => r.ChatId == chatId)
            .Select(r => r.Id)
            .ToListAsync(ct);
        foreach (var runId in runIds)
        {
            var dir = await worktrees.DeleteForRunAsync(
                userId, runId, mainWorkdir, ct);
            if (dir is not null)
            {
                checkpoints.DeleteForWorkdir(dir);
            }
        }
        checkpoints.DeleteForWorkdir(mainWorkdir);
        await repos.ReleaseChatWorkspaceAsync(userId, chatId, keepBinding: false, ct);
    }

    private static async Task<IResult> TogglePinAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        chat.Pinned = !chat.Pinned;
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> GetPinnedAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct);
        return chat is null
            ? Results.NotFound()
            : Results.Ok(new { pinned = chat.Pinned });
    }

    private static async Task<IResult> ToggleArchiveAsync(
        string id, HttpContext http, AppDbContext db,
        WorkspaceRepoService repos, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        chat.Archived = !chat.Archived;
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // SPEC-15: arquivar remove o worktree da conversa (libera o
        // checkout de trabalho) mantendo o binding e o checkout base — o
        // worktree é recriado sob demanda ao desarquivar e usar. Excluir
        // libera também o binding/base.
        if (chat.Archived)
        {
            await repos.ReleaseChatWorkspaceAsync(user!.Id, id, keepBinding: true, ct);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> ArchiveAllAsync(
        HttpContext http, AppDbContext db,
        WorkspaceRepoService repos, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var chatIds = await db.Chats.AsNoTracking()
            .Where(c => c.UserId == user.Id && !c.Archived)
            .Select(c => c.Id)
            .ToListAsync(ct);
        foreach (var chatId in chatIds)
        {
            await repos.ReleaseChatWorkspaceAsync(user.Id, chatId, keepBinding: true, ct);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.Chats
            .Where(c => c.UserId == user.Id && !c.Archived)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Archived, true)
                .SetProperty(c => c.UpdatedAt, now), ct);

        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> UnarchiveAllAsync(
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.Chats
            .Where(c => c.UserId == user.Id && c.Archived)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Archived, false)
                .SetProperty(c => c.UpdatedAt, now), ct);

        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> ShareChatAsync(
        string id, HttpContext http, AppDbContext db, PermissionService permissions,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        // RF-004: flag sharing.public_chats avaliada por união dos grupos.
        if (!await permissions.HasAsync(user!, PermissionService.SharingPublicChats, ct))
        {
            return Results.Forbid();
        }

        chat.ShareId ??= Guid.NewGuid().ToString("N")[..8];
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> UnshareChatAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        chat.ShareId = null;
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    private static async Task<IResult> CloneChatAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct);
        if (chat is null)
        {
            return Results.NotFound();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var clone = new Chat
        {
            UserId = user!.Id,
            Title = $"{chat.Title} (Clone)",
            ModelsJson = chat.ModelsJson,
            TagsJson = chat.TagsJson,
            CreatedAt = now,
            UpdatedAt = now,
        };
        // A árvore é preservada: ids novos, ParentId remapeado (RF-001).
        var idMap = chat.Messages.ToDictionary(
            m => m.Id, _ => Guid.NewGuid().ToString(), StringComparer.Ordinal);
        clone.Messages = chat.Messages
            .OrderBy(m => m.Position)
            .Select((m, i) => new ChatMessage
            {
                Id = idMap[m.Id],
                ChatId = clone.Id,
                Role = m.Role,
                Content = m.Content,
                Model = m.Model,
                FilesJson = m.FilesJson,
                Position = i,
                Timestamp = m.Timestamp,
                ParentId = m.ParentId is null
                    ? null
                    : idMap.GetValueOrDefault(m.ParentId),
            })
            .ToList();
        clone.LeafMessageId = chat.LeafMessageId is null
            ? null
            : idMap.GetValueOrDefault(chat.LeafMessageId);

        db.Chats.Add(clone);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(clone));
    }

    private static async Task<IResult> SetFolderAsync(
        string id,
        SetFolderRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        if (!string.IsNullOrEmpty(request.FolderId))
        {
            var folderExists = await db.Folders
                .AnyAsync(f => f.Id == request.FolderId && f.UserId == user!.Id, ct);
            if (!folderExists)
            {
                return Results.NotFound(new { detail = "Pasta não encontrada." });
            }
        }

        chat.FolderId = string.IsNullOrEmpty(request.FolderId) ? null : request.FolderId;
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> GetTagsAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct);
        return chat is null
            ? Results.NotFound()
            : Results.Ok(JsonSerializer.Deserialize<List<string>>(chat.TagsJson, JsonOptions) ?? []);
    }

    private static async Task<IResult> SetTagsAsync(
        string id,
        TagUpdateRequest request,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        var tags = (request.Tags ?? [])
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        chat.TagsJson = JsonSerializer.Serialize(tags, JsonOptions);
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> ClearTagsAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        chat.TagsJson = "[]";
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new StatusResponse(true));
    }

    /// <summary>
    /// Leafs divergentes do chat (SPEC-20261013-chat-branching RF-003):
    /// cada ponta de ramo com profundidade, contagem, preview e resumo.
    /// </summary>
    private static async Task<IResult> ListBranchesAsync(
        string id, HttpContext http, AppDbContext db, BranchService branches,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct);
        if (chat is null)
        {
            return Results.NotFound();
        }

        var leaves = await branches.ListBranchesAsync(chat, ct);
        return Results.Ok(leaves.Select(l => new BranchLeafResponse(
            l.LeafId, l.Depth, l.MessageCount, l.Preview, l.Summary,
            l.UpdatedAt, l.IsActive)));
    }

    /// <summary>
    /// <c>POST /{id}/compact</c> (RF-004, SPEC-20261018-agent-polish):
    /// força a compactação do histórico via
    /// <see cref="ContextCompactionService.CompactNowAsync"/> — com
    /// <c>compaction:native</c> + capacidade, tenta o endpoint nativo do
    /// provider e cai no summarizer interno com warning.
    /// </summary>
    private static async Task<IResult> CompactChatAsync(
        string id, HttpContext http, AppDbContext db,
        ContextBoundaryService boundaries,
        ContextCompactionService compaction,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        var chat = await db.Chats.Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == user.Id, ct);
        if (chat is null)
        {
            return Results.NotFound(new { detail = "Chat não encontrado." });
        }

        var path = BranchService.WalkPath(chat);
        var visible = await boundaries.FilterAsync(chat.Id, path, ct);
        var history = visible
            .Select(m => new ChatCompletionMessage(
                m.Role, m.Content, m.ToolCallId, m.ToolCallsJson))
            .ToList();

        // Modelo para o resumo: 1º da lista do chat → último assistant.
        var models = JsonSerializer.Deserialize<List<string>>(chat.ModelsJson);
        var model = models?.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
            ?? visible.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Model))?.Model;
        if (model is null)
        {
            return Results.BadRequest(new
            {
                detail = "Chat sem modelo associado — envie uma mensagem primeiro.",
            });
        }

        var result = await compaction.CompactNowAsync(
            chat.Id, history, model, user.Id, ct);
        return Results.Ok(new ChatCompactResponse(
            result.Compacted, result.Native, result.Warning));
    }

    /// <summary>
    /// Ativa uma mensagem como leaf (RF-004): o próximo envio pendura nela.
    /// O ramo abandonado com ≥4 mensagens ganha resumo no papel
    /// <c>memory</c> (RF-005); a resposta renderiza o novo caminho ativo.
    /// </summary>
    private static async Task<IResult> ActivateBranchAsync(
        string id,
        ActivateBranchRequest request,
        HttpContext http,
        AppDbContext db,
        BranchService branches,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        var model = JsonSerializer.Deserialize<List<string>>(chat.ModelsJson, JsonOptions)
            ?.FirstOrDefault();
        if (!await branches.ActivateLeafAsync(chat, request.LeafId, model, ct))
        {
            return Results.NotFound(new { detail = "Leaf não encontrado neste chat." });
        }
        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> UpdateMessageAsync(
        string id,
        string messageId,
        MessageUpdateRequest request,
        HttpContext http,
        AppDbContext db,
        IHubContext<ChatHub> hub,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        var message = chat.Messages.FirstOrDefault(m => m.Id == messageId);
        if (message is null)
        {
            return Results.NotFound();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (message.Content != request.Content)
        {
            message.VersionsJson = PushVersion(
                message.VersionsJson, message.Content, message.Model, now);
        }
        message.Content = request.Content;
        chat.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await hub.Clients.Group(ChatHub.ChatGroupName(id))
            .SendAsync("message:updated", id, messageId, request.Content, ct);
        return Results.Ok(ToResponse(chat));
    }

    private static async Task<IResult> DeleteMessageAsync(
        string id,
        string messageId,
        HttpContext http,
        AppDbContext db,
        IHubContext<ChatHub> hub,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct, tracking: true);
        if (chat is null)
        {
            return Results.NotFound();
        }

        var index = chat.Messages.FindIndex(m => m.Id == messageId);
        if (index < 0)
        {
            return Results.NotFound();
        }

        // Remove a mensagem e a subárvore dela (SPEC-20261013-chat-branching):
        // descendentes explícitos por ParentId + a continuação legada
        // implícita por Position. Num chat que nunca ramificou isso equivale
        // ao "ela e tudo que veio depois" de antes; ramos irmãos sobrevivem.
        var target = chat.Messages[index];
        var toRemoveIds = new HashSet<string>(StringComparer.Ordinal) { target.Id };
        var queue = new Queue<string>();
        queue.Enqueue(target.Id);
        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            foreach (var child in chat.Messages.Where(m => m.ParentId == parentId))
            {
                if (toRemoveIds.Add(child.Id))
                {
                    queue.Enqueue(child.Id);
                }
            }
        }
        foreach (var m in chat.Messages.Where(
            m => m.ParentId is null && m.Position >= target.Position))
        {
            toRemoveIds.Add(m.Id);
        }

        var toRemove = chat.Messages.Where(m => toRemoveIds.Contains(m.Id)).ToList();
        db.ChatMessages.RemoveRange(toRemove);
        chat.Messages.RemoveAll(m => toRemoveIds.Contains(m.Id));

        // Leaf deletado (RF-edge): volta ao pai do alvo — explícito ou a
        // ponta legada restante (maior Position sem pai sobrevivente).
        if (chat.LeafMessageId is not null && toRemoveIds.Contains(chat.LeafMessageId))
        {
            chat.LeafMessageId = target.ParentId
                ?? chat.Messages
                    .Where(m => m.ParentId is null)
                    .MaxBy(m => m.Position)?.Id;
        }
        chat.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        await hub.Clients.Group(ChatHub.ChatGroupName(id))
            .SendAsync("message:deleted", id, messageId, ct);
        return Results.Ok(ToResponse(chat));
    }

    private const int MaxVersionsPerMessage = 50;

    /// <summary>Empurra a versão anterior para o histórico (JSON), com limite de 50.</summary>
    private static string PushVersion(string versionsJson, string content, string? model, long timestamp)
    {
        var versions = JsonSerializer.Deserialize<List<ChatMessageVersionModel>>(versionsJson, JsonOptions)
            ?? [];
        versions.Add(new ChatMessageVersionModel(content, model, timestamp));
        if (versions.Count > MaxVersionsPerMessage)
        {
            versions.RemoveAt(0);
        }
        return JsonSerializer.Serialize(versions, JsonOptions);
    }

    private static async Task<IResult> ListAllChatsAdminAsync(
        HttpContext http, AppDbContext db, string? query, int page, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        var current = page < 1 ? 1 : page;
        const int pageSize = 20;

        var chats = db.Chats.AsNoTracking()
            .Join(db.Users.AsNoTracking(),
                c => c.UserId,
                u => u.Id,
                (c, u) => new { c, u });

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLower();
            chats = chats.Where(x => x.c.Title.ToLower().Contains(term));
        }

        var total = await chats.CountAsync(ct);
        var items = await chats
            .OrderByDescending(x => x.c.UpdatedAt)
            .Skip((current - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AdminChatSummaryResponse(
                x.c.Id, x.c.Title, x.u.Id, x.u.Name, x.u.Email,
                x.c.Messages.Count, x.c.Archived, x.c.CreatedAt, x.c.UpdatedAt))
            .ToListAsync(ct);

        return Results.Ok(new AdminChatListResponse(items, total));
    }

    private static async Task<IResult> GetMessageVersionsAsync(
        string id,
        string messageId,
        HttpContext http,
        AppDbContext db,
        CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        var chat = await LoadChatAsync(id, user?.Id, db, ct);
        var message = chat?.Messages.FirstOrDefault(m => m.Id == messageId);
        if (message is null)
        {
            return Results.NotFound();
        }

        var versions = JsonSerializer.Deserialize<List<ChatMessageVersionModel>>(
            message.VersionsJson, JsonOptions) ?? [];
        return Results.Ok(versions);
    }

    private static List<ChatMessage> MapMessages(
        IReadOnlyList<ChatMessageModel>? models, string chatId, long now) =>
        (models ?? [])
            .Select((m, i) => new ChatMessage
            {
                Id = string.IsNullOrEmpty(m.Id) ? Guid.NewGuid().ToString() : m.Id,
                ChatId = chatId,
                Role = m.Role,
                Content = m.Content,
                Model = m.Model,
                ToolCallId = m.ToolCallId,
                ToolCallsJson = m.ToolCallsJson,
                FilesJson = m.Files is null
                    ? null
                    : JsonSerializer.Serialize(m.Files, JsonOptions),
                ParentId = m.ParentId,
                Position = i,
                Timestamp = m.Timestamp > 0 ? m.Timestamp : now,
            })
            .ToList();

    /// <summary>FilesJson → lista de anexos; null/malformado → null (SPEC-20261013-chat-media-attachments).</summary>
    private static List<ChatMessageFileModel>? ParseFiles(string? filesJson)
    {
        if (string.IsNullOrEmpty(filesJson))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<List<ChatMessageFileModel>>(
                filesJson, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task<Chat?> LoadChatAsync(
        string id, string? userId, AppDbContext db, CancellationToken ct, bool tracking = false)
    {
        if (userId is null)
        {
            return Task.FromResult<Chat?>(null);
        }

        var query = tracking ? db.Chats.AsQueryable() : db.Chats.AsNoTracking();
        return query
            .Include(c => c.Messages.OrderBy(m => m.Position))
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, ct);
    }

    private static ChatSummaryResponse ToSummary(
        Chat chat, bool awaiting = false, int childrenCount = 0) => new(
        chat.Id,
        chat.Title,
        chat.Pinned,
        chat.FolderId,
        JsonSerializer.Deserialize<List<string>>(chat.TagsJson, JsonOptions) ?? [],
        chat.CreatedAt,
        chat.UpdatedAt,
        awaiting,
        chat.ParentChatId,
        childrenCount);

    private static ChatResponse ToResponse(Chat chat, string? parentTitle = null, int? tail = null)
    {
        var path = BranchService.WalkPath(chat);
        var start = tail is { } n && n > 0 && path.Count > n ? path.Count - n : 0;
        // Snap de fronteira na cauda: mesma regra do endpoint paginado.
        while (start > 0 && path[start].Role == "tool" && path[start - 1].Role == "tool")
        {
            start--;
        }
        return new ChatResponse(
        chat.Id,
        chat.Title,
        JsonSerializer.Deserialize<List<string>>(chat.ModelsJson, JsonOptions) ?? [],
        // O cliente renderiza o caminho ativo (leaf→raiz): fora dele, os
        // outros ramos não aparecem (SPEC-20261013-chat-branching RF-002).
        path.Skip(start)
            .Select(m => new ChatMessageModel(
                m.Id, m.Role, m.Content, m.Model, m.Timestamp,
                JsonSerializer.Deserialize<List<ChatMessageVersionModel>>(m.VersionsJson, JsonOptions),
                m.ToolCallId, m.ToolCallsJson,
                ParseFiles(m.FilesJson), m.ParentId))
            .ToList(),
        chat.Pinned,
        chat.Archived,
        JsonSerializer.Deserialize<List<string>>(chat.TagsJson, JsonOptions) ?? [],
        chat.FolderId,
        chat.ShareId,
        JsonSerializer.Deserialize<List<string>>(chat.ToolIdsJson, JsonOptions) ?? [],
        chat.CreatedAt,
        chat.UpdatedAt,
        chat.ApprovalPreset,
        chat.Mode,
        chat.ParentChatId,
        parentTitle,
        chat.AdvisorEnabled,
        BranchService.ResolveLeaf(chat)?.Id,
        BranchService.ListLeaves(chat).Count,
        start > 0);
    }

    /// <summary>Filtro por tags.</summary>
    public sealed record TagQueryRequest(IReadOnlyList<string> Tags);

    /// <summary>Atribuição de pasta a um chat.</summary>
    public sealed record SetFolderRequest(string? FolderId);

    /// <summary>Atualização das tags de um chat.</summary>
    public sealed record TagUpdateRequest(IReadOnlyList<string>? Tags);
}
