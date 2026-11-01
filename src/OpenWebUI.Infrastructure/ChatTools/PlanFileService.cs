using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Diretório de planos + persistência da tool <c>write_plan</c>
/// (SPEC-20261031-plan-mode-v2 RF-001/RF-005): planos vivem em
/// <c>{workdir}/plans/</c> (fallback <c>~/.openwebui/plans/</c> sem
/// workspace), um arquivo <c>YYYY-MM-DD-{slug}.md</c> por plano — mesma
/// sessão atualiza o mesmo arquivo; sessões concorrentes com o mesmo
/// slug ganham sufixo <c>-2</c>, <c>-3</c>... O registry
/// <see cref="Latest"/> expõe o último path por run para
/// <c>plan_exit</c> referenciar.
/// </summary>
public sealed partial class PlanFileService
{
    /// <summary>Nome do diretório dentro do workdir.</summary>
    public const string DirName = "plans";

    /// <summary>Último arquivo de plano por runId — lido por plan_exit.</summary>
    private static readonly ConcurrentDictionary<string, string> Latest = new();

    /// <summary>Arquivo por sessão (chatId ?? runId) — update in-place.</summary>
    private static readonly ConcurrentDictionary<string, string> SessionFile = new();

    /// <summary>Home resolver injetável para testes.</summary>
    internal static Func<string?> HomeResolver { get; set; } =
        () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// Diretório de planos absoluto: <c>{workspace}/plans</c> com workdir,
    /// senão <c>~/.openwebui/plans</c>.
    /// </summary>
    public static string DirFor(string? workspacePath)
    {
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            return Path.Combine(workspacePath, DirName);
        }
        var home = HomeResolver() ?? ".";
        return Path.Combine(home, ".openwebui", DirName);
    }

    /// <summary>Registra o path relativo do último plano da run.</summary>
    public static void SetLatest(string? runId, string relative)
    {
        if (!string.IsNullOrEmpty(runId))
        {
            Latest[runId] = relative;
        }
    }

    /// <summary>Último path relativo gravado pela run (null = nenhum).</summary>
    public static string? LatestOf(string? runId) =>
        runId is not null && Latest.TryGetValue(runId, out var p) ? p : null;

    /// <summary>
    /// Grava o plano: mesma sessão sobrescreve seu arquivo; slug derivado
    /// do título explícito, do heading `# ` ou da primeira linha. Devolve
    /// path relativo ao workdir (ou absoluto no fallback) + warnings.
    /// </summary>
    public PlanWriteResult Write(
        string? workspacePath, string sessionKey, string content,
        string? title, string? runId)
    {
        var dir = DirFor(workspacePath);
        Directory.CreateDirectory(dir);

        string fileName;
        if (SessionFile.TryGetValue(sessionKey, out var existing))
        {
            fileName = existing;
        }
        else
        {
            var slug = SlugOf(title ?? TitleOf(content));
            fileName = $"{DateTime.UtcNow:yyyy-MM-dd}-{slug}.md";
            // Sufixo para planos distintos com o mesmo slug na mesma data.
            for (var i = 2;
                File.Exists(Path.Combine(dir, fileName));
                i++)
            {
                fileName = $"{DateTime.UtcNow:yyyy-MM-dd}-{slug}-{i}.md";
            }
            SessionFile[sessionKey] = fileName;
        }

        var full = Path.GetFullPath(Path.Combine(dir, fileName));
        // Guard anti-traversal: o nome é gerado internamente, mas o jail é
        // reconfirmado antes de escrever.
        if (!full.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Caminho do plano fora do diretório de planos.");
        }

        var doc = content.TrimStart().StartsWith("# ", StringComparison.Ordinal)
            ? content
            : $"# {title ?? "Plano"}\n\n{content}";
        File.WriteAllText(full, doc);

        var relative = string.IsNullOrWhiteSpace(workspacePath)
            ? full
            : $"{DirName}/{fileName}";
        SetLatest(runId, relative);

        return new PlanWriteResult(full, relative,
            new FileInfo(full).Length, MissingSections(doc));
    }

    /// <summary>
    /// Lista os <c>.md</c> do diretório de planos (mais recente primeiro).
    /// </summary>
    public IReadOnlyList<PlanEntry> List(string? workspacePath)
    {
        var dir = DirFor(workspacePath);
        if (!Directory.Exists(dir))
        {
            return [];
        }
        var root = Path.GetFullPath(dir);
        return new DirectoryInfo(dir)
            .EnumerateFiles("*.md", SearchOption.TopDirectoryOnly)
            .Select(f => new PlanEntry(
                f.Name, RelativeOf(root, f, workspacePath),
                FirstHeading(f.FullName), f.LastWriteTimeUtc))
            .OrderByDescending(e => e.ModifiedAt)
            .ToList();
    }

    /// <summary>Seções do template do plano (RF-003) — warning não-bloqueante.</summary>
    public static readonly string[] RequiredSections =
    [
        "Summary", "Implementation Steps", "Files to Modify",
        "Verification", "Risks",
    ];

    /// <summary>Seções obrigatórias ausentes no documento.</summary>
    public static List<string> MissingSections(string plan) =>
        RequiredSections
            .Where(s => !SectionRegex(s).IsMatch(plan))
            .ToList();

    /// <summary>Match de heading `## Seção` case-insensitive.</summary>
    public static Regex SectionRegex(string section) =>
        new($@"^#{1,3}\s+{Regex.Escape(section)}\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Primeiro heading `# ` do arquivo (ou primeira linha).</summary>
    private static string? FirstHeading(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path).Take(10))
            {
                if (line.StartsWith("# ", StringComparison.Ordinal))
                {
                    return line[2..].Trim();
                }
                if (!string.IsNullOrWhiteSpace(line))
                {
                    return line.Trim()[..Math.Min(line.Trim().Length, 80)];
                }
            }
        }
        catch (IOException)
        {
            // arquivo sumiu entre list e read — devolve null
        }
        return null;
    }

    private static string RelativeOf(string dirFull, FileInfo f, string? ws) =>
        string.IsNullOrWhiteSpace(ws)
            ? f.FullName
            : $"{DirName}/{f.Name}";

    /// <summary>Título do documento: primeiro heading ou primeira linha.</summary>
    private static string TitleOf(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                return trimmed[2..];
            }
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }
        return "plan";
    }

    /// <summary>slug kebab-case do título (máx 60, fallback "plan").</summary>
    internal static string SlugOf(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "plan";
        }
        var sb = new StringBuilder(title.Length);
        var dash = true; // sem hífen de borda/duplo
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                dash = false;
            }
            else if (!dash)
            {
                sb.Append('-');
                dash = true;
            }
        }
        var slug = sb.ToString().TrimEnd('-');
        return slug.Length is 0 ? "plan" : slug[..Math.Min(slug.Length, 60)];
    }
}

/// <summary>Resultado da gravação do plano.</summary>
public sealed record PlanWriteResult(
    string FullPath, string RelativePath, long Bytes,
    IReadOnlyList<string> MissingSections);

/// <summary>Item da listagem de planos.</summary>
public sealed record PlanEntry(
    string Name, string Path, string? Title, DateTime ModifiedAt);
