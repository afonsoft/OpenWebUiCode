using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>Testes dos endpoints de arquivos (upload, listagem, conteúdo, delete, contexto no chat e ownership).</summary>
[TestFixture, IsolateEnvironment]
public class FileEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private volatile string? _lastChatBody;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-files-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@files.local", "senha123");
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

    private async Task<HttpResponseMessage> UploadAsync(
        string filename, byte[] bytes, string contentType)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", filename);
        return await _client.PostAsync("/api/v1/files/", content);
    }

    [Test]
    public async Task Upload_Texto_SalvaEExtraiConteudo()
    {
        var user = await SignUpAsync("Up", "up@files.local", "senha123");
        UseToken(user.Token);

        var texto = "CONTEUDO_MARCADOR do arquivo de teste";
        var response = await UploadAsync("notas.txt", Encoding.UTF8.GetBytes(texto), "text/plain");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());

        var file = (await response.Content.ReadFromJsonAsync<FileResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(file.Filename, Is.EqualTo("notas.txt"));
            Assert.That(file.HasText, Is.True);
            Assert.That(file.Size, Is.EqualTo(Encoding.UTF8.GetByteCount(texto)));
        });

        var meta = await _client.GetFromJsonAsync<FileResponse>($"/api/v1/files/{file.Id}");
        Assert.That(meta!.Id, Is.EqualTo(file.Id));

        var content = await _client.GetFromJsonAsync<FileContentResponse>(
            $"/api/v1/files/{file.Id}/content");
        Assert.That(content!.Content, Does.Contain("CONTEUDO_MARCADOR"));
    }

    [Test]
    public async Task Upload_SemArquivo_Retorna400()
    {
        var user = await SignUpAsync("Empty", "empty@files.local", "senha123");
        UseToken(user.Token);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("valor"), "campo");
        var response = await _client.PostAsync("/api/v1/files/", form);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Upload_Binario_NaoExtraiTextoEServidoComoArquivo()
    {
        var user = await SignUpAsync("Bin", "bin@files.local", "senha123");
        UseToken(user.Token);

        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01, 0x02 };
        var response = await UploadAsync("foto.png", bytes, "image/png");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var file = (await response.Content.ReadFromJsonAsync<FileResponse>())!;
        Assert.That(file.HasText, Is.False);

        // Sem texto extraído o endpoint devolve o arquivo bruto do disco.
        var content = await _client.GetAsync($"/api/v1/files/{file.Id}/content");
        Assert.That(content.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var downloaded = await content.Content.ReadAsByteArrayAsync();
        Assert.That(downloaded, Is.EqualTo(bytes));
    }

    [Test]
    public async Task Files_Listagem_SoRetornaDoProprioUsuario()
    {
        var userA = await SignUpAsync("FA", "fa@files.local", "senha123");
        UseToken(userA.Token);
        await UploadAsync("a1.txt", Encoding.UTF8.GetBytes("a1"), "text/plain");
        await UploadAsync("a2.txt", Encoding.UTF8.GetBytes("a2"), "text/plain");

        var list = await _client.GetFromJsonAsync<List<FileResponse>>("/api/v1/files/");
        Assert.That(list, Has.Count.EqualTo(2));

        var userB = await SignUpAsync("FB", "fb@files.local", "senha123");
        UseToken(userB.Token);
        var listB = await _client.GetFromJsonAsync<List<FileResponse>>("/api/v1/files/");
        Assert.That(listB, Is.Empty);
    }

    [Test]
    public async Task Files_Alheio_MetaConteudoEDelete_Retornam404()
    {
        var owner = await SignUpAsync("FOwner", "fowner@files.local", "senha123");
        UseToken(owner.Token);
        var uploaded = await UploadAsync("privado.txt", Encoding.UTF8.GetBytes("segredo"), "text/plain");
        var file = (await uploaded.Content.ReadFromJsonAsync<FileResponse>())!;

        var other = await SignUpAsync("FOther", "fother@files.local", "senha123");
        UseToken(other.Token);

        var meta = await _client.GetAsync($"/api/v1/files/{file.Id}");
        var content = await _client.GetAsync($"/api/v1/files/{file.Id}/content");
        var delete = await _client.DeleteAsync($"/api/v1/files/{file.Id}");

        Assert.Multiple(() =>
        {
            Assert.That(meta.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(content.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(delete.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task Files_Delete_RemoveRegistro()
    {
        var user = await SignUpAsync("Del", "del@files.local", "senha123");
        UseToken(user.Token);
        var uploaded = await UploadAsync("apagar.txt", Encoding.UTF8.GetBytes("x"), "text/plain");
        var file = (await uploaded.Content.ReadFromJsonAsync<FileResponse>())!;

        var deleted = await _client.DeleteAsync($"/api/v1/files/{file.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var meta = await _client.GetAsync($"/api/v1/files/{file.Id}");
        Assert.That(meta.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var list = await _client.GetFromJsonAsync<List<FileResponse>>("/api/v1/files/");
        Assert.That(list!.Any(f => f.Id == file.Id), Is.False);
    }

    [Test]
    public async Task Completion_ComFileIds_InjetaConteudoDoArquivo()
    {
        var user = await SignUpAsync("Ctx", "ctx@files.local", "senha123");
        UseToken(user.Token);
        var texto = "TEXTO_INJETADO_VIA_CONTEXTO";
        var uploaded = await UploadAsync("contexto.md", Encoding.UTF8.GetBytes(texto), "text/markdown");
        var file = (await uploaded.Content.ReadFromJsonAsync<FileResponse>())!;

        var request = new ChatCompletionRequest(
            "fake:1",
            [new ChatCompletionMessage("user", "resuma o anexo")],
            FileIds: [file.Id]);
        var response = await _client.PostAsJsonAsync("/api/chat/completions", request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // attachments-v2 RF-001: o texto do arquivo vai inline na mensagem
        // do usuário como <file name="…"> — não mais via system prompt.
        Assert.That(_lastChatBody, Is.Not.Null);
        var payload = JsonNode.Parse(_lastChatBody!)!;
        var userMsg = payload["messages"]!.AsArray()
            .FirstOrDefault(m => m?["role"]?.GetValue<string>() == "user");
        Assert.That(userMsg, Is.Not.Null);
        var userContent = userMsg!["content"]!.GetValue<string>();
        Assert.Multiple(() =>
        {
            Assert.That(userContent, Does.Contain("<file name=\"contexto.md\">"));
            Assert.That(userContent, Does.Contain("TEXTO_INJETADO_VIA_CONTEXTO"));
            Assert.That(userContent, Does.Contain("resuma o anexo"));
        });
    }

    [Test]
    public async Task Completion_FileIdAlheio_NaoInjetaConteudo()
    {
        var owner = await SignUpAsync("COwner", "cowner@files.local", "senha123");
        UseToken(owner.Token);
        var uploaded = await UploadAsync(
            "secreto.txt", Encoding.UTF8.GetBytes("SEGREDO_DE_OUTRO_USUARIO"), "text/plain");
        var file = (await uploaded.Content.ReadFromJsonAsync<FileResponse>())!;

        var other = await SignUpAsync("COther", "cother@files.local", "senha123");
        UseToken(other.Token);
        _lastChatBody = null;

        var request = new ChatCompletionRequest(
            "fake:1",
            [new ChatCompletionMessage("user", "resuma")],
            FileIds: [file.Id]);
        var response = await _client.PostAsJsonAsync("/api/chat/completions", request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Assert.That(_lastChatBody, Is.Not.Null);
        var payload = JsonNode.Parse(_lastChatBody!)!;
        var allText = string.Join("\n", payload["messages"]!.AsArray()
            .Select(m => m?["content"]?.GetValue<string>()));
        Assert.That(allText, Does.Not.Contain("SEGREDO_DE_OUTRO_USUARIO"));
    }
}

/// <summary>Testes dos endpoints de avaliação (feedback por id, listagens admin, export e delete-all).</summary>
[TestFixture, IsolateEnvironment]
public class EvaluationEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _adminToken = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-eval-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@eval.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
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

    [Test]
    public async Task Feedback_RatingInvalido_Retorna400()
    {
        var user = await SignUpAsync("Eval1", "e1@eval.local", "senha123");
        UseToken(user.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/evaluations/feedback",
            new FeedbackUpsertRequest("chat1", "msg1", "fake:1", 0, null));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Feedback_CriaBuscaAtualizaEDeletaPorId()
    {
        var user = await SignUpAsync("Eval2", "e2@eval.local", "senha123");
        UseToken(user.Token);

        var created = await _client.PostAsJsonAsync("/api/v1/evaluations/feedback",
            new FeedbackUpsertRequest("chat1", "msg9", "fake:1", 1, "bom"));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var feedback = (await created.Content.ReadFromJsonAsync<FeedbackResponse>())!;
        Assert.That(feedback.Rating, Is.EqualTo(1));

        var fetched = await _client.GetFromJsonAsync<FeedbackResponse>(
            $"/api/v1/evaluations/feedback/{feedback.Id}");
        Assert.That(fetched!.Rating, Is.EqualTo(1));

        // POST /feedback/{id} atualiza a avaliação da mesma mensagem.
        var updated = await _client.PostAsJsonAsync(
            $"/api/v1/evaluations/feedback/{feedback.Id}",
            new FeedbackUpsertRequest("chat1", "msg9", "fake:1", -1, "piorou"));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var updatedFeedback = (await updated.Content.ReadFromJsonAsync<FeedbackResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(updatedFeedback.Rating, Is.EqualTo(-1));
            Assert.That(updatedFeedback.Reason, Is.EqualTo("piorou"));
        });

        var deleted = await _client.DeleteAsync($"/api/v1/evaluations/feedback/{feedback.Id}");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var gone = await _client.GetAsync($"/api/v1/evaluations/feedback/{feedback.Id}");
        Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Feedbacks_User_ListaSomenteDoUsuario()
    {
        var userA = await SignUpAsync("Eval3A", "e3a@eval.local", "senha123");
        UseToken(userA.Token);
        await _client.PostAsJsonAsync("/api/v1/evaluations/feedback",
            new FeedbackUpsertRequest("c1", "m1", null, 1, null));
        await _client.PostAsJsonAsync("/api/v1/evaluations/feedback",
            new FeedbackUpsertRequest("c1", "m2", null, -1, "ruim"));

        var userB = await SignUpAsync("Eval3B", "e3b@eval.local", "senha123");
        UseToken(userB.Token);
        await _client.PostAsJsonAsync("/api/v1/evaluations/feedback",
            new FeedbackUpsertRequest("c9", "m9", null, 1, null));

        var listB = await _client.GetFromJsonAsync<List<FeedbackResponse>>(
            "/api/v1/evaluations/feedbacks/user");
        Assert.That(listB, Has.Count.EqualTo(1));

        UseToken(userA.Token);
        var listA = await _client.GetFromJsonAsync<List<FeedbackResponse>>(
            "/api/v1/evaluations/feedbacks/user");
        Assert.That(listA, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Feedbacks_ListEExport_ExigemAdmin()
    {
        var user = await SignUpAsync("Eval4", "e4@eval.local", "senha123");
        UseToken(user.Token);
        await _client.PostAsJsonAsync("/api/v1/evaluations/feedback",
            new FeedbackUpsertRequest("cx", "mx", "fake:1", 1, null));

        var listForbidden = await _client.GetAsync("/api/v1/evaluations/feedbacks/list");
        var exportForbidden = await _client.GetAsync("/api/v1/evaluations/feedbacks/all/export");
        Assert.Multiple(() =>
        {
            Assert.That(listForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(exportForbidden.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        });

        UseToken(_adminToken);
        var all = await _client.GetFromJsonAsync<List<AdminFeedbackResponse>>(
            "/api/v1/evaluations/feedbacks/list");
        Assert.That(all!.Any(f => f.MessageId == "mx" && f.UserName == "Eval4"), Is.True);

        var export = await _client.GetFromJsonAsync<List<FeedbackResponse>>(
            "/api/v1/evaluations/feedbacks/all/export");
        Assert.That(export!.Any(f => f.MessageId == "mx"), Is.True);
    }

    [Test]
    public async Task Feedbacks_DeleteAll_RemoveApenasDoUsuario()
    {
        var userA = await SignUpAsync("Eval5A", "e5a@eval.local", "senha123");
        UseToken(userA.Token);
        await _client.PostAsJsonAsync("/api/v1/evaluations/feedback",
            new FeedbackUpsertRequest("ca", "ma", null, 1, null));

        var userB = await SignUpAsync("Eval5B", "e5b@eval.local", "senha123");
        UseToken(userB.Token);
        await _client.PostAsJsonAsync("/api/v1/evaluations/feedback",
            new FeedbackUpsertRequest("cb", "mb", null, -1, null));

        UseToken(userA.Token);
        var deleted = await _client.DeleteAsync("/api/v1/evaluations/feedbacks/all");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var listA = await _client.GetFromJsonAsync<List<FeedbackResponse>>(
            "/api/v1/evaluations/feedbacks/user");
        Assert.That(listA, Is.Empty);

        // Feedback de outro usuário permanece (visão admin).
        UseToken(_adminToken);
        var export = await _client.GetFromJsonAsync<List<FeedbackResponse>>(
            "/api/v1/evaluations/feedbacks/all/export");
        Assert.Multiple(() =>
        {
            Assert.That(export!.Any(f => f.MessageId == "mb"), Is.True);
            Assert.That(export!.Any(f => f.MessageId == "ma"), Is.False);
        });
    }
}
