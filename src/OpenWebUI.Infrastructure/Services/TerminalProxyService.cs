using System.Net.Http.Headers;
using System.Net.WebSockets;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Proxy autenticado para servidores de terminal/Jupyter configurados pelo
/// admin (`terminals.servers` em config). Encaminha HTTP (allowlist `/api/*`)
/// e WebSocket (tunnel bidirecional), injetando a key do servidor sem nunca
/// expô-la ao cliente. Servidores <c>type: "local"</c> são spawnados como
/// processos filhos via <see cref="LocalTerminalSpawner"/> antes do proxy.
/// </summary>
public class TerminalProxyService(
    IHttpClientFactory httpClientFactory, ConfigService config,
    LocalTerminalSpawner spawner)
{
    /// <summary>Lista os servidores configurados (com key real — uso interno).</summary>
    public async Task<List<TerminalServerConfig>> GetServersAsync(CancellationToken ct = default) =>
        await config.GetAsync("terminals.servers", new List<TerminalServerConfig>(), ct);

    /// <summary>Persiste a lista de servidores (uso admin).</summary>
    public async Task SaveServersAsync(List<TerminalServerConfig> servers, CancellationToken ct = default) =>
        await config.SetAsync("terminals.servers", servers, ct);

    /// <summary>Resolve um servidor pelo id; null quando não existe.</summary>
    public async Task<TerminalServerConfig?> GetServerAsync(string id, CancellationToken ct = default) =>
        (await GetServersAsync(ct)).FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// Resolve o servidor garantindo que ele está acessível: servidores
    /// <c>local</c> são spawnados/sob demanda e retornam URL+token reais.
    /// </summary>
    /// <exception cref="InvalidOperationException">Spawn local falhou.</exception>
    public async Task<TerminalServerConfig?> ResolveServerAsync(string id, CancellationToken ct = default)
    {
        var server = await GetServerAsync(id, ct);
        if (server is null)
        {
            return null;
        }
        return server.Type == "local"
            ? await spawner.EnsureStartedAsync(server, ct)
            : server;
    }

    /// <summary>Encerra o processo local de um servidor removido da config.</summary>
    public void StopLocal(string serverId) => spawner.Stop(serverId);

    /// <summary>
    /// Sanitiza o path do proxy: deve ser relativo, dentro de `api/` e sem
    /// traversal (`..`) — mesmo critério do `_sanitize_proxy_path` do upstream.
    /// </summary>
    /// <returns>Path válido ou null.</returns>
    public static string? SanitizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.TrimStart('/');
        if (!normalized.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (normalized.Split('/').Any(segment => segment is ".." || segment.Contains('%')))
        {
            return null;
        }

        return normalized;
    }

    /// <summary>Encaminha uma requisição HTTP ao servidor de terminal.</summary>
    public async Task<ProxiedResponse> ForwardAsync(
        TerminalServerConfig server, HttpMethod method, string path, string? queryString,
        byte[]? body, string? contentType, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, BuildUrl(server, path, queryString));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
            {
                request.Content.Headers.ContentType = mediaType;
            }
        }

        ApplyAuth(server, request.Headers);

        try
        {
            var upstream = await httpClientFactory.CreateClient()
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return new ProxiedResponse((int)upstream.StatusCode, upstream, null);
        }
        catch (HttpRequestException)
        {
            return new ProxiedResponse(502, null, "Servidor de terminal indisponível.");
        }
        catch (TaskCanceledException)
        {
            return new ProxiedResponse(502, null, "Servidor de terminal indisponível.");
        }
    }

    /// <summary>
    /// Abre um WebSocket contra o servidor de terminal e bombeia frames nas
    /// duas direções até uma das pontas fechar (ou o idle timeout estourar).
    /// </summary>
    /// <param name="client">WebSocket já aceito do cliente.</param>
    /// <param name="server">Servidor de destino.</param>
    /// <param name="path">Path relativo (api/...).</param>
    /// <param name="queryString">Query original (protocolos sub etc.).</param>
    /// <param name="subProtocol">Sub-protocolo negociado com o cliente (opcional).</param>
    /// <param name="idleTimeout">Tempo sem tráfego que fecha o túnel.</param>
    /// <param name="ct">Token de cancelamento.</param>
    public static async Task TunnelAsync(
        WebSocket client, TerminalServerConfig server, string path, string? queryString,
        string? subProtocol, TimeSpan idleTimeout, CancellationToken ct)
    {
        var upstream = new ClientWebSocket();
        if (!string.IsNullOrEmpty(subProtocol))
        {
            upstream.Options.AddSubProtocol(subProtocol);
        }
        if (server.AuthType is "token" or "password" && !string.IsNullOrEmpty(server.Key))
        {
            upstream.Options.SetRequestHeader("Authorization", $"Bearer {server.Key}");
        }

        var url = BuildUrl(server, path, queryString);
        var wsUrl = url.StartsWith("https", StringComparison.OrdinalIgnoreCase)
            ? "wss" + url["https".Length..]
            : "ws" + url["http".Length..];

        try
        {
            await upstream.ConnectAsync(new Uri(wsUrl), ct);
        }
        catch (WebSocketException)
        {
            await client.CloseAsync(
                WebSocketCloseStatus.InternalServerError, "upstream indisponível", ct);
            return;
        }

        using (upstream)
        {
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var clientToUpstream = PumpAsync(client, upstream, idleCts, idleTimeout);
            var upstreamToClient = PumpAsync(upstream, client, idleCts, idleTimeout);
            await Task.WhenAny(clientToUpstream, upstreamToClient);
            idleCts.Cancel();
            try
            {
                await Task.WhenAll(clientToUpstream, upstreamToClient);
            }
            catch (OperationCanceledException)
            {
                // Relay falhou (sessão fechou) — ignora.
            }
        }
    }

    /// <summary>Bombeia frames de src→dst; texto/binário preservados.</summary>
    private static async Task PumpAsync(
        WebSocket src, WebSocket dst, CancellationTokenSource idleCts, TimeSpan idleTimeout)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (src.State == WebSocketState.Open && !idleCts.IsCancellationRequested)
            {
                var receiveTask = src.ReceiveAsync(buffer, idleCts.Token);
                var timeoutTask = Task.Delay(idleTimeout, idleCts.Token);
                var finished = await Task.WhenAny(receiveTask, timeoutTask);
                if (finished == timeoutTask)
                {
                    await CloseBothQuietlyAsync(src, dst, WebSocketCloseStatus.NormalClosure, "idle");
                    return;
                }

                var message = await receiveTask;
                if (message.MessageType == WebSocketMessageType.Close)
                {
                    await CloseBothQuietlyAsync(src, dst, message.CloseStatus, message.CloseStatusDescription);
                    return;
                }

                await dst.SendAsync(
                    buffer.AsMemory(0, message.Count), message.MessageType,
                    message.EndOfMessage, idleCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Relay falhou (sessão fechou) — ignora.
        }
        catch (WebSocketException)
        {
            // Relay falhou (sessão fechou) — ignora.
        }
    }

    private static async Task CloseBothQuietlyAsync(
        WebSocket src, WebSocket dst, WebSocketCloseStatus? status, string? description)
    {
        var closeStatus = status ?? WebSocketCloseStatus.NormalClosure;
        try
        {
            if (dst.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await dst.CloseAsync(closeStatus, description, CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // Relay falhou (sessão fechou) — ignora.
        }

        try
        {
            if (src.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await src.CloseAsync(closeStatus, description, CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
            // Relay falhou (sessão fechou) — ignora.
        }
    }

    private static string BuildUrl(TerminalServerConfig server, string path, string? queryString) =>
        $"{server.Url.TrimEnd('/')}/{path}{queryString}";

    private static void ApplyAuth(TerminalServerConfig server, HttpRequestHeaders headers)
    {
        if (server.AuthType is "token" or "password" && !string.IsNullOrEmpty(server.Key))
        {
            headers.Authorization = new AuthenticationHeaderValue("Bearer", server.Key);
        }
    }
}
