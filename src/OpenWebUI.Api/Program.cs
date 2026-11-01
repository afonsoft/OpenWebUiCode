using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Hybrid;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Browser;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Api;
using OpenWebUI.Api.Endpoints;
using OpenWebUI.Api.Runs;
using OpenWebUI.Api.Notifications;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.Services.Image;
using OpenWebUI.Infrastructure.Services.Video;
using OpenWebUI.Infrastructure.Terminal;
using OpenWebUI.Infrastructure.Lsp;
using OpenWebUI.Application.Contracts;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=data/openwebui.db";

builder.Services.AddDbContext<AppDbContext>(options =>
    ConfigureDatabase(options, connectionString));

builder.Services.AddScoped<ConfigService>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<ProviderService>();
builder.Services.AddScoped<ProviderCapabilityService>();
builder.Services.AddHostedService<ProviderCapabilityBootstrapper>();
builder.Services.AddScoped<AutomationService>();
builder.Services.AddScoped<ComboService>();
builder.Services.AddScoped<ExternalAgentService>();
builder.Services.AddHostedService<AutomationScheduler>();
builder.Services.AddScoped<MemorySyncService>();
builder.Services.AddHostedService<MemorySyncScheduler>();
builder.Services.AddScoped<ModelWarmer>();
builder.Services.AddHostedService<ModelWarmerScheduler>();
builder.Services.AddHostedService<EvalRunnerService>();
builder.Services.AddScoped<RetentionService>();
builder.Services.AddScoped<ToolOutputService>();
builder.Services.AddHostedService<RetentionScheduler>();
builder.Services.AddScoped<OAuthService>();
builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<PolicyService>();
// Budgets de custo (SPEC-20261023-run-cost-budgets): ledger consultável
// por run + resolução de políticas por sujeito (user/grupo/global).
builder.Services.AddScoped<RunUsageLedgerService>();
builder.Services.AddScoped<BudgetPolicyService>();
builder.Services.AddScoped<EvalGraderFactory>();
builder.Services.AddScoped<EvalGraderService>();
builder.Services.AddScoped<EmbeddingService>();
builder.Services.AddScoped<RagService>();
builder.Services.AddScoped<ToolExecutor>();
builder.Services.AddScoped<PythonToolExecutor>();
builder.Services.AddSingleton<ImageEngineFactory>();
builder.Services.AddScoped<ImageGenerationService>();
builder.Services.AddScoped<AudioService>();
builder.Services.AddScoped<WebLoaderService>();
builder.Services.AddScoped<WebSearchService>();
builder.Services.AddScoped<AccessControlService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<ProviderProxyService>();
builder.Services.AddScoped<TerminalProxyService>();
builder.Services.AddSingleton<LocalTerminalSpawner>();
builder.Services.AddSingleton<TerminalSessionManager>();
builder.Services.AddScoped<ScimService>();
builder.Services.AddScoped<SamlService>();
builder.Services.AddScoped<PipelineClientService>();
builder.Services.AddScoped<McpClientService>();
builder.Services.AddSingleton<RateLimitService>();

// Runs de chat desacopladas (SPEC-20261007-chat-detached-runs).
builder.Services.AddSingleton<ChatRunBroadcaster>();
builder.Services.AddSingleton<ChatRunApprovals>();
builder.Services.AddScoped<PermissionRuleEvaluator>();
builder.Services.AddSingleton<OpenWebUI.Infrastructure.Context.RestrictedExecRunner>();
builder.Services.AddSingleton<OpenWebUI.Infrastructure.Context.StallWatchdog>();
builder.Services.AddSingleton<OpenWebUI.Infrastructure.Context.ExtendContextBudget>();
builder.Services.AddSingleton<ExternalAccessRegistry>();
builder.Services.AddSingleton<ChatRunPauses>();
builder.Services.AddSingleton<ChatRunDispatcher>();
builder.Services.AddSingleton<IChatRunDispatcher>(
    sp => sp.GetRequiredService<ChatRunDispatcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChatRunDispatcher>());
builder.Services.AddScoped<ChatRunPipelineDeps>();
builder.Services.AddScoped<ChatRunToolsDeps>();
builder.Services.AddScoped<ChatRunLifecycleDeps>();
builder.Services.AddScoped<ChatRunExecutor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.Services.CompactionDeps>();
builder.Services.AddScoped<ContextCompactionService>();
builder.Services.AddScoped<RunEventWriter>();
// Seams internos de run (SPEC-20261021-harness-seams RF-A1/A2/A3):
// barramento de eventos read-only + waterfall de interceptors +
// contribuidores compostáveis de system prompt.
builder.Services.AddScoped<OpenWebUI.Infrastructure.Runs.AgentRunEventBus>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.Runs.RunInterceptorRunner>();
builder.Services.AddScoped<OpenWebUI.Application.Interfaces.IAgentRunObserver,
    OpenWebUI.Infrastructure.Runs.TelemetryObserver>();
// Inbox de atenção: run.completed/failed → item persistido (SPEC-20261022 RF-C2).
builder.Services.AddScoped<OpenWebUI.Application.Interfaces.IAgentRunObserver,
    OpenWebUI.Infrastructure.Runs.AttentionObserver>();
// Instincts (SPEC-20261023-instincts-learning): extração pós-run via
// observer; injeção session-start via contributor; núcleo no service.
builder.Services.AddScoped<OpenWebUI.Infrastructure.Services.InstinctService>();
builder.Services.AddScoped<OpenWebUI.Application.Interfaces.IAgentRunObserver,
    OpenWebUI.Infrastructure.Runs.InstinctExtractor>();
builder.Services.AddScoped<OpenWebUI.Application.Interfaces.IRunInterceptor,
    OpenWebUI.Infrastructure.Runs.RepeatToolGuard>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.BaseAgentContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.AlwaysAllowContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.SddWorkflowContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.DelegationPolicyContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.CustomModelContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.ModelSkillsContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.FileContextContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.WebSearchContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.MemoriesContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.RepoBindingContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.AutoinvokeSkillsContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.AgentMemoryContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.InstinctContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.MentionsContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.AgentDisciplineContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.AgentModeContributor>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.SystemPrompt.ISystemPromptContributor,
    OpenWebUI.Api.Completions.Contributors.DeferredToolIndexContributor>();
// Gerenciamento de contexto dirigido pelo agente (SPEC-20261013-agent-
// context-management): boundaries + notebook + flag new_context via kv.
builder.Services.AddScoped<ContextBoundaryService>();
builder.Services.AddScoped<ContextSnapshotService>();
// Camadas de configuração por usuário (SPEC-20261014-config-layers).
builder.Services.AddScoped<UserConfigOverlayService>();
// Pseudo-recurso history:// — leitura do histórico persistido (SPEC-20261014).
builder.Services.AddScoped<HistoryResourceService>();
// Referências nomeadas ref:// (SPEC-20261018-references): dir local ou
// clone git cacheado, somente-leitura nas tools de arquivo.
builder.Services.AddScoped<ReferenceService>();
builder.Services.AddScoped<IContextSummarizer, LlmContextSummarizer>();
builder.Services.AddScoped<INativeContextCompactor, ProviderNativeCompactor>();
// Árvore de conversa (SPEC-20261013-chat-branching): walk leaf→raiz,
// leafs divergentes e resumo do ramo abandonado.
builder.Services.AddScoped<BranchService>();
// Papéis/capacidades de modelo (SPEC-20261013-model-roles).
builder.Services.AddScoped<ModelCapabilityResolver>();
builder.Services.AddScoped(sp => new ModelRoleResolver(
    sp.GetRequiredService<AppDbContext>(),
    sp.GetRequiredService<ConfigService>(),
    ct => sp.GetRequiredService<ProviderService>().ListModelsAsync(ct),
    sp.GetRequiredService<ILogger<ModelRoleResolver>>(),
    sp.GetRequiredService<BudgetPolicyService>()));
// Revisor de run (SPEC-20261013-run-advisor): delegates encapsulam as
// chamadas externas para a sessão isolada read-only do advisor.
builder.Services.AddScoped(sp => new RunAdvisorService(
    sp.GetRequiredService<AppDbContext>(),
    sp.GetRequiredService<BuiltinToolRegistry>(),
    sp.GetRequiredService<ModelRoleResolver>(),
    sp.GetRequiredService<ConfigService>(),
    (req, uid, ct) => sp.GetRequiredService<ProviderService>().CompleteWithToolsAsync(req, uid, ct),
    (tools, name, args, bctx, ct) => sp.GetRequiredService<ToolExecutor>()
        .ExecuteAsync(tools, name, args, bctx, ct)));
// Hook prompt usa o mesmo delegate de completion do advisor.
builder.Services.AddScoped(sp => new PromptHookEvaluator(
    sp.GetRequiredService<ModelRoleResolver>(),
    sp.GetRequiredService<ConfigService>(),
    (req, uid, ct) => sp.GetRequiredService<ProviderService>().CompleteWithToolsAsync(req, uid, ct)));
// Juiz do preset smart (SPEC-20261031): mesmo delegate de completion.
builder.Services.AddScoped(sp => new SmartPermissionClassifier(
    sp.GetRequiredService<ModelRoleResolver>(),
    sp.GetRequiredService<ConfigService>(),
    (req, uid, ct) => sp.GetRequiredService<ProviderService>().CompleteWithToolsAsync(req, uid, ct),
    sp.GetService<ILogger<SmartPermissionClassifier>>()));

// Notificações de run (SPEC-20261007-chat-notifications): SignalR para abas
// conectadas + Web Push quando nenhuma aba está conectada.
builder.Services.AddScoped<VapidKeyService>();
builder.Services.AddScoped<IWebPushSender, WebPushSender>();
builder.Services.AddScoped<IChatRunNotifier, SignalRChatRunNotifier>();
builder.Services.AddScoped<IChatRunNotifier, WebPushChatRunNotifier>();
builder.Services.AddScoped<IChatRunNotifier, PersonaTurnNotifier>();
builder.Services.AddScoped<PersonaDispatchService>();
builder.Services.AddHttpClient("webpush");

// Messaging gateway (SPEC-20261023-messaging-gateway): adapters por
// plataforma (v1: telegram), pipeline inbound e reply via notifier.
builder.Services.AddScoped<OpenWebUI.Infrastructure.Services.Gateway.IGatewayAdapter,
    OpenWebUI.Infrastructure.Services.Gateway.TelegramAdapter>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.Services.Gateway.GatewayPairingService>();
builder.Services.AddScoped<OpenWebUI.Infrastructure.Services.Gateway.GatewayInboundService>();
builder.Services.AddScoped<IChatRunNotifier,
    OpenWebUI.Infrastructure.Services.Gateway.GatewayReplyService>();
builder.Services.AddHttpClient("gateway");
builder.Services.AddHttpClient(McpOAuthService.HttpClientName,
    client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<McpOAuthService>();

// Tools built-in do chat (SPEC-20261007-chat-agent-tools): registro
// resolve ids "builtin:*"; desligar via config BuiltinTools:Disabled (csv).
builder.Services.AddScoped<IBuiltinChatTool, GenerateImageBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, CodeInterpreterBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, ShellExecBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, JobListBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, JobOutputBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, JobKillBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, FetchUrlBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, WebSearchBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, FileListBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, FileReadBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, FileGrepBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, FileGlobBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, FileWriteBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, FileEditBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, ApplyPatchBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, TodoWriteBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, CheckpointBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, RewindBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, ContextNotesBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, NewContextBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, ContextSaveBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, ContextRecoveryBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, ContextCompactBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, DelegateTaskBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, RunResultBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, WorktreeDiffBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, WorktreeMergeBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, MemorySaveBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, MemorySearchBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, MemoryLearnBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, AgentCatalogBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, SpecsBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, UnseenMessagesBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, ExtendContextBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, InstantContextBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, RoutineBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, ReminderBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, BrowserScreenshotBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, BrowserNavigateBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, BrowserActBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, BrowserObserveBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, BrowserCloseBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, N8nListWorkflowsBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, N8nTriggerBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, SkillBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, GenerateVideoBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, AskUserBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, PlanExitBuiltinTool>();
// Tool-search-deferred (SPEC-20261031): estado por run + índice + loader.
builder.Services.AddSingleton<DeferredToolState>();
builder.Services.AddSingleton<ToolIndexService>();
builder.Services.AddScoped<IBuiltinChatTool, ToolSearchBuiltinTool>();
// Plan mode v2 (SPEC-20261031): write_plan + diretório de planos.
builder.Services.AddSingleton<PlanFileService>();
// Lifecycle hooks (SPEC-20261031): command runner + evaluator + orquestrador.
builder.Services.AddSingleton<CommandHookRunner>();
builder.Services.AddScoped<LifecycleHookService>();
builder.Services.AddScoped<IBuiltinChatTool, WritePlanBuiltinTool>();
// Agent teams (SPEC-20261021-harness-seams RF-E2).
builder.Services.AddScoped<IBuiltinChatTool, TeamTaskBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, TeamMessageBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, TeamStatusBuiltinTool>();
// LSP (SPEC-20261009-lsp-diagnostics): singleton dono dos processos
// filhos + tools read-only builtin:lsp_*.
builder.Services.AddSingleton<LspService>();
builder.Services.AddScoped<IBuiltinChatTool, LspDiagnosticsBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, LspSymbolsBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, LspWorkspaceSymbolsBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, LspDefinitionBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, LspReferencesBuiltinTool>();
builder.Services.AddScoped<IBuiltinChatTool, LspHoverBuiltinTool>();
builder.Services.AddSingleton<VideoEngineFactory>();
builder.Services.AddScoped<VideoGenerationService>();
builder.Services.AddSingleton<BrowserScreenshotService>();
builder.Services.AddSingleton<BrowserSessionService>();
builder.Services.AddSingleton<IBrowserSessions>(
    sp => sp.GetRequiredService<BrowserSessionService>());
builder.Services.AddSingleton<TesseractOcr>();
builder.Services.AddScoped<WorkspaceRepoService>();
builder.Services.AddScoped<WorkspacePublishService>();
builder.Services.AddSingleton<WorktreeService>();
builder.Services.AddScoped<FormatHookService>();
builder.Services.AddSingleton<CheckpointService>();
builder.Services.AddSingleton<RemoteSkillCatalog>();
builder.Services.AddSingleton<SkillDiscoveryService>();
builder.Services.AddScoped<AgentDefinitionService>();
builder.Services.AddScoped<RepoCommandService>();
builder.Services.AddScoped<GitHubService>();
builder.Services.AddHttpClient(GitHubService.HttpClientName,
    client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<UserSecretsService>();
builder.Services.AddScoped<AgentSecretsEnvService>();
builder.Services.AddScoped<N8nService>();
builder.Services.AddHttpClient(N8nService.HttpClientName,
    client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<BuiltinToolRegistry>();
builder.Services.AddScoped<ToolApprovalOverrideService>();
builder.Services.AddSingleton<ChatJobService>();
builder.Services.AddHttpClient(nameof(FetchUrlBuiltinTool));

// Proxy do port preview (SPEC-20261009-port-preview): sem redirects,
// sem cookie jar compartilhado entre usuários, sem descompressão
// (o cliente recebe o payload exato do upstream).
builder.Services.AddHttpClient(PreviewEndpoints.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    });

builder.Services.AddMemoryCache();
builder.Services.AddHybridCache(options =>
    // A lista agregada de /v1/models (gateways tipo OmniRoute) passa do
    // default de 1MB — toda escrita falhava com "Max length exceeded" e
    // o cache nunca servia (fail recorrente + refetch a cada request).
    options.MaximumPayloadBytes = 16 * 1024 * 1024);
builder.Services.AddHttpClient();
builder.Services.AddOpenApi();

// Inicializa o banco e obtém o segredo JWT antes de configurar a autenticação.
var jwtSecret = BootstrapDatabase(connectionString);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = JwtTokenService.BuildValidationParameters(jwtSecret);
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = AcceptTokenFromQueryString,
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddSignalR();

// JSON das APIs (listas de chats, configs, i18n) é o payload que mais se repete;
// os assets WASM já são servidos comprimidos pelo MapStaticAssets.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

// Modo headless one-shot (SPEC-20261021-harness-seams RF-C1): registra o
// observer JSON Lines antes de construir o provider — os eventos da run
// saem em stdout e o processo termina com o status da run.
if (OpenWebUI.Api.Headless.HeadlessRunner.IsHeadless(args))
{
    builder.Services.AddSingleton<IAgentRunObserver,
        OpenWebUI.Api.Headless.JsonLinesRunObserver>();
}

var app = builder.Build();

if (OpenWebUI.Api.Headless.HeadlessRunner.IsHeadless(args))
{
    return await OpenWebUI.Api.Headless.HeadlessRunner.RunAsync(
        app.Services, args, CancellationToken.None);
}

app.UseResponseCompression();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseWebAssemblyDebugging();
}

// Atrás de reverse proxy (nginx/Cloudflare/preview): honra X-Forwarded-*
// para que Scheme/Host reflitam a URL real — sem isso o redirect_uri do
// OAuth sai como http://interno e o callback quebra (mesmo fix do agent-harness).
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost,
});

UseContentSecurityPolicy(app);

// Serve os static web assets com fingerprinting e resolve os placeholders
// #[.{fingerprint}] do index.html (UseStaticFiles não faz essa substituição).
app.MapStaticAssets();

UseSpaNoCacheHeaders(app);

app.UseAuthentication();
UseApiKeyAuthentication(app);
app.UseAuthorization();
app.UseWebSockets();

MapAllEndpoints(app);

app.Run();
return 0;

/// <summary>SignalR envia o JWT via query string no handshake do WebSocket.</summary>
static Task AcceptTokenFromQueryString(MessageReceivedContext context)
{
    var token = context.Request.Query["access_token"];
    if (!string.IsNullOrEmpty(token) &&
        (context.HttpContext.Request.Path.StartsWithSegments("/ws") ||
         context.HttpContext.Request.Path.StartsWithSegments("/api/v1/terminals")))
    {
        context.Token = token;
    }
    return Task.CompletedTask;
}

/// <summary>
/// Content-Security-Policy própria (SPEC-20261010-app-csp-header): antes a app
/// não emitia CSP nenhum e herdava um report-only frouxo do proxy de produção
/// (script-src unsafe-inline/eval, connect-src 'none') que só gerava ruído de
/// violação para o service worker e as chamadas de API. A política abaixo é a
/// que o boot WASM realmente precisa:
///  - 'unsafe-inline' cobre os scripts inline do index.html (tema + registro
///    do service worker); um nonce fica para uma iteração futura.
///  - 'wasm-unsafe-eval'/'unsafe-eval' cobrem o WebAssembly.instantiate do
///    dotnet.wasm e o eval do runner JS de codeexec (browsers antigos sem a
///    keyword wasm-* caem para 'unsafe-eval').
///  - https://cdn.jsdelivr.net cobre o importScripts do Pyodide em
///    js/py-worker.js (o worker herda o script-src do documento).
///  - worker-src blob: cobre new Worker(URL.createObjectURL(...)) do codeexec.
///  - connect-src https: cobre chamadas a provedores/CDN client-side
///    (ex.: pacotes Pyodide), wss: o SignalR /ws e data:/blob: os fallbacks
///    b64→Response do boot e object URLs geradas pelo cliente.
/// Aplica-se só a documentos HTML: o proxy de port preview (/preview/{port})
/// deve repassar o payload do upstream intacto — um CSP nosso ali quebraria
/// apps terceiros que consomem CDNs externos.
/// </summary>
static void UseContentSecurityPolicy(WebApplication app)
{
    // CSP mínima funcional (Sonar S7039 — revisado):
    // - 'unsafe-inline' (script/style): index.html tem <script> inline de boot/tema.
    // - 'unsafe-eval' + 'wasm-unsafe-eval': codeexec.js roda código do usuário via
    //   eval() dentro de Web Worker; WASM exige wasm-unsafe-eval.
    // - connect-src https:/wss:/data:/blob:: BYOK permite providers arbitrários
    //   (Ollama/OpenAI customizados) — restringir quebraria deployments locais.
    // - img-src https:/frame-src https:: markdown/artefatos embutem URLs externas.
    const string ContentSecurityPolicy =
        "default-src 'self'; " +
        // S7039: sem 'unsafe-eval' — o boot WASM só precisa de
        // 'wasm-unsafe-eval' (CSP3). 'unsafe-inline' permanece: os dois
        // scripts inline do index.html exigem (remover = refactor de
        // nonces/hashes, fora do escopo do finding).
        "script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval' https://cdn.jsdelivr.net; " +
        "worker-src 'self' blob:; " +
        "connect-src 'self' https: wss: data: blob:; " +
        "img-src 'self' data: blob: https:; " +
        "style-src 'self' 'unsafe-inline'; " +
        "font-src 'self' data:; " +
        "frame-src 'self' https:; " +
        "media-src 'self' blob: data:; " +
        "object-src 'none'; base-uri 'self'; frame-ancestors 'self'; form-action 'self'";

    app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var isHtml =
                context.Response.ContentType?.StartsWith(
                    "text/html", StringComparison.OrdinalIgnoreCase) == true;
            if (isHtml
                && !path.StartsWith("/preview/", StringComparison.OrdinalIgnoreCase)
                && !context.Response.Headers.ContainsKey("Content-Security-Policy"))
            {
                context.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
            }

            return Task.CompletedTask;
        });

        await next();
    });
}

/// <summary>
/// Documento/rotas da SPA e a cadeia mutável de boot (index.html, boot.js e os
/// loaders não-fingerprinted) sempre revalidam — caso contrário uma cópia antiga
/// em cache continua apontando para fingerprints antigos e o deploy nunca chega
/// ao browser (previne também o loop de reload do PWA conhecido no upstream).
/// </summary>
static void UseSpaNoCacheHeaders(WebApplication app)
{
    app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var isDocument =
                context.Request.Method == "GET" &&
                !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/ws", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("/framework-assets/", StringComparison.OrdinalIgnoreCase) &&
                (path == "/" || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith("/service-worker.js", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith("/js/boot.js", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith("/manifest.webmanifest", StringComparison.OrdinalIgnoreCase) ||
                 path is "/_framework/blazor.webassembly.js"
                     or "/_framework/dotnet.js"
                     or "/_framework/dotnet.boot.js" ||
                 !path.Contains('.'));
            if (isDocument)
            {
                context.Response.Headers.CacheControl = "no-cache";
            }

            return Task.CompletedTask;
        });

        await next();
    });
}

/// <summary>Chaves de API (Bearer sk-...) autenticam como o usuário dono da chave.</summary>
static void UseApiKeyAuthentication(WebApplication app)
{
    app.Use(async (context, next) =>
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer sk-", StringComparison.Ordinal))
        {
            var identity = await ResolveApiKeyIdentityAsync(
                context, header["Bearer ".Length..].Trim());
            if (identity is not null)
            {
                context.User = new ClaimsPrincipal(identity);
            }
        }

        await next();
    });
}

/// <summary>
/// Resolve a identidade do dono de uma chave <c>sk-*</c>; null quando a flag
/// está desligada, a chave não existe ou o usuário está pendente.
/// </summary>
static async Task<ClaimsIdentity?> ResolveApiKeyIdentityAsync(HttpContext context, string key)
{
    var hash = AuthEndpoints.HashApiKey(key);
    var hybridCache = context.RequestServices.GetRequiredService<HybridCache>();
    // Flag lida fora do cache (ConfigService já cacheia) para que desligar
    // API keys passe a valer imediatamente para chaves já resolvidas.
    using (var flagScope = context.RequestServices.CreateScope())
    {
        var flagConfig = flagScope.ServiceProvider.GetRequiredService<ConfigService>();
        var flagAdmin = await flagConfig.GetAdminConfigAsync();
        if (!flagAdmin.EnableApiKeys)
        {
            return null;
        }
    }
    // Cache de 2min (HybridCache — coalesces resoluções concorrentes da
    // mesma chave): evita escopo DI + 2 queries ao SQLite por request.
    // Revogação/rotação evicta explicitamente em AuthEndpoints; o TTL curto
    // cobre mudanças de role (admin rebaixa usuário) fora desse caminho.
    return await hybridCache.GetOrCreateAsync(
        ApiKeyAuthCache.CacheKey(hash),
        async cancel =>
        {
            _ = cancel;
            using var scope = context.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var apiKey = await db.ApiKeys.AsNoTracking()
                .FirstOrDefaultAsync(k => k.KeyHash == hash);
            var user = apiKey is null
                ? null
                : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == apiKey.UserId);
            if (user is null || user.Role == UserRoles.Pending)
            {
                return null;
            }
            return new ApiKeyIdentity(user.Id, user.Name, user.Email, user.Role);
        },
        new HybridCacheEntryOptions
        {
            Expiration = ApiKeyAuthCache.Ttl,
            LocalCacheExpiration = ApiKeyAuthCache.Ttl,
        }) is { } cached
        ? new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, cached.Id),
            new Claim(ClaimTypes.Name, cached.Name),
            new Claim(ClaimTypes.Email, cached.Email),
            new Claim(ClaimTypes.Role, cached.Role),
        ], "ApiKey")
        : null;
}


/// <summary>
/// Mapeia todos os endpoints na MESMA ordem original — a precedência de
/// rotas sobrepostas depende da sequência de registro.
/// </summary>
static void MapAllEndpoints(WebApplication app)
{
    MapPlatformEndpoints(app);
    MapDomainEndpoints(app);
    MapFeatureEndpoints(app);
    app.MapFrameworkAssetsEndpoints();
    app.MapHub<OpenWebUI.Api.Hubs.ChatHub>("/ws");
    app.MapFallbackToFile("index.html");
}

/// <summary>Auth, notificações, passthrough, terminais e identidade federada.</summary>
static void MapPlatformEndpoints(WebApplication app)
{
    app.MapAuthEndpoints();
    app.MapNotificationEndpoints();
    app.MapPassthroughEndpoints();
    app.MapTerminalEndpoints();
    app.MapTerminalPtyEndpoints();
    app.MapBrowserToolEndpoints();
    app.MapTerminalWebSocket();
    app.MapTerminalMultiplexWebSocket();
    app.MapScimEndpoints();
    app.MapSamlEndpoints();
    app.MapPluginEndpoints();
}

/// <summary>Domínio principal: chats, usuários, workspace, modelos, tasks e tools.</summary>
static void MapDomainEndpoints(WebApplication app)
{
    app.MapChatEndpoints();
    app.MapAttentionEndpoints();
    app.MapUserEndpoints();
    app.MapWorkspaceEndpoints();
    app.MapUtilsEndpoints();
    app.MapFileEndpoints();
    app.MapModelEndpoints();
    app.MapEvaluationEndpoints();
    app.MapAnalyticsEndpoints();
    app.MapTaskEndpoints();
    app.MapApiEndpoints();
    app.MapGroupEndpoints();
    app.MapAdminPolicyEndpoints();
    app.MapPermissionEndpoints();
    app.MapUsageEndpoints();
    app.MapEvalEndpoints();
    app.MapInstinctEndpoints();
    app.MapGatewayEndpoints();
    app.MapOAuthEndpoints();
    app.MapKnowledgeEndpoints();
    app.MapToolEndpoints();
    app.MapChatJobEndpoints();
    app.MapMcpEndpoints();
    app.MapUserMcpEndpoints();
    app.MapMcpOAuthEndpoints();
    app.MapReferenceEndpoints();
    app.MapThemeEndpoints();
    app.MapChannelEndpoints();
    app.MapSecretEndpoints();
}

/// <summary>Mídia, automações, integrações e recursos do workspace.</summary>
static void MapFeatureEndpoints(WebApplication app)
{
    app.MapImageEndpoints();
    app.MapAutomationEndpoints();
    app.MapComboEndpoints();
    app.MapMaintenanceEndpoints();
    app.MapExternalAgentEndpoints();
    app.MapAgentPersonaEndpoints();
    app.MapAutomationHookEndpoints();
    app.MapRpcEndpoints();
    app.MapN8nEndpoints();
    app.MapVideoEndpoints();
    app.MapConfigEndpoints();
    app.MapGitHubEndpoints();
    app.MapWorkspaceFileEndpoints();
    app.MapCheckpointEndpoints();
    app.MapWorkspaceTestRunEndpoints();
    app.MapRepoSkillEndpoints();
    app.MapPlanEndpoints();
    app.MapHookEndpoints();
    app.MapSandboxEndpoints();
    app.MapAgentEndpoints();
    app.MapIdeEndpoints();
    app.MapPreviewEndpoints();
    app.MapLspEndpoints();
    app.MapAudioEndpoints();
    app.MapRetrievalEndpoints();
    app.MapCalendarEndpoints();
}

/// <summary>
/// Inicializa o banco (diretório + migrations + seeds de env) e devolve o
/// segredo JWT — criado quando a base ainda não tem um.
/// </summary>
static string BootstrapDatabase(string connectionString)
{
    using var bootstrap = new AppDbContext(CreateDbOptions(connectionString));
    if (connectionString.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
    {
        var path = connectionString["Data Source=".Length..].Split(';')[0].Trim();
        if (!string.IsNullOrEmpty(path) && path != ":memory:")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
    }

    DatabaseMigrator.MigrateAsync(bootstrap).GetAwaiter().GetResult();
    SeedConnectionsFromEnv(bootstrap);
    SeedWhisperUrlFromEnv(bootstrap);
    SeedAdminUserFromEnv(bootstrap);
    var entry = bootstrap.ConfigEntries.Find("webui.jwt.secret");
    if (entry is not null)
    {
        return System.Text.Json.JsonSerializer.Deserialize<string>(entry.ValueJson)!;
    }

    var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
    bootstrap.ConfigEntries.Add(new ConfigEntry
    {
        Key = "webui.jwt.secret",
        ValueJson = System.Text.Json.JsonSerializer.Serialize(secret),
        UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    });
    bootstrap.SaveChanges();
    return secret;
}

static void ConfigureDatabase(DbContextOptionsBuilder options, string connectionString) =>
    options.UseSqlite(connectionString);

/// <summary>
/// Semeia as conexões com provedores a partir de variáveis de ambiente (mesmos
/// nomes do Open WebUI original) apenas na primeira execução — depois disso a
/// configuração é gerenciada pela UI admin.
/// </summary>
static void SeedConnectionsFromEnv(AppDbContext db)
{
    if (db.ConfigEntries.Find("connections") is not null)
    {
        return;
    }

    var ollama = SplitEnvUrls(
        Environment.GetEnvironmentVariable("OLLAMA_BASE_URLS")
        ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL"));
    var openAi = SplitEnvUrls(
        Environment.GetEnvironmentVariable("OPENAI_API_BASE_URLS")
        ?? Environment.GetEnvironmentVariable("OPENAI_API_BASE_URL"));
    var openAiKeys = SplitEnvUrls(
        Environment.GetEnvironmentVariable("OPENAI_API_KEYS")
        ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
    var anthropicKeys = SplitEnvUrls(
        Environment.GetEnvironmentVariable("ANTHROPIC_API_KEYS")
        ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
    var anthropicBase = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL")
        ?? ProviderTypes.DefaultBaseUrl(ProviderTypes.Anthropic);
    var googleKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
        ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
    var googleBase = Environment.GetEnvironmentVariable("GOOGLE_BASE_URL")
        ?? ProviderTypes.DefaultBaseUrl(ProviderTypes.Google);

    var providers = new List<ProviderConnection>();
    foreach (var key in anthropicKeys)
    {
        providers.Add(new ProviderConnection(ProviderTypes.Anthropic, anthropicBase, key));
    }

    if (!string.IsNullOrWhiteSpace(googleKey))
    {
        providers.Add(new ProviderConnection(ProviderTypes.Google, googleBase, googleKey));
    }

    if (ollama.Count == 0 && openAi.Count == 0 && providers.Count == 0)
    {
        return;
    }

    db.ConfigEntries.Add(new ConfigEntry
    {
        Key = "connections",
        ValueJson = System.Text.Json.JsonSerializer.Serialize(
            new ConnectionsConfig(ollama, openAi, openAiKeys, Providers: providers)),
        UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    });
    db.SaveChanges();
}

/// <summary>WHISPER_URL configura a engine STT whisper (faster-whisper-server) no primeiro boot.</summary>
static void SeedWhisperUrlFromEnv(AppDbContext db)
{
    var whisperUrl = Environment.GetEnvironmentVariable("WHISPER_URL");
    if (string.IsNullOrWhiteSpace(whisperUrl)
        || db.ConfigEntries.Find("audio.config") is not null)
    {
        return;
    }

    var config = AudioConfig.Default with { SttEngine = "whisper", SttBaseUrl = whisperUrl };
    db.ConfigEntries.Add(new ConfigEntry
    {
        Key = "audio.config",
        ValueJson = System.Text.Json.JsonSerializer.Serialize(
            config, new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web)),
        UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    });
    db.SaveChanges();
}

/// <summary>
/// Cria o usuário admin inicial a partir de <c>ADMIN_EMAIL</c> +
/// <c>ADMIN_PASSWORD</c> (opcional <c>ADMIN_NAME</c>) somente quando a base
/// está vazia — equivalente ao primeiro signup virar admin, mas via env.
/// </summary>
static void SeedAdminUserFromEnv(AppDbContext db)
{
    var email = Environment.GetEnvironmentVariable("ADMIN_EMAIL")?.Trim().ToLowerInvariant();
    var password = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)
        || db.Users.Any())
    {
        return;
    }

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var name = Environment.GetEnvironmentVariable("ADMIN_NAME");
    var user = new User
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Admin" : name.Trim(),
        Email = email,
        Role = UserRoles.Admin,
        PermissionsJson = "{}",
        CreatedAt = now,
        UpdatedAt = now,
    };
    user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
    db.Users.Add(user);
    db.SaveChanges();
}

static List<string> SplitEnvUrls(string? value) =>
    value?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? [];

static DbContextOptions<AppDbContext> CreateDbOptions(string connectionString)
{
    var builder = new DbContextOptionsBuilder<AppDbContext>();
    ConfigureDatabase(builder, connectionString);
    return builder.Options;
}

/// <summary>Ponto de entrada para testes de integração com WebApplicationFactory.</summary>
public partial class Program;

namespace OpenWebUI.Api
{
    /// <summary>Identidade serializável do dono de uma API key (ClaimsIdentity não serializa no HybridCache).</summary>
    internal sealed record ApiKeyIdentity(string Id, string Name, string Email, string Role);
}
