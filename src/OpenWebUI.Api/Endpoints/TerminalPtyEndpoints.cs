using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.Terminal;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Terminal PTY embutido: sessões de shell por usuário no workspace
/// (<c>data/workspaces/{userId}</c>) multiplexadas por WebSocket — o terminal
/// xterm.js da UI. Feature flag: ligada por padrão — admin desativa por
/// <c>PUT /api/v1/terminal/config</c> ou <c>Terminal:Enabled</c>/
/// <c>TERMINAL_ENABLED</c>.
/// </summary>
public static class TerminalPtyEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan WsIdleTimeout = TimeSpan.FromMinutes(60);

    /// <summary>Mapeia as rotas REST do terminal PTY.</summary>
    public static RouteGroupBuilder MapTerminalPtyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/terminal").RequireAuthorization();

        group.MapGet("/config", (HttpContext http, ConfigService config,
            IConfiguration configuration, CancellationToken ct) =>
            ConfigGetAsync(http, config, configuration, ct));
        group.MapPut("/config", (HttpContext http, ConfigService config,
            AppDbContext db, CancellationToken ct) =>
            ConfigPutAsync(http, config, ct));

        group.MapPost("/sessions", (HttpContext http,
            [AsParameters] TerminalDeps deps, CancellationToken ct) =>
            CreateSessionAsync(http, deps, ct));
        group.MapGet("/sessions", (HttpContext http, TerminalSessionManager terminals,
            ConfigService config, IConfiguration configuration,
            string? chatId, CancellationToken ct) =>
            ListSessionsAsync(http, terminals, config, configuration, chatId, ct));
        group.MapDelete("/sessions/{id}", (HttpContext http, string id,
            TerminalSessionManager terminals, ConfigService config,
            IConfiguration configuration, CancellationToken ct) =>
            DeleteSessionAsync(http, id, terminals, config, configuration, ct));

        return group;
    }

    /// <summary>Mapeia o WebSocket multiplexador <c>/ws/terminal/{id}</c>.</summary>
    public static IEndpointConventionBuilder MapTerminalWebSocket(this IEndpointRouteBuilder app) =>
        app.Map("/ws/terminal/{id}", (HttpContext http, string id,
            TerminalSessionManager terminals, ConfigService config,
            IConfiguration configuration, CancellationToken ct) =>
            TerminalWsAsync(http, id, terminals, config, configuration, ct))
        .RequireAuthorization();

    /// <summary>
    /// Mapeia o WebSocket multiplex <c>/ws/terminal</c> (SPEC-20261022): uma
    /// conexão carrega N sessões — frames <c>{type, sid, ...}</c> com
    /// <c>attach</c>/<c>detach</c>/<c>input</c>/<c>resize</c>/<c>create</c>/
    /// <c>kill</c>. O endpoint por-sessão permanece para compat.
    /// </summary>
    public static IEndpointConventionBuilder MapTerminalMultiplexWebSocket(
        this IEndpointRouteBuilder app) =>
        app.Map("/ws/terminal", (HttpContext http,
            [AsParameters] TerminalDeps deps, CancellationToken ct) =>
            TerminalMuxWsAsync(http, deps, ct))
        .RequireAuthorization();

    private static Task<IResult> ConfigGetAsync(
        HttpContext http, ConfigService config, IConfiguration configuration,
        CancellationToken ct) =>
        FeatureFlagEndpoints.GetAsync(
            http, config, configuration, FeatureFlagEndpoints.Terminal, ct);

    private static Task<IResult> ConfigPutAsync(
        HttpContext http, ConfigService config, CancellationToken ct) =>
        FeatureFlagEndpoints.PutAsync(http, config, FeatureFlagEndpoints.Terminal, ct);

    private static async Task<IResult> CreateSessionAsync(
        HttpContext http, TerminalDeps deps, CancellationToken ct)
    {
        var (user, error) = await AuthorizedAsync(
            http, deps.Config, deps.Configuration, ct);
        if (error is not null)
        {
            return error;
        }

        var request = http.Request.ContentLength is > 0
            ? await JsonSerializer.DeserializeAsync<TerminalSessionRequest>(
                http.Request.Body, JsonOptions, ct)
            : null;

        // SPEC-20261022: sessão bindada — resolve o cwd pela ordem
        // workdir explícito (contido em DataPaths.Root) → worktree isolado
        // da run → workspace binding do chat → root do usuário (default).
        var (workdir, workdirError) = await ResolveBoundWorkdirAsync(
            user!.Id, request?.Workdir, request?.RunId, request?.ChatId, deps, ct);
        if (workdirError is not null)
        {
            return workdirError;
        }

        // Secrets do usuário ∪ global ∪ managed GitHub entram na env da PTY
        // (SPEC-20261017-agent-secrets-env RF-006) — sessão é por usuário.
        var secrets = await deps.AgentEnv.BuildAsync(user!.Id, ct);
        try
        {
            var id = deps.Terminals.Create(
                user!.Id, request?.Cols ?? 120, request?.Rows ?? 30,
                secrets.Vars, workdir, request?.ChatId, request?.RunId);
            return Results.Created($"/api/v1/terminal/sessions/{id}", new { id });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: 429);
        }
    }

    /// <summary>
    /// Resolve o cwd de uma sessão bindada (compartilhado pelo POST REST e
    /// pelo <c>create</c> do WS multiplex): workdir explícito contido em
    /// <see cref="DataPaths.Root"/> → worktree isolado da run → binding do
    /// chat → null (o manager cai no root do usuário).
    /// </summary>
    private static async Task<(string? Workdir, IResult? Error)>
        ResolveBoundWorkdirAsync(
            string userId, string? workdir, string? runId, string? chatId,
            TerminalDeps deps, CancellationToken ct)
    {
        if (workdir is not null)
        {
            return IsInsideDataRoot(workdir, deps.Env)
                ? (workdir, null)
                : ((string?)null, Results.Problem(
                    "workdir deve ficar dentro de data/.", statusCode: 400));
        }
        if (runId is not null)
        {
            workdir = deps.Worktrees.ResolveIsolated(userId, runId);
        }
        if (workdir is null && chatId is not null)
        {
            workdir = await deps.Repos.ResolveWorkdirAsync(userId, chatId, ct);
        }
        return (workdir, null);
    }

    /// <summary>Path-traversal guard: workdir explícito só vale dentro de
    /// <see cref="DataPaths.Root"/> — senão o PTY abriria fora do sandbox
    /// de dados do app.</summary>
    private static bool IsInsideDataRoot(string workdir, IHostEnvironment env)
    {
        var root = Path.GetFullPath(DataPaths.Root(env.ContentRootPath));
        var full = Path.GetFullPath(workdir);
        return full.StartsWith(
            root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static async Task<IResult> ListSessionsAsync(
        HttpContext http, TerminalSessionManager terminals,
        ConfigService config, IConfiguration configuration,
        string? chatId, CancellationToken ct)
    {
        var (user, error) = await AuthorizedAsync(http, config, configuration, ct);
        if (error is not null)
        {
            return error;
        }

        var sessions = terminals.List(user!.Id, chatId).Select(s =>
            new TerminalSessionInfoResponse(
                s.Id, s.IsRunning, s.CreatedAtUtc, s.LastActivityUtc,
                s.ExitCode, s.ChatId, s.RunId, s.Workdir));
        return Results.Ok(sessions);
    }

    private static async Task<IResult> DeleteSessionAsync(
        HttpContext http, string id, TerminalSessionManager terminals,
        ConfigService config, IConfiguration configuration,
        CancellationToken ct)
    {
        var (user, error) = await AuthorizedAsync(http, config, configuration, ct);
        if (error is not null)
        {
            return error;
        }

        return await terminals.KillAsync(user!.Id, id)
            ? Results.Ok(new { ok = true })
            : Results.NotFound(new { detail = "Sessão de terminal não encontrada." });
    }

    private static async Task<IResult> TerminalWsAsync(
        HttpContext http, string id, TerminalSessionManager terminals,
        ConfigService config, IConfiguration configuration,
        CancellationToken ct)
    {
        var (user, error) = await AuthorizedAsync(http, config, configuration, ct);
        if (error is not null)
        {
            return error;
        }

        if (!http.WebSockets.IsWebSocketRequest)
        {
            return Results.BadRequest(new { detail = "Esperado WebSocket." });
        }

        using var sendLock = new SemaphoreSlim(1, 1);
        using var socket = await http.WebSockets.AcceptWebSocketAsync();

        Task SendOutput(string chunk) =>
            SendJsonAsync(socket, sendLock, new
            {
                type = "output",
                data = chunk,
            }, ct);
        Task SendExit(int code) =>
            SendJsonAsync(socket, sendLock, new { type = "exit", code }, ct);

        using var handle = terminals.Attach(user!.Id, id, SendOutput, SendExit);
        if (handle is null)
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.PolicyViolation, "sessão não encontrada", ct);
            return Results.Empty;
        }

        if (handle.Scrollback.Length > 0)
        {
            await SendOutput(handle.Scrollback);
        }

        if (handle.ExitCode is { } exitCode)
        {
            await SendExit(exitCode);
        }

        // Loop de recepção: frames JSON {"type":"input"|"resize",...}; texto
        // não-JSON é tratado como input verbatim.
        var buffer = new byte[16 * 1024];
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(WsIdleTimeout);
        try
        {
            while (!idleCts.IsCancellationRequested
                   && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                var result = await socket.ReceiveAsync(buffer, idleCts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                idleCts.CancelAfter(WsIdleTimeout);
                if (!TryHandleFrame(text, handle, out var rawInput))
                {
                    await handle.WriteAsync(rawInput);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Idle-timeout ou shutdown — desanexa sem matar a sessão.
        }
        catch (WebSocketException)
        {
            // Cliente caiu — sessão segue viva para reconexão.
        }

        return Results.Empty;
    }

    /// <summary>
    /// WebSocket multiplex (SPEC-20261022 RF-A2): uma conexão gerencia N
    /// sessões. Cliente → servidor: <c>{type:"attach",sid}</c>,
    /// <c>{type:"detach",sid}</c>, <c>{type:"input",sid,data}</c>,
    /// <c>{type:"resize",sid,cols,rows}</c>, <c>{type:"create",...}</c>,
    /// <c>{type:"kill",sid}</c>. Servidor → cliente: <c>{type:"sessions"}</c>
    /// inicial, <c>{type:"output",sid,data}</c>,
    /// <c>{type:"exit",sid,code}</c>, <c>{type:"created",sid}</c>,
    /// <c>{type:"error",sid?,detail}</c>. Detach ao desconectar — sessões
    /// sobrevivem para reattach (mesmo modelo do agent-harness).
    /// </summary>
    private static async Task<IResult> TerminalMuxWsAsync(
        HttpContext http, TerminalDeps deps, CancellationToken ct)
    {
        var (user, error) = await AuthorizedAsync(
            http, deps.Config, deps.Configuration, ct);
        if (error is not null)
        {
            return error;
        }

        if (!http.WebSockets.IsWebSocketRequest)
        {
            return Results.BadRequest(new { detail = "Esperado WebSocket." });
        }

        using var sendLock = new SemaphoreSlim(1, 1);
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var handles = new Dictionary<string, TerminalSessionManager.SessionHandle>();
        var uid = user!.Id;

        Task Send(object payload) =>
            SendJsonAsync(socket, sendLock, payload, ct);

        async Task AttachAsync(string sid)
        {
            if (handles.ContainsKey(sid))
            {
                return;
            }

            var h = deps.Terminals.Attach(
                uid, sid,
                chunk => Send(new { type = "output", sid, data = chunk }),
                code => Send(new { type = "exit", sid, code }));
            if (h is null)
            {
                await Send(new { type = "error", sid, detail = "Sessão não encontrada." });
                return;
            }

            handles[sid] = h;
            if (h.Scrollback.Length > 0)
            {
                await Send(new { type = "output", sid, data = h.Scrollback });
            }
            if (h.ExitCode is { } code)
            {
                await Send(new { type = "exit", sid, code });
            }
        }

        // Lista inicial — o cliente abre abas para sessões já vivas.
        await Send(new
        {
            type = "sessions",
            items = deps.Terminals.List(uid).Select(s => new
            {
                s.Id, s.IsRunning, s.ExitCode, s.ChatId, s.RunId, s.Workdir,
            }),
        });

        var buffer = new byte[16 * 1024];
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(WsIdleTimeout);
        try
        {
            while (!idleCts.IsCancellationRequested
                   && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                var result = await socket.ReceiveAsync(buffer, idleCts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                idleCts.CancelAfter(WsIdleTimeout);
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t)
                    && t.ValueKind is JsonValueKind.String
                        ? t.GetString() : null;
                var sid = root.TryGetProperty("sid", out var s)
                    && s.ValueKind is JsonValueKind.String
                        ? s.GetString() : null;

                switch (type)
                {
                    case "attach" when sid is not null:
                        await AttachAsync(sid);
                        break;
                    case "detach" when sid is not null:
                        if (handles.Remove(sid, out var det))
                        {
                            det.Dispose();
                        }
                        break;
                    case "input" when sid is not null
                        && handles.TryGetValue(sid, out var inp)
                        && root.TryGetProperty("data", out var data):
                        await inp.WriteAsync(
                            data.ValueKind is JsonValueKind.String
                                ? data.GetString() ?? string.Empty
                                : string.Empty);
                        break;
                    case "resize" when sid is not null
                        && handles.TryGetValue(sid, out var rsz):
                        await rsz.ResizeAsync(
                            root.TryGetProperty("cols", out var c)
                                && c.ValueKind is JsonValueKind.Number
                                && c.TryGetInt32(out var cv) ? cv : 120,
                            root.TryGetProperty("rows", out var r)
                                && r.ValueKind is JsonValueKind.Number
                                && r.TryGetInt32(out var rv) ? rv : 30);
                        break;
                    case "kill" when sid is not null:
                        if (handles.Remove(sid, out var kh))
                        {
                            kh.Dispose();
                        }
                        await deps.Terminals.KillAsync(uid, sid);
                        break;
                    case "create":
                    {
                        var chatId = root.TryGetProperty("chatId", out var ci)
                            && ci.ValueKind is JsonValueKind.String
                                ? ci.GetString() : null;
                        var runId = root.TryGetProperty("runId", out var ri)
                            && ri.ValueKind is JsonValueKind.String
                                ? ri.GetString() : null;
                        var wdir = root.TryGetProperty("workdir", out var wd)
                            && wd.ValueKind is JsonValueKind.String
                                ? wd.GetString() : null;
                        var cols = root.TryGetProperty("cols", out var cc)
                            && cc.ValueKind is JsonValueKind.Number
                            && cc.TryGetInt32(out var ccv) ? ccv : 120;
                        var rows = root.TryGetProperty("rows", out var rr)
                            && rr.ValueKind is JsonValueKind.Number
                            && rr.TryGetInt32(out var rrv) ? rrv : 30;

                        var (resolved, resolveError) =
                            await ResolveBoundWorkdirAsync(
                                uid, wdir, runId, chatId, deps, ct);
                        if (resolveError is not null)
                        {
                            await Send(new { type = "error", detail = "workdir fora de data/." });
                            break;
                        }

                        var secrets = await deps.AgentEnv.BuildAsync(uid, ct);
                        try
                        {
                            var newSid = deps.Terminals.Create(
                                uid, cols, rows, secrets.Vars,
                                resolved, chatId, runId);
                            await Send(new
                            {
                                type = "created", sid = newSid, chatId, runId,
                            });
                            await AttachAsync(newSid);
                        }
                        catch (InvalidOperationException ex)
                        {
                            await Send(new { type = "error", detail = ex.Message });
                        }
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Idle-timeout ou shutdown — desanexa sem matar as sessões.
        }
        catch (WebSocketException)
        {
            // Cliente caiu — sessões seguem vivas para reconexão.
        }
        catch (JsonException)
        {
            // Frame malformado encerra o loop de leitura.
        }
        finally
        {
            foreach (var h in handles.Values)
            {
                h.Dispose();
            }
        }

        return Results.Empty;
    }

    /// <summary>Interpreta um frame de controle; devolve o texto cru quando não é JSON.</summary>
    private static bool TryHandleFrame(
        string text, TerminalSessionManager.SessionHandle handle, out string rawInput)
    {
        rawInput = text;
        if (!text.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            switch (root.TryGetProperty("type", out var type)
                    && type.ValueKind is JsonValueKind.String
                        ? type.GetString() : null)
            {
                case "input" when root.TryGetProperty("data", out var data):
                    rawInput = data.ValueKind is JsonValueKind.String
                        ? data.GetString() ?? string.Empty
                        : string.Empty;
                    return false;
                case "resize":
                    var cols = root.TryGetProperty("cols", out var c)
                        && c.ValueKind is JsonValueKind.Number
                        && c.TryGetInt32(out var cv) ? cv : 120;
                    var rows = root.TryGetProperty("rows", out var r)
                        && r.ValueKind is JsonValueKind.Number
                        && r.TryGetInt32(out var rv) ? rv : 30;
                    _ = handle.ResizeAsync(cols, rows);
                    return true;
                default:
                    return true; // JSON desconhecido — descarta sem ecoar.
            }
        }
        catch (JsonException)
        {
            return false; // texto que parecia JSON mas não era → input.
        }
    }

    private static async Task SendJsonAsync(
        WebSocket socket, SemaphoreSlim sendLock, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await sendLock.WaitAsync(ct);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(
                    bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task<(User? User, IResult? Error)> AuthorizedAsync(
        HttpContext http, ConfigService config,
        IConfiguration configuration, CancellationToken ct)
    {
        var user = await FeatureFlagEndpoints.CurrentUserAsync(http, ct);
        if (user is null)
        {
            return (null, Results.Unauthorized());
        }

        return await FeatureFlagEndpoints.IsEnabledAsync(config, configuration, FeatureFlagEndpoints.Terminal, ct)
            ? (user, null)
            : (null, Results.Json(
                new { detail = "Terminal desativado pelo administrador." },
                statusCode: 403));
    }

    /// <summary>
    /// Flag: ligado por padrão. Ordem de precedência:
    /// <c>Terminal:Enabled</c>/<c>TERMINAL_ENABLED</c> (override booleano) →
    /// <c>terminal.enabled</c> persistido → default <c>true</c>.
    /// </summary>
    private sealed record TerminalSessionRequest(
        int? Cols, int? Rows, string? ChatId, string? RunId,
        string? Workdir);

    /// <summary>Bundle DI dos endpoints de terminal — mantém as
    /// assinaturas dentro do limite de parâmetros (S107).</summary>
    private sealed record TerminalDeps(
        TerminalSessionManager Terminals, AppDbContext Db,
        ConfigService Config, IConfiguration Configuration,
        AgentSecretsEnvService AgentEnv, WorkspaceRepoService Repos,
        WorktreeService Worktrees, IHostEnvironment Env);
}
