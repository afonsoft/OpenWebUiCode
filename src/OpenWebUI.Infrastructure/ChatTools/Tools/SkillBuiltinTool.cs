using System.Text.Json;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:skill</c> — carrega o corpo de uma skill do repositório
/// vinculado como instrução extra da run (SPEC-20261009-repo-skills-slash-
/// commands RF-004, padrão <c>src/tool/skill.ts</c> do opencode).
/// Somente leitura: retorna markdown, nunca executa código.
/// </summary>
public sealed class SkillBuiltinTool(SkillDiscoveryService skills) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "skill";

    /// <inheritdoc />
    public string Description =>
        "Load a repository skill by name (SKILL.md under .claude/.agents/" +
        ".devin/.opencode/skills dirs) — call to pull the repo's own " +
        "conventions/runbooks into context before following them.";

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
    public Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var name = args.TryGetProperty("name", out var n) ? n.GetString() : null;
        var scan = skills.Scan(context.WorkspacePath);
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult(new BuiltinToolResult(
                "Parâmetro 'name' obrigatório. Skills disponíveis: " + Names(scan)));
        }

        var entry = scan.Skills.FirstOrDefault(
            s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(entry is null
            ? new BuiltinToolResult(
                $"Skill '{name}' não encontrada no repositório. Disponíveis: {Names(scan)}")
            : new BuiltinToolResult(
                $"Skill '{entry.Name}' carregada de {entry.Path} — siga as instruções abaixo:\n\n{entry.Body}",
                new { entry.Name, entry.Path }));
    }

    private static string Names(RepoScanResult scan) =>
        scan.Skills.Count == 0
            ? "(nenhuma)"
            : string.Join(", ", scan.Skills.Select(s => s.Name));
}
