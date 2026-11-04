using Microsoft.EntityFrameworkCore;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.SystemPrompt;

namespace OpenWebUI.Api.Completions.Contributors;

/// <summary>
/// Bloco de notas de conhecimento (SPEC-20261104-customize-hub RF-004):
/// injeta no system prompt as <see cref="Domain.KnowledgeNote"/>s efetivas —
/// pinadas sempre; demais quando o trigger casa a última mensagem do
/// usuário. Escopo: globais (<c>UserId null</c>) menos tombstones
/// (<c>u:{id}:knowledge-disabled</c>) + pessoais do chamador. Tier Dynamic —
/// o conteúdo depende da mensagem corrente.
/// </summary>
public sealed class KnowledgeNotesContributor(
    AppDbContext db, UserConfigOverlayService overlay) : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 73;

    /// <inheritdoc />
    public PromptCacheTier CacheTier => PromptCacheTier.Dynamic;

    /// <summary>Teto total do bloco de conhecimento (chars).</summary>
    public const int MaxBlockChars = 4000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        var disabled = await overlay.GetKnowledgeDisabledAsync(ctx.User.Id, ctx.Ct);
        var notes = await db.KnowledgeNotes.AsNoTracking()
            .Where(n => n.UserId == null || n.UserId == ctx.User.Id)
            .ToListAsync(ctx.Ct);
        var effective = notes
            .Where(n => n.UserId is not null || !disabled.Contains(n.Id))
            .ToList();
        if (effective.Count == 0)
        {
            return [];
        }

        var text = ctx.LastUserText ?? string.Empty;
        var pinned = effective.Where(n => n.Pinned);
        var matched = effective
            .Where(n => !n.Pinned && Matches(n.Trigger, text));
        var ordered = pinned.Concat(matched)
            .OrderByDescending(n => n.UpdatedAt);

        var sections = new List<string>();
        var chars = 0;
        foreach (var note in ordered)
        {
            var section = $"## Knowledge: {note.Title}\n{note.Content}";
            if (chars + section.Length > MaxBlockChars)
            {
                break;
            }
            chars += section.Length;
            sections.Add(section);
        }
        return sections;
    }

    /// <summary>Trigger = termos separados por vírgula/ponto-e-vírgula —
    /// basta um termo estar contido no texto (case-insensitive).</summary>
    private static bool Matches(string? trigger, string text)
    {
        if (string.IsNullOrWhiteSpace(trigger) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        return trigger.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
