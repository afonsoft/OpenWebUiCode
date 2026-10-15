using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes E2E de anexos de mídia no chat (SPEC-20261013-chat-media-attachments):
/// persistência de <c>FilesJson</c> na mensagem, posse do arquivo e envio
/// multimodal ao provider (imagens em <c>images[]</c> no Ollama).
/// </summary>
[TestFixture, IsolateEnvironment]
public class ChatMediaAttachmentsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private volatile string? _lastChatBody;

    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD];

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-media-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@media.local", "senha123");
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);

        var baseUrl = StartMock();
        var connections = new ConnectionsConfig([baseUrl], [], []);
        var connResponse = await _client.PostAsJsonAsync("/api/v1/configs/connections", connections);
        Assert.That(connResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _client.DefaultRequestHeaders.Authorization = null;
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
        var random = new Random();
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port = random.Next(40000, 60000);
            _mock = new HttpListener();
            _mock.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _mock.Start();
                break;
            }
            catch (HttpListenerException)
            {
                _mock.Close();
            }
        }
        if (!_mock.IsListening)
        {
            throw new InvalidOperationException("Nenhuma porta livre para o mock.");
        }
        _mockCts = new CancellationTokenSource();
        _ = Task.Run(() => MockLoopAsync(_mockCts.Token));
        return $"http://localhost:{port}";
    }

    private async Task MockLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _mock.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }

            var path = ctx.Request.Url!.AbsolutePath;
            if (path == "/api/chat")
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                _lastChatBody = await reader.ReadToEndAsync(ct);
            }

            var (status, json) = path switch
            {
                "/api/tags" => (200, "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}"),
                "/api/chat" => (200, "{\"message\":{\"content\":\"resposta do mock\"}}"),
                _ => (404, "{}"),
            };
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email, string password)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, password));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<FileResponse> UploadAsync(
        string filename, byte[] bytes, string contentType)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", filename);
        var response = await _client.PostAsync("/api/v1/files/", content);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<FileResponse>())!;
    }

    private async Task<ChatResponse> CreateChatAsync()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("chat mídia", ["fake:1"], []));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    [Test]
    public async Task Completion_ComImagem_PayloadOllamaTemImages()
    {
        var user = await SignUpAsync("Vis", "vis@media.local", "senha123");
        UseToken(user.Token);
        var file = await UploadAsync("foto.png", PngBytes, "image/png");
        _lastChatBody = null;

        var request = new ChatCompletionRequest(
            "fake:1",
            [new ChatCompletionMessage("user", "o que tem na imagem?")],
            FileIds: [file.Id]);
        var response = await _client.PostAsJsonAsync("/api/chat/completions", request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());

        var payload = JsonNode.Parse(_lastChatBody!)!;
        var userMsg = payload["messages"]!.AsArray()
            .Last(m => m?["role"]?.GetValue<string>() == "user")!;
        Assert.Multiple(() =>
        {
            // Ollama: content segue string + images[] com o base64 do arquivo.
            Assert.That(userMsg["content"]!.GetValue<string>(),
                Is.EqualTo("o que tem na imagem?"));
            var images = userMsg["images"]!.AsArray();
            Assert.That(images, Has.Count.EqualTo(1));
            Assert.That(images[0]!.GetValue<string>(),
                Is.EqualTo(Convert.ToBase64String(PngBytes)));
        });
    }

    [Test]
    public async Task Completion_ComVideo_NaoViraImagem()
    {
        var user = await SignUpAsync("Vid", "vid@media.local", "senha123");
        UseToken(user.Token);
        var file = await UploadAsync("clipe.mp4", [0x00, 0x01, 0x02], "video/mp4");
        _lastChatBody = null;

        var request = new ChatCompletionRequest(
            "fake:1",
            [new ChatCompletionMessage("user", "veja o vídeo")],
            FileIds: [file.Id]);
        var response = await _client.PostAsJsonAsync("/api/chat/completions", request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var payload = JsonNode.Parse(_lastChatBody!)!;
        var userMsg = payload["messages"]!.AsArray()
            .Last(m => m?["role"]?.GetValue<string>() == "user")!;
        // Vídeo nunca vira conteúdo multimodal (RF-006).
        Assert.That(userMsg["images"], Is.Null);
    }

    [Test]
    public async Task Enqueue_PersisteFilesJson_EGetRetornaFiles()
    {
        var user = await SignUpAsync("Enq", "enq@media.local", "senha123");
        UseToken(user.Token);
        var png = await UploadAsync("foto.png", PngBytes, "image/png");
        var video = await UploadAsync("clipe.mp4", [0x00], "video/mp4");
        var chat = await CreateChatAsync();

        var enqueue = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/messages",
            new EnqueueChatRunRequest("analisa os anexos", "fake:1",
                FileIds: [png.Id, video.Id]));
        Assert.That(enqueue.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await enqueue.Content.ReadAsStringAsync());

        var loaded = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{chat.Id}");
        var message = loaded!.Messages.Single(m => m.Role == "user");
        Assert.Multiple(() =>
        {
            Assert.That(message.Files, Has.Count.EqualTo(2));
            Assert.That(message.Files![0].Id, Is.EqualTo(png.Id));
            Assert.That(message.Files[0].Type, Is.EqualTo("image"));
            Assert.That(message.Files[0].Name, Is.EqualTo("foto.png"));
            Assert.That(message.Files[1].Type, Is.EqualTo("video"));
        });
    }

    [Test]
    public async Task Enqueue_FileIdDeOutroUsuario_EIgnorado()
    {
        var owner = await SignUpAsync("Own", "own@media.local", "senha123");
        UseToken(owner.Token);
        var file = await UploadAsync("privada.png", PngBytes, "image/png");

        var other = await SignUpAsync("Oth", "oth@media.local", "senha123");
        UseToken(other.Token);
        var chat = await CreateChatAsync();

        var enqueue = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/messages",
            new EnqueueChatRunRequest("tenta anexar alheio", "fake:1",
                FileIds: [file.Id]));
        Assert.That(enqueue.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        var loaded = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{chat.Id}");
        var message = loaded!.Messages.Single(m => m.Role == "user");
        Assert.That(message.Files, Is.Null.Or.Empty);
    }

    [Test]
    public async Task Upsert_SemFiles_PreservaAnexosExistentes()
    {
        var user = await SignUpAsync("Ups", "ups@media.local", "senha123");
        UseToken(user.Token);
        var png = await UploadAsync("foto.png", PngBytes, "image/png");
        var chat = await CreateChatAsync();

        await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/messages",
            new EnqueueChatRunRequest("com anexo", "fake:1", FileIds: [png.Id]));

        // Upsert de um cliente antigo (sem files) não pode apagar os anexos.
        var before = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{chat.Id}");
        var withoutFiles = before!.Messages
            .Select(m => m with { Files = null }).ToList();
        var upsert = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}",
            new ChatUpsertRequest(before.Title, before.Models, withoutFiles));
        Assert.That(upsert.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var after = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{chat.Id}");
        var message = after!.Messages.Single(m => m.Role == "user");
        Assert.That(message.Files, Has.Count.EqualTo(1));
        Assert.That(message.Files![0].Id, Is.EqualTo(png.Id));
    }

    [Test]
    public async Task Get_MensagemLegadaSemAnexos_FilesNull()
    {
        var user = await SignUpAsync("Leg", "leg@media.local", "senha123");
        UseToken(user.Token);
        var chat = await CreateChatAsync();

        await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/messages",
            new EnqueueChatRunRequest("só texto", "fake:1"));

        var loaded = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{chat.Id}");
        var message = loaded!.Messages.Single(m => m.Role == "user");
        Assert.That(message.Files, Is.Null);
    }
}
