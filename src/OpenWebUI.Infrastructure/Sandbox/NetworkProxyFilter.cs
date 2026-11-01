using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenWebUI.Infrastructure.Sandbox;

/// <summary>
/// Allowlist de domínios do proxy do sandbox
/// (SPEC-20261031-sandboxed-exec RF-003): <c>*.example.com</c> cobre
/// subdomínios; <c>localhost</c>/<c>127.0.0.1</c>/<c>::1</c> sempre passam.
/// </summary>
public static class DomainAllowlist
{
    /// <summary>Host permitido por wildcard exato ou <c>*.dominio</c>.</summary>
    public static bool IsAllowed(string host, IReadOnlyList<string> allowlist)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (host is "localhost" or "127.0.0.1" or "::1"
            || host.EndsWith(".localhost", StringComparison.Ordinal))
        {
            return true;
        }
        foreach (var entry in allowlist)
        {
            var e = entry.Trim().TrimEnd('.').ToLowerInvariant();
            if (e.Length == 0)
            {
                continue;
            }
            if (e.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = e[1..]; // ".example.com"
                if (host.EndsWith(suffix, StringComparison.Ordinal)
                    || host == e[2..])
                {
                    return true;
                }
            }
            else if (host == e)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Proxy HTTP mínimo em processo (CONNECT + GET absolute-URI) que filtra
/// por <see cref="DomainAllowlist"/>. Domínio negado → 403 e registro em
/// <see cref="DeniedHosts"/> (lido como evento
/// <c>sandbox.network.denied</c> pelo runner).
/// </summary>
public sealed class SandboxProxyServer : IAsyncDisposable
{
    private readonly IReadOnlyList<string> _allowlist;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Hosts negados nesta instância (ordem de chegada).</summary>
    public readonly System.Collections.Concurrent
        .ConcurrentDictionary<string, byte> DeniedHosts = new(StringComparer.OrdinalIgnoreCase);

    public int Port { get; }

    private SandboxProxyServer(IReadOnlyList<string> allowlist, int port)
    {
        _allowlist = allowlist;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>Sobe o proxy em porta efêmera do loopback.</summary>
    public static SandboxProxyServer Start(IReadOnlyList<string> allowlist)
    {
        var server = new SandboxProxyServer(allowlist, 0);
        _ = Task.Run(() => server.AcceptLoopAsync(server._stop.Token));
        return server;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            _ = Task.Run(() => HandleAsync(client), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = new List<byte>();
            var buf = new byte[1];
            try
            {
                // Lê até \r\n\r\n.
                while (header.Count < 64 * 1024)
                {
                    var n = await stream.ReadAsync(buf);
                    if (n <= 0) { return; }
                    header.Add(buf[0]);
                    var count = header.Count;
                    if (count >= 4
                        && header[count - 4] == (byte)'\r'
                        && header[count - 3] == (byte)'\n'
                        && header[count - 2] == (byte)'\r'
                        && header[count - 1] == (byte)'\n')
                    {
                        break;
                    }
                }
            }
            catch (IOException) { return; }
            catch (SocketException) { return; }

            var request = Encoding.ASCII.GetString(header.ToArray());
            var first = request.Split("\r\n", 2)[0];
            var parts = first.Split(' ');
            if (parts.Length < 2)
            {
                return;
            }

            var host = ExtractHost(parts[1], request);
            if (host is null || !DomainAllowlist.IsAllowed(host, _allowlist))
            {
                if (host is not null)
                {
                    DeniedHosts.TryAdd(host, 1);
                }
                await WriteAsync(stream,
                    "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n");
                return;
            }

            if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                await TunnelConnectAsync(stream, host!, request);
            }
            else
            {
                await WriteAsync(stream,
                    "HTTP/1.1 501 Not Implemented\r\nContent-Length: 0\r\n\r\n");
            }
        }
    }

    private static string? ExtractHost(string target, string request)
    {
        // CONNECT host:port
        var connect = target.Split(':')[0];
        if (!target.Contains("://", StringComparison.Ordinal))
        {
            return connect;
        }
        // absolute-URI → header Host:
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }
        foreach (var line in request.Split("\r\n"))
        {
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                return line[5..].Trim().Split(':')[0];
            }
        }
        return null;
    }

    private static async Task TunnelConnectAsync(
        NetworkStream clientStream, string host, string request)
    {
        var parts = request.Split(' ')[1].Split(':');
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p)
            ? p : 443;
        TcpClient upstream;
        try
        {
            upstream = new TcpClient();
            await upstream.ConnectAsync(host, port);
        }
        catch (SocketException)
        {
            await WriteAsync(clientStream,
                "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n");
            return;
        }
        using (upstream)
        {
            await WriteAsync(clientStream,
                "HTTP/1.1 200 Connection Established\r\n\r\n");
            var up = upstream.GetStream();
            var t1 = clientStream.CopyToAsync(up);
            var t2 = up.CopyToAsync(clientStream);
            try
            {
                await Task.WhenAny(t1, t2);
            }
            catch (IOException) { /* túnel fechado */ }
        }
    }

    private static Task WriteAsync(NetworkStream stream, string text) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}
