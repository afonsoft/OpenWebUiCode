using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.SystemPrompt;

namespace OpenWebUI.Api.Completions.Contributors;

/// <summary>
/// Bloco 3.5a: catálogo compacto das skills descobertas (repo + global +
/// remoto) — nome e descrição, sem o corpo (progressive disclosure
/// RF-007): o modelo decide pela descrição e lê a skill completa sob
/// demanda via <c>builtin:skill</c>. Skills <c>autoinvoke:true</c> já têm
/// o corpo injetado por <see cref="AutoinvokeSkillsContributor"/> e são
/// omitidas da lista. Respeita os toggles do usuário e o deny de admin.
/// </summary>
public sealed class SkillCatalogContributor(
    SkillDiscoveryService skills,
    RemoteSkillCatalog remote,
    UserConfigOverlayService overlay,
    ConfigService config) : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 71;

    /// <summary>Teto do bloco de catálogo (chars) — mantém o prompt enxuto.</summary>
    public const int MaxBlockChars = 2000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        await remote.RefreshIfStaleAsync(config, ctx.Ct);

        var workdir = await ctx.GetWorkdirAsync();
        var scan = skills.Scan(
            string.IsNullOrWhiteSpace(workdir) ? null : workdir);
        var hidden = await overlay.HiddenSkillNamesAsync(ctx.User.Id, ctx.Ct);
        var adminOff = await overlay.AdminDisabledSkillNamesAsync(ctx.Ct);
        var enabled = scan.Skills
            .Where(s => !s.Autoinvoke
                && !hidden.Contains(s.Name) && !adminOff.Contains(s.Name))
            .ToList();
        if (enabled.Count == 0)
        {
            return [];
        }

        var lines = new List<string>
        {
            "Available skills (procedural instructions for this workspace). "
                + "When a task matches a skill's description, load it with "
                + "builtin:skill{name} BEFORE acting on it:",
        };
        var chars = lines[0].Length;
        foreach (var skill in enabled)
        {
            var desc = string.IsNullOrWhiteSpace(skill.Description)
                ? "(sem descrição)" : skill.Description.Replace('\n', ' ');
            var line = $"- {skill.Name} — {desc}";
            if (chars + line.Length > MaxBlockChars)
            {
                break;
            }
            chars += line.Length;
            lines.Add(line);
        }
        return lines.Count > 1 ? [string.Join("\n", lines)] : [];
    }
}
