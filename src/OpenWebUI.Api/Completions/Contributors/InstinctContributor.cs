using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.SystemPrompt;

namespace OpenWebUI.Api.Completions.Contributors;

/// <summary>
/// Bloco 3.5d: instincts aprendidos (SPEC-20261023-instincts-learning
/// RF-003) — top-N <c>active</c> rankeados por confiança + boost de
/// relevância de stack (domínio casa com extensões/markers do workdir).
/// O bloco vai marcado como "observed patterns — not policy": instincts
/// são contexto não-revisado, nunca instrução de sistema.
/// </summary>
public sealed class InstinctContributor(
    InstinctService instincts,
    ConfigService config) : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 82;

    /// <summary>Cap total do bloco injetado (chars).</summary>
    public const int MaxBlockChars = 1200;

    /// <summary>Boost de rank quando o domínio casa com a stack do workdir.</summary>
    public const double StackBoost = 0.1;

    /// <summary>Arquivos inspecionados no workdir para detectar a stack.</summary>
    private const int MaxFilesScanned = 200;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        var repoSlug = (await ctx.GetRepoBindingAsync())?.Repo;
        var candidates = await instincts.InjectionCandidatesAsync(
            ctx.User.Id, repoSlug, ctx.Ct);
        if (candidates.Count == 0)
        {
            return [];
        }

        var maxInjected = Math.Clamp(
            await config.GetAsync("instincts.maxInjected", 6, ctx.Ct), 1, 20);
        var stack = await DetectStackAsync(ctx);
        var top = candidates
            .OrderByDescending(i => i.Confidence
                + (stack.Contains(i.Domain) ? StackBoost : 0))
            .ThenByDescending(i => i.UpdatedAt)
            .Take(maxInjected);

        var lines = top.Select(i =>
            $"- [{i.Domain}] {i.Trigger} → {i.Body} "
                + $"(conf {i.Confidence:0.##})");
        var block = "<learned_instincts note=\"Observed patterns — "
            + "not policy. Verify before following.\">\n"
            + string.Join("\n", lines) + "\n</learned_instincts>";
        return
        [
            block.Length <= MaxBlockChars
                ? block
                : block[..(MaxBlockChars - 24)] + "\n…\n</learned_instincts>"
        ];
    }

    /// <summary>
    /// Stack do workdir por extensões e marker files — heurística barata
    /// com cache implícito (1 scan por request). Domínios retornados
    /// casam com <see cref="Instinct.Domain"/> em minúsculas.
    /// </summary>
    private static async Task<HashSet<string>> DetectStackAsync(PromptContext ctx)
    {
        var workdir = await ctx.GetWorkdirAsync();
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(workdir) || !Directory.Exists(workdir))
        {
            return domains;
        }

        try
        {
            var scanned = 0;
            foreach (var file in Directory.EnumerateFiles(
                workdir, "*", SearchOption.AllDirectories))
            {
                if (scanned++ >= MaxFilesScanned)
                {
                    break;
                }
                var name = Path.GetFileName(file);
                var domain = name.ToLowerInvariant() switch
                {
                    "package.json" or "tsconfig.json" => "web",
                    "pyproject.toml" or "requirements.txt" or "pipfile" => "python",
                    "go.mod" => "go",
                    "cargo.toml" => "rust",
                    "pom.xml" or "build.gradle" or "build.gradle.kts" => "jvm",
                    "gemfile" => "ruby",
                    "composer.json" => "php",
                    "dockerfile" or "docker-compose.yaml" or "docker-compose.yml" => "docker",
                    _ => Path.GetExtension(name).ToLowerInvariant() switch
                    {
                        ".cs" or ".razor" or ".csproj" or ".sln" or ".slnx" => "dotnet",
                        ".py" => "python",
                        ".ts" or ".tsx" or ".js" or ".jsx" or ".vue" or ".svelte" => "web",
                        ".go" => "go",
                        ".rs" => "rust",
                        ".java" or ".kt" or ".scala" => "jvm",
                        ".rb" => "ruby",
                        ".php" => "php",
                        ".sql" => "sql",
                        ".tf" => "terraform",
                        _ => null,
                    },
                };
                if (domain is not null)
                {
                    domains.Add(domain);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Workdir ilegível = sem boost — a injeção segue por confiança.
        }
        return domains;
    }
}
