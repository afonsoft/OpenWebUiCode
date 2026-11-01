using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:specs</c> — ciclo de vida SDD sobre <c>.specs/SPEC-*.md</c>
/// do workspace (SPEC-20261023-sdd-orchestrator-prompts cap. E):
/// <c>list</c> (nome + status), <c>show</c> (conteúdo, cap 12KB) e
/// <c>transition</c> (regrava o status validando arestas
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

    /// <summary>Labels de etapa da Issue (create-issues SKILL) — a
    /// sincronização remove todas as anteriores antes de aplicar a nova.</summary>
    private static readonly string[] StageLabels =
        ["todo", "in_progress", "in_review", "in_pullrequest", "done"];

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
        var specDir = Path.Join(workdir, ActiveDir);

        return op switch
        {
            "list" => await ListAsync(specDir, ct),
            "show" => await ShowAsync(specDir, NameArg(args), ct),
            "transition" => await TransitionAsync(
                workdir, specDir, args, ct),
            _ => new BuiltinToolResult("op deve ser list|show|transition."),
        };
    }

    private static string? NameArg(JsonElement args) =>
        args.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;

    private static async Task<BuiltinToolResult> ListAsync(
        string specDir, CancellationToken ct)
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
            return new BuiltinToolResult("Nenhuma SPEC-*.md em .specs/.");
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

    private static async Task<BuiltinToolResult> ShowAsync(
        string specDir, string? name, CancellationToken ct)
    {
        var (file, error) = ResolveSpec(specDir, name);
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

    private static async Task<BuiltinToolResult> TransitionAsync(
        string workdir, string specDir, JsonElement args, CancellationToken ct)
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
        var match = StatusPattern().Match(text);
        if (!match.Success)
        {
            return new BuiltinToolResult(
                "Status não encontrado (formato '| Status | … |', "
                    + "'Status: …' ou '**Status**: …') — a SPEC não segue "
                    + "o template.");
        }
        var from = StatusValue(match);

        if (!AllowedEdges.TryGetValue(from, out var next)
            || !next.Contains(to, StringComparer.OrdinalIgnoreCase))
        {
            var allowed = AllowedEdges.TryGetValue(from, out var a)
                ? string.Join(" | ", a) : "(nenhuma — terminal)";
            return new BuiltinToolResult(
                $"Transição inválida: '{from}' → '{to}'. "
                    + $"Destinos válidos a partir de '{from}': {allowed}.");
        }

        var updated = StatusPattern().Replace(
            text, m => RebuildStatusLine(m, to), count: 1);
        var notes = new StringBuilder();

        if (!to.Equals("Completed", StringComparison.OrdinalIgnoreCase))
        {
            await File.WriteAllTextAsync(file, updated, ct);
        }
        else
        {
            try
            {
                var movedTo = await ArchiveAsync(workdir, file, updated, notes, ct);
                file = movedTo;
            }
            catch (InvalidOperationException ex)
            {
                return new BuiltinToolResult(ex.Message);
            }
        }

        notes.Append(await SyncIssueLabelAsync(workdir, updated, to, ct));

        return new BuiltinToolResult(
            $"SPEC {Path.GetFileName(file)}: '{from}' → '{to}'." + notes);
    }

    /// <summary>Normaliza <paramref name="name"/> para um arquivo dentro de
    /// <paramref name="specDir"/> — recusa path escape (<c>..</c>, separadores,
    /// prefixo não-SPEC) e links simbólicos, e retorna erro amigável.</summary>
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
        var full = Path.GetFullPath(Path.Join(specDir, fileName + ".md"));
        var root = Path.GetFullPath(specDir);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return (null, "Path fora de .specs/ recusado.");
        }
        if (File.Exists(full) && IsSymlink(full))
        {
            return (null, "SPEC é link simbólico — recusada (contenção).");
        }
        return File.Exists(full)
            ? (full, null)
            : (null, $"SPEC '{fileName}.md' não existe em .specs/.");
    }

    /// <summary>True quando o arquivo é um symlink (link direto ou final —
    /// defensivo contra leitura/escrita fora do workspace).</summary>
    private static bool IsSymlink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null)
            {
                return true;
            }
            if (info.ResolveLinkTarget(returnFinalTarget: true) is { } final
                && !final.FullName.StartsWith(
                    Path.GetFullPath(Path.GetDirectoryName(path) ?? path)
                        + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                return true;
            }
            return Directory.Exists(path)
                && new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Lê o status no cabeçalho — tabela (<c>| Status | `X` |</c>),
    /// linha (<c>Status: X</c>) ou negrito (<c>**Status**: X</c>), os três
    /// formatos aceitos pelo validador de SPECs.</summary>
    private static async Task<string?> ReadStatusAsync(
        string file, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(file, ct);
        var m = StatusPattern().Match(text);
        return m.Success ? StatusValue(m) : null;
    }

    /// <summary>Valor do status no formato que casou (tabela/linha/negrito).</summary>
    private static string StatusValue(Match m)
    {
        if (m.Groups["line"].Success)
        {
            return m.Groups["line"].Value.Trim();
        }
        if (m.Groups["bold"].Success)
        {
            return m.Groups["bold"].Value.Trim();
        }
        return m.Groups["value"].Value.Trim();
    }

    /// <summary>Reconstrói a linha de status preservando o formato original
    /// (tabela, linha ou negrito).</summary>
    private static string RebuildStatusLine(Match m, string to)
    {
        if (m.Groups["line"].Success)
        {
            return $"Status: {to}";
        }
        if (m.Groups["bold"].Success)
        {
            return $"**Status**: {to}";
        }
        return $"| Status | `{to}` |";
    }

    /// <summary>Move a SPEC para <c>docs/specs/</c> — <c>git mv</c> quando o
    /// workdir é repo git (dir <c>.git</c> ou arquivo de worktree), senão
    /// move de filesystem; grava o status novo antes de mover. Colisão com
    /// arquivo arquivado homônimo é recusada — nunca sobrescreve.</summary>
    private static async Task<string> ArchiveAsync(
        string workdir, string file, string updated,
        StringBuilder notes, CancellationToken ct)
    {
        var archiveDir = Path.Join(workdir, ArchiveDir);
        var dest = Path.Join(archiveDir, Path.GetFileName(file));

        if (File.Exists(dest))
        {
            throw new InvalidOperationException(
                $"docs/specs/{Path.GetFileName(dest)} já existe — arquive "
                    + "com outro nome (o histórico arquivado não é "
                    + "sobrescrito).");
        }

        Directory.CreateDirectory(archiveDir);
        await File.WriteAllTextAsync(file, updated, ct);

        var isGitRepo = Directory.Exists(Path.Join(workdir, ".git"))
            || File.Exists(Path.Join(workdir, ".git"));
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

        File.Move(file, dest);
        return dest;
    }

    /// <summary>Sync best-effort do label da Issue apontada em
    /// <c>| Ticket |</c> — roda <c>gh issue edit</c> removendo todas as
    /// labels de etapa anteriores antes de aplicar a nova (contrato de
    /// exclusividade do create-issues); qualquer falha é relatada sem
    /// bloquear a transição.</summary>
    private static async Task<string> SyncIssueLabelAsync(
        string workdir, string text, string to, CancellationToken ct)
    {
        var ticket = TicketRegex().Match(text);
        if (!ticket.Success
            || !StatusLabels.TryGetValue(to, out var label))
        {
            return string.Empty;
        }
        var removes = string.Concat(
            StageLabels.Where(l => l != label)
                .Select(l => $" --remove-label {l}"));
        var gh = await ChatProcessRunner.RunAsync(
            $"gh issue edit {ticket.Groups[1].Value} --add-label {label}"
                + removes,
            workdir, TimeSpan.FromSeconds(15), ct: ct);
        return gh.ExitCode == 0
            ? $" Label '{label}' aplicada na issue #{ticket.Groups[1].Value}."
            : " (sync de label ignorado — gh indisponível/sem auth)";
    }

    private static string Quote(string path) =>
        "'" + path.Replace("'", "'\\''") + "'";

    /// <summary>Casa os três formatos de status aceitos pelo validador —
    /// célula de tabela, linha simples e negrito — com grupos por formato
    /// para reescrita no mesmo estilo.</summary>
    [GeneratedRegex(
        @"^\|\s*Status\s*\|\s*`?(?<value>[^`|]+?)`?\s*\|"
            + @"|^Status\s*:\s*(?<line>[^\r\n]+)"
            + @"|^\*\*Status\*\*\s*:\s*(?<bold>[^\r\n]+)",
        RegexOptions.Multiline)]
    private static partial Regex StatusPattern();

    [GeneratedRegex(@"^SPEC-[A-Za-z0-9][A-Za-z0-9_-]*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SpecNameRegex();

    [GeneratedRegex(@"issues/(\d+)")]
    private static partial Regex TicketRegex();
}