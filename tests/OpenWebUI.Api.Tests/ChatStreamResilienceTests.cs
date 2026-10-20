using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Resiliência do stream de chat quando o provider morre no meio:
/// falha de transporte depois dos headers (conexão resetada, chunk
/// truncado) vira evento SSE <c>{"error": ...}</c> + <c>[DONE]</c> em vez
/// de derrubar a resposta — o <c>catch (HttpRequestException)</c> do
/// endpoint não alcançava <see cref="HttpIOException"/>, que herda de
/// <see cref="IOException"/> (regressão vista no e2e real: provider
/// abortou o stream e o cliente recebeu a resposta cortada).
/// O mock usa TCP cru porque <see cref="HttpListener.Abort"/> entrega os
/// bytes pendentes e fecha em EOF limpo — só um RST real derruba o
/// ReadLineAsync com exceção como no incidente.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class ChatStreamResilienceTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private TcpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var mockBase = StartMock();
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-sstream-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var signup = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest("Admin", "admin@sstream.local", "senha123"));
        Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await signup.Content.ReadAsStringAsync());
        var admin = (await signup.Content.ReadFromJsonAsync<AuthResponse>())!;
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", admin.Token);

        var conn = await _client.PostAsJsonAsync(
            "/api/v1/configs/connections", new ConnectionsConfig([], [mockBase], ["k"]));
        Assert.That(conn.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await conn.Content.ReadAsStringAsync());
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mockCts.Cancel();
        _mock.Stop();
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private string StartMock()
    {
        _mock = new TcpListener(IPAddress.Loopback, 0);
        _mock.Start();
        var port = ((IPEndPoint)_mock.LocalEndpoint).Port;
        _mockCts = new CancellationTokenSource();
        _ = Task.Run(() => MockLoopAsync(_mockCts.Token));
        return $"http://localhost:{port}";
    }

    private async Task MockLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _mock.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is SocketException
                or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            _ = Task.Run(() => RespondAsync(client), CancellationToken.None);
        }
    }

    /// <summary>
    /// HTTP mínimo: <c>GET /models</c> responde a lista; o POST do chat
    /// devolve um chunk SSE válido seguido de um chunk declarado e nunca
    /// completado — o socket fecha com RST (linger 0), matando a leitura
    /// do provider no meio do frame.
    /// </summary>
    private static async Task RespondAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[16 * 1024];
                var request = await ReadHeadAsync(stream, buffer);
                if (request is null)
                {
                    return;
                }

                if (request.StartsWith("GET /models", StringComparison.Ordinal))
                {
                    var body = "{\"data\":[{\"id\":\"m1\"}]}";
                    var head = "HTTP/1.1 200 OK\r\nContent-Type: application/json"
                        + $"\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                    return;
                }

                var sseHead = "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream"
                    + "\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
                var delta = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"parcial\"}}]}\n\n";
                var fullChunk = $"{delta.Length:X}\r\n{delta}\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(sseHead + fullChunk));
                // Chunk declarado de 0x64 bytes com só o começo do payload —
                // o ReadLineAsync do provider fica pendente quando o RST chega.
                await stream.WriteAsync(Encoding.ASCII.GetBytes("64\r\ndata: {\"choices\":[{\"in"));
                await stream.FlushAsync();
                await Task.Delay(300);
                client.Client.SetSocketOption(
                    SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(true, 0));
            }
        }
        catch (IOException) { /* cliente desconectou — ignora */ }
        catch (SocketException) { /* idem */ }
        catch (ObjectDisposedException) { /* listener parou */ }
    }

    /// <summary>Lê a request line + headers (e o corpo Content-Length, se
    /// vier junto) até o primeiro <c>\r\n\r\n</c>.</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total));
            if (n <= 0)
            {
                return total > 0 ? Encoding.ASCII.GetString(buffer, 0, total) : null;
            }
            total += n;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                return text;
            }
        }
        return Encoding.ASCII.GetString(buffer, 0, total);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Chat_ProviderAbortaMidStream_ErroSseEmVezDeRespostaCortada()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/completions")
        {
            Content = JsonContent.Create(new ChatCompletionRequest(
                "m1", [new ChatCompletionMessage("user", "oi")], Stream: true)),
        };
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await response.Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("\"error\""),
                "stream morto do provider deveria virar evento de erro SSE");
            Assert.That(body, Does.Contain("data: [DONE]"),
                "o evento de erro encerra o stream com [DONE]");
        });
    }
}
