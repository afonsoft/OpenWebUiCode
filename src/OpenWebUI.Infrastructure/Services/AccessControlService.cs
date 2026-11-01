using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Resolve grants de acesso ({principal_type, principal_id, permission}) de entidades
/// compartilháveis: owner e admin têm controle total; "user" casa o id diretamente;
/// "group" resolve via membership; "*" casa qualquer usuário autenticado.
/// </summary>
public class AccessControlService(AppDbContext db)
{
    /// <summary>Nível de acesso sem grant.</summary>
    public const string None = "none";

    /// <summary>Nível de acesso de leitura.</summary>
    public const string Read = "read";

    /// <summary>Nível de acesso de escrita (inclui read).</summary>
    public const string Write = "write";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Grupos dos quais o usuário é membro (para resolver grants de grupo).</summary>
    public async Task<HashSet<string>> GetGroupIdsAsync(string userId, CancellationToken ct = default) =>
        (await db.GroupMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.GroupId)
            .ToListAsync(ct)).ToHashSet();

    /// <summary>Deserializa a lista de grants (JSON inválido/vazio → lista vazia).</summary>
    public static List<AccessGrant> Parse(string? grantsJson)
    {
        if (string.IsNullOrWhiteSpace(grantsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<AccessGrant>>(grantsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Serializa a lista de grants para persistência.</summary>
    public static string Serialize(List<AccessGrant> grants) =>
        JsonSerializer.Serialize(grants, JsonOptions);

    /// <summary>
    /// Nível de acesso efetivo do usuário à entidade: none | read | write.
    /// Owner e admin sempre write; write concedido por qualquer grant vence.
    /// </summary>
    /// <param name="user">Usuário avaliado.</param>
    /// <param name="ownerUserId">Dono da entidade.</param>
    /// <param name="grantsJson">JSON de grants da entidade.</param>
    /// <param name="userGroupIds">Grupos do usuário (de <see cref="GetGroupIdsAsync"/>).</param>
    public static string Level(User user, string ownerUserId, string? grantsJson, HashSet<string> userGroupIds)
    {
        if (user.Id == ownerUserId || user.Role == UserRoles.Admin)
        {
            return Write;
        }

        return LevelByGrants(user, grantsJson, userGroupIds);
    }

    /// <summary>
    /// Nível de acesso considerando apenas grants — sem bypass de owner/admin.
    /// Usado por recursos cuja visibilidade é governada por membership (canais).
    /// </summary>
    public static string LevelByGrants(User user, string? grantsJson, HashSet<string> userGroupIds)
    {
        var level = None;
        foreach (var grant in Parse(grantsJson))
        {
            var matches = grant.PrincipalId == "*"
                || (grant.PrincipalType == "user" && grant.PrincipalId == user.Id)
                || (grant.PrincipalType == "group" && userGroupIds.Contains(grant.PrincipalId));
            if (!matches)
            {
                continue;
            }
            if (grant.Permission == Write)
            {
                return Write;
            }
            level = Read;
        }

        return level;
    }

    /// <summary>Versão assíncrona de <see cref="LevelByGrants"/>.</summary>
    public async Task<string> LevelByGrantsAsync(
        User user, string? grantsJson, CancellationToken ct = default) =>
        LevelByGrants(user, grantsJson, await GetGroupIdsAsync(user.Id, ct));

    /// <summary>Versão assíncrona que resolve os grupos internamente.</summary>
    public async Task<string> LevelAsync(
        User user, string ownerUserId, string? grantsJson, CancellationToken ct = default) =>
        Level(user, ownerUserId, grantsJson, await GetGroupIdsAsync(user.Id, ct));

    /// <summary>True quando o usuário tem pelo menos leitura na entidade.</summary>
    public async Task<bool> CanReadAsync(
        User user, string ownerUserId, string? grantsJson, CancellationToken ct = default) =>
        await LevelAsync(user, ownerUserId, grantsJson, ct) is not None;

    /// <summary>True quando o usuário tem escrita na entidade.</summary>
    public async Task<bool> CanWriteAsync(
        User user, string ownerUserId, string? grantsJson, CancellationToken ct = default) =>
        await LevelAsync(user, ownerUserId, grantsJson, ct) is Write;

    /// <summary>Filtra uma lista em memória para os itens com pelo menos leitura.</summary>
    public List<T> FilterReadable<T>(
        User user, IEnumerable<T> items, HashSet<string> userGroupIds,
        Func<T, string> owner, Func<T, string?> grants) =>
        items.Where(i => Level(user, owner(i), grants(i), userGroupIds) is not None).ToList();
}
