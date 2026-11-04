using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Importa um "plugin" de repositório git público — paridade Devin
/// Plugins (SPEC-20261104-skills-allowed-tools-plugins RF-003): o
/// manifesto <c>plugin.json</c> na raiz lista <c>skills[]</c>,
/// <c>commands[]</c> e <c>mcps[]</c>; tudo vira entrada
/// <b>organization/global</b> (skill global, McpServer global).
/// Somente admin — SSRF restrito a github.com/raw.githubusercontent.com,
/// mesmo padrão do <see cref="SkillImportService"/>. Idempotente:
/// skills por nome, MCPs por nome.
/// </summary>
public sealed partial class PluginImportService(
    AppDbContext db, IHttpClientFactory httpFactory)
{
    private const int MaxFiles = 100;
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxManifestBytes = 64 * 1024;

    private static readonly Regex RepoUrlRe = new(
        @"^https?://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Baixa <c>plugin.json</c> de <paramref name="repoUrl"/> e importa
    /// as entradas declaradas. Erros por entrada viram
    /// <c>skipped[]</c>; manifesto ausente/malformado →
    /// <see cref="InvalidOperationException"/> ("invalid:"/"github:").
    /// </summary>
    public async Task<PluginImportResult> ImportAsync(string repoUrl, CancellationToken ct)
    {
        var match = RepoUrlRe.Match(repoUrl?.Trim() ?? string.Empty);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                "invalid: URL deve ser um repositório público do GitHub (https://github.com/{owner}/{repo}).");
        }
        var owner = match.Groups["owner"].Value;
        var repo = match.Groups["repo"].Value;

        var http = httpFactory.CreateClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenWebUI-Plugins/1.0");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(60));

        var branch = await DefaultBranchAsync(http, owner, repo, linked.Token);
        var manifest = await FetchManifestAsync(http, owner, repo, branch, linked.Token);
        var files = await ListFilesAsync(http, owner, repo, branch, linked.Token);

        var warnings = new List<string>();
        var importedSkills = new List<string>();
        var importedMcps = new List<string>();
        var skipped = new List<PluginImportSkip>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var path in StringsOf(manifest, "skills"))
        {
            var resolved = ResolveSkillPath(files, path);
            if (resolved is null)
            {
                skipped.Add(new PluginImportSkip($"skills:{path}", "SKILL.md não encontrado"));
                continue;
            }
            try
            {
                var body = await http.GetStringAsync(
                    $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{resolved}",
                    linked.Token);
                if (body.Length > MaxFileBytes)
                {
                    skipped.Add(new PluginImportSkip($"skills:{path}", "arquivo maior que 2MB"));
                    continue;
                }
                var (name, description, content, allowedTools) =
                    SkillImportService.ParseSkillMarkdown(body);
                var skillName = string.IsNullOrWhiteSpace(name) ? path : name;
                await UpsertSkillAsync(skillName, description, content, allowedTools, now, linked.Token);
                importedSkills.Add(skillName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped.Add(new PluginImportSkip($"skills:{path}", $"falha no download — {ex.Message}"));
            }
        }

        // Commands → skills globais (aproximação documentada: command de
        // repo vira skill injetável; não existe entidade Command no DB).
        foreach (var path in StringsOf(manifest, "commands"))
        {
            var resolved = files.FirstOrDefault(f =>
                f.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (resolved is null)
            {
                skipped.Add(new PluginImportSkip($"commands:{path}", "arquivo não encontrado"));
                continue;
            }
            try
            {
                var body = await http.GetStringAsync(
                    $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{resolved}",
                    linked.Token);
                if (body.Length > MaxFileBytes)
                {
                    skipped.Add(new PluginImportSkip($"commands:{path}", "arquivo maior que 2MB"));
                    continue;
                }
                var name = Path.GetFileNameWithoutExtension(resolved);
                await UpsertSkillAsync(name, $"Plugin command {name}", body, null, now, linked.Token);
                importedSkills.Add(name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped.Add(new PluginImportSkip($"commands:{path}", $"falha no download — {ex.Message}"));
            }
        }

        foreach (var mcp in McpsOf(manifest, warnings))
        {
            var existing = await db.McpServers.FirstOrDefaultAsync(
                s => s.UserId == null && s.Name == mcp.Name, linked.Token);
            if (existing is null)
            {
                // Guardrail SPEC-20261104: MCP importado nasce desabilitado
                // e SEM headers — secrets nunca vêm do manifest; o admin
                // configura credenciais depois na UI.
                db.McpServers.Add(new McpServer
                {
                    UserId = null,
                    Name = mcp.Name,
                    Transport = mcp.Transport,
                    Command = mcp.Command,
                    ArgsJson = JsonSerializer.Serialize(mcp.Args ?? (List<string>)[]),
                    EnvJson = JsonSerializer.Serialize(mcp.EnvNames ?? (List<string>)[]),
                    Url = mcp.Url,
                    HeadersJson = "{}",
                    Enabled = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                existing.Transport = mcp.Transport;
                existing.Command = mcp.Command;
                existing.ArgsJson = JsonSerializer.Serialize(mcp.Args ?? (List<string>)[]);
                existing.Url = mcp.Url;
                existing.UpdatedAt = now;
            }
            importedMcps.Add(mcp.Name);
        }

        await db.SaveChangesAsync(ct);
        return new PluginImportResult(importedSkills, importedMcps, skipped, warnings);
    }

    /// <summary>Upsert de skill global por nome (idempotente).</summary>
    private async Task UpsertSkillAsync(
        string name, string? description, string content,
        string? allowedTools, long now, CancellationToken ct)
    {
        var existing = await db.Skills.FirstOrDefaultAsync(
            s => s.UserId == null && s.Name == name, ct);
        if (existing is null)
        {
            db.Skills.Add(new Skill
            {
                UserId = null, Name = name, Description = description,
                Content = content, AllowedTools = allowedTools,
                IsActive = true, CreatedAt = now, UpdatedAt = now,
            });
        }
        else
        {
            existing.Description = description;
            existing.Content = content;
            existing.AllowedTools = allowedTools;
            existing.UpdatedAt = now;
        }
    }

    /// <summary>Path de entrada ("skills/foo" ou "skills/foo/SKILL.md")
    /// → blob SKILL.md na árvore.</summary>
    private static string? ResolveSkillPath(List<string> files, string entry)
    {
        var norm = entry.Trim().Trim('/');
        return files.FirstOrDefault(f =>
            f.Equals(norm, StringComparison.OrdinalIgnoreCase)
            || f.Equals($"{norm}/SKILL.md", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Array de strings do manifesto; ignora entradas não-string.</summary>
    private static List<string> StringsOf(JsonElement manifest, string key)
    {
        if (!manifest.TryGetProperty(key, out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(MaxFiles)
            .ToList();
    }

    /// <summary>Array <c>mcps[]</c> validado; entradas ruins → warning.</summary>
    private static List<PluginMcpSpec> McpsOf(JsonElement manifest, List<string> warnings)
    {
        if (!manifest.TryGetProperty("mcps", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var result = new List<PluginMcpSpec>();
        var i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            i++;
            if (el.ValueKind != JsonValueKind.Object
                || !el.TryGetProperty("name", out var n)
                || n.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(n.GetString()))
            {
                warnings.Add($"mcps[{i}] ignorado — objeto sem 'name'.");
                continue;
            }
            var transport = el.TryGetProperty("transport", out var t)
                && t.ValueKind == JsonValueKind.String
                    ? t.GetString()!.Trim().ToLowerInvariant()
                    : "http";
            var url = el.TryGetProperty("url", out var u)
                && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            var command = el.TryGetProperty("command", out var c)
                && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (transport == "http" && string.IsNullOrWhiteSpace(url))
            {
                warnings.Add($"mcps[{i}] '{n.GetString()}' ignorado — http sem 'url'.");
                continue;
            }
            if (transport == "stdio" && string.IsNullOrWhiteSpace(command))
            {
                warnings.Add($"mcps[{i}] '{n.GetString()}' ignorado — stdio sem 'command'.");
                continue;
            }
            if (el.TryGetProperty("headers", out var hdrs) && hdrs.ValueKind == JsonValueKind.Object)
            {
                warnings.Add(
                    $"mcps[{i}] '{n.GetString()}' — 'headers' ignorado (secrets nunca do manifest; configure na UI).");
            }
            result.Add(new PluginMcpSpec
            {
                Name = n.GetString()!.Trim(),
                Transport = transport is "http" or "stdio" ? transport : "http",
                Url = url,
                Command = command,
                Args = StringListOf(el, "args"),
                EnvNames = StringListOf(el, "env"),
            });
        }
        return result;
    }

    private static List<string>? StringListOf(JsonElement el, string key) =>
        el.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!).ToList()
            : null;

    private static async Task<string> DefaultBranchAsync(
        HttpClient http, string owner, string repo, CancellationToken ct)
    {
        try
        {
            var info = await http.GetFromJsonAsync<JsonElement>(
                $"https://api.github.com/repos/{owner}/{repo}", ct);
            return info.TryGetProperty("default_branch", out var b)
                ? b.GetString() ?? "main" : "main";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"github: falha ao consultar o repositório — {ex.Message}");
        }
    }

    private static async Task<JsonElement> FetchManifestAsync(
        HttpClient http, string owner, string repo, string branch, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.GetAsync(
                $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/plugin.json", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"github: falha ao baixar plugin.json — {ex.Message}");
        }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    "invalid: plugin.json não encontrado na raiz do repositório.");
            }
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (raw.Length > MaxManifestBytes)
            {
                throw new InvalidOperationException("invalid: plugin.json excede 64KB.");
            }
            try
            {
                var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        "invalid: plugin.json deve ser um objeto JSON.");
                }
                return doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"invalid: plugin.json malformado — {ex.Message}");
            }
        }
    }

    private static async Task<List<string>> ListFilesAsync(
        HttpClient http, string owner, string repo, string branch, CancellationToken ct)
    {
        try
        {
            var tree = await http.GetFromJsonAsync<JsonElement>(
                $"https://api.github.com/repos/{owner}/{repo}/git/trees/{branch}?recursive=1", ct);
            return tree.GetProperty("tree").EnumerateArray()
                .Where(e => e.GetProperty("type").GetString() == "blob")
                .Select(e => e.GetProperty("path").GetString() ?? string.Empty)
                .Take(MaxFiles * 10)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"github: falha ao ler a árvore do repo — {ex.Message}");
        }
    }

    /// <summary>Spec MCP validada do manifesto.</summary>
    private sealed class PluginMcpSpec
    {
        public required string Name { get; init; }
        public required string Transport { get; init; }
        public string? Url { get; init; }
        public string? Command { get; init; }
        public List<string>? Args { get; init; }
        public List<string>? EnvNames { get; init; }
    }
}
