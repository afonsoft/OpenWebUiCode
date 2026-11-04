using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace OpenWebUI.Api.Tests.Providers;

/// <summary>Respostas enfileiradas por ordem de chamada; registra o wire
/// (método, url, body) de cada request para asserts de payload. Sem
/// HttpListener — roda em-process.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body,
        TimeSpan? RetryAfter)> _responses = new();

    public List<RecordedHttpRequest> Requests { get; } = new();

    public void Enqueue(
        HttpStatusCode status, string body, TimeSpan? retryAfter = null) =>
        _responses.Enqueue((status, body, retryAfter));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(new RecordedHttpRequest(
            request.Method.Method,
            request.RequestUri?.ToString() ?? string.Empty,
            request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(ct),
            request.Headers.ToDictionary(
                h => h.Key, h => string.Join(",", h.Value),
                StringComparer.OrdinalIgnoreCase)));

        var (status, body, retryAfter) = _responses.Count > 0
            ? _responses.Dequeue()
            : (HttpStatusCode.NotFound, "{}", (TimeSpan?)null);
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8,
                body.StartsWith("data:", StringComparison.Ordinal)
                    ? "text/event-stream" : "application/json"),
        };
        if (retryAfter is not null)
        {
            response.Headers.RetryAfter =
                new RetryConditionHeaderValue(retryAfter.Value);
        }
        return response;
    }
}

internal sealed class StubHttpFactory(HttpMessageHandler handler)
    : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler);
}

internal sealed record RecordedHttpRequest(
    string Method, string Url, string Body,
    IReadOnlyDictionary<string, string> Headers);
