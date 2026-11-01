using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Hubs;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints REST de canais (/api/v1/channels) com difusão SignalR.</summary>
public static class ChannelEndpoints
{
    private const string AdminRole = "admin";
    private const string ChannelNotFoundMessage = "Canal não encontrado.";
    private const string ModelAuthorName = "modelo";
    /// <summary>Mapeia o grupo /api/v1/channels.</summary>
    public static void MapChannelEndpoints(this WebApplication app)
    {
        var channels = app.MapGroup("/api/v1/channels").RequireAuthorization();
        channels.MapGet("/", ListChannelsAsync);
        channels.MapPost("/", CreateChannelAsync);
        channels.MapGet("/{id}", GetChannelAsync);
        channels.MapPut("/{id}", UpdateChannelAsync);
        channels.MapDelete("/{id}", DeleteChannelAsync);
        channels.MapGet("/{id}/messages", ListMessagesAsync);
        channels.MapPost("/{id}/messages", PostMessageAsync);
        channels.MapPost("/{id}/members", AddMembersAsync);
        channels.MapDelete("/{id}/members/{userId}", RemoveMemberAsync);
        channels.MapPost("/dm", CreateDmAsync);
        channels.MapGet("/{id}/access", GetAccessAsync);
        channels.MapPost("/{id}/access/update", UpdateAccessAsync);
        channels.MapPost("/{id}/read", MarkReadAsync);
        channels.MapGet("/{id}/messages/{mid}/replies", ListRepliesAsync);
        channels.MapPost("/{id}/messages/{mid}/reactions/{emoji}", AddReactionAsync);
        channels.MapDelete("/{id}/messages/{mid}/reactions/{emoji}", RemoveReactionAsync);
        channels.MapPost("/{id}/messages/{mid}/pin", PinMessageAsync);
        channels.MapDelete("/{id}/messages/{mid}/pin", UnpinMessageAsync);
        channels.MapGet("/{id}/pinned", ListPinnedAsync);
    }

    private static async Task<IResult> ListChannelsAsync(
        HttpContext http, AppDbContext db, AccessControlService access, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var all = await db.Channels.AsNoTracking()
            .Include(c => c.Members)
            .ThenInclude(m => m.User)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);
        var groups = await access.GetGroupIdsAsync(user.Id, ct);

        var channels = all
            .Where(c => c.Members.Any(m => m.UserId == user.Id)
                || access.LevelByGrants(user, c.AccessGrantsJson, groups)
                    is not AccessControlService.None)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Description,
                c.Type,
                c.CreatedAt,
                MemberCount = c.Members.Count,
                MyRole = c.Members.FirstOrDefault(m => m.UserId == user.Id)?.Role ?? "viewer",
                MyLastRead = c.Members.Where(m => m.UserId == user.Id)
                    .Select(m => m.LastReadAt).FirstOrDefault(),
                OtherNames = c.Type == "dm"
                    ? c.Members.Where(m => m.UserId != user.Id).Select(m => m.User!.Name)
                    : null,
            })
            .ToList();

        var unreadByChannel = new Dictionary<string, int>();
        foreach (var c in channels)
        {
            unreadByChannel[c.Id] = await db.ChannelMessages
                .CountAsync(m => m.ChannelId == c.Id
                    && m.CreatedAt > c.MyLastRead
                    && m.UserId != user.Id, ct);
        }

        return Results.Ok(channels.Select(c => new ChannelResponse(
            c.Id,
            c.Type == "dm" ? (c.OtherNames!.FirstOrDefault() ?? c.Name) : c.Name,
            c.Description, c.Type, c.MemberCount, c.MyRole,
            unreadByChannel[c.Id], c.CreatedAt)));
    }

    private static async Task<IResult> CreateChannelAsync(
        [FromBody] CreateChannelRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { detail = "Nome do canal é obrigatório." });
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var channel = new Channel
        {
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            CreatedByUserId = user.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        channel.Members.Add(new ChannelMember
        {
            ChannelId = channel.Id, UserId = user.Id, Role = AdminRole, CreatedAt = now,
        });

        var wantedIds = (request.MemberIds ?? []).Where(id => id != user.Id).Distinct().ToList();
        var validIds = await db.Users.AsNoTracking()
            .Where(u => wantedIds.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct);
        foreach (var memberId in validIds)
        {
            channel.Members.Add(new ChannelMember
            {
                ChannelId = channel.Id, UserId = memberId, CreatedAt = now,
            });
        }

        db.Channels.Add(channel);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new ChannelResponse(
            channel.Id, channel.Name, channel.Description, channel.Type,
            channel.Members.Count, AdminRole, 0, channel.CreatedAt));
    }

    private static async Task<IResult> GetChannelAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, channel) = await LoadMembershipAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = ChannelNotFoundMessage });
        }

        var membership = await db.ChannelMembers.AsNoTracking()
            .Where(m => m.ChannelId == id)
            .Join(db.Users, m => m.UserId, u => u.Id,
                (m, u) => new ChannelMemberResponse(u.Id, u.Name, u.ProfileImageUrl, m.Role))
            .ToListAsync(ct);

        var myRole = membership.FirstOrDefault(m => m.UserId == user.Id)?.Role ?? "viewer";
        return Results.Ok(new ChannelDetailResponse(
            channel.Id, channel.Name, channel.Description, channel.Type,
            myRole, channel.CreatedAt, membership));
    }

    private static async Task<IResult> UpdateChannelAsync(
        string id, [FromBody] UpdateChannelRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, channel, role) = await LoadWithRoleAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = ChannelNotFoundMessage });
        }
        if (!IsChannelAdmin(user, role))
        {
            return Results.Forbid();
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            channel.Name = request.Name.Trim();
        }
        if (request.Description is not null)
        {
            channel.Description = request.Description.Trim();
        }
        channel.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    private static async Task<IResult> DeleteChannelAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, channel, role) = await LoadWithRoleAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = ChannelNotFoundMessage });
        }
        if (!IsChannelAdmin(user, role))
        {
            return Results.Forbid();
        }

        db.Channels.Remove(channel);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    private static async Task<IResult> ListMessagesAsync(
        string id, HttpContext http, AppDbContext db,
        [FromQuery] int skip = 0, [FromQuery] int take = 0, CancellationToken ct = default)
    {
        var (user, channel) = await LoadMembershipAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = ChannelNotFoundMessage });
        }

        var messages = await db.ChannelMessages.AsNoTracking()
            .Where(m => m.ChannelId == id && m.ParentId == null)
            .OrderBy(m => m.CreatedAt)
            .Skip(Math.Max(0, skip))
            .Take(take is > 0 and <= 200 ? take : 50)
            .Select(m => new
            {
                m.Id,
                m.ChannelId,
                m.UserId,
                m.ModelId,
                AuthorName = m.UserId == null ? m.ModelId ?? ModelAuthorName : m.User!.Name,
                m.User!.ProfileImageUrl,
                m.Content,
                m.IsPinned,
                m.CreatedAt,
                ReplyCount = db.ChannelMessages.Count(r => r.ParentId == m.Id),
            })
            .ToListAsync(ct);

        var reactions = await LoadReactionsAsync(db, messages.Select(m => m.Id), ct);
        return Results.Ok(messages.Select(m => new ChannelMessageResponse(
            m.Id, m.ChannelId, m.UserId, m.ModelId, m.AuthorName,
            m.ProfileImageUrl, m.Content, null, m.ReplyCount, m.IsPinned,
            reactions.GetValueOrDefault(m.Id, []), m.CreatedAt)));
    }

    private static async Task<IResult> PostMessageAsync(
        string id, [FromBody] CreateChannelMessageRequest request,
        HttpContext http, AppDbContext db,
        IHubContext<ChatHub> hub, IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        var (user, channel) = await LoadMembershipAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        // Leitores via grant (viewer) não postam — é preciso ser membro.
        if (channel is not null
            && !await db.ChannelMembers.AnyAsync(
                m => m.ChannelId == id && m.UserId == user.Id, ct))
        {
            return Results.Forbid();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = ChannelNotFoundMessage });
        }
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return Results.BadRequest(new { detail = "Conteúdo é obrigatório." });
        }

        string? parentId = null;
        if (!string.IsNullOrWhiteSpace(request.ParentId))
        {
            var exists = await db.ChannelMessages.AnyAsync(
                m => m.Id == request.ParentId && m.ChannelId == id, ct);
            if (!exists)
            {
                return Results.BadRequest(new { detail = "Mensagem pai não encontrada." });
            }
            parentId = request.ParentId;
        }

        var message = new ChannelMessage
        {
            ChannelId = id,
            UserId = user.Id,
            Content = request.Content,
            ParentId = parentId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        db.ChannelMessages.Add(message);
        channel.UpdatedAt = message.CreatedAt;
        await db.SaveChangesAsync(ct);

        var response = new ChannelMessageResponse(
            message.Id, id, user.Id, null, user.Name,
            user.ProfileImageUrl, message.Content, parentId, 0, false,
            [], message.CreatedAt);
        await hub.Clients.Group(ChatHub.GroupName(id))
            .SendAsync("message:new", response, ct);

        var mention = ExtractModelMention(request.Content);
        if (mention is not null)
        {
            _ = ReplyWithModelAsync(scopeFactory, id, mention, request.Content);
        }

        return Results.Ok(response);
    }

    private static async Task<IResult> AddMembersAsync(
        string id, [FromBody] AddChannelMembersRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, channel, role) = await LoadWithRoleAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = ChannelNotFoundMessage });
        }
        if (!IsChannelAdmin(user, role) || channel.Type == "dm")
        {
            return Results.Forbid();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var memberRole = request.Role == AdminRole ? AdminRole : "member";
        foreach (var memberId in (request.UserIds ?? []).Distinct())
        {
            var exists = await db.ChannelMembers
                .AnyAsync(m => m.ChannelId == id && m.UserId == memberId, ct);
            if (!exists && await db.Users.AnyAsync(u => u.Id == memberId, ct))
            {
                db.ChannelMembers.Add(new ChannelMember
                {
                    ChannelId = id, UserId = memberId, Role = memberRole, CreatedAt = now,
                });
            }
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    private static async Task<IResult> RemoveMemberAsync(
        string id, string userId,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, channel, role) = await LoadWithRoleAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = ChannelNotFoundMessage });
        }
        if ((!IsChannelAdmin(user, role) && user.Id != userId) || channel.Type == "dm")
        {
            return Results.Forbid();
        }

        var member = await db.ChannelMembers
            .FirstOrDefaultAsync(m => m.ChannelId == id && m.UserId == userId, ct);
        if (member is null)
        {
            return Results.NotFound(new { detail = "Membro não encontrado." });
        }

        db.ChannelMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    /// <summary>Cria ou retorna o DM entre o chamador e outro usuário (2 membros fixos).</summary>
    private static async Task<IResult> CreateDmAsync(
        [FromBody] CreateDmRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var other = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (other is null)
        {
            return Results.NotFound(new { detail = "Usuário não encontrado." });
        }
        if (other.Id == user.Id)
        {
            return Results.BadRequest(new { detail = "DM requer outro usuário." });
        }

        var memberIds = new[] { user.Id, other.Id };
        var existing = await db.Channels.AsNoTracking()
            .Where(c => c.Type == "dm"
                && c.Members.Count == 2
                && c.Members.All(m => memberIds.Contains(m.UserId)))
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return Results.Ok(new ChannelResponse(
                existing.Id, other.Name, existing.Description, "dm", 2,
                "member", 0, existing.CreatedAt));
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var channel = new Channel
        {
            Name = "dm",
            Type = "dm",
            CreatedByUserId = user.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };
        foreach (var memberId in memberIds)
        {
            channel.Members.Add(new ChannelMember
            {
                ChannelId = channel.Id, UserId = memberId, CreatedAt = now,
            });
        }
        db.Channels.Add(channel);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new ChannelResponse(
            channel.Id, other.Name, null, "dm", 2, "member", 0, now));
    }

    /// <summary>Marca o canal como lido para o chamador.</summary>
    private static async Task<IResult> MarkReadAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, channel) = await LoadMembershipAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = ChannelNotFoundMessage });
        }

        var member = await db.ChannelMembers
            .FirstAsync(m => m.ChannelId == id && m.UserId == user.Id, ct);
        member.LastReadAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true });
    }

    /// <summary>Lista as respostas (thread) de uma mensagem.</summary>
    private static async Task<IResult> ListRepliesAsync(
        string id, string mid, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, channel) = await LoadMembershipAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = ChannelNotFoundMessage });
        }

        var replies = await db.ChannelMessages.AsNoTracking()
            .Where(m => m.ChannelId == id && m.ParentId == mid)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new
            {
                m.Id,
                m.ChannelId,
                m.UserId,
                m.ModelId,
                AuthorName = m.UserId == null ? m.ModelId ?? ModelAuthorName : m.User!.Name,
                m.User!.ProfileImageUrl,
                m.Content,
                m.IsPinned,
                m.CreatedAt,
            })
            .ToListAsync(ct);

        var reactions = await LoadReactionsAsync(db, replies.Select(r => r.Id), ct);
        return Results.Ok(replies.Select(m => new ChannelMessageResponse(
            m.Id, m.ChannelId, m.UserId, m.ModelId, m.AuthorName,
            m.ProfileImageUrl, m.Content, mid, 0, m.IsPinned,
            reactions.GetValueOrDefault(m.Id, []), m.CreatedAt)));
    }

    private static async Task<IResult> ToggleReactionAsync(
        string id, string mid, string emoji, bool add,
        HttpContext http, AppDbContext db,
        IHubContext<ChatHub> hub, CancellationToken ct)
    {
        var (user, channel) = await LoadMembershipAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = ChannelNotFoundMessage });
        }
        if (string.IsNullOrWhiteSpace(emoji) || emoji.Length > 32)
        {
            return Results.BadRequest(new { detail = "Emoji inválido." });
        }

        var messageExists = await db.ChannelMessages
            .AnyAsync(m => m.Id == mid && m.ChannelId == id, ct);
        if (!messageExists)
        {
            return Results.NotFound(new { detail = "Mensagem não encontrada." });
        }

        var existing = await db.ChannelMessageReactions.FirstOrDefaultAsync(
            r => r.ChannelMessageId == mid && r.UserId == user.Id && r.Emoji == emoji, ct);
        if (add && existing is null)
        {
            db.ChannelMessageReactions.Add(new ChannelMessageReaction
            {
                ChannelMessageId = mid,
                UserId = user.Id,
                Emoji = emoji,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }
        else if (!add && existing is not null)
        {
            db.ChannelMessageReactions.Remove(existing);
        }
        await db.SaveChangesAsync(ct);

        var reactions = await LoadReactionsAsync(db, [mid], ct);
        var aggregate = reactions.GetValueOrDefault(mid, []);
        await hub.Clients.Group(ChatHub.GroupName(id))
            .SendAsync("reaction", new { channel_id = id, message_id = mid, reactions = aggregate }, ct);
        return Results.Ok(aggregate);
    }

    private static Task<IResult> AddReactionAsync(
        string id, string mid, string emoji,
        HttpContext http, AppDbContext db, IHubContext<ChatHub> hub, CancellationToken ct) =>
        ToggleReactionAsync(id, mid, emoji, true, http, db, hub, ct);

    private static Task<IResult> RemoveReactionAsync(
        string id, string mid, string emoji,
        HttpContext http, AppDbContext db, IHubContext<ChatHub> hub, CancellationToken ct) =>
        ToggleReactionAsync(id, mid, emoji, false, http, db, hub, ct);

    private static async Task<IResult> SetPinnedAsync(
        string id, string mid, bool pinned,
        HttpContext http, AppDbContext db,
        IHubContext<ChatHub> hub, CancellationToken ct)
    {
        var (user, channel) = await LoadMembershipAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = ChannelNotFoundMessage });
        }

        var message = await db.ChannelMessages
            .FirstOrDefaultAsync(m => m.Id == mid && m.ChannelId == id, ct);
        if (message is null)
        {
            return Results.NotFound(new { detail = "Mensagem não encontrada." });
        }

        message.IsPinned = pinned;
        await db.SaveChangesAsync(ct);
        await hub.Clients.Group(ChatHub.GroupName(id))
            .SendAsync("message:update", new { channel_id = id, id = mid, is_pinned = pinned }, ct);
        return Results.Ok(new { status = true, is_pinned = pinned });
    }

    private static Task<IResult> PinMessageAsync(
        string id, string mid,
        HttpContext http, AppDbContext db, IHubContext<ChatHub> hub, CancellationToken ct) =>
        SetPinnedAsync(id, mid, true, http, db, hub, ct);

    private static Task<IResult> UnpinMessageAsync(
        string id, string mid,
        HttpContext http, AppDbContext db, IHubContext<ChatHub> hub, CancellationToken ct) =>
        SetPinnedAsync(id, mid, false, http, db, hub, ct);

    /// <summary>Lista mensagens fixadas do canal.</summary>
    private static async Task<IResult> ListPinnedAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var (user, channel) = await LoadMembershipAsync(http, id, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (channel is null)
        {
            return Results.NotFound(new { detail = ChannelNotFoundMessage });
        }

        var pinned = await db.ChannelMessages.AsNoTracking()
            .Where(m => m.ChannelId == id && m.IsPinned)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new
            {
                m.Id,
                m.ChannelId,
                m.UserId,
                m.ModelId,
                AuthorName = m.UserId == null ? m.ModelId ?? ModelAuthorName : m.User!.Name,
                m.User!.ProfileImageUrl,
                m.Content,
                m.ParentId,
                m.CreatedAt,
            })
            .ToListAsync(ct);

        var reactions = await LoadReactionsAsync(db, pinned.Select(m => m.Id), ct);
        return Results.Ok(pinned.Select(m => new ChannelMessageResponse(
            m.Id, m.ChannelId, m.UserId, m.ModelId, m.AuthorName,
            m.ProfileImageUrl, m.Content, m.ParentId, 0, true,
            reactions.GetValueOrDefault(m.Id, []), m.CreatedAt)));
    }

    /// <summary>Reações agregadas por emoji para um conjunto de mensagens.</summary>
    private static async Task<Dictionary<string, List<ChannelReactionResponse>>> LoadReactionsAsync(
        AppDbContext db, IEnumerable<string> messageIds, CancellationToken ct)
    {
        var ids = messageIds.ToList();
        var rows = await db.ChannelMessageReactions.AsNoTracking()
            .Where(r => ids.Contains(r.ChannelMessageId))
            .Select(r => new { r.ChannelMessageId, r.Emoji, r.UserId })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.ChannelMessageId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .GroupBy(r => r.Emoji)
                    .Select(e => new ChannelReactionResponse(
                        e.Key, e.Count(), e.Select(r => r.UserId).ToList()))
                    .ToList());
    }

    /// <summary>Extrai o identificador de modelo da primeira menção @modelo.</summary>
    private static string? ExtractModelMention(string content)
    {
        var match = Regex.Match(content, @"@([\w.:\-/]+)", RegexOptions.None, RegexDefaults.MatchTimeout);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Resolve a menção contra modelos conhecidos e posta a resposta do
    /// provedor no canal (ou mensagem de erro) via SignalR.
    /// </summary>
    private static async Task ReplyWithModelAsync(
        IServiceScopeFactory scopeFactory, string channelId,
        string mention, string prompt)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var providers = scope.ServiceProvider.GetRequiredService<ProviderService>();
        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<ChatHub>>();
        var ct = CancellationToken.None;

        var modelId = await ResolveModelAsync(db, providers, mention);
        string content;
        string? resolvedModel = modelId;
        if (modelId is null)
        {
            content = $"Modelo '@{mention}' não encontrado.";
            resolvedModel = mention;
        }
        else
        {
            try
            {
                content = await providers.CompleteAsync(
                    new ChatCompletionRequest(
                        modelId,
                        [new ChatCompletionMessage("user", prompt)],
                        Stream: false),
                    userId: null, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                content = $"Erro ao consultar o modelo '{modelId}': {ex.Message}";
            }
        }

        var message = new ChannelMessage
        {
            ChannelId = channelId,
            UserId = null,
            ModelId = resolvedModel,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        db.ChannelMessages.Add(message);
        await db.SaveChangesAsync(ct);

        await hub.Clients.Group(ChatHub.GroupName(channelId)).SendAsync(
            "message:new",
            new ChannelMessageResponse(
                message.Id, channelId, null, resolvedModel,
                resolvedModel ?? ModelAuthorName, null, content, null, 0, false,
                [], message.CreatedAt),
            ct);
    }

    /// <summary>Resolve a menção contra modelos customizados e provedores.</summary>
    private static async Task<string?> ResolveModelAsync(
        AppDbContext db, ProviderService providers, string mention)
    {
        var custom = await db.ModelEntries.AsNoTracking()
            .FirstOrDefaultAsync(m =>
                m.IsActive && (m.Id == mention || m.Name == mention));
        if (custom is not null)
        {
            return custom.Id;
        }

        try
        {
            var models = await providers.ListModelsAsync();
            return models
                .FirstOrDefault(m =>
                    m.Id.Equals(mention, StringComparison.OrdinalIgnoreCase) ||
                    m.Name.Equals(mention, StringComparison.OrdinalIgnoreCase))
                ?.Id;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<(User? user, Channel? channel)> LoadMembershipAsync(
        HttpContext http, string channelId, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return (null, null);
        }

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, ct);
        if (channel is null)
        {
            return (user, null);
        }

        var isMember = await db.ChannelMembers
            .AnyAsync(m => m.ChannelId == channelId && m.UserId == user.Id, ct);
        if (!isMember && !await HasGrantAsync(http, db, user, channel, ct))
        {
            return (user, null);
        }

        return (user, channel);
    }

    private static async Task<(User? user, Channel? channel, string? role)> LoadWithRoleAsync(
        HttpContext http, string channelId, AppDbContext db, CancellationToken ct)
    {
        var (user, channel) = await LoadMembershipAsync(http, channelId, db, ct);
        if (user is null || channel is null)
        {
            return (user, channel, null);
        }

        var role = await db.ChannelMembers
            .Where(m => m.ChannelId == channelId && m.UserId == user.Id)
            .Select(m => m.Role)
            .FirstOrDefaultAsync(ct);
        return (user, channel, role);
    }

    private static async Task<IResult> GetAccessAsync(
        string id, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (channel is null || (channel.CreatedByUserId != user.Id && user.Role != UserRoles.Admin))
        {
            return Results.NotFound(new { detail = ChannelNotFoundMessage });
        }

        return Results.Ok(new { access_grants = AccessControlService.Parse(channel.AccessGrantsJson) });
    }

    private static async Task<IResult> UpdateAccessAsync(
        string id, [FromBody] AccessUpdateRequest request,
        HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (channel is null || (channel.CreatedByUserId != user.Id && user.Role != UserRoles.Admin))
        {
            return Results.NotFound(new { detail = ChannelNotFoundMessage });
        }
        if (request.AccessGrants.Any(g =>
                g.PrincipalType is not ("user" or "group")
                || string.IsNullOrWhiteSpace(g.PrincipalId)
                || g.Permission is not ("read" or "write")))
        {
            return Results.BadRequest(new { detail = "Grant inválido." });
        }

        channel.AccessGrantsJson = AccessControlService.Serialize(request.AccessGrants);
        channel.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = true, access_grants = request.AccessGrants });
    }

    /// <summary>Verdadeiro se o usuário tem pelo menos leitura via access_grants do canal.</summary>
    private static async Task<bool> HasGrantAsync(
        HttpContext http, AppDbContext db, User user, Channel channel, CancellationToken ct)
    {
        var access = http.RequestServices.GetRequiredService<AccessControlService>();
        return await access.LevelByGrantsAsync(user, channel.AccessGrantsJson, ct)
            is not AccessControlService.None;
    }

    private static bool IsChannelAdmin(User user, string? role) =>
        user.Role == AdminRole || role == AdminRole;

    private static async Task<User?> CurrentUserAsync(
        HttpContext http, AppDbContext db, CancellationToken ct) =>
        await AuthEndpoints.FindUserAsync(http, db, ct);
}
