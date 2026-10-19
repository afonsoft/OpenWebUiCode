using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:skill</c> — carrega o corpo de uma skill do repositório
/// vinculado como instrução extra da run (SPEC-20261009-repo-skills-slash-
/// commands RF-004, padrão <c>src/tool/skill.ts</c> do opencode).
/// Somente leitura: retorna markdown, nunca executa código.
/// </summary>
public sealed class SkillBuiltinTool(
    SkillDiscoveryService skills,
    UserConfigOverlayService overlay,
    PolicyService? policies = null) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "skill";

    /// <inheritdoc />
    public string Description =>
        "Load a skill by name (SKILL.md under ~/.agents/skills, " +
        "~/.claude/skills or the repo's .claude/.agents/.devin/.opencode/" +
        "skills dirs) — call to pull project/global conventions/runbooks " +
        "into context before following them.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "Skill name as listed by GET /api/v1/workspace/repo/skills." }
          },
          "required": ["name"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;
    /// <inheritdoc />
    public bool SafeInParallel => true;


    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var name = args.TryGetProperty("name", out var n) ? n.GetString() : null;
        // Policies admin (SPEC-20261018-policies-admin RF-004): deny de
        // integração por nome de skill antes de carregar.
        if (!string.IsNullOrWhiteSpace(name) && policies is not null
            && await policies.DenyAsync(PolicyService.ActionIntegrationUse,
                new[] { $"skill:{name}" }, context.UserId, ct) is { } denied)
        {
            return new BuiltinToolResult(
                "Erro: " + PolicyService.DeniedMessage(denied, $"skill:{name}"));
        }

        // RF-007: sem workdir (chat sem repo) o scan cai no escopo global.
        var scan = skills.Scan(
            string.IsNullOrWhiteSpace(context.WorkspacePath) ? null : context.WorkspacePath);
        var hidden = await overlay.HiddenSkillNamesAsync(context.UserId, ct);
        var visible = scan.Skills
            .Where(s => !hidden.Contains(s.Name)).ToList();
        if (string.IsNullOrWhiteSpace(name))
        {
            return new BuiltinToolResult(
                "Parâmetro 'name' obrigatório. Skills disponíveis: " + Names(visible));
        }

        var entry = visible.FirstOrDefault(
            s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return entry is null
            ? new BuiltinToolResult(
                $"Skill '{name}' não encontrada ou desabilitada. Disponíveis: {Names(visible)}")
            : new BuiltinToolResult(
                $"Skill '{entry.Name}' carregada de {entry.Path} — siga as instruções abaixo:\n\n{entry.Body}",
                new { entry.Name, entry.Path });
    }

    private static string Names(IReadOnlyList<RepoEntry> skills) =>
        skills.Count == 0
            ? "(nenhuma)"
            : string.Join(", ", skills.Select(s => s.Name));
}
