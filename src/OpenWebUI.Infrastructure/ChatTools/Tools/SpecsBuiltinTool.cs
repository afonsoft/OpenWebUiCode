using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:specs</c> — ciclo de vida SDD sobre <c>.specs/SPEC-*.md</c>
/// do workspace (SPEC-20261023-sdd-orchestrator-prompts cap. E):
/// <c>list</c> (nome + status), <c>show</c> (conteúdo, cap 12KB) e
/// <c>transition</c> (regrava <c>| Status |</c> validando arestas
/// <c>Draft→Approved</c>, <c>Approved→In implementation</c>,
/// <c>In implementation→Completed</c>; <c>Completed</c> move o arquivo
/// para <c>docs/specs/</c> via <c>git mv</c> quando o workdir é repo,
/// senão <see cref="File.Move(string,string)"/>). Confinado aos diretórios
/// de SPEC do workdir; sync de label da Issue via <c>gh</c> é melhor
/// esforço — nunca bloqueia.
/// </summary>
public sealed partial class SpecsBuiltinTool : IBuiltinChatTool
{
    private const int MaxShowChars = 12_000;
    private const string ActiveDir = ".specs";
    private const string ArchiveDir = "docs/specs";

    private static readonly IReadOnlyDictionary<string, string[]> AllowedEdges =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Draft"] = ["Approved"],
            ["Approved"] = ["In implementation"],
            ["In implementation"] = ["Completed"],
        };

    private static readonly IReadOnlyDictionary<string, string> StatusLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["In implementation"] = "in_progress",
            ["Completed"] = "done",
        };

    /// <inheritdoc />
    public string Name => "specs";

    /// <inheritdoc />
    public string Description =>
        "Spec-driven development workflow on this workspace — op: list | "
            + "show | transition. 'list' enumerates .specs/SPEC-*.md with "
            + "their Status; 'show' prints one SPEC (arg: name, with or "
            + "without .md); 'transition' moves a SPEC along the allowed "
            + "edges Draft→Approved→In implementation→Completed (args: "
            + "name, to) — Completed archives the file under docs/specs/ "
            + "and best-effort syncs the linked GitHub issue label.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "op": {
              "type": "string",
              "enum": ["list", "show", "transition"],
              "description": "list = enumerate .specs with status; show = print a SPEC; transition = advance its Status."
            },
            "name": {
              "type": "string",
              "description": "SPEC name (e.g. SPEC-20261023-my-feature), .md suffix optional. Required for show/transition."
            },
            "to": {
              "type": "string",
              "enum": ["Approved", "In implementation", "Completed"],
              "description": "Target status for op=transition. Only adjacent edges are allowed."
            }
          },
          "required": ["op"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Write;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var op = args.TryGetProperty("op", out var opEl)
            ? opEl.GetString()?.ToLowerInvariant() : null;
        var workdir = Path.GetFullPath(context.WorkspacePath);
        var specDir = Path.Combine(workdir, ActiveDir);

        switch (op)
        {
            case "list":
            {
                if (!Directory.Exists(specDir))
                {
                    return new BuiltinToolResult(
                        "Nenhum diretório .specs/ no workspace.");
                }
                var files = Directory.EnumerateFiles(specDir, "SPEC-*.md")
                    .OrderBy(f => f, StringComparer.Ordinal).ToList();
                if (files.Count == 0)
                {
                    return new BuiltinToolResult(
                        "Nenhuma SPEC-*.md em .specs/.");
                }
                var lines = new List<string>(files.Count);
                foreach (var file in files)
                {
                    var status = await ReadStatusAsync(file, ct) ?? "?";
                    lines.Add($"- {Path.GetFileName(file)} [{status}]");
                }
                return new BuiltinToolResult(
                    "SPECs ativas em .specs/:\n" + string.Join("\n", lines));
            }

            case "show":
            {
                var (file, error) = ResolveSpec(specDir, NameArg(args));
                if (file is null)
                {
                    return new BuiltinToolResult(error!);
                }
                var content = await File.ReadAllTextAsync(file, ct);
                return new BuiltinToolResult(
                    content.Length <= MaxShowChars
                        ? content
                        : content[..MaxShowChars] + "\n[conteúdo truncado]");
            }

            case "transition":
            {
                var (file, error) = ResolveSpec(specDir, NameArg(args));
                if (file is null)
                {
                    return new BuiltinToolResult(error!);
                }
                var to = args.TryGetProperty("to", out var toEl)
                    ? toEl.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(to))
                {
                    return new BuiltinToolResult(
                        "'to' é obrigatório para op=transition.");
                }

                var text = await File.ReadAllTextAsync(file, ct);
                var statusMatch = StatusLineRegex().Match(text);
                if (!statusMatch.Success)
                {
                    return new BuiltinToolResult(
                        "Cabeçalho '| Status | … |' não encontrado — a SPEC "
                            + "não segue o template.");
                }
                var from = statusMatch.Groups[1].Value.Trim();

                if (!AllowedEdges.TryGetValue(from, out var next)
                    || !next.Contains(to, StringComparer.OrdinalIgnoreCase))
                {
                    var allowed = AllowedEdges.TryGetValue(from, out var a)
                        ? string.Join(" | ", a) : "(nenhuma — terminal)";
                    return new BuiltinToolResult(
                        $"Transição inválida: '{from}' → '{to}'. "
                            + $"Destinos válidos a partir de '{from}': {allowed}.");
                }

                var updated = StatusLineRegex().Replace(
                    text, $"| Status | `{to}` |", count: 1);
                var notes = new StringBuilder();

                if (!to.Equals("Completed", StringComparison.OrdinalIgnoreCase))
                {
                    await File.WriteAllTextAsync(file, updated, ct);
                }
                else
                {
                    var movedTo = await ArchiveAsync(
                        workdir, file, updated, notes, ct);
                    file = movedTo;
                }

                var issueNote = await SyncIssueLabelAsync(workdir, updated, to, ct);
                notes.Append(issueNote);

                return new BuiltinToolResult(
                    $"SPEC {Path.GetFileName(file)}: '{from}' → '{to}'."
                        + notes);
            }

            default:
                return new BuiltinToolResult(
                    "op deve ser list|show|transition.");
        }
    }

    private static string? NameArg(JsonElement args) =>
        args.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;

    /// <summary>Normaliza <paramref name="name"/> para um arquivo dentro de
    /// <paramref name="specDir"/> — recusa path escape (<c>..</c>, separadores,
    /// prefixo não-SPEC) e retorna erro amigável.</summary>
    private static (string? File, string? Error) ResolveSpec(
        string specDir, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, "'name' é obrigatório para esta op.");
        }
        var fileName = name.Trim();
        if (fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^3];
        }
        if (!SpecNameRegex().IsMatch(fileName))
        {
            return (null,
                "'name' deve ser SPEC-<id>-<slug> (sem path ou '..').");
        }
        var full = Path.GetFullPath(Path.Combine(specDir, fileName + ".md"));
        var root = Path.GetFullPath(specDir);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return (null, "Path fora de .specs/ recusado.");
        }
        return File.Exists(full)
            ? (full, null)
            : (null, $"SPEC '{fileName}.md' não existe em .specs/.");
    }

    /// <summary>Lê <c>| Status | `…` |</c> do cabeçalho (primeiras linhas).</summary>
    private static async Task<string?> ReadStatusAsync(
        string file, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(file, ct);
        var m = StatusLineRegex().Match(text);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>Move a SPEC para <c>docs/specs/</c> — <c>git mv</c> quando o
    /// workdir é repo git (dir <c>.git</c> ou arquivo de worktree), senão
    /// move de filesystem; grava o status novo antes de mover.</summary>
    private static async Task<string> ArchiveAsync(
        string workdir, string file, string updated,
        StringBuilder notes, CancellationToken ct)
    {
        var archiveDir = Path.Combine(workdir, ArchiveDir);
        Directory.CreateDirectory(archiveDir);
        var dest = Path.Combine(archiveDir, Path.GetFileName(file));

        await File.WriteAllTextAsync(file, updated, ct);

        var isGitRepo = Directory.Exists(Path.Combine(workdir, ".git"))
            || File.Exists(Path.Combine(workdir, ".git"));
        if (isGitRepo)
        {
            var rel = Path.GetRelativePath(workdir, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            var relDest = Path.GetRelativePath(workdir, dest)
                .Replace(Path.DirectorySeparatorChar, '/');
            var git = await ChatProcessRunner.RunAsync(
                $"git mv -- {Quote(rel)} {Quote(relDest)}",
                workdir, TimeSpan.FromSeconds(15), ct: ct);
            if (git.ExitCode == 0)
            {
                return dest;
            }
            notes.Append(" (git mv falhou — fallback File.Move)");
        }

        File.Move(file, dest, overwrite: true);
        return dest;
    }

    /// <summary>Sync best-effort do label da Issue apontada em
    /// <c>| Ticket |</c> — roda <c>gh issue edit</c> somente quando o número
    /// é extraível; qualquer falha é relatada sem bloquear a transição.</summary>
    private static async Task<string> SyncIssueLabelAsync(
        string workdir, string text, string to, CancellationToken ct)
    {
        var ticket = TicketRegex().Match(text);
        if (!ticket.Success
            || !StatusLabels.TryGetValue(to, out var label))
        {
            return string.Empty;
        }
        var gh = await ChatProcessRunner.RunAsync(
            $"gh issue edit {ticket.Groups[1].Value} --add-label {label}"
                + " --remove-label todo",
            workdir, TimeSpan.FromSeconds(15), ct: ct);
        return gh.ExitCode == 0
            ? $" Label '{label}' aplicada na issue #{ticket.Groups[1].Value}."
            : " (sync de label ignorado — gh indisponível/sem auth)";
    }

    private static string Quote(string path) =>
        "'" + path.Replace("'", "'\\''") + "'";

    [GeneratedRegex(@"\|\s*Status\s*\|\s*`?([^`|]+?)`?\s*\|")]
    private static partial Regex StatusLineRegex();

    [GeneratedRegex(@"^SPEC-[A-Za-z0-9][A-Za-z0-9_-]*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SpecNameRegex();

    [GeneratedRegex(@"issues/(\d+)")]
    private static partial Regex TicketRegex();
}
