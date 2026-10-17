using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Api.Endpoints;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Completions;

/// <summary>
/// Pipeline compartilhado de completions: enriquecimento do request
/// (modelo custom, arquivos, web search, memórias, filtros), loop de tools
/// e roteamento arena/pipeline. Extraído de <see cref="ApiEndpoints"/> para
/// ser reusado pelo executor de runs desacopladas
/// (SPEC-20261007-chat-detached-runs).
/// </summary>
public static class ChatPipeline
{
    /// <summary>Prompt base injetado em todo request — postura de agente
    /// autônomo (pode instalar ferramentas, age sem pedir confirmação a
    /// cada passo), com disciplina obrigatória de tracking de tarefas
    /// (builtin_todo_write) e de memória durável (builtin_memory_save/
    /// builtin_memory_search); o system prompt do modelo custom e os
    /// demais blocos vêm depois e refinam.</summary>
    private const string DefaultSystemPrompt =
        "You are the Open WebUI agent — an autonomous agent that executes tasks, "
        + "not a passive assistant. Always answer in the user's language. "
        + "Before answering, analyze the user's intent and the conversation context; "
        + "structure the response clearly (sections and lists when they help), explain "
        + "reasoning and trade-offs in technical decisions, and give concrete examples. "
        + "You may use the available tools and install tools, packages and "
        + "dependencies the task needs — when an install requires user approval, "
        + "propose it via ask_user. "
        + "For every task that is more than a one-shot answer, ALWAYS manage a "
        + "task list via builtin_todo_write: break the work into concrete steps "
        + "up front, keep exactly one item in_progress, mark each item completed "
        + "as soon as it finishes, and rewrite the list whenever the plan "
        + "changes. "
        + "ALWAYS persist durable knowledge via builtin_memory_save — decisions "
        + "made, repo conventions, gotchas, user preferences and facts that must "
        + "survive the session (never secrets or credentials); re-saving the "
        + "same title updates it. When prior context may exist, recall it first "
        + "via builtin_memory_search. "
        + "Act autonomously and carry the task end-to-end; only ask the user "
        + "when you genuinely need something from them "
        + "(a decision, a secret or an approval).";
    /// <summary>Aplica modelo personalizado, contexto de arquivos e memórias à requisição.</summary>
    public static async Task<ChatCompletionRequest> EnrichRequestAsync(
        ChatCompletionRequest request,
        User user,
        AppDbContext db,
        ConfigService config,
        RagService rag,
        WebSearchService webSearch,
        WorkspaceRepoService repos,
        CancellationToken ct,
        string? agentMode = null,
        string? chatId = null,
        Microsoft.Extensions.Logging.ILogger? log = null)
    {
        var model = request.Model;
        var messages = request.Messages.ToList();
        var parameters = request.Params?.ToDictionary(kv => kv.Key, kv => kv.Value);
        var systemParts = new List<string>();

        // 0. Prompt base do assistente: injetado sempre, antes do system
        // prompt do modelo custom e demais blocos de contexto.
        systemParts.Add(DefaultSystemPrompt);

        // 1. Modelo personalizado do workspace → redireciona para o modelo base e aplica config.
        var customModel = await db.ModelEntries.AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.IsActive && (m.Id == model || m.Name == model)
                    && (m.UserId == user.Id || m.UserId == "public"), ct);
        if (customModel is not null
            && !await ModelEndpoints.HasModelAccessAsync(user, customModel, db, ct))
        {
            customModel = null;
        }
        if (customModel is not null)
        {
            if (!string.IsNullOrWhiteSpace(customModel.BaseModelId))
            {
                model = customModel.BaseModelId;
            }
            if (!string.IsNullOrWhiteSpace(customModel.SystemPrompt))
            {
                systemParts.Add(customModel.SystemPrompt);
            }

            if (!string.IsNullOrWhiteSpace(customModel.ParamsJson))
            {
                try
                {
                    var customParams = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        customModel.ParamsJson);
                    if (customParams is not null)
                    {
                        parameters ??= [];
                        foreach (var (key, value) in customParams)
                        {
                            parameters.TryAdd(key, value);
                        }
                    }
                }
                catch (JsonException)
                {
                    // Payload auxiliar malformado — segue sem ele.
                }
            }
        }

        // 1.5. Skills anexadas ao modelo custom (MetaJson.skill_ids) → system prompt.
        if (customModel?.MetaJson is not null)
        {
            try
            {
                using var meta = JsonDocument.Parse(customModel.MetaJson);
                if (meta.RootElement.TryGetProperty("skill_ids", out var skillIds)
                    && skillIds.ValueKind == JsonValueKind.Array)
                {
                    var ids = skillIds.EnumerateArray()
                        .Select(e => e.GetString()).Where(s => s is not null).ToList();
                    var contents = await db.Skills.AsNoTracking()
                        .Where(s => ids.Contains(s.Id) && s.IsActive)
                        .Select(s => s.Content).ToListAsync(ct);
                    systemParts.AddRange(contents.Where(c => !string.IsNullOrWhiteSpace(c)));
                }
            }
            catch (JsonException)
            {
                // Payload auxiliar malformado — segue sem ele.
            }
        }

        // 2. Contexto de arquivos/referências: anexos + #arquivo/#coleção.
        //    Com embeddings disponíveis injeta os top-K chunks por similaridade;
        //    sem provider cai no fallback de texto integral atual.
        var lastUserText = messages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
        var referenced = await ResolveReferenceFileIdsAsync(lastUserText, user.Id, db, ct);
        var scopedFileIds = (request.FileIds ?? [])
            .Concat(referenced).Distinct().ToList();
        if (scopedFileIds.Count > 0)
        {
            var fileContext = lastUserText.Length > 0
                ? await rag.RetrieveAsync(user.Id, lastUserText, scopedFileIds, ct)
                : null;
            fileContext ??= await FileEndpoints.BuildFileContextAsync(
                scopedFileIds, user.Id, db, ct);
            if (!string.IsNullOrEmpty(fileContext))
            {
                systemParts.Add(fileContext);
            }
        }

        // 2.5. Busca web opcional: injeta os resultados como contexto com fontes.
        if (request.WebSearch == true && lastUserText.Length > 0)
        {
            try
            {
                var results = await webSearch.SearchAsync(lastUserText, count: 5, ct);
                if (results is { Count: > 0 })
                {
                    var lines = results.Select((r, i) =>
                        $"[{i + 1}] {r.Title}\nURL: {r.Url}\n{r.Snippet}");
                    systemParts.Add(
                        "Resultados da busca web (use-os para responder e cite as fontes como [n]):\n"
                        + string.Join("\n\n", lines));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Engine indisponível: segue sem contexto web.
            }
        }

        // 3. Memórias persistentes do usuário.
        var adminConfig = await config.GetAdminConfigAsync(ct);
        if (adminConfig.EnableMemories)
        {
            var memories = await db.Memories.AsNoTracking()
                .Where(m => m.UserId == user.Id)
                .Select(m => m.Content)
                .ToListAsync(ct);
            if (memories.Count > 0)
            {
                systemParts.Add(
                    "Memórias do usuário:\n" + string.Join("\n", memories.Select(m => $"- {m}")));
            }
        }

        // 3.5. Repositório vinculado ao workspace (SPEC-20261008-github-repo-workspace):
        // o modelo precisa saber que as tools file_*/shell_exec operam dentro
        // do checkout do repo vinculado (a raiz do workspace vira a raiz do repo).
        // Binding por chat precede o global do usuário
        // (SPEC-20261010-chat-repo-binding).
        var (repoBinding, _) = await repos.ResolveBindingAsync(user.Id, chatId, ct);
        if (repoBinding is not null)
        {
            systemParts.Add(
                $"Repositório vinculado ao workspace: {repoBinding.Repo} " +
                $"(branch '{repoBinding.Branch}'). As tools de arquivo e shell " +
                "operam dentro do checkout desse repositório — a raiz do " +
                "workspace é a raiz do repo.");

            // SPEC-20261009-repo-skills-slash-commands RF-005: instruções do
            // projeto (AGENTS.md → CLAUDE.md → .cursor/rules/*.md → README.md,
            // cap 32KB) entram como bloco marcado no system prompt.
            var workdir = await repos.ResolveWorkdirAsync(user.Id, chatId, ct);
            var instructions = SkillDiscoveryService.LoadProjectInstructions(workdir);
            if (!string.IsNullOrWhiteSpace(instructions))
            {
                systemParts.Add(
                    "<project_instructions>\n" + instructions + "\n</project_instructions>");
            }
        }

        // 3.5a. Memórias duráveis do agente (SPEC-20261010-agent-memory):
        // globais + as do repo bound deste chat — as 12 mais recentes, cap
        // ~1800 chars, num bloco marcado. O agente grava/edita via
        // builtin_memory_save/builtin_memory_learn; a busca completa é
        // builtin_memory_search.
        // SPEC-20261014-memory-pipeline RF-004: o mesmo kv em camadas do
        // auto-sync controla a injeção — off é silenciador (não purge).
        // Leitura em camadas manual: `u:` ausente (ou override removido —
        // ValueJson="null" desserializa como null em bool?) herda o global;
        // global unset assume o default true. `memAdminOff` mantém a
        // negativa absoluta do admin mesmo com opt-in do usuário.
        var memAdminOff = await config.GetAsync<bool?>(
            "memsync.enabled", null, ct) == false;
        var memEnabled = !memAdminOff
            && (await config.GetAsync<bool?>(
                    $"u:{user.Id}:memsync.enabled", null, ct)
                ?? await config.GetAsync("memsync.enabled", true, ct));
        if (memEnabled)
        {
            var memories = await db.AgentMemories.AsNoTracking()
                .Where(m => m.UserId == user.Id
                    && (m.Scope == "global"
                        || (repoBinding != null && m.RepoSlug == repoBinding.Repo)))
                .OrderByDescending(m => m.UpdatedAt)
                .Take(12)
                .Select(m => new { m.Title, m.Scope, m.RepoSlug, m.Content })
                .ToListAsync(ct);
            if (memories.Count > 0)
            {
                var lines = memories.Select(m =>
                    $"- [{m.Scope}{(m.RepoSlug is not null ? $" {m.RepoSlug}" : "")}] "
                    + $"{m.Title}: {m.Content}");
                // SPEC-20261014-memory-pipeline RF-002: ressalva
                // não-autoritativa — memória é heurística e pode estar
                // desatualizada; o modelo deve validar antes de confiar.
                var block = "<agent_memory>\n(memórias derivadas de sessões "
                    + "anteriores — heurísticas, podem estar desatualizadas)\n"
                    + string.Join("\n", lines) + "\n</agent_memory>";
                systemParts.Add(block.Length <= 1800
                    ? block
                    : block[..1800] + "\n…\n</agent_memory>");
            }
        }

        // 3.5b. Menções @path do composer (SPEC-20261009-ide-mentions-tests
        // RF-002): cada chip vira um bloco <file path="…"> no prompt — cap
        // 16KB/arquivo, 64KB total; fora do jail é descartado em silêncio.
        if (request.MentionPaths is { Count: > 0 } mentionPaths)
        {
            var mentionWorkdir = await repos.ResolveWorkdirAsync(user.Id, chatId, ct);
            var mentions = await OpenWebUI.Infrastructure.ChatTools.WorkspaceMentionContext
                .BuildAsync(mentionWorkdir, mentionPaths, ct);
            if (!string.IsNullOrEmpty(mentions))
            {
                systemParts.Add(mentions);
            }
        }

        // 3.6. Modo plan do agente (SPEC-20261009-agent-modes-plan-build
        // RF-002/RF-004): instrução de somente-leitura + encerramento via
        // plan_exit — o spec de tools anunciado já reflete o ruleset, mas
        // o prompt reforça o contrato para modelos teimosos.
        if (PermissionRuleset.Normalize(agentMode) == PermissionRuleset.PlanMode)
        {
            systemParts.Add(
                "Modo PLAN ativo: você está em modo de planejamento somente-leitura. "
                + "NÃO crie, modifique ou apague arquivos, NÃO execute comandos que "
                + "alterem o sistema e NÃO chame tools de escrita — apenas leia, "
                + "pesquise e analise. "
                // SPEC-20261015-plan-spec-sdd RF-001: a análise do requisito e
                // a estrutura SDD são contrato — plan_exit recusa plano fora dele.
                + OpenWebUI.Infrastructure.ChatTools.PlanSpecTemplate.Prompt
                + " O usuário decide se executa (modo build) — nunca comece a "
                + "implementar sem aprovação (Status = Approved).");
        }
        else if (PermissionRuleset.Normalize(agentMode) == PermissionRuleset.ChatMode)
        {
            // SPEC-15: chat sem workspace — o spec de tools já vem filtrado,
            // mas o prompt deixa claro que não há acesso a arquivos/shell.
            systemParts.Add(
                "Modo CHAT ativo: conversa comum sem workspace — NÃO existem "
                + "tools de arquivo, shell, jobs ou worktree nesta conversa e "
                + "nenhuma call desse tipo será aceita. Responda com texto "
                + "(web_search/fetch_url seguem disponíveis se anunciadas). "
                + "Para trabalhar num repositório o usuário precisa vincular "
                + "um repo ao chat e mudar para o modo build.");
        }

        // 4. Mescla partes de sistema numa única mensagem inicial.
        if (systemParts.Count > 0)
        {
            var merged = string.Join("\n\n", systemParts);
            var existing = messages.FindIndex(m => m.Role == "system");
            if (existing >= 0)
            {
                messages[existing] = messages[existing] with { Content = $"{merged}\n\n{messages[existing].Content}" };
            }
            else
            {
                messages.Insert(0, new ChatCompletionMessage("system", merged));
            }
        }

        // 4.6. Mídia anexada → conteúdo multimodal (SPEC-20261013-chat-media-
        // attachments RF-006): imagens dos anexos entram na mensagem dona —
        // FileIds por mensagem (do FilesJson persistido) + FileIds do
        // request (última msg do usuário). Só image/* e só arquivos do dono;
        // arquivo apagado/não legível é omitido (não derruba a run).
        var imageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in messages)
        {
            if (m.FileIds is { } mids)
            {
                imageIds.UnionWith(mids);
            }
        }
        if (request.FileIds is { } topFileIds)
        {
            imageIds.UnionWith(topFileIds);
        }
        if (imageIds.Count > 0)
        {
            var entries = await db.Files.AsNoTracking()
                .Where(f => imageIds.Contains(f.Id) && f.UserId == user.Id
                    && f.ContentType != null && f.ContentType.StartsWith("image/"))
                .ToListAsync(ct);
            if (entries.Count > 0)
            {
                var byId = entries.ToDictionary(f => f.Id);
                var lastUserIdx = messages.FindLastIndex(m => m.Role == "user");
                for (var i = 0; i < messages.Count; i++)
                {
                    var ids = messages[i].FileIds?.ToList() ?? [];
                    if (i == lastUserIdx && request.FileIds is not null)
                    {
                        ids.AddRange(request.FileIds);
                    }
                    if (ids.Count == 0)
                    {
                        continue;
                    }

                    var images = new List<ChatImageData>();
                    foreach (var fid in ids.Distinct())
                    {
                        if (!byId.TryGetValue(fid, out var entry)
                            || !File.Exists(entry.StoragePath))
                        {
                            continue;
                        }
                        try
                        {
                            var bytes = await File.ReadAllBytesAsync(entry.StoragePath, ct);
                            images.Add(new ChatImageData(
                                entry.ContentType!, Convert.ToBase64String(bytes)));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            log?.LogDebug(
                                "Anexo {FileId} ilegível — imagem omitida da mensagem.", fid);
                        }
                    }
                    if (images.Count > 0)
                    {
                        messages[i] = messages[i] with { Images = images };
                    }
                }
            }
        }

        var built = request with
        {
            Model = model,
            Messages = messages,
            Params = parameters,
            FileIds = null,
        };

        // 5. Filtros declarativos do modelo custom (inlet) em ordem estável.
        var inletFilters = ModelFilterService.Parse(customModel?.MetaJson);
        return inletFilters.Count > 0
            ? ModelFilterService.ApplyInlet(built, inletFilters)
            : built;
    }

    /// <summary>Resolve referências #nome (arquivo ou coleção) para ids de arquivo.</summary>
    private static async Task<List<string>> ResolveReferenceFileIdsAsync(
        string text, string userId, AppDbContext db, CancellationToken ct)
    {
        var fileIds = new List<string>();
        if (!text.Contains('#'))
        {
            return fileIds;
        }

        var tokens = System.Text.RegularExpressions.Regex
            .Matches(text, @"#([\w.\-]+)", System.Text.RegularExpressions.RegexOptions.None, RegexDefaults.MatchTimeout)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        if (tokens.Count == 0)
        {
            return fileIds;
        }

        // #coleção → todos os arquivos vinculados.
        var collections = await db.KnowledgeCollections.AsNoTracking()
            .Where(k => k.UserId == userId && tokens.Contains(k.Name))
            .Select(k => k.Id)
            .ToListAsync(ct);
        if (collections.Count > 0)
        {
            fileIds.AddRange(await db.KnowledgeFiles.AsNoTracking()
                .Where(f => collections.Contains(f.CollectionId))
                .Select(f => f.FileId)
                .ToListAsync(ct));
        }

        // #arquivo → arquivo do usuário com esse nome.
        fileIds.AddRange(await db.Files.AsNoTracking()
            .Where(f => f.UserId == userId && tokens.Contains(f.Filename))
            .Select(f => f.Id)
            .ToListAsync(ct));

        return fileIds;
    }

    /// <summary>
    /// Resultado do loop de tools: o conteúdo final do modelo e as
    /// mensagens geradas durante o loop (assistant com ToolCallsJson +
    /// respostas role=tool) para persistência na run
    /// (SPEC-20261007-chat-tool-streaming).
    /// </summary>
    public sealed record ToolLoopOutcome(
        string? FinalContent,
        IReadOnlyList<ChatCompletionMessage> ToolMessages,
        /// <summary>Uso reportado pelo provider por step
        /// (SPEC-20261016-agent-steps-usage RF-004) — só os steps cujo
        /// provider informou usage aparecem; nunca estimado.</summary>
        IReadOnlyList<RoundUsage>? StepUsages = null,
        /// <summary>Completions executadas no loop (steps totais — inclui
        /// rounds sem usage reportado).</summary>
        int TotalSteps = 0);

    /// <summary>Uso de tokens de um step do loop (índice 1-based).</summary>
    public sealed record RoundUsage(
        int Step, int PromptTokens, int CompletionTokens, int? ReasoningTokens);

    /// <summary>
    /// Callbacks do loop de tools para streaming/gate
    /// (SPEC-20261007-chat-tool-streaming): <paramref name="OnPhaseAsync"/>
    /// recebe a fase (generating|running_tool|awaiting_approval) antes de
    /// cada etapa; <paramref name="OnCallAsync"/> antes de executar;
    /// <paramref name="GateAsync"/> decide se a tool mutável executa
    /// (negado injeta "negado pelo usuário", com a instrução opcional do
    /// dono — SPEC-20261007-chat-agent-ux RF-002); <paramref name="OnResultAsync"/>
    /// recebe (call, output, result, denied) após cada execução/decisão —
    /// <c>result</c> é o payload estruturado opcional (ex.: imagePath de
    /// generate_image). <paramref name="OnDeltaAsync"/> recebe os pedaços
    /// de texto conforme o modelo gera (round streamed; no fallback
    /// buffered o conteúdo inteiro chega numa chamada só).
    /// </summary>
    public sealed record ToolLoopCallbacks(
        Func<string, string?, CancellationToken, Task>? OnPhaseAsync = null,
        Func<ProviderToolCall, CancellationToken, Task>? OnCallAsync = null,
        Func<ProviderToolCall, CancellationToken, Task<ToolGateDecision>>? GateAsync = null,
        Func<ProviderToolCall, string, JsonElement?, bool, CancellationToken, Task>? OnResultAsync = null,
        Func<string, CancellationToken, Task>? OnDeltaAsync = null,
        /// <summary>
        /// Raciocínio/thinking do modelo (SPEC-20261015-chat-thinking-stream):
        /// reasoning_content/thinking_delta/thought:true/thinking chegam por
        /// este canal separado — a UI exibe colapsado e nada é persistido.
        /// </summary>
        Func<string, CancellationToken, Task>? OnReasoningAsync = null,
        /// <summary>
        /// Spec de tools anunciado por rodada (SPEC-20261009-agent-modes-
        /// plan-build): o executor refiltra pelo modo a cada round — o
        /// plan_exit aprovado promove a build já na próxima rodada. Null
        /// anuncia <c>request.Tools</c> como está.
        /// </summary>
        Func<CancellationToken, Task<IReadOnlyList<JsonElement>?>>? AnnounceToolsAsync = null,
        /// <summary>
        /// Fronteira de rodada (SPEC-20261010-steer-queue): chamado no topo de
        /// cada round com <paramref name="queuedOnly"/> = false (promove
        /// steers pendentes) e quando a run ficaria ociosa com true (promove
        /// queued). Mensagens retornadas são injetadas no histórico e zeram o
        /// teto de rounds — o turno recomeça a contagem.
        /// </summary>
        Func<bool, CancellationToken, Task<IReadOnlyList<ChatCompletionMessage>>>? OnRoundBoundaryAsync = null,
        /// <summary>
        /// Notas de contexto (SPEC-20261013-run-advisor RF-003): drenadas no
        /// topo de cada round e anexadas ao histórico <em>sem</em> zerar o
        /// teto — são avisos (ex.: conselho do advisor), não input do usuário.
        /// </summary>
        Func<CancellationToken, Task<IReadOnlyList<ChatCompletionMessage>>>? OnContextNotesAsync = null,
        /// <summary>
        /// Contador de step (SPEC-20261016-agent-steps-usage RF-006):
        /// chamado no início de cada round com (step 1-based, maxSteps)
        /// — o executor emite no evento de fase <c>generating</c>.
        /// </summary>
        Func<int, int, CancellationToken, Task>? OnStepAsync = null);

    /// <summary>
    /// Loop de tool calling: chama o modelo com tools até resposta final
    /// (sem tool_calls) ou teto de <paramref name="maxSteps"/> iterações
    /// (default 25 — SPEC-20261016-agent-steps-usage RF-001; agents com
    /// <c>steps:</c> chegam já clampados pelo caller).
    /// <paramref name="callbacks"/> recebe os eventos de fase/call/gate/
    /// resultado (runs desacopladas emitem no SSE; o endpoint legado passa
    /// null — tools invisíveis lá).
    /// </summary>
    public static async Task<ToolLoopOutcome?> RunToolLoopAsync(
        ChatCompletionRequest effective,
        IReadOnlyList<Tool> tools,
        ToolExecutor toolExecutor,
        ProviderService providers,
        CancellationToken ct,
        ToolLoopCallbacks? callbacks = null,
        BuiltinToolContext? builtinContext = null,
        string? userId = null,
        Microsoft.Extensions.Logging.ILogger? log = null,
        int? maxSteps = null)
    {
        // Teto global default do tool loop (SPEC-20261016-agent-steps-usage
        // RF-001): 5 → 25 agora que agents podem declarar `steps:` próprios.
        const int defaultMaxRounds = 25;
        var maxRounds = Math.Clamp(maxSteps ?? defaultMaxRounds, 1, 100);
        // Rodadas vazias (só reasoning, sem content nem tool_calls) não
        // encerram a run em silêncio: o modelo leva um nudge e tenta de
        // novo — autonomia para continuar o pensamento sem esperar o
        // usuário. Limitado para não virar loop infinito de nudges.
        const int maxEmptyRounds = 3;
        // Gateways com teto baixo de completion (ex.: rotas Cloudflare do
        // OmniRoute ~256 tokens) deixam o modelo de raciocínio sem orçamento
        // para responder — o retry de round vazio sobe o teto.
        const int retryMaxTokens = 8192;
        var emptyRounds = 0;
        var stepCount = 0;
        var request = effective;
        var messages = effective.Messages.ToList();
        var toolMessages = new List<ChatCompletionMessage>();
        var stepUsages = new List<RoundUsage>();

        for (var round = 0; round < maxRounds; round++)
        {
            if (callbacks?.OnRoundBoundaryAsync is not null)
            {
                var steered = await callbacks.OnRoundBoundaryAsync(false, ct);
                if (steered.Count > 0)
                {
                    messages.AddRange(steered);
                    // Steer zera o teto: o turno recomeça do input novo.
                    round = -1;
                    continue;
                }
            }
            // Notas de contexto (advisor): entram sem zerar o teto — o
            // conselho guia a rodada atual, não recomeça o turno.
            if (callbacks?.OnContextNotesAsync is not null)
            {
                var notes = await callbacks.OnContextNotesAsync(ct);
                if (notes.Count > 0)
                {
                    messages.AddRange(notes);
                }
            }
            if (callbacks?.OnStepAsync is not null)
            {
                await callbacks.OnStepAsync(round + 1, maxRounds, ct);
            }
            if (callbacks?.OnPhaseAsync is not null)
            {
                await callbacks.OnPhaseAsync("generating", null, ct);
            }
            // Último step permitido (RF-002): round roda sem tools e com o
            // prompt de wrap-up — o modelo se encerra graciosamente em vez
            // de bater no teto sem aviso. A mensagem sintética não vira
            // ChatMessage persistida (só vive no request desta rodada).
            var lastStep = round == maxRounds - 1;
            var roundRequest = lastStep
                ? request with
                {
                    Messages = [..messages, new ChatCompletionMessage(
                        "user", MaxStepsPrompt)],
                    Tools = null,
                }
                : request with { Messages = messages };
            var step = await CompleteRoundAsync(
                providers, roundRequest, callbacks, ct,
                userId ?? builtinContext?.UserId);
            stepCount++;
            if (step.Usage is { } usage)
            {
                stepUsages.Add(new RoundUsage(stepCount,
                    usage.PromptTokens, usage.CompletionTokens,
                    usage.ReasoningTokens));
            }
            if (step.ToolCalls.Count == 0)
            {
                // Antes de ficar ociosa, promove mensagens queued (SPEC-20261010).
                if (callbacks?.OnRoundBoundaryAsync is not null)
                {
                    var queued = await callbacks.OnRoundBoundaryAsync(true, ct);
                    if (queued.Count > 0)
                    {
                        messages.AddRange(queued);
                        round = -1;
                        continue;
                    }
                }
                if (string.IsNullOrWhiteSpace(step.Content) && emptyRounds < maxEmptyRounds)
                {
                    // Round vazio (ex.: gateway emitiu só reasoning, ou o
                    // markup DSML veio truncado e não rendeu call): a run
                    // continuava "concluída" com 0 chars — a UI congelava
                    // depois do pensamento. O nudge reabre o turno para o
                    // modelo produzir a resposta ou a tool call — e o retry
                    // sobe o teto de tokens para backends que travam o
                    // pensamento num orçamento pequeno demais.
                    emptyRounds++;
                    var bumped = BumpMaxTokens(request.Params, retryMaxTokens);
                    if (bumped is not null)
                    {
                        request = request with { Params = bumped };
                        log?.LogInformation(
                            "Round vazio: max_tokens elevado para {MaxTokens} no retry.",
                            retryMaxTokens);
                    }
                    log?.LogWarning(
                        "Round {Round} retornou vazio (sem content nem tool calls) — "
                        + "auto-continue {Empty}/{Max} injetado.",
                        round + 1, emptyRounds, maxEmptyRounds);
                    messages.Add(new ChatCompletionMessage(
                        "user",
                        "Sua resposta anterior veio vazia (sem texto nem tool calls). "
                        + "Responda agora de forma direta — ou chame a tool "
                        + "apropriada — sem reiniciar o raciocínio."));
                    continue;
                }
                return new ToolLoopOutcome(
                    step.Content, toolMessages, stepUsages, stepCount);
            }

            var assistantMessage = new ChatCompletionMessage(
                "assistant", step.Content, ToolCallsJson: step.ToolCallsJson);
            messages.Add(assistantMessage);
            toolMessages.Add(assistantMessage);

            log?.LogInformation(
                "Tool loop round {Round}/{Max}: {Count} call(s) ({Names}).",
                round + 1, maxRounds, step.ToolCalls.Count,
                string.Join(", ", step.ToolCalls.Select(c => c.Name)));

            foreach (var call in step.ToolCalls)
            {
                if (callbacks?.OnPhaseAsync is not null)
                {
                    await callbacks.OnPhaseAsync("running_tool", call.Name, ct);
                }
                if (callbacks?.OnCallAsync is not null)
                {
                    await callbacks.OnCallAsync(call, ct);
                }

                var gate = callbacks?.GateAsync is not null
                    ? await callbacks.GateAsync(call, ct)
                    : ToolGateDecision.Allow;
                var denied = !gate.Approved;
                var callSw = Stopwatch.StartNew();
                var outcome = denied
                    ? new ToolExecutionOutcome(
                        string.IsNullOrWhiteSpace(gate.DenyMessage)
                            ? "Erro: execução negada pelo usuário."
                            : $"Erro: execução negada pelo usuário: {gate.DenyMessage}")
                    // ask_user e gates parecidos podem devolver o resultado
                    // direto via ToolGateDecision.Output (RF-005).
                    : gate.Output is { Length: > 0 } gateOutput
                        ? new ToolExecutionOutcome(gateOutput)
                        : await toolExecutor.ExecuteAsync(
                            tools, call.Name, call.ArgumentsJson, builtinContext, ct);
                var output = outcome.Text;
                log?.LogInformation(
                    "Tool {Name}: {Elapsed}ms, denied={Denied}, output={Chars} chars.",
                    call.Name, callSw.ElapsedMilliseconds, denied, output.Length);

                if (callbacks?.OnResultAsync is not null)
                {
                    await callbacks.OnResultAsync(call, output, outcome.Result, denied, ct);
                }

                var toolMessage = new ChatCompletionMessage(
                    "tool", output, ToolCallId: call.Id);
                messages.Add(toolMessage);
                toolMessages.Add(toolMessage);
            }
        }

        // Teto de iterações atingido: o último round já rodou sem tools e
        // com wrap-up — chegar aqui significa que o modelo insistiu em
        // tool_calls mesmo anunciadas nenhuma (ex.: markup DSML sintetizado).
        // Uma rodada final toolless garante encerramento com texto.
        log?.LogWarning(
            "Tool loop atingiu o teto de {Max} rounds — resposta final sem tools.",
            maxRounds);
        var final = await CompleteRoundAsync(
            providers, request with { Messages = messages, Tools = null }, callbacks, ct,
            userId ?? builtinContext?.UserId);
        stepCount++;
        if (final.Usage is { } finalUsage)
        {
            stepUsages.Add(new RoundUsage(stepCount,
                finalUsage.PromptTokens, finalUsage.CompletionTokens,
                finalUsage.ReasoningTokens));
        }
        return new ToolLoopOutcome(
            final.Content, toolMessages, stepUsages, stepCount);
    }

    /// <summary>
    /// Prompt sintético do último step (SPEC-20261016-agent-steps-usage
    /// RF-002, padrão MAX_STEPS_PROMPT do opencode): injetado como
    /// mensagem <c>user</c> no request da rodada final — nunca persistido
    /// no histórico do chat.
    /// </summary>
    public const string MaxStepsPrompt =
        "Você atingiu o número máximo de passos desta execução — este é o "
        + "último. Sem novas tool calls: resuma o que foi feito, o estado "
        + "atual e os próximos passos sugeridos.";

    /// <summary>Sobe o teto de <c>max_tokens</c> no retry de round vazio —
    /// backends com default baixo (ex.: Cloudflare ~256) deixam o modelo de
    /// raciocínio sem orçamento para responder depois do pensamento.
    /// Devolve null quando o usuário já pediu um teto igual ou maior.</summary>
    private static IReadOnlyDictionary<string, object>? BumpMaxTokens(
        IReadOnlyDictionary<string, object>? parameters, int target)
    {
        var merged = parameters is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(parameters);
        var current = merged.TryGetValue("max_tokens", out var v)
            || merged.TryGetValue("max_completion_tokens", out v)
            ? v : null;
        var currentInt = current switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement { ValueKind: JsonValueKind.Number } e
                when e.TryGetInt32(out var n) => n,
            _ => (int?)null,
        };
        if (currentInt >= target)
        {
            return null;
        }
        merged["max_tokens"] = target;
        return merged;
    }

    /// <summary>
    /// Uma rodada do loop com streaming de texto: tenta a variante
    /// streamed (deltas via <c>OnDeltaAsync</c>) e cai na buffered quando
    /// o endpoint recusa stream+tools — aí o conteúdo inteiro é emitido
    /// num único delta para manter o texto visível do mesmo jeito.
    /// </summary>
    private static async Task<ProviderCompletion> CompleteRoundAsync(
        ProviderService providers,
        ChatCompletionRequest request,
        ToolLoopCallbacks? callbacks,
        CancellationToken ct,
        string? userId = null)
    {
        // Refiltra o spec anunciado por rodada (modo plan/build pode
        // mudar mid-run); Tools=null explícito (round final) respeitado.
        if (request.Tools is not null && callbacks?.AnnounceToolsAsync is { } announce)
        {
            request = request with { Tools = await announce(ct) };
        }
        var step = await providers.CompleteWithToolsStreamingAsync(
            request, callbacks?.OnDeltaAsync, callbacks?.OnReasoningAsync, userId, ct);
        if (step is not null)
        {
            return step;
        }

        var buffered = await providers.CompleteWithToolsAsync(request, userId, ct);
        if (buffered.Content.Length > 0 && callbacks?.OnDeltaAsync is not null)
        {
            await callbacks.OnDeltaAsync(buffered.Content, ct);
        }
        // Fallback buffered: o reasoning chega inteiro de uma vez quando o
        // endpoint recusou stream+tools.
        if (!string.IsNullOrEmpty(buffered.Reasoning)
            && callbacks?.OnReasoningAsync is not null)
        {
            await callbacks.OnReasoningAsync(buffered.Reasoning, ct);
        }
        return buffered;
    }

    /// <summary>
    /// Executa uma batalha de arena quando o modelo pedido é do tipo arena:
    /// sorteia 2 concorrentes do MetaJson, completa ambos e emite um payload
    /// {"arena": {battle_id, responses:[{label, content}]}} via
    /// <paramref name="emitLineAsync"/> (linha SSE `data: ...` pronta).
    /// Retorna true quando tratou a requisição (mesmo em erro já emitido).
    /// </summary>
    public static async Task<bool> TryRunArenaAsync(
        ChatCompletionRequest request,
        string modelKey,
        User user,
        AppDbContext db,
        ConfigService config,
        RagService rag,
        ProviderService providers,
        WebSearchService webSearch,
        WorkspaceRepoService repos,
        Func<string, Task> emitLineAsync,
        CancellationToken ct)
    {
        var arenaEntry = await db.ModelEntries.AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.IsActive && (m.Id == modelKey || m.Name == modelKey)
                    && (m.UserId == user.Id || m.UserId == "public"), ct);
        var arena = arenaEntry is null ? null : ModelEndpoints.ParseArenaMeta(arenaEntry.MetaJson);
        if (arena is null)
        {
            return request.Model.StartsWith("arena:", StringComparison.Ordinal);
        }
        if (!await ModelEndpoints.HasModelAccessAsync(user, arenaEntry!, db, ct))
        {
            await EmitDoneErrorAsync(emitLineAsync, "Acesso negado ao modelo arena.");
            return true;
        }

        var competitors = arena.Value.ModelIds
            .OrderBy(_ => Random.Shared.Next())
            .Take(2)
            .ToList();
        if (competitors.Count < 2)
        {
            await EmitDoneErrorAsync(emitLineAsync, "Modelo arena sem concorrentes suficientes.");
            return true;
        }

        var responses = new List<string>(2);
        try
        {
            foreach (var competitor in competitors)
            {
                var effective = await EnrichRequestAsync(
                    request with { Model = competitor, Stream = false },
                    user, db, config, rag, webSearch, repos, ct);
                responses.Add(await providers.CompleteAsync(effective, user.Id, ct));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            await EmitDoneErrorAsync(emitLineAsync, $"Falha ao gerar respostas da arena: {ex.Message}");
            return true;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var battle = new ArenaBattle
        {
            UserId = user.Id,
            ArenaModelId = arenaEntry!.Id,
            ModelA = competitors[0],
            ModelB = competitors[1],
            ResponseA = responses[0],
            ResponseB = responses[1],
            CreatedAt = now,
        };
        db.ArenaBattles.Add(battle);
        await db.SaveChangesAsync(ct);

        var payload = JsonSerializer.Serialize(new
        {
            arena = new
            {
                battle_id = battle.Id,
                responses = new[]
                {
                    new { label = "A", content = responses[0] },
                    new { label = "B", content = responses[1] },
                },
            },
        });
        await emitLineAsync($"data: {payload}");
        await emitLineAsync("data: [DONE]");
        return true;
    }

    /// <summary>
    /// Prepara o roteamento `pipeline:{id}`: resolve o servidor que hospeda o
    /// pipe e monta o corpo (modelo/messages/stream) + valves das functions
    /// ativas. Retorna null quando o pipe é desconhecido.
    /// </summary>
    public static async Task<(PipelineServer? Server, string Body, string? ValvesJson)>
        PreparePipelineRouteAsync(
            ChatCompletionRequest request,
            AppDbContext db,
            PipelineClientService pipelines,
            CancellationToken ct)
    {
        var pipeId = request.Model["pipeline:".Length..];
        var server = await pipelines.FindServerForPipeAsync(pipeId, ct);
        if (server is null)
        {
            return (null, string.Empty, null);
        }

        // Valves de functions ativas — enviadas ao servidor como {"valves": {id: {...}}}.
        var functions = await db.Functions.AsNoTracking()
            .Where(f => f.Active && f.ValvesJson != null)
            .Select(f => new { f.Id, f.ValvesJson }).ToListAsync(ct);
        string? valvesJson = null;
        if (functions.Count > 0)
        {
            var map = new Dictionary<string, JsonElement>();
            foreach (var f in functions)
            {
                try
                {
                    map[f.Id] = JsonSerializer.Deserialize<JsonElement>(f.ValvesJson!);
                }
                catch (JsonException)
                {
                    // JSON da tool malformado — segue com o argumento bruto.
                }
            }

            valvesJson = JsonSerializer.Serialize(map);
        }

        var body = JsonSerializer.Serialize(new
        {
            model = pipeId,
            messages = request.Messages,
            stream = request.Stream,
        });
        return (server, body, valvesJson);
    }

    /// <summary>Emite uma linha de erro + [DONE] no formato SSE atual.</summary>
    public static async Task EmitDoneErrorAsync(Func<string, Task> emitLineAsync, string message)
    {
        var error = JsonSerializer.Serialize(new { error = message });
        await emitLineAsync($"data: {error}");
        await emitLineAsync("data: [DONE]");
    }
}
