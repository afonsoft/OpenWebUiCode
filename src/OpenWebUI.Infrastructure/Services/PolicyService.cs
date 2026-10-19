using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Motor de policies administrativas (SPEC-20261018-policies-admin):
/// statements de hard-deny avaliados ANTES das permissões de usuário,
/// aprovações salvas e risk gates. Cobre <c>tool.use</c>,
/// <c>provider.use</c>, <c>model.use</c> e <c>integration.use</c>.
/// Paridade com as policies do opencode v2 — deny é absoluto e todo
/// bloqueio efetivo é auditado em <see cref="PolicyDenial"/>.
/// </summary>
public class PolicyService(AppDbContext db)
{
    /// <summary>Ação de uso de tool (builtin ou MCP).</summary>
    public const string ActionToolUse = "tool.use";

    /// <summary>Ação de uso de provider inteiro.</summary>
    public const string ActionProviderUse = "provider.use";

    /// <summary>Ação de uso de modelo específico.</summary>
    public const string ActionModelUse = "model.use";

    /// <summary>Ação de uso de integração (mcp:, skill:, plugin:).</summary>
    public const string ActionIntegrationUse = "integration.use";

    /// <summary>Ações válidas em statements.</summary>
    public static readonly IReadOnlySet<string> ValidActions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ActionToolUse, ActionProviderUse, ActionModelUse, ActionIntegrationUse };

    /// <summary>Carrega os statements de uma ação em ordem de avaliação.</summary>
    public Task<List<AdminPolicyStatement>> LoadAsync(
        string action, CancellationToken ct = default) =>
        db.AdminPolicyStatements.AsNoTracking()
            .Where(s => s.Action == action)
            .OrderBy(s => s.Order)
            .ToListAsync(ct);

    /// <summary>
    /// Avalia um conjunto já carregado de statements — último match vence.
    /// null = permitido. Usado em listas (1 query para N itens).
    /// </summary>
    public static AdminPolicyStatement? MatchIn(
        IReadOnlyList<AdminPolicyStatement> statements, IEnumerable<string> resources)
    {
        var candidates = resources.Where(r => !string.IsNullOrEmpty(r)).ToList();
        AdminPolicyStatement? matched = null;
        foreach (var s in statements)
        {
            if (s.Effect == "deny" && candidates.Any(r => GlobMatch(s.Resource, r)))
            {
                matched = s;
            }
        }
        return matched;
    }

    /// <summary>
    /// Retorna o statement deny que casa com algum dos recursos —
    /// avaliação ordenada, último match vence. null = permitido.
    /// </summary>
    /// <param name="action">Ação avaliada (uma de <see cref="ValidActions"/>).</param>
    /// <param name="resources">Recursos candidatos (ex.: <c>shell</c>, <c>shell:rm *</c>).</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<AdminPolicyStatement?> MatchAsync(
        string action, IEnumerable<string> resources, CancellationToken ct = default) =>
        MatchIn(await LoadAsync(action, ct), resources);

    /// <summary>
    /// Igual a <see cref="MatchAsync"/>, mas registra o bloqueio na
    /// auditoria quando há match. Use nos pontos de enforcement.
    /// </summary>
    /// <param name="action">Ação avaliada.</param>
    /// <param name="resources">Recursos candidatos.</param>
    /// <param name="userId">Usuário afetado (opcional).</param>
    /// <param name="ct">Cancelamento.</param>
    public async Task<AdminPolicyStatement?> DenyAsync(
        string action, IEnumerable<string> resources, string? userId,
        CancellationToken ct = default)
    {
        var candidates = resources.Where(r => !string.IsNullOrEmpty(r)).ToList();
        var matched = await MatchAsync(action, candidates, ct);
        if (matched is null)
        {
            return null;
        }

        db.PolicyDenials.Add(new PolicyDenial
        {
            UserId = userId,
            Action = action,
            Resource = candidates.First(r => GlobMatch(matched.Resource, r)),
            StatementId = matched.Id,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        await db.SaveChangesAsync(ct);
        return matched;
    }

    /// <summary>
    /// Match glob case-insensitive: <c>*</c> casa qualquer sequência,
    /// <c>?</c> casa um caractere. Demais caracteres são literais.
    /// </summary>
    public static bool GlobMatch(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value, regex,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    /// <summary>Mensagem padrão de bloqueio nomeando a policy.</summary>
    public static string DeniedMessage(AdminPolicyStatement statement, string resource) =>
        $"Blocked by admin policy '{statement.Resource}' ({statement.Action}"
        + (string.IsNullOrWhiteSpace(statement.Note) ? "" : $" — {statement.Note}")
        + $") on '{resource}'.";
}
