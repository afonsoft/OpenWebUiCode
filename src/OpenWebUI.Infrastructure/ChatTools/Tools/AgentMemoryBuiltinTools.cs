using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:memory_save</c> — grava uma memória durável do agente
/// (SPEC-20261010-agent-memory): fatos/decisões que devem sobreviver à
/// sessão. Escopo <c>global</c> (todo chat do usuário) ou <c>repo</c>
/// (amarrada ao repo bound do chat — só entra no prompt ali). Upsert por
/// (usuário, escopo, repo, título): salvar de novo com o mesmo título
/// atualiza. As memórias recentes são injetadas automaticamente no
/// system prompt de cada run (<c>&lt;agent_memory&gt;</c>).
/// </summary>
public sealed class MemorySaveBuiltinTool(AppDbContext db, WorkspaceRepoService repos)
    : IBuiltinChatTool
{
    /// <summary>Cap de caracteres do título.</summary>
    private const int MaxTitleChars = 200;

    /// <summary>Cap de caracteres do conteúdo.</summary>
    private const int MaxContentChars = 4000;

    /// <inheritdoc />
    public string Name => "memory_save";

    /// <inheritdoc />
    public string Description =>
        "Save a durable memory — a fact, decision, or preference worth "
            + "keeping across sessions. scope=global applies everywhere; "
            + "scope=repo binds it to this chat's repo (re-surfaced only "
            + "in chats bound to that repo). Saving the same title again "
            + "updates it.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "title": {
              "type": "string",
              "description": "Short unique label (e.g. 'test-command', 'deploy-target'). Re-saving a title updates it."
            },
            "content": {
              "type": "string",
              "description": "The fact to remember (concise — it is injected into future system prompts)."
            },
            "scope": {
              "type": "string",
              "enum": ["global", "repo"],
              "description": "'global' (default): applies in every chat. 'repo': only when this chat's repo is bound."
            }
          },
          "required": ["title", "content"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var title = ReadString(args, "title", MaxTitleChars);
        var content = ReadString(args, "content", MaxContentChars);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
        {
            return new BuiltinToolResult("Parâmetros 'title' e 'content' são obrigatórios.");
        }

        var repoScope = args.TryGetProperty("scope", out var sc)
            && sc.ValueKind == JsonValueKind.String
            && string.Equals(sc.GetString(), "repo", StringComparison.OrdinalIgnoreCase);
        string? repoSlug = null;
        if (repoScope)
        {
            var (binding, _) = await repos.ResolveBindingAsync(
                context.UserId, context.ChatId, ct);
            if (binding is null)
            {
                return new BuiltinToolResult(
                    "Este chat não tem repo vinculado — use scope=global "
                        + "ou vincule um repo primeiro.");
            }
            repoSlug = binding.Repo;
        }

        var existing = await db.AgentMemories
            .FirstOrDefaultAsync(m => m.UserId == context.UserId
                && m.Scope == (repoScope ? "repo" : "global")
                && m.RepoSlug == repoSlug
                && m.Title == title, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (existing is null)
        {
            existing = new AgentMemory
            {
                UserId = context.UserId,
                Scope = repoScope ? "repo" : "global",
                RepoSlug = repoSlug,
                Title = title.Trim(),
                CreatedAt = now,
            };
            db.AgentMemories.Add(existing);
        }
        existing.Content = content.Trim();
        existing.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        return new BuiltinToolResult(
            $"Memória salva ({existing.Scope}"
                + (repoSlug is not null ? $" {repoSlug}" : string.Empty)
                + $"): '{existing.Title}'.",
            new { memoryId = existing.Id, scope = existing.Scope, repo = repoSlug });
    }

    private static string? ReadString(JsonElement args, string field, int max)
    {
        if (!args.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.String
            || el.GetString() is not { } s)
        {
            return null;
        }
        return s.Length > max ? s[..max] : s;
    }
}

/// <summary>
/// <c>builtin:memory_search</c> — busca as memórias duráveis do usuário
/// (SPEC-20261010-agent-memory): substring no título/conteúdo, escopo
/// global + repo do chat atual por padrão, <c>all</c> para varrer tudo.
/// Read-only e owner-scoped.
/// </summary>
public sealed class MemorySearchBuiltinTool(AppDbContext db, WorkspaceRepoService repos)
    : IBuiltinChatTool
{
    /// <summary>Máximo de resultados devolvidos.</summary>
    private const int MaxResults = 10;

    /// <summary>Cap de caracteres de cada conteúdo devolvido.</summary>
    private const int MaxContentChars = 500;

    /// <inheritdoc />
    public string Name => "memory_search";

    /// <inheritdoc />
    public string Description =>
        "Search your durable memories — substring match over title and "
            + "content. Default scope: global + this chat's repo; "
            + "scope=all searches everything.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Substring to match in title or content (empty/omitted lists recent memories)."
            },
            "scope": {
              "type": "string",
              "enum": ["default", "global", "repo", "all"],
              "description": "'default': global + current chat's repo. 'all': every memory you own."
            }
          },
          "required": []
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var query = args.TryGetProperty("query", out var q)
            && q.ValueKind == JsonValueKind.String
                ? (q.GetString() ?? string.Empty).Trim()
                : string.Empty;
        var scopeArg = args.TryGetProperty("scope", out var sc)
            && sc.ValueKind == JsonValueKind.String
                ? sc.GetString()
                : "default";

        string? repoSlug = null;
        if (scopeArg is "default" or "repo")
        {
            var (binding, _) = await repos.ResolveBindingAsync(
                context.UserId, context.ChatId, ct);
            repoSlug = binding?.Repo;
        }

        var scoped = ScopedMemories(context.UserId, scopeArg, repoSlug);
        if (query.Length > 0)
        {
            scoped = scoped.Where(
                m => m.Title.Contains(query) || m.Content.Contains(query));
        }

        var hits = await scoped.OrderByDescending(m => m.UpdatedAt)
            .Take(MaxResults)
            .Select(m => new { m.Title, m.Scope, m.RepoSlug, m.Content, m.UpdatedAt })
            .ToListAsync(ct);
        if (hits.Count == 0)
        {
            return new BuiltinToolResult(
                query.Length > 0
                    ? $"Nenhuma memória corresponde a '{query}'."
                    : "Nenhuma memória salva ainda — use builtin_memory_save.");
        }

        var lines = hits.Select(m =>
            $"- [{m.Scope}{(m.RepoSlug is not null ? $" {m.RepoSlug}" : "")}] "
            + $"{m.Title}: {Truncate(m.Content, MaxContentChars)}");
        return new BuiltinToolResult(
            $"{hits.Count} memória(s):\n" + string.Join("\n", lines),
            new { count = hits.Count });
    }

    /// <summary>Filtro de escopo do search: all/global/repo/(default=global+repo bound).</summary>
    private IQueryable<AgentMemory> ScopedMemories(
        string userId, string? scopeArg, string? repoSlug)
    {
        var scoped = db.AgentMemories.AsNoTracking()
            .Where(m => m.UserId == userId);
        return scopeArg switch
        {
            "all" => scoped,
            "global" => scoped.Where(m => m.Scope == "global"),
            "repo" => repoSlug is not null
                ? scoped.Where(m => m.Scope == "repo" && m.RepoSlug == repoSlug)
                : scoped.Where(m => false),
            _ => repoSlug is not null
                ? scoped.Where(m => m.Scope == "global"
                    || (m.Scope == "repo" && m.RepoSlug == repoSlug))
                : scoped.Where(m => m.Scope == "global"),
        };
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
