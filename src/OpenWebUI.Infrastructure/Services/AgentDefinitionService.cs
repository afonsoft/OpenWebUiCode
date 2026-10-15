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
    string Source);

/// <summary>
/// Descobre agentes declarativos de quatro camadas com precedência
/// crescente (RF-002): built-in embedded &lt; <c>{data}/agents/</c> (admin)
/// &lt; <c>{data}/users/{id}/agents/</c> (usuário) &lt;
/// <c>{workdir}/.openwebui/agents/</c> (workspace). Mesmo <c>name</c>: a
/// camada maior vence; o catálogo registra o <see cref="AgentDefinition.Source"/>
/// vencedor (provenance, padrão RF-006 de config-layers).
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
        var root = DataPaths.Root(env.ContentRootPath);
        ScanDir(map, Path.Join(root, "agents"), "admin");
        ScanDir(map, Path.Join(root, "users", userId, "agents"), "user");
        if (wsEnabled)
        {
            ScanDir(map, Path.Join(workdir!, ".openwebui", "agents"), "workspace");
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
        && Directory.Exists(Path.Join(workdir, ".openwebui", "agents"));

    /// <summary>Invalida o cache de um usuário (mudança de opt-in/workdir).</summary>
    public void Invalidate(string userId)
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
            files = Directory.EnumerateFiles(dir, "*.agent.md")
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

        var tools = fm.TryGetValue("tools", out var toolsRaw)
            ? ParseList(toolsRaw)
            : null;
        return new AgentDefinition(
            Name: name.Trim(),
            Description: desc.Trim().Length > MaxDescriptionChars
                ? desc.Trim()[..MaxDescriptionChars] : desc.Trim(),
            Model: fm.GetValueOrDefault("model"),
            Tools: tools,
            Spawns: fm.TryGetValue("spawns", out var spawnsRaw)
                ? ParseList(spawnsRaw) ?? [] : [],
            MaxDepth: fm.TryGetValue("maxDepth", out var depthRaw)
                && int.TryParse(depthRaw, out var depth)
                ? Math.Clamp(depth, 0, 3) : 1,
            Skills: fm.TryGetValue("skills", out var skillsRaw)
                ? ParseList(skillsRaw) ?? [] : [],
            Blocking: !fm.TryGetValue("blocking", out var blockingRaw)
                || !blockingRaw.Equals("false", StringComparison.OrdinalIgnoreCase),
            SystemPrompt: prompt,
            Source: source);
    }

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
