using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Avalia permissões granulares por união dos grupos do usuário.
/// Admin sempre tem tudo; usuário sem grupo mantém o comportamento default
/// (todas as permissões, como antes dos grupos existirem).
/// </summary>
public class PermissionService(AppDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Permissão <c>sharing.public_chats</c> (compartilhar chat publicamente).</summary>
    public const string SharingPublicChats = "sharing.public_chats";

    /// <summary>Permissão <c>workspace.prompts</c>.</summary>
    public const string WorkspacePrompts = "workspace.prompts";

    /// <summary>Permissão <c>workspace.models</c>.</summary>
    public const string WorkspaceModels = "workspace.models";

    /// <summary>Permissão <c>workspace.files</c>.</summary>
    public const string WorkspaceFiles = "workspace.files";

    /// <summary>Permissão <c>workspace.knowledge</c>.</summary>
    public const string WorkspaceKnowledge = "workspace.knowledge";

    /// <summary>Permissão <c>workspace.tools</c>.</summary>
    public const string WorkspaceTools = "workspace.tools";

    /// <summary>Permissão <c>chat.controls</c>.</summary>
    public const string ChatControls = "chat.controls";

    /// <summary>Avalia se o usuário possui a permissão (união usuário + grupos).</summary>
    /// <param name="user">Usuário avaliado.</param>
    /// <param name="permission">Chave no formato "seção.flag".</param>
    /// <param name="ct">Token de cancelamento.</param>
    /// <remarks>
    /// Semântica: flags explícitas de grupo sobrescrevem o default do papel e,
    /// por fim, flags explícitas do usuário (override do admin) vencem tudo.
    /// Sem valor explícito em nenhuma camada, o default é permitir.
    /// JSON de grupo inválido (ou não-objeto) conta como negação explícita
    /// (fail-closed), que outro grupo pode sobrescrever.
    /// </remarks>
    public async Task<bool> HasAsync(User user, string permission, CancellationToken ct = default)
    {
        if (user.Role == UserRoles.Admin)
        {
            return true;
        }

        var groups = await db.GroupMembers
            .Where(m => m.UserId == user.Id)
            .OrderBy(m => m.GroupId)
            .Select(m => m.Group!.PermissionsJson)
            .ToListAsync(ct);

        var effective = groups
            .Select(json => ExplicitValue(json, permission, invalidAsFalse: true))
            .LastOrDefault(v => v.HasValue);

        // Override por usuário (definido pelo admin) vence qualquer grupo.
        var userValue = ExplicitValue(user.PermissionsJson, permission, invalidAsFalse: false);
        if (userValue.HasValue)
        {
            effective = userValue;
        }

        return effective ?? true;
    }

    /// <summary>
    /// Lê o valor explícito de uma flag "seção.flag" no JSON de permissões.
    /// Retorna <c>null</c> quando ausente — flags não declaradas não decidem nada.
    /// </summary>
    /// <param name="permissionsJson">JSON no formato de <see cref="GroupPermissions"/>.</param>
    /// <param name="permission">Chave "seção.flag" (flags em snake_case, JSON em camelCase).</param>
    /// <param name="invalidAsFalse">Se true, JSON inválido/não-objeto conta como negação; senão, como ausente.</param>
    private static bool? ExplicitValue(string? permissionsJson, string permission, bool invalidAsFalse)
    {
        var parts = permission.Split('.');
        if (parts.Length != 2)
        {
            return null; // permissão desconhecida → não decide (comportamento permissivo)
        }

        try
        {
            using var doc = JsonDocument.Parse(permissionsJson ?? "{}");
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return invalidAsFalse ? false : null;
            }
            if (!root.TryGetProperty(parts[0], out var section)
                || section.ValueKind != JsonValueKind.Object
                || !section.TryGetProperty(ToCamelCase(parts[1]), out var flag)
                || flag.ValueKind != JsonValueKind.True && flag.ValueKind != JsonValueKind.False)
            {
                return null;
            }

            return flag.GetBoolean();
        }
        catch (JsonException)
        {
            return invalidAsFalse ? false : null;
        }
    }

    private static string ToCamelCase(string snake) =>
        string.Concat(snake.Split('_').Select((part, i) =>
            i == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
}
