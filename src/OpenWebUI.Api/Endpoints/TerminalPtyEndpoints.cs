using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
            ConfigPutAsync(http, config, db, ct));

        group.MapPost("/sessions", (HttpContext http, TerminalSessionManager terminals,
            AppDbContext db, ConfigService config, IConfiguration configuration,
            AgentSecretsEnvService agentEnv, CancellationToken ct) =>
            CreateSessionAsync(http, terminals, db, config, configuration, agentEnv, ct));
        group.MapGet("/sessions", (HttpContext http, TerminalSessionManager terminals,
            AppDbContext db, ConfigService config, IConfiguration configuration,
            CancellationToken ct) =>
            ListSessionsAsync(http, terminals, db, config, configuration, ct));
        group.MapDelete("/sessions/{id}", (HttpContext http, string id,
            TerminalSessionManager terminals, AppDbContext db, ConfigService config,
            IConfiguration configuration, CancellationToken ct) =>
            DeleteSessionAsync(http, id, terminals, db, config, configuration, ct));

        return group;
    }

    /// <summary>Mapeia o WebSocket multiplexador <c>/ws/terminal/{id}</c>.</summary>
    public static IEndpointConventionBuilder MapTerminalWebSocket(this IEndpointRouteBuilder app) =>
        app.Map("/ws/terminal/{id}", (HttpContext http, string id,
            TerminalSessionManager terminals, AppDbContext db, ConfigService config,
            IConfiguration configuration, CancellationToken ct) =>
            TerminalWsAsync(http, id, terminals, db, config, configuration, ct))
        .RequireAuthorization();

    private static async Task<IResult> ConfigGetAsync(
        HttpContext http, ConfigService config, IConfiguration configuration,
        CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, ct);
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(new { enabled = await IsEnabledAsync(config, configuration, ct) });
    }

    private static async Task<IResult> ConfigPutAsync(
        HttpContext http, ConfigService config, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (user.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }

        var request = await JsonSerializer.DeserializeAsync<TerminalConfigRequest>(
            http.Request.Body, JsonOptions, ct);
        await config.SetAsync("terminal.enabled", request?.Enabled == true, ct);
        return Results.Ok(new { enabled = request?.Enabled == true });
    }

    private static async Task<IResult> CreateSessionAsync(
        HttpContext http, TerminalSessionManager terminals, AppDbContext db,
        ConfigService config, IConfiguration configuration,
        AgentSecretsEnvService agentEnv, CancellationToken ct)
    {
        var (user, error) = await AuthorizedAsync(http, db, config, configuration, ct);
        if (error is not null)
        {
            return error;
        }

        var request = http.Request.ContentLength is > 0
            ? await JsonSerializer.DeserializeAsync<TerminalSessionRequest>(
                http.Request.Body, JsonOptions, ct)
            : null;

        // Secrets do usuário ∪ global ∪ managed GitHub entram na env da PTY
        // (SPEC-20261017-agent-secrets-env RF-006) — sessão é por usuário.
        var env = await agentEnv.BuildAsync(user!.Id, ct);
        try
        {
            var id = terminals.Create(
                user!.Id, request?.Cols ?? 120, request?.Rows ?? 30, env.Vars);
            return Results.Created($"/api/v1/terminal/sessions/{id}", new { id });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: 429);
        }
    }

    private static async Task<IResult> ListSessionsAsync(
        HttpContext http, TerminalSessionManager terminals, AppDbContext db,
        ConfigService config, IConfiguration configuration, CancellationToken ct)
    {
        var (user, error) = await AuthorizedAsync(http, db, config, configuration, ct);
        if (error is not null)
        {
            return error;
        }

        var sessions = terminals.List(user!.Id).Select(s =>
            new TerminalSessionInfoResponse(
                s.Id, s.IsRunning, s.CreatedAtUtc, s.LastActivityUtc, s.ExitCode));
        return Results.Ok(sessions);
    }

    private static async Task<IResult> DeleteSessionAsync(
        HttpContext http, string id, TerminalSessionManager terminals,
        AppDbContext db, ConfigService config, IConfiguration configuration,
        CancellationToken ct)
    {
        var (user, error) = await AuthorizedAsync(http, db, config, configuration, ct);
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
        AppDbContext db, ConfigService config, IConfiguration configuration,
        CancellationToken ct)
    {
        var (user, error) = await AuthorizedAsync(http, db, config, configuration, ct);
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
        HttpContext http, AppDbContext db, ConfigService config,
        IConfiguration configuration, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, ct);
        if (user is null)
        {
            return (null, Results.Unauthorized());
        }

        return await IsEnabledAsync(config, configuration, ct)
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
    private static async Task<bool> IsEnabledAsync(
        ConfigService config, IConfiguration configuration, CancellationToken ct)
    {
        if (bool.TryParse(configuration["Terminal:Enabled"], out var fromConfig)
            || bool.TryParse(Environment.GetEnvironmentVariable("TERMINAL_ENABLED"),
                out fromConfig))
        {
            return fromConfig;
        }

        // bool? null = nunca configurado → default ligado.
        return await config.GetAsync<bool?>("terminal.enabled", null, ct) ?? true;
    }

    private static Task<User?> CurrentUserAsync(HttpContext http, CancellationToken ct)
    {
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        return AuthEndpoints.FindUserAsync(http, db, ct);
    }

    private sealed record TerminalSessionRequest(int? Cols, int? Rows);
    private sealed record TerminalConfigRequest(bool Enabled);
}
