using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Restrição de tools por skills ativas — paridade Devin
/// <c>allowed-tools</c> (SPEC-20261104-skills-allowed-tools-plugins RF-002).
///
/// Uma skill é "ativa" para a run quando injeta conteúdo server-side:
/// <list type="bullet">
///   <item>DB skills listadas em <c>MetaJson.skill_ids</c> do modelo
///   custom efetivo (mesmo critério do ModelSkillsContributor);</item>
///   <item>repo/global/remote skills com <c>autoinvoke:true</c> que
///   passam nos toggles do usuário e no deny do admin.</item>
/// </list>
///
/// Regra: skills sem <c>AllowedTools</c> não restringem; com restrição,
/// o conjunto permitido é a <b>interseção</b> das listas. Interseção
/// vazia mantém um mínimo essencial de built-ins de leitura/planejamento
/// para o agente não ficar cego. Tools fora do conjunto são removidas
/// da lista carregada — antes de anúncio e deferral — então nem spec nem
/// execução as alcançam.
///
/// Limitação conhecida: skill inserida pelo composer via <c>!</c> vira
/// texto no prompt (o servidor não vê a referência), logo não aplica
/// restrição — só skills ativadas server-side restringem.
/// </summary>
public sealed class SkillToolRestrictionService(
    AppDbContext db,
    SkillDiscoveryService skills,
    RemoteSkillCatalog remote,
    ConfigService config,
    UserConfigOverlayService overlay)
{
    /// <summary>Built-ins mínimas que sobrevivem a uma interseção vazia —
    /// leitura do workspace + tracking de tarefas (nenhuma mutável).</summary>
    public static readonly IReadOnlySet<string> EssentialBuiltins =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "file_list", "file_read", "file_grep", "file_glob", "todo_write",
        };

    /// <summary>
    /// Conjunto permitido para a run ou <c>null</c> quando nenhuma skill
    /// ativa declara restrição (comportamento atual).
    /// </summary>
    public async Task<HashSet<string>?> ResolveAsync(
        string userId, string? modelRef, string? workdir, CancellationToken ct)
    {
        var restrictive = new List<HashSet<string>>(2);

        // 1) DB skills do modelo custom (MetaJson.skill_ids).
        var ids = await ModelSkillIdsAsync(userId, modelRef, ct);
        if (ids.Count > 0)
        {
            var dbAllowed = await db.Skills.AsNoTracking()
                .Where(s => ids.Contains(s.Id) && s.IsActive && s.AllowedTools != null)
                .Select(s => s.AllowedTools)
                .ToListAsync(ct);
            foreach (var csv in dbAllowed)
            {
                restrictive.Add(SplitCsv(csv));
            }
        }

        // 2) Skills autoinvoke do repo/global/remoto ativas p/ o usuário.
        await remote.RefreshIfStaleAsync(config, ct);
        var scan = skills.Scan(string.IsNullOrWhiteSpace(workdir) ? null : workdir);
        var auto = scan.Skills.Where(s => s.Autoinvoke && s.AllowedTools is { Count: > 0 }).ToList();
        if (auto.Count > 0)
        {
            var hidden = await overlay.HiddenSkillNamesAsync(userId, ct);
            var adminOff = await overlay.AdminDisabledSkillNamesAsync(ct);
            foreach (var s in auto)
            {
                if (!hidden.Contains(s.Name) && !adminOff.Contains(s.Name))
                {
                    restrictive.Add([.. s.AllowedTools!]);
                }
            }
        }

        if (restrictive.Count == 0)
        {
            return null;
        }

        var intersection = new HashSet<string>(restrictive[0], StringComparer.OrdinalIgnoreCase);
        foreach (var set in restrictive.Skip(1))
        {
            intersection.IntersectWith(set);
        }
        return intersection.Count > 0
            ? intersection
            : [.. EssentialBuiltins];
    }

    /// <summary>
    /// <paramref name="tool"/> é permitida pelo conjunto? Casa o bare name
    /// da built-in (<c>builtin://{name}</c> / <c>builtin_{name}</c> /
    /// <c>builtin/{name}</c>) ou o nome declarado de tool DB/MCP.
    /// </summary>
    public static bool Permits(IReadOnlySet<string> allowed, Tool tool)
    {
        foreach (var candidate in Candidates(tool))
        {
            if (allowed.Contains(candidate))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Nomes candidatos da tool em todas as formas conhecidas.</summary>
    private static IEnumerable<string> Candidates(Tool tool)
    {
        if (tool.Url?.StartsWith(
                ChatTools.BuiltinToolRegistry.UrlPrefix,
                StringComparison.OrdinalIgnoreCase) == true)
        {
            yield return tool.Url[ChatTools.BuiltinToolRegistry.UrlPrefix.Length..];
        }
        if (tool.Name is { Length: > 0 } name)
        {
            yield return name;
            var slash = name.LastIndexOf('/');
            if (slash >= 0 && slash < name.Length - 1)
            {
                yield return name[(slash + 1)..];
            }
        }
        var fn = ToolExecutor.FunctionName(tool);
        if (fn is { Length: > 0 })
        {
            yield return fn;
            if (fn.StartsWith(
                    ChatTools.BuiltinToolRegistry.SpecPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                yield return fn[ChatTools.BuiltinToolRegistry.SpecPrefix.Length..];
            }
        }
    }

    /// <summary>Ids de skill do modelo custom efetivo (id/nome/base do
    /// <paramref name="modelRef"/>, visível ao usuário — mesmo escopo do
    /// CustomModelContributor).</summary>
    private async Task<List<string>> ModelSkillIdsAsync(
        string userId, string? modelRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(modelRef))
        {
            return [];
        }
        var (baseRef, _) = ModelVariantService.Split(modelRef);
        var entry = await db.ModelEntries.AsNoTracking().FirstOrDefaultAsync(
            m => m.IsActive
                && (m.Id == modelRef || m.Name == modelRef
                    || m.Id == baseRef || m.Name == baseRef
                    || m.BaseModelId == modelRef || m.BaseModelId == baseRef)
                && (m.UserId == userId || m.UserId == "public"), ct);
        if (entry?.MetaJson is null)
        {
            return [];
        }
        try
        {
            using var meta = System.Text.Json.JsonDocument.Parse(entry.MetaJson);
            if (!meta.RootElement.TryGetProperty("skill_ids", out var skillIds)
                || skillIds.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return [];
            }
            return skillIds.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static HashSet<string> SplitCsv(string? csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
