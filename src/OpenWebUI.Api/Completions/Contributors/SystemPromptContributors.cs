using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        WorkspaceRepoService repos,
        AgentDefinitionService agents,
        IConfiguration configuration,
        InstinctService? instincts = null) =>
    [
        new BaseAgentContributor(),
        new AlwaysAllowContributor(db),
        new SddWorkflowContributor(configuration),
        new CustomModelContributor(db),
        new ModelSkillsContributor(db),
        new FileContextContributor(db, rag),
        new WebSearchContributor(webSearch),
        new MemoriesContributor(db, config),
        new RepoBindingContributor(),
        new AgentMemoryContributor(db, config),
        .. (IEnumerable<ISystemPromptContributor>)(instincts is null
            ? []
            : [new InstinctContributor(instincts, config)]),
        new DelegationPolicyContributor(agents, db),
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
/// Bloco 0.5: ciclo de vida SDD (SPEC-20261023-sdd-orchestrator-prompts
/// cap. B) — emitido quando o workdir contém <c>.specs/</c> ou
/// <c>docs/specs/</c> (detecção <c>auto</c>), ou sempre com
/// <c>Agent:Sdd=on</c>; <c>off</c> desliga. Ensina convenção SPEC/RF/AC,
/// modo entrevista e modo execução TDD.
/// </summary>
public sealed class SddWorkflowContributor(IConfiguration cfg)
    : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 15;

    private const string Block =
        "Spec-driven development (SDD) applies in this workspace: "
            + ".specs/SPEC-*.md files are the source of truth for planned "
            + "work — requirements RF-###, acceptance criteria AC-##. "
            + "Lifecycle: Draft → Approved → In implementation → Completed; "
            + "completed SPECs archive under docs/specs/ and the Ticket "
            + "field links a GitHub issue whose label mirrors progress. "
            + "Manage SPECs via builtin_specs (list/show/transition). Two "
            + "modes: (1) Spec-writing — when the request is ambiguous or "
            + "lacks a SPEC, interview the user in rounds (design-tree "
            + "frontier questions), never guess; mark undecided points "
            + "[A DEFINIR] and do not implement — deliver the SPEC. "
            + "(2) Execution — when a SPEC is Approved: implement in TDD "
            + "slices (red→green→refactor), tag each test with the "
            + "requirement it covers (\"# Covers RF-001\"), revalidate the "
            + "suite per slice, never pause for permission between slices "
            + "of an approved SPEC, and keep SPEC/issue status in sync.";

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        var mode = cfg["Agent:Sdd"] ?? "auto";
        if (mode.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
        if (mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var workdir = await ctx.GetWorkdirAsync();
            if (!Path.IsPathRooted(workdir) || !HasSpecSurface(workdir))
            {
                return [];
            }
        }
        return [Block];
    }

    /// <summary>Superfície SDD: <c>.specs/</c> com ao menos uma
    /// <c>SPEC-*.md</c>, ou <c>docs/specs/</c> com arquivos arquivados.</summary>
    private static bool HasSpecSurface(string workdir) =>
        ContainsSpecFile(Path.Combine(workdir, ".specs"))
            || ContainsSpecFile(Path.Combine(workdir, "docs", "specs"));

    private static bool ContainsSpecFile(string dir) =>
        Directory.Exists(dir)
            && Directory.EnumerateFiles(dir, "SPEC-*.md",
                SearchOption.TopDirectoryOnly).Any();
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
        // SPEC-20261018-model-variants RF-001: "model#variant" → resolve
        // o modelo base e aplica a variant (settings > params do modelo).
        var (baseRef, variantId) = ModelVariantService.Split(ctx.Model);
        var customModel = await FindModelAsync(ctx, baseRef)
            ?? (variantId is null ? null : await FindModelAsync(ctx, ctx.Model));
        customModel ??= await FindByAliasAsync(ctx, baseRef);
        if (customModel is not null
            && !await ModelEndpoints.HasModelAccessAsync(
                ctx.User, customModel, db, ctx.Ct))
        {
            customModel = null;
        }

        ctx.CustomModel = customModel;
        if (customModel is null)
        {
            // Modelo provider com "#variant" não declarada → o sufixo não
            // é um id de provider válido; envia o base (variant ignorada).
            if (variantId is not null)
            {
                ctx.Model = baseRef;
            }
            return [];
        }

        if (!string.IsNullOrWhiteSpace(customModel.BaseModelId))
        {
            ctx.Model = customModel.BaseModelId;
        }
        else if (variantId is not null)
        {
            // Sem BaseModelId o id servido é o do próprio entry — tira o
            // sufixo #variant antes de enviar ao provider.
            ctx.Model = customModel.Id;
        }

        if (variantId is not null)
        {
            var variant = ModelVariantService.Resolve(
                customModel.MetaJson, variantId);
            if (variant is null)
            {
                throw new ModelVariantException(
                    $"unknown variant '{variantId}' para o modelo '{baseRef}'.");
            }
            ApplyVariant(ctx, variant);
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

    /// <summary>Lookup direto por id/nome visível ao usuário.</summary>
    private async Task<ModelEntry?> FindModelAsync(PromptContext ctx, string modelRef) =>
        await db.ModelEntries.AsNoTracking().FirstOrDefaultAsync(
            m => m.IsActive && (m.Id == modelRef || m.Name == modelRef)
                && (m.UserId == ctx.User.Id || m.UserId == "public"), ctx.Ct);

    /// <summary>RF-004: alias em MetaJson.aliases[] → entry real.</summary>
    private async Task<ModelEntry?> FindByAliasAsync(PromptContext ctx, string modelRef)
    {
        var candidates = await db.ModelEntries.AsNoTracking()
            .Where(m => m.IsActive && m.MetaJson != null
                && (m.UserId == ctx.User.Id || m.UserId == "public"))
            .ToListAsync(ctx.Ct);
        return candidates.FirstOrDefault(
            m => ModelVariantService.MatchesAlias(m.MetaJson, modelRef));
    }

    /// <summary>Settings da variant vencem params do modelo (merge profundo).</summary>
    private static void ApplyVariant(PromptContext ctx, ModelVariant variant)
    {
        if (variant.Settings is not null)
        {
            ctx.Parameters ??= [];
            foreach (var (key, value) in variant.Settings)
            {
                // TryAdd: params do request/persona já semeados vencem a
                // variant; params do modelo (TryAdd abaixo) perdem para ela —
                // ordem provider < model < variant < persona/command (RF-001).
                ctx.Parameters.TryAdd(key, value is null
                    ? null!
                    : JsonSerializer.Deserialize<JsonElement>(
                        value.ToJsonString()));
            }
        }
        if (variant.Headers is { Count: > 0 } headers)
        {
            ctx.ExtraHeaders = headers.ToDictionary(
                kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        }
        if (variant.Body is not null)
        {
            ctx.ExtraBody = (JsonObject)variant.Body.DeepClone();
        }
        ctx.CacheFormat = variant.CacheFormat;
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
        // attachments-v2: FileIds do request viram blocos <file> na
        // mensagem do usuário (ChatPipeline §4.6) — o contributor fica só
        // com as referências "#nome" resolvidas via RAG.
        var scopedFileIds = referenced.Distinct().ToList();
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
/// Bloco 3.5c: política de delegação (SPEC-20261023 cap. C) — emitida
/// quando há agentes declarativos no catálogo ou o chat pertence a um
/// team; orienta "delegue ao especialista" (leader fatia/revisa, members
/// executam).
/// </summary>
public sealed class DelegationPolicyContributor(
    AgentDefinitionService agents, AppDbContext db)
    : ISystemPromptContributor
{
    /// <inheritdoc />
    public int Order => 85;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx)
    {
        var workdir = await ctx.GetWorkdirAsync();
        var catalog = await agents.ListAsync(
            ctx.User.Id, workdir.Length > 0 ? workdir : null, ctx.Ct);
        var inTeam = ctx.ChatId is not null
            && await db.Chats.AsNoTracking()
                .Where(c => c.Id == ctx.ChatId)
                .Select(c => c.TeamId)
                .FirstOrDefaultAsync(ctx.Ct) is not null;
        if (catalog.Count == 0 && !inTeam)
        {
            return [];
        }

        return
        [
            "Specialist delegation: for multi-slice or specialized work "
                + "prefer builtin_delegate_task with an agent or persona "
                + "matching the task type — query builtin_agent_catalog "
                + "before choosing. As leader you plan, slice, assign and "
                + "review; members execute. Use team:true + resume_chat_id "
                + "for continuity across runs.",
        ];
    }
}

/// <summary>
/// Bloco 3.5b: menções @path do composer — cada chip vira
/// <c>&lt;file path="…"&gt;</c> (cap 16KB/arquivo, 64KB total; fora do
/// jail é descartado em silêncio). Chips <c>ref:alias</c> anexam a
/// referência nomeada: descrição + listing (cap 2000) viram bloco
/// <c>&lt;reference&gt;</c> e o alias entra em
/// <see cref="PromptContext.AttachedRefs"/> (RF-002/RF-003).
/// </summary>
public sealed class MentionsContributor(ReferenceService? refs = null)
    : ISystemPromptContributor
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

        var filePaths = mentionPaths
            .Where(p => !ReferenceService.TrySplitRef(p, out _, out _))
            .ToList();
        var sections = new List<string>();

        if (refs is not null)
        {
            foreach (var raw in mentionPaths)
            {
                if (await TryAttachRefAsync(raw, ctx) is { } block)
                {
                    sections.Add(block);
                }
            }
        }

        if (filePaths.Count > 0)
        {
            var mentionWorkdir = await ctx.GetWorkdirAsync();
            var mentions = await WorkspaceMentionContext.BuildAsync(
                mentionWorkdir, filePaths, ctx.Ct);
            if (!string.IsNullOrEmpty(mentions))
            {
                sections.Add(mentions);
            }
        }

        return sections;
    }

    /// <summary>
    /// Menção <c>ref:alias</c> → bloco <c>&lt;reference&gt;</c> com
    /// descrição + listing capado; marca o alias em
    /// <see cref="PromptContext.AttachedRefs"/>. Null quando a menção
    /// não é ref ou o alias não resolve (drop silencioso).
    /// </summary>
    private async Task<string?> TryAttachRefAsync(string raw, PromptContext ctx)
    {
        if (!ReferenceService.TrySplitRef(raw, out var alias, out _))
        {
            return null;
        }
        var resolved = await refs!.ResolveAsync(ctx.User.Id, alias, ctx.Ct);
        if (resolved is null)
        {
            return null; // alias inexistente/inacessível — drop silencioso
        }

        ctx.AttachedRefs.Add(alias);
        var tree = await ReferenceService.ListTreeAsync(resolved, ctx.Ct);
        var block = new StringBuilder($"<reference alias=\"{alias}\">");
        if (!string.IsNullOrWhiteSpace(resolved.Entry.Description))
        {
            block.Append('\n').Append(resolved.Entry.Description.Trim());
        }
        if (resolved.Warning is not null)
        {
            block.Append("\n[aviso] ").Append(resolved.Warning);
        }
        block.Append("\nLeitura via ref://").Append(alias)
            .Append("/caminho (somente-leitura). Arquivos:\n");
        foreach (var p in tree)
        {
            block.Append("- ").Append(p).Append('\n');
        }
        if (tree.Count >= ReferenceService.MaxListingPaths)
        {
            block.Append($"[… truncado em {ReferenceService.MaxListingPaths} paths]");
        }
        return block.Append("</reference>").ToString();
    }
}

/// <summary>
/// Bloco 3.6: diretiva do modo do agente — plan (somente leitura +
/// plan_exit, com template SDD) ou chat (workspace scratch da conversa);
/// build não emite bloco.
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
                // SPEC-20261027-scratch-workspace: a conversa tem pasta
                // própria (workspaces/{u}/scratch/{chat}) — tools de
                // arquivo/shell existem e passam pelo preset de aprovação.
                "Modo CHAT ativo: a conversa tem uma pasta de trabalho scratch "
                    + "própria — tools de arquivo e shell estão disponíveis "
                    + "sob aprovação do usuário e escrevem nela. Para trabalhar "
                    + "num repositório versionado o usuário pode vincular um "
                    + "repo ao chat e mudar para o modo build.",
            ]);
        }

        return Task.FromResult<IReadOnlyList<string>>([]);
    }
}
