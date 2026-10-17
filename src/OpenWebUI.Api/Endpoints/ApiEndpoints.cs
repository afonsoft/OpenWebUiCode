using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Api.Completions;

namespace OpenWebUI.Api.Endpoints;

/// <summary>Endpoints de modelos, completions, configuração pública e conexões.</summary>
public static class ApiEndpoints
{
    private const string BackendVersion = "0.11.4-dotnet";

    /// <summary>Mapeia as rotas de modelos, completions e configuração.</summary>
    public static void MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = true }));
        app.MapGet("/api/version", () => Results.Ok(new VersionResponse(BackendVersion)));
        app.MapGet("/api/version/updates", () =>
            Results.Ok(new VersionUpdateResponse(BackendVersion, BackendVersion)));
        app.MapGet("/api/changelog", () => Results.Ok(new { releases = Array.Empty<object>() }));

        app.MapGet("/api/config", GetAppConfigAsync);
        app.MapPost("/api/config", UpdateAppConfigAsync).RequireAuthorization();
        app.MapGet("/api/models", ListAllModelsAsync).RequireAuthorization();
        app.MapGet("/api/v1/models/base", ListAllModelsAsync).RequireAuthorization();
        app.MapPost("/api/chat/completions", ChatCompletionsAsync)
            .RequireAuthorization()
            .AddEndpointFilter(CompletionRateLimitFilterAsync);
        app.MapPost("/api/chat/completed", () => Results.Ok(new StatusResponse(true)))
            .RequireAuthorization();

        var configs = app.MapGroup("/api/v1/configs").RequireAuthorization();
        configs.MapGet("/connections", GetConnectionsAsync);
        configs.MapPost("/connections", UpdateConnectionsAsync)
            .AddEndpointFilter(InvalidateModelListCacheAsync);
        configs.MapGet("/capabilities", GetCapabilitiesAsync);
        configs.MapGet("/connections/models", ListConnectionModelsAsync);
        configs.MapGet("/connections/capabilities", GetConnectionCapabilitiesAsync);
        configs.MapGet("/export", ExportConfigAsync);
        configs.MapPost("/import", ImportConfigAsync);
    }

    /// <summary>
    /// Invalida o cache HybridCache da lista agregada de modelos (tag "models")
    /// após uma mutação bem-sucedida — aplicado nos grupos de models/connections.
    /// </summary>
    internal static async ValueTask<object?> InvalidateModelListCacheAsync(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var result = await next(ctx);
        if (!HttpMethods.IsGet(ctx.HttpContext.Request.Method)
            && ctx.HttpContext.Response.StatusCode < 400)
        {
            var cache = ctx.HttpContext.RequestServices.GetRequiredService<HybridCache>();
            await cache.RemoveByTagAsync("models");
            await cache.RemoveByTagAsync("providers");
        }
        return result;
    }

    /// <summary>
    /// Filtro de rate limiting para completions: aplica a janela por usuário
    /// apenas quando habilitado na config (default desligado — compat).
    /// </summary>
    private static async ValueTask<object?> CompletionRateLimitFilterAsync(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var config = http.RequestServices.GetRequiredService<ConfigService>();
        var limits = http.RequestServices.GetRequiredService<RateLimitService>();
        var rateConfig = await config.GetAsync(
            "ratelimit", RateLimitConfig.Default, http.RequestAborted);
        if (rateConfig.Enabled)
        {
            var userId = http.User
                .FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                ?? http.Connection.RemoteIpAddress?.ToString()
                ?? "anon";
            var retryAfter = limits.TryAcquire(
                userId, rateConfig, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (retryAfter is { } retry)
            {
                http.Response.Headers.RetryAfter = retry.ToString();
                return Results.Json(
                    new { detail = "Limite de requisições excedido. Tente novamente em instantes." },
                    statusCode: 429);
            }
        }

        return await next(ctx);
    }

    private static async Task<IResult> GetAppConfigAsync(
        HttpContext http, ConfigService config, ImageGenerationService images,
        VideoGenerationService videos, AudioService audio, CancellationToken ct)
    {
        var adminConfig = await config.GetAdminConfigAsync(ct);
        var imagesConfig = await images.GetConfigAsync(ct);
        var videoConfig = await videos.GetConfigAsync(ct);
        var audioConfig = await audio.GetResolvedConfigAsync(ct);
        var modelsConfig = await ConfigEndpoints.GetModelsConfigInternalAsync(config, ct);
        var retrieval = await config.GetAsync("retrieval.config", RetrievalConfig.Default, ct);

        return Results.Ok(new AppConfigResponse(
            Status: true,
            Name: adminConfig.WebUiName,
            Version: BackendVersion,
            DefaultLocale: "pt-BR",
            Features: new AppFeatures(
                Auth: true,
                EnableSignup: adminConfig.EnableSignup,
                EnableLoginForm: adminConfig.EnableLoginForm,
                EnableApiKeys: adminConfig.EnableApiKeys,
                EnableMessageRating: adminConfig.EnableMessageRating,
                EnableFolders: adminConfig.EnableFolders,
                EnableMemories: adminConfig.EnableMemories,
                EnableNotes: true,
                EnableChannels: true,
                EnableWebSearch: retrieval.Engine is not "none",
                EnableImageGeneration: imagesConfig.Enabled,
                EnableCodeExecution: true,
                EnableCommunitySharing: true,
                EnableVideoGeneration: videoConfig.Enabled,
                EnableTextToSpeech: audioConfig.TtsEnabled,
                EnableSpeechToText: audioConfig.SttEnabled),
            DefaultModels: modelsConfig.DefaultModels,
            DefaultPromptSuggestions: modelsConfig.PromptSuggestions,
            OAuthProviders: OAuthProviderCatalog.ConfiguredProviders()));
    }

    /// <summary>Persiste as feature flags administráveis (somente admin).</summary>
    private static async Task<IResult> UpdateAppConfigAsync(
        AdminConfig request, HttpContext http, ConfigService config, CancellationToken ct)
    {
        if (!http.User.IsInRole(UserRoles.Admin))
        {
            return Results.Forbid();
        }

        await config.SetAsync("admin.config", request, ct);
        return Results.Ok(request);
    }

    private static async Task<IResult> ListAllModelsAsync(
        HttpContext http, ProviderService providers, AppDbContext db,
        PipelineClientService pipelines, HybridCache cache,
        ModelCapabilityResolver capabilities, CancellationToken ct)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var response = await GetCachedModelListAsync(
            user, providers, db, pipelines, cache, capabilities, ct);
        var disabled = ParseDisabledModels(user.SettingsJson);
        var models = disabled.Count == 0
            ? response.Data
            : response.Data.Where(m =>
                !disabled.Contains($"{m.Provider}:{m.Id}")).ToList();
        return Results.Ok(new ModelListResponse(models));
    }

    /// <summary>Lista agregada de modelos do usuário cacheada por 60s
    /// (sem o filtro de visibilidade — aplicado por request).</summary>
    internal static async Task<ModelListResponse> GetCachedModelListAsync(
        User user, ProviderService providers, AppDbContext db,
        PipelineClientService pipelines, HybridCache cache,
        ModelCapabilityResolver capabilities, CancellationToken ct)
        => await cache.GetOrCreateAsync(
            $"models:list:{user.Id}",
            async _ => await BuildModelListAsync(
                user, providers, db, pipelines, capabilities, ct),
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromSeconds(60),
                LocalCacheExpiration = TimeSpan.FromSeconds(60),
            },
            tags: ["models", $"models:{user.Id}"],
            cancellationToken: ct);

    /// <summary>Conjunto de modelos desabilitados do usuário
    /// (<c>disabledModels: ["{provider}:{id}"]</c> em SettingsJson; default = nenhum).
    /// Comparação case-insensitive; entradas obsoletas são ignoradas.</summary>
    internal static HashSet<string> ParseDisabledModels(string? settingsJson)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return set;
        }
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.TryGetProperty("disabledModels", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String
                        && item.GetString() is { } entry && entry.Contains(':'))
                    {
                        set.Add(entry);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Settings corrompido não deve derrubar a listagem — trata como vazio.
        }
        return set;
    }

    internal static async Task<ModelListResponse> BuildModelListAsync(
        User user, ProviderService providers, AppDbContext db,
        PipelineClientService pipelines, ModelCapabilityResolver capabilities,
        CancellationToken ct)
    {
        var models = await providers.ListModelsAsync(ct);

        // Inclui modelos personalizados ativos do workspace (do usuário + públicos).
        var custom = await db.ModelEntries.AsNoTracking()
            .Where(m => m.IsActive && (m.UserId == user.Id || m.UserId == "public"))
            .ToListAsync(ct);

        foreach (var entry in custom)
        {
            if (await ModelEndpoints.HasModelAccessAsync(user, entry, db, ct))
            {
                models.Add(new ModelInfo(entry.Id, entry.Name, "custom", "openwebui"));
            }
        }

        // Pipes de servidores de pipelines registrados (expostos como pipeline:{id}).
        var servers = await db.PipelineServers.AsNoTracking().ToListAsync(ct);
        foreach (var server in servers)
        {
            var pipes = await pipelines.ListPipesAsync(server, ct);
            if (pipes is not null)
            {
                models.AddRange(pipes.Select(
                    p => new ModelInfo($"pipeline:{p.Id}", p.Name, "pipeline", "openwebui")));
            }
        }

        // Capacidades: override admin → reportado pelo provider → catálogo.
        models = await capabilities.EnrichAsync(models, ct);
        return new ModelListResponse(models);
    }

    private static async Task ChatCompletionsAsync(
        ChatCompletionRequest request,
        HttpContext http,
        AppDbContext db,
        ConfigService config,
        ProviderService providers,
        RagService rag,
        ToolExecutor toolExecutor,
        PipelineClientService pipelines,
        WebSearchService webSearch,
        WorkspaceRepoService repos,
        IWebHostEnvironment env,
        CancellationToken ct)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Connection = "keep-alive";
        // Desativa buffering do SSE em proxies (nginx) — senão os deltas
        // só chegam ao cliente quando a resposta fecha.
        http.Response.Headers["X-Accel-Buffering"] = "no";

        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            http.Response.StatusCode = 401;
            return;
        }

        await using var writer = new StreamWriter(http.Response.Body, Encoding.UTF8);
        Task EmitAsync(string line) { writer.WriteLine(line); writer.WriteLine(); return writer.FlushAsync(http.RequestAborted); }

        // Modelos pipeline:{id} são roteados ao servidor de pipelines externo.
        if (request.Model.StartsWith("pipeline:", StringComparison.Ordinal))
        {
            var pipeId = request.Model["pipeline:".Length..];
            var (server, body, valvesJson) = await ChatPipeline.PreparePipelineRouteAsync(
                request, db, pipelines, ct);
            if (server is null)
            {
                http.Response.StatusCode = 404;
                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(new { error = $"Pipe '{pipeId}' não encontrado." }));
                await writer.FlushAsync(http.RequestAborted);
                return;
            }

            var proxied = await pipelines.RouteCompletionAsync(server, body, valvesJson, ct);
            if (proxied.Response is null)
            {
                http.Response.StatusCode = proxied.StatusCode;
                await writer.WriteLineAsync(
                    JsonSerializer.Serialize(new { error = $"Falha no servidor de pipelines: {proxied.Error}" }));
                await writer.FlushAsync(http.RequestAborted);
                return;
            }

            using var upstream = proxied.Response;
            http.Response.StatusCode = (int)upstream.StatusCode;
            await upstream.Content.CopyToAsync(http.Response.Body, ct);
            return;
        }

        // Modelos arena geram duas respostas anonimizadas de concorrentes sorteados.
        var arenaModel = request.Model.StartsWith("arena:", StringComparison.Ordinal)
            ? request.Model["arena:".Length..]
            : request.Model;
        if (await ChatPipeline.TryRunArenaAsync(
            request, arenaModel, user, db, config, rag, providers, webSearch, repos, EmitAsync, ct))
        {
            return;
        }

        var effective = await ChatPipeline.EnrichRequestAsync(
            request, user, db, config, rag, webSearch, repos, ct);

        // Filtros outlet do modelo custom: redação por regex nas linhas SSE.
        var outletRules = ModelFilterService.OutletRules(
            ModelFilterService.Parse(await db.ModelEntries.AsNoTracking()
                .Where(m => m.IsActive && (m.Id == request.Model || m.Name == request.Model)
                    && (m.UserId == user.Id || m.UserId == "public"))
                .Select(m => m.MetaJson).FirstOrDefaultAsync(ct)));

        try
        {
            var tools = request.ToolIds is { Count: > 0 }
                ? await toolExecutor.LoadEnabledAsync(user.Id, request.ToolIds, ct)
                : null;
            if (tools is { Count: > 0 })
            {
                effective = effective with
                {
                    Tools = tools
                        .Select(t => JsonSerializer.Deserialize<JsonElement>(t.SpecJson))
                        .ToList(),
                };
                var builtinContext = new BuiltinToolContext(
                    user.Id,
                    ChatId: null,
                    RunId: null,
                    // Com repo vinculado o workdir vira o checkout do repo (mesmo jail).
                    await repos.ResolveWorkdirAsync(user.Id, ct),
                    Path.Join(DataPaths.Root(env.ContentRootPath), "uploads", user.Id));
                var outcome = await ChatPipeline.RunToolLoopAsync(
                    effective, tools, toolExecutor, providers, ct,
                    builtinContext: builtinContext, userId: user.Id,
                    maxSteps: await config.GetAsync("agentLoop.maxRounds", 25, ct),
                    maxParallelTools: toolExecutor.MaxParallel);
                if (outcome?.FinalContent is { } finished)
                {
                    foreach (var (regex, replacement) in outletRules)
                    {
                        finished = regex.Replace(finished, replacement);
                    }
                    var chunk = JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { index = 0, delta = new { content = finished } } },
                    });
                    await writer.WriteLineAsync($"data: {chunk}");
                    await writer.WriteLineAsync();
                    await writer.WriteLineAsync("data: [DONE]");
                    await writer.WriteLineAsync();
                    await writer.FlushAsync(http.RequestAborted);
                    return;
                }
            }

            await foreach (var line in providers.StreamCompletionAsync(effective, user.Id, ct))
            {
                await writer.WriteLineAsync(
                    ModelFilterService.ProcessSseLine(line, outletRules));
                await writer.WriteLineAsync();
                await writer.FlushAsync(http.RequestAborted);
            }
        }
        catch (InvalidOperationException ex)
        {
            await WriteSseErrorAsync(writer, ex.Message, http.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            await WriteSseErrorAsync(writer, $"Falha ao contactar o provedor: {ex.Message}", http.RequestAborted);
        }
    }

    private static async Task WriteSseErrorAsync(StreamWriter writer, string message, CancellationToken ct)
    {
        var error = JsonSerializer.Serialize(new { error = message });
        await writer.WriteLineAsync($"data: {error}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync("data: [DONE]");
        await writer.WriteLineAsync();
        await writer.FlushAsync(ct);
    }

    private static async Task<IResult> GetConnectionsAsync(
        HttpContext http, ConfigService config, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }

        var connections = await config.GetConnectionsAsync(ct);
        return Results.Ok(new ConnectionsConfigResponse(
            connections.OllamaBaseUrls,
            connections.OpenAiBaseUrls,
            connections.OpenAiBaseUrls
                .Select((_, i) => !string.IsNullOrEmpty(connections.OpenAiApiKeys.ElementAtOrDefault(i)))
                .ToList(),
            connections.OllamaNames,
            connections.OpenAiNames,
            connections.ProvidersOrEmpty
                .Select(p => new ProviderConnectionResponse(
                    p.Type, p.BaseUrl, !string.IsNullOrEmpty(p.ApiKey), p.Name))
                .ToList()));
    }

    /// <summary>
    /// Modelos detectados por capacidade (admin). Lê o kv persistido pela
    /// detecção; se nunca rodou, faz uma classificação rápida do catálogo
    /// (GET /models, sem probes) — suficiente para os combos.
    /// </summary>
    private static async Task<IResult> GetCapabilitiesAsync(
        HttpContext http, ConfigService config,
        ProviderCapabilityService capabilities, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }

        var detected = await capabilities.GetAggregatedAsync(ct);
        if (detected is null)
        {
            var connections = await config.GetConnectionsAsync(ct);
            for (var i = 0; i < connections.OpenAiBaseUrls.Count && detected is null; i++)
            {
                try
                {
                    detected = await capabilities.DetectConnectionAsync("openai", i, ct);
                }
                catch (HttpRequestException) { /* provider fora do ar */ }
                catch (JsonException) { /* resposta malformada */ }
                catch (InvalidOperationException) { /* provider fora do ar */ }
            }
        }

        return detected is null
            ? Results.Ok(new DetectedCapabilities([], [], [], [], [], null, 0))
            : Results.Ok(detected);
    }

    /// <summary>
    /// Capacidades detectadas de UMA conexão (admin) — os combos de STT/TTS,
    /// imagem e vídeo filtram pelos modelos do provider selecionado, não pelo
    /// catálogo inteiro nem por outro provider. Faz a detecção rápida
    /// (/models + heurística) e atualiza o mapa persistido.
    /// </summary>
    private static async Task<IResult> GetConnectionCapabilitiesAsync(
        HttpContext http, ProviderCapabilityService capabilities,
        HybridCache cache, string type, int index, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }
        if (type is not ("openai" or "ollama"))
        {
            return Results.BadRequest(new { detail = "type deve ser openai|ollama." });
        }

        var detected = await cache.GetOrCreateAsync(
            $"caps:{type}:{index}",
            async _ =>
            {
                try
                {
                    return await capabilities.DetectConnectionAsync(type, index, ct);
                }
                catch (Exception)
                {
                    // Provider fora do ar: devolve vazio — combos caem em texto livre.
                    return null;
                }
            },
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromSeconds(60),
                LocalCacheExpiration = TimeSpan.FromSeconds(60),
            },
            tags: ["providers"],
            cancellationToken: ct);
        return Results.Ok(detected ?? new DetectedCapabilities([], [], [], [], [], null, 0));
    }

    /// <summary>Lista modelos de uma conexão cadastrada (admin) — usado pelos combos da UI.</summary>
    private static async Task<IResult> ListConnectionModelsAsync(
        HttpContext http, ProviderService providers, HybridCache cache,
        string type, int index, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }

        var models = await cache.GetOrCreateAsync(
            $"connmodels:{type}:{index}",
            async _ => await providers.ListModelsForConnectionAsync(type, index, ct),
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromSeconds(60),
                LocalCacheExpiration = TimeSpan.FromSeconds(60),
            },
            tags: ["providers"],
            cancellationToken: ct);
        return Results.Ok(new ModelListResponse(models));
    }

    private static async Task<IResult> UpdateConnectionsAsync(
        ConnectionsConfig request,
        HttpContext http,
        ConfigService config,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }

        // Preserva chaves existentes quando o campo vier vazio (UI não reenvia segredos).
        var current = await config.GetConnectionsAsync(ct);
        var currentUrls = current.OpenAiBaseUrls.ToList();
        var keys = request.OpenAiBaseUrls
            .Select((url, i) =>
            {
                var incoming = request.OpenAiApiKeys.ElementAtOrDefault(i);
                if (!string.IsNullOrEmpty(incoming))
                {
                    return incoming;
                }

                var previousIndex = currentUrls.IndexOf(url);
                return previousIndex >= 0 ? current.OpenAiApiKeys.ElementAtOrDefault(previousIndex) ?? string.Empty : string.Empty;
            })
            .ToList();

        // Conexões tipadas: valida tipo/chave e preserva chaves existentes
        // quando o campo vier vazio (a UI não reenvia segredos).
        var typedProviders = new List<ProviderConnection>();
        foreach (var p in request.ProvidersOrEmpty)
        {
            var type = p.Type?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!ProviderTypes.All.Contains(type))
            {
                return Results.BadRequest(new { error = $"provider type desconhecido: {p.Type}" });
            }

            var baseUrl = p.BaseUrl?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(baseUrl))
            {
                return Results.BadRequest(new { error = $"provider {type} sem baseUrl" });
            }

            var previous = current.ProvidersOrEmpty.FirstOrDefault(c =>
                string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.BaseUrl?.TrimEnd('/'), baseUrl.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase));
            var apiKey = !string.IsNullOrEmpty(p.ApiKey) ? p.ApiKey : previous?.ApiKey;
            if (ProviderTypes.KeyRequired(type) && string.IsNullOrEmpty(apiKey))
            {
                return Results.BadRequest(new { error = $"provider {type} exige apiKey" });
            }

            typedProviders.Add(new ProviderConnection(type, baseUrl, apiKey, p.Name));
        }

        var updated = request with { OpenAiApiKeys = keys, Providers = typedProviders };
        await config.SetAsync("connections", updated, ct);

        // Detecção de capacidades em background: classifica /models e
        // probeia TTS/imagem/vídeo/embeddings, preenchendo configs ausentes.
        _ = Task.Run(async () =>
        {
            var log = loggerFactory.CreateLogger("OpenWebUI.ProviderCapability");
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ProviderCapabilityService>()
                    .AutoConfigureAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Auto-config de capacidades falhou.");
            }
        }, CancellationToken.None);

        return Results.Ok(new ConnectionsConfigResponse(
            updated.OllamaBaseUrls,
            updated.OpenAiBaseUrls,
            updated.OpenAiBaseUrls
                .Select((_, i) => !string.IsNullOrEmpty(updated.OpenAiApiKeys.ElementAtOrDefault(i)))
                .ToList(),
            updated.OllamaNames,
            updated.OpenAiNames,
            updated.ProvidersOrEmpty
                .Select(p => new ProviderConnectionResponse(
                    p.Type, p.BaseUrl, !string.IsNullOrEmpty(p.ApiKey), p.Name))
                .ToList()));
    }

    private static async Task<IResult> ExportConfigAsync(
        HttpContext http, ConfigService config, AppDbContext db, CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }

        var entries = await db.ConfigEntries.AsNoTracking()
            .ToDictionaryAsync(e => e.Key, e => e.ValueJson, ct);
        return Results.Ok(entries);
    }

    private static async Task<IResult> ImportConfigAsync(
        Dictionary<string, JsonElement> request,
        HttpContext http,
        ConfigService config,
        CancellationToken ct)
    {
        if (!IsAdmin(http))
        {
            return Results.Forbid();
        }

        foreach (var (key, value) in request)
        {
            await config.SetAsync(key, value, ct);
        }

        return Results.Ok(new StatusResponse(true));
    }

    private static bool IsAdmin(HttpContext http) =>
        http.User.IsInRole(UserRoles.Admin);
}
