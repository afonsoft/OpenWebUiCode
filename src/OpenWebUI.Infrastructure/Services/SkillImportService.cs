using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Importa <c>SKILL.md</c> de repositórios git públicos como skills globais
/// (SPEC-20261104-customize-hub RF-008): varre <c>{subPath}/*/SKILL.md</c>
/// via GitHub trees API, faz parse do frontmatter e upserta
/// <see cref="Skill"/> com <c>UserId == null</c> por nome (idempotente).
/// Somente admin — SSRF restrito a github.com/raw.githubusercontent.com.
/// </summary>
public sealed partial class SkillImportService(
    AppDbContext db, IHttpClientFactory httpFactory)
{
    private const int MaxFiles = 100;
    private const int MaxFileBytes = 2 * 1024 * 1024;

    private static readonly Regex RepoUrlRe = new(
        @"^https?://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Baixa e importa as skills de <paramref name="repoUrl"/>.
    /// <paramref name="names"/> vazio/nulo = todas encontradas.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// URL inválida (400-level) ou falha de rede/API do GitHub (502-level) —
    /// o endpoint mapeia pelo prefixo da mensagem.
    /// </exception>
    public async Task<SkillImportResult> ImportAsync(
        string repoUrl, string? subPath, IReadOnlyList<string>? names,
        CancellationToken ct)
    {
        var match = RepoUrlRe.Match(repoUrl?.Trim() ?? string.Empty);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                "invalid: URL deve ser um repositório público do GitHub (https://github.com/{owner}/{repo}).");
        }
        var owner = match.Groups["owner"].Value;
        var repo = match.Groups["repo"].Value;
        var path = string.IsNullOrWhiteSpace(subPath) ? "skills" : subPath.Trim().Trim('/');
        var wanted = names is { Count: > 0 }
            ? names.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        var http = httpFactory.CreateClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenWebUI-CustomizeHub/1.0");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(60));

        JsonElement repoInfo;
        try
        {
            repoInfo = await http.GetFromJsonAsync<JsonElement>(
                $"https://api.github.com/repos/{owner}/{repo}", linked.Token);
            if (repoInfo.ValueKind == JsonValueKind.Undefined)
            {
                throw new InvalidOperationException("github: resposta vazia do GitHub.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"github: falha ao consultar o repositório — {ex.Message}");
        }
        var branch = repoInfo.TryGetProperty("default_branch", out var b)
            ? b.GetString() ?? "main"
            : "main";

        JsonElement tree;
        try
        {
            tree = await http.GetFromJsonAsync<JsonElement>(
                $"https://api.github.com/repos/{owner}/{repo}/git/trees/{branch}?recursive=1",
                linked.Token);
            if (tree.ValueKind == JsonValueKind.Undefined)
            {
                throw new InvalidOperationException("github: resposta vazia do GitHub.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"github: falha ao ler a árvore do repo — {ex.Message}");
        }

        var prefix = $"{path}/";
        var files = tree.GetProperty("tree").EnumerateArray()
            .Where(e => e.GetProperty("type").GetString() == "blob")
            .Select(e => e.GetProperty("path").GetString() ?? string.Empty)
            .Where(p => p.StartsWith(prefix, StringComparison.Ordinal)
                && p.EndsWith("/SKILL.md", StringComparison.Ordinal))
            .Take(MaxFiles)
            .ToList();

        var imported = new List<string>();
        var skipped = new List<SkillImportSkip>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var file in files)
        {
            var dirName = file.Split('/')[^2];
            if (wanted is not null && !wanted.Contains(dirName))
            {
                skipped.Add(new SkillImportSkip(dirName, "não selecionado"));
                continue;
            }

            string body;
            try
            {
                body = await http.GetStringAsync(
                    $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{file}",
                    linked.Token);
            }
            catch (Exception ex)
            {
                skipped.Add(new SkillImportSkip(dirName, $"falha no download — {ex.Message}"));
                continue;
            }
            if (body.Length > MaxFileBytes)
            {
                skipped.Add(new SkillImportSkip(dirName, "arquivo maior que 2MB"));
                continue;
            }

            var (name, description, content, allowedTools) = ParseSkillMarkdown(body);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = dirName;
            }
            if (wanted is not null && !wanted.Contains(dirName) && !wanted.Contains(name))
            {
                skipped.Add(new SkillImportSkip(name, "não selecionado"));
                continue;
            }

            var existing = await db.Skills.FirstOrDefaultAsync(
                s => s.UserId == null && s.Name == name, linked.Token);
            if (existing is null)
            {
                db.Skills.Add(new Skill
                {
                    UserId = null,
                    Name = name,
                    Description = description,
                    Content = content,
                    AllowedTools = allowedTools,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                existing.Description = description;
                existing.Content = content;
                existing.AllowedTools = allowedTools;
                existing.UpdatedAt = now;
            }
            imported.Add(name);
        }

        await db.SaveChangesAsync(ct);
        return new SkillImportResult(imported, skipped);
    }

    /// <summary>
    /// Parse do frontmatter YAML simples de um SKILL.md:
    /// <c>name</c> e <c>description</c> na primeira linha de cada chave
    /// (valores multilinha não suportados — segue o padrão do repo).
    /// O corpo retornado é o arquivo inteiro (frontmatter incluído) — o
    /// conteúdo injetável carrega os metadados junto, como no repo-skill.
    /// </summary>
    public static (string? Name, string? Description, string Content, string? AllowedTools) ParseSkillMarkdown(
        string markdown)
    {
        string? name = null;
        string? description = null;
        string? allowedTools = null;
        var span = markdown.AsSpan().TrimStart('\uFEFF');
        if (span.StartsWith("---"))
        {
            var rest = span[3..].ToString();
            var end = rest.IndexOf("\n---", StringComparison.Ordinal);
            var front = end >= 0 ? rest[..end] : rest;
            foreach (var line in front.Split('\n'))
            {
                var trimmed = line.TrimEnd();
                if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    name = trimmed[5..].Trim().Trim('"', '\'');
                }
                else if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                {
                    description = trimmed[12..].Trim().Trim('"', '\'');
                }
                else if (trimmed.StartsWith("allowed-tools:", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("allowed_tools:", StringComparison.OrdinalIgnoreCase))
                {
                    var raw = trimmed[(trimmed.IndexOf(':') + 1)..]
                        .Trim().Trim('[', ']');
                    var tools = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(t => t.Trim('"', '\'').ToLowerInvariant())
                        .Where(t => t.Length > 0).Distinct().ToList();
                    allowedTools = tools.Count == 0 ? null : string.Join(',', tools);
                }
            }
        }
        return (name, description, markdown, allowedTools);
    }
}
