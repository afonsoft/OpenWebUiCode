using System.Text;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Client.Services;

/// <summary>Item de sugestão do composer para <c>/</c> — de qualquer fonte.</summary>
/// <param name="Command">Nome após a barra.</param>
/// <param name="Title">Descrição curta exibida na lista.</param>
/// <param name="Source"><c>prompt</c> | <c>skill</c> | <c>command</c> | <c>mcp</c> | <c>builtin</c>.</param>
public sealed record SlashItem(string Command, string Title, string Source);

/// <summary>
/// Mescla e interpola slash commands do composer
/// (SPEC-20261009-repo-skills-slash-commands RF-003): prompts do usuário +
/// skills + commands do repositório, filtrados pelo prefixo digitado, e a
/// interpolação <c>$ARGUMENTS</c>/<c>$1..$N</c> dos templates de command.
/// </summary>
public static class RepoSlashCommands
{
    /// <summary>
    /// Comandos internos do app (SPEC-20261015-plan-spec-sdd RF-003):
    /// sempre disponíveis independente do repo vinculado. O
    /// <see cref="SlashItem.Title"/> é a chave i18n (<c>chat.cmd_*</c>) —
    /// resolvida no render do composer.
    /// </summary>
    public static IReadOnlyList<SlashItem> Builtins { get; } =
    [
        new SlashItem("plan", "chat.cmd_plan", "builtin"),
        new SlashItem("compact", "chat.cmd_compact", "builtin"),
    ];

    /// <summary>
    /// Sugestões mescladas: comandos built-in + prompts (source
    /// <c>prompt</c>) + skills + commands do repo cujo nome começa com
    /// <paramref name="prefix"/> (sem a barra).
    /// </summary>
    public static List<SlashItem> Merge(
        IEnumerable<PromptResponse> prompts,
        IEnumerable<RepoSkillItemResponse> skills,
        IEnumerable<RepoCommandItemResponse> commands,
        string prefix, int take = 8,
        IEnumerable<SlashItem>? builtins = null)
    {
        bool Match(string s) => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        return (builtins ?? []).Where(b => Match(b.Command))
            .Concat(prompts.Where(p => Match(p.Command))
                .Select(p => new SlashItem(p.Command, p.Title, "prompt")))
            .Concat(skills.Where(s => Match(s.Name))
                .Select(s => new SlashItem(s.Name, Scoped(s.Description, s.Scope), "skill")))
            .Concat(commands.Where(c => Match(c.Name))
                .Select(c => new SlashItem(c.Name, Scoped(c.Description, c.Scope), "command")))
            .Take(take)
            .ToList();
    }

    /// <summary>Sufixa <c>· global</c> em itens vindos de <c>~/.agents</c> (RF-005).</summary>
    private static string Scoped(string description, string? scope) =>
        scope == "global" ? $"{description} · global" : description;

    /// <summary>Texto após <c>/nome</c> no input (os argumentos do command).</summary>
    public static string ArgsAfter(string input)
    {
        var space = input.IndexOf(' ');
        return space < 0 ? string.Empty : input[(space + 1)..].Trim();
    }

    /// <summary>
    /// Interpola o template de um command: <c>$ARGUMENTS</c> recebe o texto
    /// inteiro dos args; <c>$1</c>..<c>$N</c> recebem os tokens separados por
    /// espaço (placeholders sem arg viram string vazia).
    /// </summary>
    public static string Interpolate(string template, string args)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder(template.Length + args.Length);
        var i = 0;
        while (i < template.Length)
        {
            var consumed = template[i] == '$'
                ? TryPlaceholder(template, i, args, tokens, sb)
                : 0;
            if (consumed > 0)
            {
                i += consumed;
                continue;
            }
            sb.Append(template[i]);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Tenta um placeholder na posição <paramref name="i"/> (<c>$ARGUMENTS</c>
    /// ou <c>$N</c>) e escreve o valor em <paramref name="sb"/>. Retorna os
    /// chars consumidos (0 = não era placeholder).
    /// </summary>
    private static int TryPlaceholder(string template, int i,
        string args, string[] tokens, StringBuilder sb)
    {
        if (template.AsSpan(i).StartsWith("$ARGUMENTS", StringComparison.Ordinal))
        {
            sb.Append(args);
            return "$ARGUMENTS".Length;
        }

        var j = i + 1;
        while (j < template.Length && char.IsDigit(template[j]))
        {
            j++;
        }

        if (j > i + 1
            && int.TryParse(template[(i + 1)..j], out var n)
            && n >= 1)
        {
            sb.Append(n <= tokens.Length ? tokens[n - 1] : string.Empty);
            return j - i;
        }
        return 0;
    }
}
