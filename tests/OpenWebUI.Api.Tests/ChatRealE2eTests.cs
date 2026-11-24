using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes E2E com provider LLM real (OpenAI-compatível, ex.: OmniRoute).
/// Só rodam quando <c>E2E_CHAT=1</c> e as variáveis <c>E2E_LLM_BASE_URL</c>,
/// <c>E2E_LLM_API_KEY</c> e <c>E2E_LLM_MODEL</c> estão definidas — fora disso a
/// fixture é ignorada (o gate do workflow <c>chat-e2e.yml</c> ativa via secrets).
/// </summary>
[TestFixture, IsolateEnvironment]
[Category("e2e-chat")]
public class ChatRealE2eTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _model = null!;
    private string? _savedBaseUrl;
    private string? _savedApiKey;
    private string? _savedBaseUrls;
    private string? _savedApiKeys;
    private string? _savedConnStr;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var baseUrl = Environment.GetEnvironmentVariable("E2E_LLM_BASE_URL");
        var apiKey = Environment.GetEnvironmentVariable("E2E_LLM_API_KEY");
        _model = Environment.GetEnvironmentVariable("E2E_LLM_MODEL") ?? string.Empty;
        if (Environment.GetEnvironmentVariable("E2E_CHAT") != "1"
            || string.IsNullOrWhiteSpace(baseUrl)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(_model))
        {
            Assert.Ignore(
                "E2E de chat desligado — defina E2E_CHAT=1, E2E_LLM_BASE_URL, "
                + "E2E_LLM_API_KEY e E2E_LLM_MODEL para rodar contra o provider real.");
        }

        // Seed de conexão por env segue o mesmo caminho do primeiro boot real:
        // OPENAI_API_BASE_URL(S) + OPENAI_API_KEY(S) → ConfigEntry "connections".
        _savedBaseUrl = Environment.GetEnvironmentVariable("OPENAI_API_BASE_URL");
        _savedApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        _savedBaseUrls = Environment.GetEnvironmentVariable("OPENAI_API_BASE_URLS");
        _savedApiKeys = Environment.GetEnvironmentVariable("OPENAI_API_KEYS");
        _savedConnStr = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-e2e-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("OPENAI_API_BASE_URLS", null);
        Environment.SetEnvironmentVariable("OPENAI_API_KEYS", null);
        Environment.SetEnvironmentVariable("OPENAI_API_BASE_URL", baseUrl);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", apiKey);

        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        // Sonda de disponibilidade: provider fora do ar (rede/5xx) não é falha
        // da aplicação — a fixture é ignorada em vez de quebrar o CI por
        // infraestrutura externa. Respondendo, as asserções valem de verdade.
        using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            try
            {
                using var probeRequest = new HttpRequestMessage(
                    HttpMethod.Get, $"{baseUrl!.TrimEnd('/')}/models");
                probeRequest.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
                using var probeResponse = await probe.SendAsync(probeRequest);
                if (!probeResponse.IsSuccessStatusCode)
                {
                    Assert.Ignore(
                        $"provider real indisponível ({(int)probeResponse.StatusCode}) — e2e pulado");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Assert.Ignore($"provider real inalcançável ({ex.Message}) — e2e pulado");
            }
        }

        var signup = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest("E2E", "e2e@test.local", "senha123"));
        Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await signup.Content.ReadAsStringAsync());
        var auth = (await signup.Content.ReadFromJsonAsync<AuthResponse>())!;
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.Token);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
        Environment.SetEnvironmentVariable("OPENAI_API_BASE_URL", _savedBaseUrl);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", _savedApiKey);
        Environment.SetEnvironmentVariable("OPENAI_API_BASE_URLS", _savedBaseUrls);
        Environment.SetEnvironmentVariable("OPENAI_API_KEYS", _savedApiKeys);
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", _savedConnStr);
        if (_dbPath is not null && File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    [Test]
    [CancelAfter(180_000)]
    [Retry(1)]
    public async Task Models_ProviderReal_ListaModelosDaConexao()
    {
        var result = await _client.GetFromJsonAsync<ModelListResponse>("/api/models");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Data, Is.Not.Empty,
            "conexão seedada via OPENAI_API_BASE_URL deveria listar modelos do provider real");
        TestContext.Out.WriteLine(
            $"modelos expostos pelo provider: {string.Join(", ", result.Data.Take(10).Select(m => m.Id))}");
    }

    [Test]
    [CancelAfter(360_000)]
    [Retry(1)]
    public async Task Chat_ProviderReal_StreamSSE_RetornaConteudoEDone()
    {
        var request = new ChatCompletionRequest(
            _model,
            [new ChatCompletionMessage("user", "Responda exatamente com a palavra: OK")],
            Stream: true);

        var content = await StreamCompletionAsync(request);

        Assert.That(content, Is.Not.Empty, "o provider real deveria produzir conteúdo");
        TestContext.Out.WriteLine($"resposta do modelo: {content[..Math.Min(120, content.Length)]}");
    }

    [Test]
    [CancelAfter(420_000)]
    [Retry(1)]
    public async Task Chat_ProviderReal_DoisTurnos_PersistemNoChat()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/",
            new ChatUpsertRequest("E2E real", [_model],
                [new ChatMessageModel("u1", "user", "Qual a capital do Brasil?", null, now)]));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;

        var messages = new List<ChatCompletionMessage>
        {
            new("user", "Qual a capital do Brasil?"),
        };
        var first = await StreamCompletionAsync(
            new ChatCompletionRequest(_model, messages, Stream: true));
        Assert.That(first, Is.Not.Empty);
        Assert.That(first, Does.Contain("Bras").IgnoreCase,
            "resposta deveria mencionar Brasília");

        // Segundo turno com histórico: o contexto precisa ser respeitado.
        messages.Add(new ChatCompletionMessage("assistant", first));
        messages.Add(new ChatCompletionMessage(
            "user", "E qual a população dela? Responda em uma frase."));
        var second = await StreamCompletionAsync(
            new ChatCompletionRequest(_model, messages, Stream: true));
        Assert.That(second, Is.Not.Empty);

        // Persistência como o cliente faz: mensagens completas no chat.
        var persisted = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}",
            new ChatUpsertRequest("E2E real", [_model],
                [
                    new ChatMessageModel("u1", "user", "Qual a capital do Brasil?", null, now),
                    new ChatMessageModel("a1", "assistant", first, _model, now + 1),
                    new ChatMessageModel("u2", "user", "E qual a população dela?", null, now + 2),
                    new ChatMessageModel("a2", "assistant", second, _model, now + 3),
                ]));
        Assert.That(persisted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var loaded = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{chat.Id}");
        Assert.Multiple(() =>
        {
            Assert.That(loaded!.Messages, Has.Count.EqualTo(4));
            Assert.That(loaded.Messages[3].Content, Is.EqualTo(second));
            Assert.That(loaded.Messages[3].Role, Is.EqualTo("assistant"));
        });
    }

    /// <summary>
    /// Consome o SSE de <c>/api/chat/completions</c> e retorna o conteúdo
    /// concatenado dos deltas. Falha o teste quando o stream emite
    /// <c>{"error": ...}</c> ou termina sem <c>data: [DONE]</c>.
    /// </summary>
    private async Task<string> StreamCompletionAsync(ChatCompletionRequest request)
    {
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, "/api/chat/completions")
        {
            Content = JsonContent.Create(request),
        };
        using var response = await _client.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        Assert.That(response.Content.Headers.ContentType?.MediaType,
            Does.Contain("text/event-stream"));

        var content = new StringBuilder();
        var done = false;
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]")
            {
                done = true;
                break;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(payload);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            var error = node?["error"]?.GetValue<string>();
            if (error is not null)
            {
                Assert.Fail($"erro no stream do provider real: {error}");
            }

            var delta = node?["choices"] is JsonArray { Count: > 0 } choices
                ? choices[0]?["delta"]?["content"]?.GetValue<string>()
                : null;
            if (!string.IsNullOrEmpty(delta))
            {
                content.Append(delta);
            }
        }

        Assert.That(done, Is.True, "stream terminou sem o marcador data: [DONE]");
        return content.ToString();
    }
}
