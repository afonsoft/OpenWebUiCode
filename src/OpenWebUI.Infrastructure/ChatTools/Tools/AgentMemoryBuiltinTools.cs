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
    public string ParametersJson => $$"""
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
              "enum": ["{{MemoryToolScopes.Global}}", "repo"],
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
                && m.Scope == (repoScope ? "repo" : MemoryToolScopes.Global)
                && m.RepoSlug == repoSlug
                && m.Title == title, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (existing is null)
        {
            existing = new AgentMemory
            {
                UserId = context.UserId,
                Scope = repoScope ? "repo" : MemoryToolScopes.Global,
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
    public string ParametersJson => $$"""
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "Substring to match in title or content (empty/omitted lists recent memories)."
            },
            "scope": {
              "type": "string",
              "enum": ["default", "{{MemoryToolScopes.Global}}", "repo", "all"],
              "description": "'default': global + current chat's repo. 'all': every memory you own."
            }
          },
          "required": []
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;
    /// <inheritdoc />
    public bool SafeInParallel => true;


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
            MemoryToolScopes.Global => scoped.Where(m => m.Scope == MemoryToolScopes.Global),
            "repo" => repoSlug is not null
                ? scoped.Where(m => m.Scope == "repo" && m.RepoSlug == repoSlug)
                : scoped.Where(m => false),
            _ => repoSlug is not null
                ? scoped.Where(m => m.Scope == MemoryToolScopes.Global
                    || (m.Scope == "repo" && m.RepoSlug == repoSlug))
                : scoped.Where(m => m.Scope == MemoryToolScopes.Global),
        };
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

/// <summary>
/// <c>builtin:memory_learn</c> — lição durável estruturada (SPEC-20261014
/// memory-pipeline RF-003): o modelo ensina sem esperar a consolidação em
/// background. Dedup determinístico — mesma <c>lesson</c> no mesmo
/// <c>context</c> atualiza a entrada <c>learn:</c> existente em vez de
/// duplicar. Tags livres ajudam o <c>memory_search</c>.
/// </summary>
public sealed class MemoryLearnBuiltinTool(AppDbContext db) : IBuiltinChatTool
{
    /// <summary>Cap de caracteres da lição.</summary>
    private const int MaxLessonChars = 2000;

    /// <summary>Cap de caracteres do contexto.</summary>
    private const int MaxContextChars = 300;

    /// <summary>Máximo de tags aceitas.</summary>
    private const int MaxTags = 8;

    /// <summary>Cap de caracteres de cada tag.</summary>
    private const int MaxTagChars = 50;

    /// <inheritdoc />
    public string Name => "memory_learn";

    /// <inheritdoc />
    public string Description =>
        "Record a durable lesson — structured knowledge worth keeping "
            + "across sessions (e.g. 'integration tests need Docker "
            + "running'). Re-learning the same lesson in the same context "
            + "updates it. Memories surface in future system prompts.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "lesson": {
              "type": "string",
              "description": "The lesson to remember (concise, one fact)."
            },
            "context": {
              "type": "string",
              "description": "Where the lesson applies (e.g. 'deploy', 'repo:owner/name'). Optional."
            },
            "tags": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Optional free-form tags for memory_search."
            }
          },
          "required": ["lesson"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Write;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var lesson = ReadStringField(args, "lesson", MaxLessonChars);
        if (string.IsNullOrWhiteSpace(lesson))
        {
            return new BuiltinToolResult("Parâmetro 'lesson' é obrigatório.");
        }
        var ctx = ReadStringField(args, "context", MaxContextChars)?.Trim();
        var tags = ReadTags(args);

        // Título determinístico: mesmo (contexto, lesson) → mesma chave →
        // update em vez de duplicar (RF-003, similaridade simples).
        var ctxSlug = Slug(ctx ?? "general");
        var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(
                        ctxSlug + "\n" + lesson.Trim())))
            .ToLowerInvariant()[..8];
        var title = $"learn:{ctxSlug}:{hash}";

        var content = lesson.Trim();
        if (tags.Count > 0)
        {
            content += "\n\ntags: " + string.Join(", ", tags);
        }

        var existing = await db.AgentMemories
            .FirstOrDefaultAsync(m => m.UserId == context.UserId
                && m.Scope == MemoryToolScopes.Global && m.Title == title, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var updated = existing is not null;
        if (existing is null)
        {
            existing = new AgentMemory
            {
                UserId = context.UserId,
                Scope = MemoryToolScopes.Global,
                Title = title,
                CreatedAt = now,
            };
            db.AgentMemories.Add(existing);
        }
        existing.Content = content;
        existing.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        return new BuiltinToolResult(
            (updated ? "Lição atualizada: " : "Lição registrada: ")
                + $"'{title}'"
                + (tags.Count > 0 ? $" [tags: {string.Join(", ", tags)}]" : string.Empty),
            new { memoryId = existing.Id, title, updated });
    }

    private static string? ReadStringField(JsonElement args, string field, int max)
    {
        if (!args.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.String
            || el.GetString() is not { } s)
        {
            return null;
        }
        return s.Length > max ? s[..max] : s;
    }

    private static List<string> ReadTags(JsonElement args)
    {
        var tags = new List<string>();
        if (args.TryGetProperty("tags", out var el)
            && el.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in el.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.String
                    && t.GetString() is { Length: > 0 } s)
                {
                    tags.Add(s.Length > MaxTagChars ? s[..MaxTagChars] : s);
                    if (tags.Count == MaxTags)
                    {
                        break;
                    }
                }
            }
        }
        return tags;
    }

    private static string Slug(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        }
        var slug = sb.ToString().Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }
        return slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }
}

/// <summary>Escopos das memórias de agente (global = todo chat do usuário).</summary>
file static class MemoryToolScopes
{
    internal const string Global = "global";
}
