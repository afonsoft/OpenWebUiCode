using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Entrada descoberta no repositório (skill ou command markdown).</summary>
/// <param name="Scope"><c>project</c> (workdir vinculado), <c>global</c>
/// (<c>~/.agents</c> etc.) ou <c>remote</c> (catálogo HTTP — RF-002).</param>
/// <param name="Autoinvoke">Frontmatter <c>autoinvoke:true</c> — injeta o
/// corpo da skill no system prompt sem ser chamada (RF-002).</param>
public sealed record RepoEntry(
    string Name, string Description, string Path, string Body,
    string? Agent = null, string? Model = null, bool Subtask = false,
    string Scope = "project", string? Mode = null, bool Autoinvoke = false);

/// <summary>Servidor MCP declarado em <c>.agents/mcp.json</c> (global ou do projeto).</summary>
public sealed record AgentsMcpServer(
    string Id, string Transport, string? Command, List<string> Args,
    Dictionary<string, string> Env, string? Url,
    Dictionary<string, string> Headers, string SourceFile);

/// <summary>Resultado do parse dos arquivos <c>mcp.json</c> (erros viram warnings, nunca lançam).</summary>
public sealed record AgentsMcpConfig(
    IReadOnlyList<AgentsMcpServer> Servers, IReadOnlyList<string> Warnings)
{
    /// <summary>Configuração vazia (nenhum arquivo encontrado).</summary>
    public static readonly AgentsMcpConfig Empty = new([], []);
}

/// <summary>Resultado do scan: skills + commands já deduplicados por nome.</summary>
public sealed record RepoScanResult(
    IReadOnlyList<RepoEntry> Skills, IReadOnlyList<RepoEntry> Commands);

/// <summary>
/// Descobre skills (<c>SKILL.md</c>) e commands markdown do repositório
/// vinculado — equivalente ao <c>src/skill/discovery.ts</c> e
/// <c>src/command/index.ts</c> do opencode
/// (SPEC-20261009-repo-skills-slash-commands, E16 S3+S4).
/// Raízes varridas (precedência, primeiro nome vence):
/// skills — <c>.claude/skills</c> &gt; <c>.agents/skills</c> &gt;
/// <c>.devin/skills</c> &gt; <c>.opencode/skill[s]</c> &gt; <c>skill[s]/</c>;
/// commands — <c>.opencode/command</c>, <c>.claude/commands</c>,
/// <c>.agents/commands</c>. Cache em memória com TTL de 60s, invalidado em
/// rebind/troca de branch via <see cref="Invalidate"/>.
/// Somente leitura: o conteúdo é markdown injetado como contexto — nunca executado.
/// </summary>
public sealed class SkillDiscoveryService(RemoteSkillCatalog? remote = null)
{
    /// <summary>Máximo de skills e de commands retornados por scan.</summary>
    public const int MaxItems = 50;

    private const string AgentKind = "agent";

    /// <summary>Teto por arquivo lido (SKILL.md ou command .md).</summary>
    public const int MaxFileBytes = 64 * 1024;

    /// <summary>Teto total das instruções de projeto injetadas no system prompt.</summary>
    public const int MaxInstructionsBytes = 32 * 1024;

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, RepoScanResult Result)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    // Raízes do projeto em ordem de precedência (primeiro nome vence).
    private static readonly string[] SkillRoots =
    [
        ".claude/skills", ".agents/skills", ".devin/skills",
        ".opencode/skill", ".opencode/skills", "skill", "skills",
    ];

    private static readonly string[] CommandRoots =
    [
        ".opencode/command", ".claude/commands", ".agents/commands",
    ];

    // Raízes globais em ordem de precedência (SPEC-20261016-global-agents-
    // scope RF-001): .agents é a fonte canônica; .claude/.config/opencode
    // são compatibilidade com os catálogos já instalados do usuário.
    private static readonly string[] GlobalSkillRoots =
    [
        ".agents/skills", ".claude/skills", ".config/opencode/skills",
    ];

    private static readonly string[] GlobalCommandRoots =
    [
        ".agents/commands", ".claude/commands", ".config/opencode/commands",
    ];

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules",
    };

    /// <summary>Invalida o cache de um workdir (rebind ou troca de branch).</summary>
    public static void Invalidate(string workdir)
    {
        foreach (var key in Cache.Keys.Where(k => k.StartsWith(workdir + "|", StringComparison.Ordinal)))
        {
            Cache.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Scan do workdir + escopo global <c>~/.agents</c> (com cache TTL 60s).
    /// <paramref name="workdir"/> null/sem repo → só o escopo global
    /// (RF-007). Nunca lança por arquivo ruim.
    /// </summary>
    /// <param name="workdir">Diretório de trabalho do repo vinculado (ou null).</param>
    /// <param name="includeGlobal">Inclui skills/commands de <c>$HOME/.agents</c> etc.</param>
    /// <param name="home">Override do home (testes); default <c>UserProfile</c>.</param>
    public RepoScanResult Scan(
        string? workdir, bool includeGlobal = true, string? home = null)
    {
        var key = $"{workdir ?? ""}|{includeGlobal}|{home ?? ""}";
        if (Cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < Ttl)
        {
            return hit.Result;
        }

        var result = new RepoScanResult(
            MergeRemote(ScanSkills(workdir, includeGlobal, home)),
            ScanCommands(workdir, includeGlobal, home));
        Cache[key] = (DateTimeOffset.UtcNow, result);
        return result;
    }

    /// <summary>
    /// Mescla as skills do catálogo remoto cacheado (RF-002 — a refresh
    /// fica em <see cref="RemoteSkillCatalog.RefreshIfStaleAsync"/>,
    /// chamada pelos pontos de entrada async). Nomes locais/globais vencem
    /// remotos com o mesmo nome.
    /// </summary>
    private List<RepoEntry> MergeRemote(IReadOnlyList<RepoEntry> local)
    {
        var remoteEntries = remote?.Entries;
        if (remoteEntries is not { Count: > 0 })
        {
            return [.. local];
        }

        var known = local.Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. local, .. remoteEntries.Where(e => known.Add(e.Name))];
    }

    /// <summary>Busca uma entrada por nome (skill primeiro, depois command).</summary>
    public static RepoEntry? Find(RepoScanResult scan, string name) =>
        scan.Skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? scan.Commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Lê os arquivos de instrução do projeto na ordem AGENTS.md → CLAUDE.md →
    /// <c>.cursor/rules/*.md</c> → README.md, até <see cref="MaxInstructionsBytes"/>.
    /// Retorna <c>null</c> quando nenhum existe.
    /// </summary>
    public static string? LoadProjectInstructions(string workdir)
    {
        var sb = new StringBuilder();
        var budget = MaxInstructionsBytes;
        foreach (var rel in new[] { "AGENTS.md", "CLAUDE.md" })
        {
            budget -= AppendFile(sb, Path.Join(workdir, rel), rel, budget);
        }

        var rulesDir = Path.Join(workdir, ".cursor", "rules");
        if (Directory.Exists(rulesDir) && !IsReparsePoint(rulesDir))
        {
            foreach (var file in Directory.EnumerateFiles(rulesDir, "*.md").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (budget <= 0)
                {
                    break;
                }

                budget -= AppendFile(sb, file, Relative(workdir, file), budget);
            }
        }

        if (budget > 0)
        {
            AppendFile(sb, Path.Join(workdir, "README.md"), "README.md", budget);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static IReadOnlyList<RepoEntry> ScanSkills(
        string? workdir, bool includeGlobal, string? home) =>
        ScanRoots(RootsInPrecedenceOrder(
            workdir, SkillRoots, includeGlobal ? GlobalSkillRoots : null, home),
            "SKILL.md", filePerEntry: true);

    private static IReadOnlyList<RepoEntry> ScanCommands(
        string? workdir, bool includeGlobal, string? home) =>
        ScanRoots(RootsInPrecedenceOrder(
            workdir, CommandRoots, includeGlobal ? GlobalCommandRoots : null, home),
            "*.md", filePerEntry: false);

    /// <summary>
    /// Lista (dir, scope, baseDir) na ordem de varredura baixa→alta
    /// precedência: globais primeiro (invertidas — a canônica vem por
    /// último), depois as do projeto invertidas (o primeiro item de
    /// <paramref name="projectRoots"/>, maior precedência, é varrido por
    /// último). Dedup "último nome vence" materializa a precedência.
    /// </summary>
    private static List<(string Dir, string Scope, string Base)> RootsInPrecedenceOrder(
        string? workdir, string[] projectRoots, string[]? globalRoots, string? home)
    {
        var roots = new List<(string Dir, string Scope, string Base)>();
        if (globalRoots is not null && ResolveHome(home) is { } h)
        {
            for (var i = globalRoots.Length - 1; i >= 0; i--)
            {
                roots.Add((Path.Join(h, globalRoots[i]), "global", h));
            }
        }

        if (!string.IsNullOrWhiteSpace(workdir))
        {
            for (var i = projectRoots.Length - 1; i >= 0; i--)
            {
                roots.Add((
                    Path.Join(workdir, projectRoots[i].Replace('/', Path.DirectorySeparatorChar)),
                    "project", workdir));
            }
        }

        return roots;
    }

    /// <summary>
    /// Seam de teste: substitui a resolução do home do escopo global.
    /// Produção nunca seta — o default é <c>UserProfile</c>/<c>HOME</c>.
    /// </summary>
    internal static Func<string?>? HomeResolver { get; set; }

    private static string? ResolveHome(string? home)
    {
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        if (HomeResolver?.Invoke() is { } resolved && resolved.Length > 0)
        {
            return resolved;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
        {
            profile = Environment.GetEnvironmentVariable("HOME");
        }

        return string.IsNullOrWhiteSpace(profile) ? null : profile;
    }

    /// <summary>
    /// Varre as raízes (já ordenadas baixa→alta precedência). Mesmo nome:
    /// a ocorrência mais recente substitui a anterior (última fonte vence —
    /// RF-002). <paramref name="filePerEntry"/>: true = SKILL.md cujo nome
    /// vem do diretório-pai; false = *.md cujo nome vem do basename.
    /// </summary>
    private static List<RepoEntry> ScanRoots(
        List<(string Dir, string Scope, string Base)> roots,
        string pattern, bool filePerEntry)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var items = new List<RepoEntry>();
        var visited = 0;
        foreach (var (dir, scope, baseDir) in roots)
        {
            if (!Directory.Exists(dir) || IsReparsePoint(dir))
            {
                continue;
            }

            foreach (var file in EnumerateSafe(dir, pattern, ref visited))
            {
                if (items.Count >= MaxItems)
                {
                    return items;
                }

                var entry = TryParse(file, filePerEntry, scope, baseDir);
                if (entry is null)
                {
                    continue;
                }

                if (index.TryGetValue(entry.Name, out var at))
                {
                    items[at] = entry;
                }
                else
                {
                    index[entry.Name] = items.Count;
                    items.Add(entry);
                }
            }
        }

        return items;
    }

    /// <summary>
    /// Lê <c>{home}/.agents/mcp.json</c> + <c>{workdir}/.agents/mcp.json</c>
    /// (RF-006) no formato <c>{"mcpServers": {id: {command|url, ...}}}</c>.
    /// Projeto sobrescreve global por id. Arquivo malformado → warning,
    /// nunca exceção.
    /// </summary>
    public static AgentsMcpConfig LoadMcpServers(string? workdir, string? home = null)
    {
        var servers = new Dictionary<string, AgentsMcpServer>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var file in new[]
        {
            ResolveHome(home) is { } h ? Path.Join(h, ".agents", "mcp.json") : null,
            string.IsNullOrWhiteSpace(workdir)
                ? null : Path.Join(workdir, ".agents", "mcp.json"),
        })
        {
            if (file is null)
            {
                continue;
            }

            foreach (var s in ParseMcpJson(file, warnings))
            {
                servers[s.Id] = s;
            }
        }

        return new AgentsMcpConfig(servers.Values.ToList(), warnings);
    }

    private static List<AgentsMcpServer> ParseMcpJson(string file, List<string> warnings)
    {
        var parsed = new List<AgentsMcpServer>();
        JsonDocument doc;
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length is 0 or > MaxFileBytes)
            {
                return parsed;
            }

            doc = JsonDocument.Parse(File.ReadAllText(file));
        }
        catch (IOException) { return parsed; }
        catch (UnauthorizedAccessException) { return parsed; }
        catch (JsonException ex)
        {
            warnings.Add($"{file}: mcp.json inválido ({ex.Message}).");
            return parsed;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("mcpServers", out var map)
                || map.ValueKind != JsonValueKind.Object)
            {
                return parsed;
            }

            foreach (var prop in map.EnumerateObject())
            {
                var e = prop.Value;
                if (e.ValueKind != JsonValueKind.Object)
                {
                    warnings.Add($"{file}: server '{prop.Name}' ignorado (não é objeto).");
                    continue;
                }

                var command = Str(e, "command");
                var url = Str(e, "url");
                if (!string.IsNullOrWhiteSpace(command))
                {
                    parsed.Add(new AgentsMcpServer(
                        prop.Name, "stdio", command, StrList(e, "args"), StrMap(e, "env"),
                        null, [], file));
                }
                else if (!string.IsNullOrWhiteSpace(url))
                {
                    parsed.Add(new AgentsMcpServer(
                        prop.Name, "http", null, [], [], url, StrMap(e, "headers"), file));
                }
                else
                {
                    warnings.Add($"{file}: server '{prop.Name}' sem 'command' nem 'url' — ignorado.");
                }
            }
        }

        return parsed;

        static string? Str(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        static List<string> StrList(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray()
                    .Where(i => i.ValueKind == JsonValueKind.String)
                    .Select(i => i.GetString()!).ToList()
                : [];

        static Dictionary<string, string> StrMap(JsonElement e, string prop) =>
            e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object
                ? v.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(p => p.Name, p => p.Value.GetString()!)
                : [];
    }

    /// <summary>Enumera arquivos recursivamente sem seguir symlink/reparse point.</summary>
    private static List<string> EnumerateSafe(string dir, string pattern, ref int visited)
    {
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0 && visited < 400)
        {
            var current = pending.Pop();
            try
            {
                var files = Directory.EnumerateFiles(current, pattern).ToList();
                visited += files.Count;
                found.AddRange(files);

                foreach (var sub in Directory.EnumerateDirectories(current)
                    .Where(d => !IsReparsePoint(d)
                        && !SkipDirs.Contains(Path.GetFileName(d))))
                {
                    pending.Push(sub);
                }
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
        }

        return found;
    }

    /// <summary>
    /// Parse de um arquivo: frontmatter YAML mínimo + corpo (≤64KB).
    /// Entries globais carregam o caminho absoluto (fora do workdir);
    /// de projeto, o relativo ao workdir.
    /// </summary>
    private static RepoEntry? TryParse(
        string file, bool filePerEntry, string scope, string baseDir)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(file);
            if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes)
            {
                return null;
            }
        }
        catch (IOException) { return null; }

        string raw;
        try
        {
            raw = File.ReadAllText(file);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var fm = ParseFrontmatter(raw, out var body);
        var fallback = filePerEntry
            ? Path.GetFileName(Path.GetDirectoryName(file)) ?? info.Name
            : Path.GetFileNameWithoutExtension(file);
        var name = fm.GetValueOrDefault("name") ?? fallback;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var description = fm.GetValueOrDefault("description")
            ?? FirstLine(body)
            ?? string.Empty;
        var path = scope == "global" ? file : Relative(baseDir, file);
        return new RepoEntry(
            name.Trim(), description.Trim(), path, body.Trim(),
            Agent: fm.GetValueOrDefault(AgentKind),
            Model: fm.GetValueOrDefault("model"),
            Subtask: fm.GetValueOrDefault("subtask") is { } s
                && (s.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || s.Equals(AgentKind, StringComparison.OrdinalIgnoreCase))
                || fm.GetValueOrDefault("subagent") is { } sa
                && sa.Equals("true", StringComparison.OrdinalIgnoreCase),
            Scope: scope,
            Mode: fm.GetValueOrDefault("mode"),
            Autoinvoke: IsTruthy(fm.GetValueOrDefault("autoinvoke")));
    }

    /// <summary>
    /// Monta uma <see cref="RepoEntry"/> a partir do conteúdo bruto de um
    /// SKILL.md — usada pelo catálogo remoto (RF-002), que não tem
    /// arquivo em disco.
    /// </summary>
    internal static RepoEntry? ParseEntryContent(
        string raw, string fallbackName, string path, string scope)
    {
        var fm = ParseFrontmatter(raw, out var body);
        var name = fm.GetValueOrDefault("name") ?? fallbackName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new RepoEntry(
            name.Trim(),
            (fm.GetValueOrDefault("description") ?? FirstLine(body) ?? string.Empty).Trim(),
            path, body.Trim(),
            Agent: fm.GetValueOrDefault(AgentKind),
            Model: fm.GetValueOrDefault("model"),
            Subtask: fm.GetValueOrDefault("subtask") is { } s
                && (s.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || s.Equals(AgentKind, StringComparison.OrdinalIgnoreCase))
                || fm.GetValueOrDefault("subagent") is { } sa
                && sa.Equals("true", StringComparison.OrdinalIgnoreCase),
            Scope: scope,
            Mode: fm.GetValueOrDefault("mode"),
            Autoinvoke: IsTruthy(fm.GetValueOrDefault("autoinvoke")));
    }

    /// <summary>Valor de frontmatter interpretado como booleano true.</summary>
    private static bool IsTruthy(string? value) =>
        value is not null
            && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value == "1");

    /// <summary>Frontmatter mínimo: bloco <c>---\nkey: value\n---</c> no topo.</summary>
    private static Dictionary<string, string> ParseFrontmatter(string raw, out string body)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        body = raw;
        if (!raw.StartsWith("---", StringComparison.Ordinal))
        {
            return map;
        }

        var firstNl = raw.IndexOf('\n');
        if (firstNl < 0 || raw[..firstNl].Trim() != "---")
        {
            return map;
        }

        var end = raw.IndexOf("\n---", firstNl + 1, StringComparison.Ordinal);
        if (end < 0)
        {
            return map;
        }

        foreach (var line in raw[(firstNl + 1)..end].Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (key.Length > 0 && value.Length > 0)
            {
                map[key] = value;
            }
        }

        var rest = raw[(end + 4)..];
        var nl = rest.IndexOf('\n');
        body = nl >= 0 ? rest[(nl + 1)..] : string.Empty;
        return map;
    }

    private static int AppendFile(StringBuilder sb, string full, string rel, int budget)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(full);
            if (!info.Exists || info.Length == 0)
            {
                return 0;
            }
        }
        catch (IOException) { return 0; }

        string text;
        try
        {
            text = File.ReadAllText(full);
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }

        var take = Math.Min(text.Length, Math.Max(0, budget - rel.Length - 16));
        if (take <= 0)
        {
            return 0;
        }

        sb.Append("--- ").Append(rel).Append(" ---\n");
        sb.Append(text.AsSpan(0, take));
        if (take < text.Length)
        {
            sb.Append("\n[…truncated]");
        }

        sb.Append("\n\n");
        return take + rel.Length + 16;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static string Relative(string workdir, string full) =>
        Path.GetRelativePath(workdir, full).Replace(Path.DirectorySeparatorChar, '/');

    private static string? FirstLine(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var t = line.Trim().TrimStart('#').Trim();
            if (t.Length > 0)
            {
                return t.Length > 160 ? t[..160] : t;
            }
        }

        return null;
    }
}
