using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Definição declarativa de sub-agente (SPEC-20261014-agent-definitions
/// RF-001): arquivo <c>*.agent.md</c> com frontmatter mínimo
/// <c>key: value</c> (mesmo parser do <see cref="SkillDiscoveryService"/>;
/// listas em CSV ou <c>[a, b]</c>) + corpo markdown que vira o system
/// prompt da run filha.
/// </summary>
/// <param name="Name">Id kebab-case — chave do catálogo (case-insensitive).</param>
/// <param name="Description">Uma linha exibida pelo <c>agent_catalog</c>/GET /agents.</param>
/// <param name="Model">Id de modelo, <c>role:tasks|memory|advisor</c> ou null (herda do pai).</param>
/// <param name="Tools">Allowlist de tools; <c>null</c> = herda do caller filtrado a Read; <c>["*"]</c> = herda integral.</param>
/// <param name="Spawns">Nomes de agentes que a run filha pode delegar (vazio = sem delegate).</param>
/// <param name="MaxDepth">Teto de profundidade da cadeia sob este agente (default 1).</param>
/// <param name="Skills">Slugs de skills pessoais ativas autoload no system prompt.</param>
/// <param name="Blocking">Default de <c>wait</c> no delegate (true = síncrono).</param>
/// <param name="SystemPrompt">Corpo markdown do arquivo.</param>
/// <param name="Source">Provenance: <c>builtin|admin|user|workspace</c>.</param>
public sealed record AgentDefinition(
    string Name,
    string Description,
    string? Model,
    IReadOnlyList<string>? Tools,
    IReadOnlyList<string> Spawns,
    int MaxDepth,
    IReadOnlyList<string> Skills,
    bool Blocking,
    string SystemPrompt,
    string Source,
    /// <summary>Teto de steps declarado no frontmatter (<c>steps:</c>);
    /// null = teto global (<c>agentLoop.maxRounds</c>).</summary>
    int? Steps = null);

/// <summary>
/// Descobre agentes declarativos em camadas com precedência crescente
/// (RF-002 + item B do afonsoft/skills): built-in embedded &lt; globais
/// (<c>~/.config/opencode/agents</c>, <c>~/.claude/agents</c>,
/// <c>~/.agents/agents</c>) &lt; <c>{data}/agents/</c> (admin) &lt;
/// <c>{data}/users/{id}/agents/</c> (usuário) &lt; workspace
/// (<c>.openwebui/agents</c>, <c>.opencode/agents</c>, <c>.claude/agents</c>,
/// <c>.agents/agents</c> do workdir). Mesmo <c>name</c>: a camada maior
/// vence; o catálogo registra o <see cref="AgentDefinition.Source"/>
/// vencedor (provenance, padrão RF-006 de config-layers).
/// <para>
/// Todo diretório dedicado a agentes aceita <c>*.md</c> (que cobre
/// <c>*.agent.md</c>) — <c>name</c>+<c>description</c> no frontmatter
/// continuam obrigatórios, então READMEs soltos são ignorados pelo parser.
/// Frontmatter estilo Claude Code é compatibilizado: <c>tools: Read, Grep,
/// Glob</c> mapeia para os nomes builtin equivalentes e <c>model: inherit</c>
/// vira "herda do pai" (null).
/// </para>
/// <para>
/// Workspace agents ficam atrás do opt-in por usuário
/// <c>u:{id}:agents.workspace.enabled</c> (default off — OQ-2 da SPEC):
/// um repositório clonado não injeta system prompt nem allowlist de tools
/// sem consentimento explícito do dono.
/// </para>
/// Cache por (usuário, workdir) com TTL de 60s — mesmo padrão do
/// <see cref="SkillDiscoveryService"/>; arquivos inválidos são ignorados
/// com log, nunca derrubam o catálogo.
/// </summary>
public sealed class AgentDefinitionService(
    IHostEnvironment env, ConfigService config,
    ILogger<AgentDefinitionService> logger)
{
    /// <summary>Seam de testes: resolve o HOME do processo.</summary>
    internal static Func<string?> HomeResolver { get; set; } =
        () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Diretórios globais de agentes (relativos ao HOME), em ordem de precedência.</summary>
    private static readonly string[] GlobalAgentDirs =
        [".config/opencode/agents", ".claude/agents", ".agents/agents"];

    /// <summary>Diretórios de agentes do workdir (opt-in), em ordem de precedência.</summary>
    private static readonly string[] WorkspaceAgentDirs =
        [".opencode/agents", ".claude/agents", ".agents/agents", ".openwebui/agents"];

    /// <summary>
    /// Aliases de tools no estilo Claude Code → nomes builtin
    /// (<c>Read</c> → <c>file_read</c>, <c>Bash</c> → <c>shell_exec</c>…).
    /// Nomes já no formato builtin passam intocados.
    /// </summary>
    private static readonly Dictionary<string, string> ToolAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Read"] = "file_read",
        ["Grep"] = "file_grep",
        ["Glob"] = "file_glob",
        ["LS"] = "file_list",
        ["List"] = "file_list",
        ["Write"] = "file_write",
        ["Edit"] = "file_edit",
        ["MultiEdit"] = "file_edit",
        ["Bash"] = "shell_exec",
        ["WebSearch"] = "web_search",
        ["WebFetch"] = "fetch_url",
        ["TodoWrite"] = "todo_write",
        ["AskUserQuestion"] = "ask_user",
        ["Task"] = "delegate_task",
        ["Skill"] = "skill",
    };
    /// <summary>TTL do catálogo cacheado.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    /// <summary>Máximo de agentes por catálogo efetivo.</summary>
    public const int MaxAgents = 50;

    /// <summary>Teto por arquivo <c>*.agent.md</c>.</summary>
    private const int MaxFileBytes = 64 * 1024;

    /// <summary>Teto do corpo (system prompt) de um agente.</summary>
    private const int MaxPromptChars = 16 * 1024;

    /// <summary>Teto da description.</summary>
    private const int MaxDescriptionChars = 300;

    /// <summary>Máximo de itens por lista frontmatter.</summary>
    private const int MaxListItems = 64;

    private static readonly ConcurrentDictionary<
        string, (DateTimeOffset At, IReadOnlyDictionary<string, AgentDefinition> Map)> Cache =
        new(StringComparer.Ordinal);

    private static readonly string[] BuiltinResourceNames =
        typeof(AgentDefinitionService).Assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".agent.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Catálogo efetivo do usuário (com workspace quando
    /// <paramref name="workdir"/>+opt-in). Chave = <see cref="AgentDefinition.Name"/>
    /// case-insensitive.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, AgentDefinition>> ListAsync(
        string userId, string? workdir, CancellationToken ct)
    {
        var wsEnabled = workdir is not null
            && await config.GetAsync($"u:{userId}:agents.workspace.enabled", false, ct);
        var cacheKey = $"{userId}|{(wsEnabled ? workdir : "")}";
        if (Cache.TryGetValue(cacheKey, out var hit)
            && DateTimeOffset.UtcNow - hit.At < Ttl)
        {
            return hit.Map;
        }

        var map = new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in Builtin())
        {
            map[def.Name] = def;
        }
        var home = HomeResolver();
        if (home is not null)
        {
            foreach (var rel in GlobalAgentDirs)
            {
                ScanDir(map, Path.Join(home, rel), "global");
            }
        }
        var root = DataPaths.Root(env.ContentRootPath);
        ScanDir(map, Path.Join(root, "agents"), "admin");
        ScanDir(map, Path.Join(root, "users", userId, "agents"), "user");
        if (wsEnabled)
        {
            foreach (var rel in WorkspaceAgentDirs)
            {
                ScanDir(map, Path.Join(workdir!, rel), "workspace");
            }
        }

        var result = map as IReadOnlyDictionary<string, AgentDefinition>;
        Cache[cacheKey] = (DateTimeOffset.UtcNow, result);
        return result;
    }

    /// <summary>Resolve um agente por nome (null quando não existe/visível).</summary>
    public async Task<AgentDefinition?> FindAsync(
        string userId, string? workdir, string name, CancellationToken ct) =>
        (await ListAsync(userId, workdir, ct))
            .TryGetValue(name, out var def) ? def : null;

    /// <summary>Indica se o workdir tem agentes de workspace (opt-in pendente).</summary>
    public static bool HasWorkspaceAgents(string? workdir) =>
        workdir is not null
        && WorkspaceAgentDirs.Any(rel => Directory.Exists(Path.Join(workdir, rel)));

    /// <summary>Invalida o cache de um usuário (mudança de opt-in/workdir).</summary>
    public static void Invalidate(string userId)
    {
        foreach (var key in Cache.Keys.Where(k => k.StartsWith(userId + "|", StringComparison.Ordinal)))
        {
            Cache.TryRemove(key, out _);
        }
    }

    /// <summary>Agentes built-in (embedded <c>agents/*.agent.md</c>).</summary>
    private IEnumerable<AgentDefinition> Builtin()
    {
        var asm = typeof(AgentDefinitionService).Assembly;
        foreach (var res in BuiltinResourceNames)
        {
            string raw;
            try
            {
                using var stream = asm.GetManifestResourceStream(res);
                if (stream is null)
                {
                    continue;
                }
                using var reader = new StreamReader(stream);
                raw = reader.ReadToEnd();
            }
            catch (IOException)
            {
                continue;
            }
            if (TryParse(raw, res, "builtin") is { } def)
            {
                yield return def;
            }
        }
    }

    /// <summary>Varre um diretório de uma camada (sem seguir reparse points).</summary>
    private void ScanDir(
        Dictionary<string, AgentDefinition> map, string dir, string source)
    {
        try
        {
            if (!Directory.Exists(dir)
                || File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        IEnumerable<string> files;
        try
        {
            // *.md cobre *.agent.md — o parser exige name+description no
            // frontmatter, então markdowns comuns do diretório são ignorados.
            files = Directory.EnumerateFiles(dir, "*.md")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        foreach (var file in files)
        {
            if (map.Count >= MaxAgents)
            {
                return;
            }
            string raw;
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes)
                {
                    continue;
                }
                raw = File.ReadAllText(file);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            if (TryParse(raw, file, source) is { } def)
            {
                map[def.Name] = def;
            }
        }
    }

    /// <summary>
    /// Parse de um <c>*.agent.md</c>: frontmatter <c>---\nkey: value\n---</c>
    /// + corpo. <c>name</c> e <c>description</c> são obrigatórios — inválido
    /// retorna null (ignorado com log, nunca derruba o catálogo — RF-001).
    /// </summary>
    internal AgentDefinition? TryParse(string raw, string path, string source)
    {
        var fm = ParseFrontmatter(raw, out var body);
        if (!fm.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name)
            || !fm.TryGetValue("description", out var desc) || string.IsNullOrWhiteSpace(desc))
        {
            logger.LogWarning("agent_definition: {Path} ignorado — name/description ausentes", path);
            return null;
        }

        var prompt = body.Trim();
        if (prompt.Length > MaxPromptChars)
        {
            prompt = prompt[..MaxPromptChars];
        }

        var model = fm.GetValueOrDefault("model");
        return new AgentDefinition(
            Name: name.Trim(),
            Description: desc.Trim().Length > MaxDescriptionChars
                ? desc.Trim()[..MaxDescriptionChars] : desc.Trim(),
            // Claude Code usa `model: inherit` — equivale ao nosso null
            // (herda do pai); qualquer outro valor passa verbatim.
            Model: string.Equals(model, "inherit", StringComparison.OrdinalIgnoreCase)
                ? null : model,
            Tools: ListField(fm, "tools")?.Select(MapToolName).ToList(),
            Spawns: ListField(fm, "spawns") ?? [],
            // `max_nesting` (SPEC-20261031-sidekick-orchestration RF-005)
            // é alias de `maxDepth`; negativo/inválido → default 1;
            // clampado ao teto global 0..3.
            MaxDepth: (fm.TryGetValue("maxDepth", out var depthRaw)
                    || fm.TryGetValue("max_nesting", out depthRaw))
                && int.TryParse(depthRaw, out var depth)
                ? Math.Clamp(depth, 0, 3) : 1,
            Skills: ListField(fm, "skills") ?? [],
            Blocking: !fm.TryGetValue("blocking", out var blockingRaw)
                || !blockingRaw.Equals("false", StringComparison.OrdinalIgnoreCase),
            SystemPrompt: prompt,
            Source: source,
            Steps: StepsField(fm));
    }

    /// <summary>Lista de um campo frontmatter — <c>[a, b]</c> ou CSV.</summary>
    private static List<string>? ListField(
        Dictionary<string, string> fm, string key) =>
        fm.TryGetValue(key, out var raw) ? ParseList(raw) : null;

    /// <summary>
    /// <c>steps</c> por agent (SPEC-20261016-agent-steps-usage RF-001):
    /// inteiro positivo; 0/negativo/inválido → null (default).
    /// </summary>
    private static int? StepsField(Dictionary<string, string> fm) =>
        fm.TryGetValue("steps", out var raw)
            && int.TryParse(raw, out var steps) && steps > 0
            ? steps : null;

    /// <summary>
    /// Frontmatter mínimo: <c>---\nkey: value\n---</c> no topo — copiado do
    /// <see cref="SkillDiscoveryService"/> para manter os dois parsers
    /// convergentes (valores com <c>#</c> ou <c>:</c> aceitos verbatim).
    /// </summary>
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

        var fmLines = raw[(firstNl + 1)..end].Split('\n');
        var i = 0;
        while (i < fmLines.Length)
        {
            var line = fmLines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                i++;
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (value is ">" or "|")
            {
                value = ReadBlockScalar(fmLines, ref i, line, value);
            }
            if (key.Length > 0 && value.Length > 0)
            {
                map[key] = value;
            }
            i++;
        }

        var rest = raw[(end + 4)..];
        var nl = rest.IndexOf('\n');
        body = nl >= 0 ? rest[(nl + 1)..] : string.Empty;
        return map;
    }

    /// <summary>
    /// Block scalar YAML (estilo Claude Code): linhas seguintes mais
    /// indentadas que a chave compõem o valor — <c>&gt;</c> junta com
    /// espaço, <c>|</c> preserva quebras. Avança <paramref name="i"/> até
    /// a última linha consumida.
    /// </summary>
    private static string ReadBlockScalar(
        string[] fmLines, ref int i, string keyLine, string marker)
    {
        var keyIndent = keyLine.TakeWhile(c => c is ' ' or '\t').Count();
        var block = new List<string>();
        while (i + 1 < fmLines.Length)
        {
            var next = fmLines[i + 1];
            var indent = next.TakeWhile(c => c is ' ' or '\t').Count();
            if (next.Trim().Length == 0)
            {
                i++;
                continue;
            }
            if (indent <= keyIndent)
            {
                break;
            }
            block.Add(next.Trim());
            i++;
        }
        return marker == "|"
            ? string.Join('\n', block)
            : string.Join(' ', block);
    }

    /// <summary>Mapeia alias de tool estilo Claude Code → nome builtin.</summary>
    private static string MapToolName(string name) =>
        ToolAliases.TryGetValue(name.Trim(), out var mapped) ? mapped : name.Trim();

    /// <summary>Lista frontmatter: <c>[a, b]</c> ou CSV <c>a, b</c>.</summary>
    private static List<string>? ParseList(string raw)
    {
        var t = raw.Trim();
        if (t.StartsWith('[') && t.EndsWith(']'))
        {
            t = t[1..^1];
        }
        var items = t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Take(MaxListItems)
            .ToList();
        return items.Count == 0 ? null : items;
    }
}
