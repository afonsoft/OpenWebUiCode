using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Endpoints;
using OpenWebUI.Infrastructure.SystemPrompt;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Completions.Contributors;

/// <summary>
/// Contribuidores de system prompt (SPEC-20261021-harness-seams RF-B2):
/// cada bloco do antigo <c>systemParts</c> do EnrichRequestAsync virou um
/// contributor com <see cref="ISystemPromptContributor.Order"/> fixo —
/// a concatenação preserva a ordem histórica byte a byte (teste golden).
/// </summary>
public static class SystemPromptContributors
{
    /// <summary>
    /// Conjunto default na ordem histórica — usado por testes e por hosts
    /// que montam contributors manualmente (DI resolve via
    /// <see cref="IEnumerable{ISystemPromptContributor}"/>).
    /// </summary>
    public static IReadOnlyList<ISystemPromptContributor> Default(
        AppDbContext db,
        ConfigService config,
        RagService rag,
        WebSearchService webSearch,
        WorkspaceRepoService repos) =>
    [
        new BaseAgentContributor(),
        new AlwaysAllowContributor(db),
        new CustomModelContributor(db),
        new ModelSkillsContributor(db),
        new FileContextContributor(db, rag),
        new WebSearchContributor(webSearch),
        new MemoriesContributor(db, config),
        new RepoBindingContributor(),
        new AgentMemoryContributor(db, config),
        new MentionsContributor(),
        new AgentModeContributor(),
    ];
}

/// <summary>Bloco 0: prompt base do assistente (sempre presente).</summary>
public sealed class BaseAgentContributor : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx) =>
        Task.FromResult<IReadOnlyList<string>>([ChatPipeline.DefaultSystemPrompt]);
}

/// <summary>
/// Bloco 0.1: diretiva do preset always-allow
/// (SPEC-20261019-run-autonomy-continue RF-003) — sem ela o modelo encerra
/// o turno perguntando permissão mesmo com o gate auto-aprovado.
/// </summary>
public sealed class AlwaysAllowContributor(AppDbContext db) : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        if (ctx.ChatId is null
            || await db.Chats.AsNoTracking()
                .Where(c => c.Id == ctx.ChatId)
                .Select(c => c.ApprovalPreset)
                .FirstOrDefaultAsync(ctx.Ct) != "always-allow")
        {
            return [];
        }

        return
        [
            "User approvals are set to always-allow: tool permissions are "
                + "auto-granted — do not ask for confirmation before acting. "
                + "Exceptions: tools the owner explicitly marked "
                + "always-ask still require confirmation, and ask for inputs "
                + "only the user can provide (a decision, a secret, or a "
                + "genuinely destructive step).",
        ];
    }
}

/// <summary>
/// Bloco 1: modelo personalizado do workspace — redireciona para o base,
/// mescla params e injeta o system prompt custom. Seta
/// <see cref="PromptContext.CustomModel"/> para os contributors seguintes.
/// </summary>
public sealed class CustomModelContributor(AppDbContext db) : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 20;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        var customModel = await db.ModelEntries.AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.IsActive && (m.Id == ctx.Model || m.Name == ctx.Model)
                    && (m.UserId == ctx.User.Id || m.UserId == "public"), ctx.Ct);
        if (customModel is not null
            && !await ModelEndpoints.HasModelAccessAsync(
                ctx.User, customModel, db, ctx.Ct))
        {
            customModel = null;
        }

        ctx.CustomModel = customModel;
        if (customModel is null)
        {
            return [];
        }

        if (!string.IsNullOrWhiteSpace(customModel.BaseModelId))
        {
            ctx.Model = customModel.BaseModelId;
        }

        if (!string.IsNullOrWhiteSpace(customModel.ParamsJson))
        {
            try
            {
                var customParams = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    customModel.ParamsJson);
                if (customParams is not null)
                {
                    ctx.Parameters ??= [];
                    foreach (var (key, value) in customParams)
                    {
                        ctx.Parameters.TryAdd(key, value);
                    }
                }
            }
            catch (JsonException)
            {
                // Payload auxiliar malformado — segue sem ele.
            }
        }

        return string.IsNullOrWhiteSpace(customModel.SystemPrompt)
            ? []
            : [customModel.SystemPrompt];
    }
}

/// <summary>
/// Bloco 1.5: skills anexadas ao modelo custom (MetaJson.skill_ids) —
/// conteúdo das skills ativas vira seções de prompt.
/// </summary>
public sealed class ModelSkillsContributor(AppDbContext db) : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 30;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        if (ctx.CustomModel?.MetaJson is null)
        {
            return [];
        }

        try
        {
            using var meta = JsonDocument.Parse(ctx.CustomModel.MetaJson);
            if (!meta.RootElement.TryGetProperty("skill_ids", out var skillIds)
                || skillIds.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var ids = skillIds.EnumerateArray()
                .Select(e => e.GetString()).Where(s => s is not null).ToList();
            var contents = await db.Skills.AsNoTracking()
                .Where(s => ids.Contains(s.Id) && s.IsActive)
                .Select(s => s.Content).ToListAsync(ctx.Ct);
            return contents.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// Bloco 2: contexto de arquivos/referências (anexos + #arquivo/#coleção)
/// — top-K por similaridade com embeddings; fallback de texto integral.
/// </summary>
public sealed class FileContextContributor(AppDbContext db, RagService rag)
    : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 40;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        var referenced = await ChatPipeline.ResolveReferenceFileIdsAsync(
            ctx.LastUserText, ctx.User.Id, db, ctx.Ct);
        var scopedFileIds = (ctx.Request.FileIds ?? [])
            .Concat(referenced).Distinct().ToList();
        if (scopedFileIds.Count == 0)
        {
            return [];
        }

        var fileContext = ctx.LastUserText.Length > 0
            ? await rag.RetrieveAsync(ctx.User.Id, ctx.LastUserText, scopedFileIds, ctx.Ct)
            : null;
        fileContext ??= await FileEndpoints.BuildFileContextAsync(
            scopedFileIds, ctx.User.Id, db, ctx.Ct);
        return string.IsNullOrEmpty(fileContext) ? [] : [fileContext];
    }
}

/// <summary>Bloco 2.5: resultados de busca web como contexto com fontes.</summary>
public sealed class WebSearchContributor(WebSearchService webSearch)
    : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 50;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        if (ctx.Request.WebSearch != true || ctx.LastUserText.Length == 0)
        {
            return [];
        }

        try
        {
            var results = await webSearch.SearchAsync(ctx.LastUserText, count: 5, ctx.Ct);
            if (results is not { Count: > 0 })
            {
                return [];
            }

            var lines = results.Select((r, i) =>
                $"[{i + 1}] {r.Title}\nURL: {r.Url}\n{r.Snippet}");
            return
            [
                "Resultados da busca web (use-os para responder e cite as fontes como [n]):\n"
                    + string.Join("\n\n", lines),
            ];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }
}

/// <summary>Bloco 3: memórias persistentes do usuário (admin flag on).</summary>
public sealed class MemoriesContributor(AppDbContext db, ConfigService config)
    : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 60;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        var adminConfig = await config.GetAdminConfigAsync(ctx.Ct);
        if (!adminConfig.EnableMemories)
        {
            return [];
        }

        var memories = await db.Memories.AsNoTracking()
            .Where(m => m.UserId == ctx.User.Id)
            .Select(m => m.Content)
            .ToListAsync(ctx.Ct);
        return memories.Count == 0
            ? []
            : ["Memórias do usuário:\n" + string.Join("\n", memories.Select(m => $"- {m}"))];
    }
}

/// <summary>
/// Bloco 3.5: binding do workspace + instruções do projeto (AGENTS.md →
/// CLAUDE.md → .cursor/rules → README, cap 32KB) num bloco marcado.
/// </summary>
public sealed class RepoBindingContributor : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 70;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        var repoBinding = await ctx.GetRepoBindingAsync();
        if (repoBinding is null)
        {
            return [];
        }

        var parts = new List<string>
        {
            $"Repositório vinculado ao workspace: {repoBinding.Repo} " +
                $"(branch '{repoBinding.Branch}'). As tools de arquivo e shell " +
                "operam dentro do checkout desse repositório — a raiz do " +
                "workspace é a raiz do repo.",
        };

        var workdir = await ctx.GetWorkdirAsync();
        var instructions = SkillDiscoveryService.LoadProjectInstructions(workdir);
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            parts.Add(
                "<project_instructions>\n" + instructions + "\n</project_instructions>");
        }

        return parts;
    }
}

/// <summary>
/// Bloco 3.5a: memórias duráveis do agente (SPEC-20261010-agent-memory +
/// memsync em camadas) — 12 mais recentes, globais + do repo bound, cap
/// ~1800 chars, em bloco <c>&lt;agent_memory&gt;</c> com ressalva
/// não-autoritativa.
/// </summary>
public sealed class AgentMemoryContributor(AppDbContext db, ConfigService config)
    : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 80;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        var memAdminOff = await config.GetAsync<bool?>(
            "memsync.enabled", null, ctx.Ct) == false;
        var memEnabled = !memAdminOff
            && (await config.GetAsync<bool?>(
                    $"u:{ctx.User.Id}:memsync.enabled", null, ctx.Ct)
                ?? await config.GetAsync("memsync.enabled", true, ctx.Ct));
        if (!memEnabled)
        {
            return [];
        }

        var repoBinding = await ctx.GetRepoBindingAsync();
        var memories = await db.AgentMemories.AsNoTracking()
            .Where(m => m.UserId == ctx.User.Id
                && (m.Scope == "global"
                    || (repoBinding != null && m.RepoSlug == repoBinding.Repo)))
            .OrderByDescending(m => m.UpdatedAt)
            .Take(12)
            .Select(m => new { m.Title, m.Scope, m.RepoSlug, m.Content })
            .ToListAsync(ctx.Ct);
        if (memories.Count == 0)
        {
            return [];
        }

        var lines = memories.Select(m =>
            $"- [{m.Scope}{(m.RepoSlug is not null ? $" {m.RepoSlug}" : "")}] "
            + $"{m.Title}: {m.Content}");
        var block = "<agent_memory>\n(memórias derivadas de sessões "
            + "anteriores — heurísticas, podem estar desatualizadas)\n"
            + string.Join("\n", lines) + "\n</agent_memory>";
        return [block.Length <= 1800 ? block : block[..1800] + "\n…\n</agent_memory>"];
    }
}

/// <summary>
/// Bloco 3.5b: menções @path do composer — cada chip vira
/// <c>&lt;file path="…"&gt;</c> (cap 16KB/arquivo, 64KB total; fora do
/// jail é descartado em silêncio).
/// </summary>
public sealed class MentionsContributor : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 90;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        if (ctx.Request.MentionPaths is not { Count: > 0 } mentionPaths)
        {
            return [];
        }

        var mentionWorkdir = await ctx.GetWorkdirAsync();
        var mentions = await WorkspaceMentionContext.BuildAsync(
            mentionWorkdir, mentionPaths, ctx.Ct);
        return string.IsNullOrEmpty(mentions) ? [] : [mentions];
    }
}

/// <summary>
/// Bloco 3.6: diretiva do modo do agente — plan (somente leitura +
/// plan_exit, com template SDD) ou chat (sem workspace); build não emite
/// bloco.
/// </summary>
public sealed class AgentModeContributor : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        if (PermissionRuleset.Normalize(ctx.AgentMode) == PermissionRuleset.PlanMode)
        {
            return Task.FromResult<IReadOnlyList<string>>(
            [
                "Modo PLAN ativo: você está em modo de planejamento somente-leitura. "
                    + "NÃO crie, modifique ou apague arquivos, NÃO execute comandos que "
                    + "alterem o sistema e NÃO chame tools de escrita — apenas leia, "
                    + "pesquise e analise. "
                    + PlanSpecTemplate.Prompt
                    + " O usuário decide se executa (modo build) — nunca comece a "
                    + "implementar sem aprovação (Status = Approved).",
            ]);
        }

        if (PermissionRuleset.Normalize(ctx.AgentMode) == PermissionRuleset.ChatMode)
        {
            return Task.FromResult<IReadOnlyList<string>>(
            [
                "Modo CHAT ativo: conversa comum sem workspace — NÃO existem "
                    + "tools de arquivo, shell, jobs ou worktree nesta conversa e "
                    + "nenhuma call desse tipo será aceita. Responda com texto "
                    + "(web_search/fetch_url seguem disponíveis se anunciadas). "
                    + "Para trabalhar num repositório o usuário precisa vincular "
                    + "um repo ao chat e mudar para o modo build.",
            ]);
        }

        return Task.FromResult<IReadOnlyList<string>>([]);
    }
}
