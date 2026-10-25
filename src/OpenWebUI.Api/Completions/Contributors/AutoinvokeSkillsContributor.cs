using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.SystemPrompt;

namespace OpenWebUI.Api.Completions.Contributors;

/// <summary>
/// Bloco 3.5b: skills <c>autoinvoke:true</c> (SPEC-20261018-agent-polish
/// RF-002) — skills do repo/global/catálogo remoto cujo frontmatter marca
/// autoinvoke têm o corpo injetado direto no system prompt como
/// <c>## Skill: {name}</c>, sem precisar ser invocadas. Respeita os
/// toggles de skill do usuário (<c>u:{id}:skills-disabled</c>) e o
/// deny global do admin — skill desligada nunca entra.
/// </summary>
public sealed class AutoinvokeSkillsContributor(
    SkillDiscoveryService skills,
    RemoteSkillCatalog remote,
    UserConfigOverlayService overlay,
    ConfigService config) : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 72;

    /// <summary>Teto total do bloco de skills autoinvoked (chars).</summary>
    public const int MaxBlockChars = 4000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        await remote.RefreshIfStaleAsync(config, ctx.Ct);

        var workdir = await ctx.GetWorkdirAsync();
        var scan = skills.Scan(
            string.IsNullOrWhiteSpace(workdir) ? null : workdir);
        var auto = scan.Skills.Where(s => s.Autoinvoke).ToList();
        if (auto.Count == 0)
        {
            return [];
        }

        var hidden = await overlay.HiddenSkillNamesAsync(ctx.User.Id, ctx.Ct);
        var adminOff = await overlay.AdminDisabledSkillNamesAsync(ctx.Ct);
        var enabled = auto
            .Where(s => !hidden.Contains(s.Name) && !adminOff.Contains(s.Name))
            .ToList();
        if (enabled.Count == 0)
        {
            return [];
        }

        var sections = new List<string>();
        var chars = 0;
        foreach (var skill in enabled)
        {
            var section = $"## Skill: {skill.Name}\n{skill.Body}";
            if (chars + section.Length > MaxBlockChars)
            {
                break;
            }
            chars += section.Length;
            sections.Add(section);
        }
        return sections;
    }
}
