using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenWebUI.Api.Endpoints;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using Microsoft.Extensions.Caching.Hybrid;

namespace OpenWebUI.Api.Tests;

/// <summary>Branches de erro e borda dos endpoints de chats, tasks, auth, grupos, imagens e do ToolExecutor.</summary>
[TestFixture, IsolateEnvironment]
public class EndpointEdgeTests
{
    private readonly HybridCache mc1 = TestCache.Create();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _dbPath = null!;
    private string _mockBaseUrl = null!;
    private string _adminToken = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _mockBaseUrl = StartMock();
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-edges-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@edges.local", "senha123");
        _adminToken = admin.Token;
        UseToken(_adminToken);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var connResponse = await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig([_mockBaseUrl], [], []));
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

    /// <summary>Sobe um provedor Ollama + endpoints de tool mockados numa porta livre.</summary>
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

            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            var (status, json) = Route(ctx.Request.Url!.AbsolutePath, body);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    /// <summary>
    /// O conteúdo do /api/chat é roteado por marcadores embutidos no histórico,
    /// permitindo simular respostas do LLM por cenário de teste.
    /// </summary>
    private static (int, string) Route(string path, string body)
    {
        switch (path)
        {
            case "/api/tags":
                return (200, "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}");
            case "/api/chat":
                var content = body switch
                {
                    _ when body.Contains("MARK_BADJSON") => "isto nao e um array json",
                    _ when body.Contains("MARK_BROKEN") => "[{quebrado}]",
                    _ when body.Contains("MARK_LONG") => new string('x', 120),
                    _ when body.Contains("MARK_QUOTED") => "\"Titulo Citado\"",
                    _ when body.Contains("MARK_MULTI") => "Titulo Um\nsegunda linha",
                    _ when body.Contains("MARK_TAGS") => "[\"  \", \"TAG\", \"tag\", \"outra\"]",
                    _ => "Titulo Mock",
                };
                return (200, "{\"message\":{\"content\":" + JsonSerializer.Serialize(content) + "}}");
            case "/tool-ok":
                return (200, "resultado em texto puro");
            case "/tool-500":
                return (500, "falha interna");
            case "/tool-big":
                return (200, new string('y', 5000));
            default:
                return (404, "{}");
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

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private static TaskGenerationRequest TaskRequest(string marker) =>
        new("fake:1", [new ChatCompletionMessage("user", $"conteudo {marker}")], null);

    // ---------- ChatEndpoints ----------

    [Test]
    public async Task Criar_PayloadInvalido_Retorna400()
    {
        var auth = await SignUpAsync("BadJson", "badjson@edges.local", "senha123");
        UseToken(auth.Token);

        using var content = new StringContent("{nao e json", Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/v1/chats/", content);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Criar_TituloVazio_UsaNewChat()
    {
        var auth = await SignUpAsync("SemTitulo", "semtitulo@edges.local", "senha123");
        UseToken(auth.Token);

        var created = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest("   ", null!, null!));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(chat.Title, Is.EqualTo("New Chat"));
            Assert.That(chat.Models, Is.Empty);
            Assert.That(chat.Messages, Is.Empty);
        });
    }

    [Test]
    public async Task Chat_Inexistente_Retorna404EmTodasAsOperacoes()
    {
        var auth = await SignUpAsync("Missing", "missing@edges.local", "senha123");
        UseToken(auth.Token);
        const string id = "chat-inexistente";

        Assert.Multiple(async () =>
        {
            Assert.That((await _client.GetAsync($"/api/v1/chats/{id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsJsonAsync($"/api/v1/chats/{id}",
                new ChatUpsertRequest("t", [], []))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.DeleteAsync($"/api/v1/chats/{id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsync($"/api/v1/chats/{id}/pin", null)).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.GetAsync($"/api/v1/chats/{id}/pinned")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsync($"/api/v1/chats/{id}/archive", null)).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsync($"/api/v1/chats/{id}/share", null)).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsync($"/api/v1/chats/{id}/clone", null)).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsJsonAsync($"/api/v1/chats/{id}/folder",
                new ChatEndpoints.SetFolderRequest("p1"))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsJsonAsync($"/api/v1/chats/{id}/tags",
                new ChatEndpoints.TagUpdateRequest(["a"]))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.DeleteAsync($"/api/v1/chats/{id}/tags")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsJsonAsync($"/api/v1/chats/{id}/messages/m1",
                new MessageUpdateRequest("x"))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.DeleteAsync($"/api/v1/chats/{id}/messages/m1")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
        });

        _client.DefaultRequestHeaders.Authorization = null;
        var publicView = await _client.GetAsync("/api/v1/chats/share/naoexiste");
        Assert.That(publicView.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Chat_DeOutroUsuario_Retorna404()
    {
        var dono = await SignUpAsync("Dono", "dono@edges.local", "senha123");
        var outro = await SignUpAsync("Outro", "outro@edges.local", "senha123");

        UseToken(dono.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest("Privado", [], []));
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;

        UseToken(outro.Token);
        Assert.Multiple(async () =>
        {
            Assert.That((await _client.GetAsync($"/api/v1/chats/{chat.Id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsJsonAsync($"/api/v1/chats/{chat.Id}",
                new ChatUpsertRequest("roubo", [], []))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsync($"/api/v1/chats/{chat.Id}/share", null)).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.DeleteAsync($"/api/v1/chats/{chat.Id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task MoverChat_PastaInexistente_Retorna404()
    {
        var auth = await SignUpAsync("Folder", "folder@edges.local", "senha123");
        UseToken(auth.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest("Movel", [], []));
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;

        var missing = await _client.PostAsJsonAsync($"/api/v1/chats/{chat.Id}/folder",
            new ChatEndpoints.SetFolderRequest("pasta-inexistente"));
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // folderId nulo limpa a pasta sem validar existência.
        var cleared = await _client.PostAsJsonAsync($"/api/v1/chats/{chat.Id}/folder",
            new ChatEndpoints.SetFolderRequest(null));
        Assert.That(cleared.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Mensagem_Inexistente_Retorna404()
    {
        var auth = await SignUpAsync("Msg", "msg@edges.local", "senha123");
        UseToken(auth.Token);
        var msgs = new List<ChatMessageModel> { new("m1", "user", "oi", null, 1) };
        var created = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest("Com msgs", [], msgs));
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;

        var updated = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/messages/m99", new MessageUpdateRequest("novo"));
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var deleted = await _client.DeleteAsync($"/api/v1/chats/{chat.Id}/messages/m99");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task DeletarMensagem_RemoveElaETodasAsPosteriores()
    {
        var auth = await SignUpAsync("Tail", "tail@edges.local", "senha123");
        UseToken(auth.Token);
        var msgs = new List<ChatMessageModel>
        {
            new("m1", "user", "um", null, 1),
            new("m2", "assistant", "dois", null, 2),
            new("m3", "user", "tres", null, 3),
        };
        var created = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest("Cauda", [], msgs));
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;

        var deleted = await _client.DeleteAsync($"/api/v1/chats/{chat.Id}/messages/m2");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var after = (await deleted.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.Multiple(() =>
        {
            Assert.That(after.Messages, Has.Count.EqualTo(1));
            Assert.That(after.Messages[0].Id, Is.EqualTo("m1"));
        });
    }

    [Test]
    public async Task Tags_NormalizaEspacosVaziasEDuplicadas()
    {
        var auth = await SignUpAsync("Tagger", "tagger@edges.local", "senha123");
        UseToken(auth.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/chats/",
            new ChatUpsertRequest("Tags", [], []));
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;

        var updated = await _client.PostAsJsonAsync($"/api/v1/chats/{chat.Id}/tags",
            new ChatEndpoints.TagUpdateRequest(["  ", "Alpha", "alpha", " beta "]));
        var after = (await updated.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.That(after.Tags, Is.EqualTo(new[] { "Alpha", "beta" }));

        var cleared = await _client.DeleteAsync($"/api/v1/chats/{chat.Id}/tags");
        Assert.That(cleared.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var updatedNull = await _client.PostAsJsonAsync($"/api/v1/chats/{chat.Id}/tags",
            new ChatEndpoints.TagUpdateRequest(null));
        var afterNull = (await updatedNull.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.That(afterNull.Tags, Is.Empty);
    }

    [Test]
    public async Task Import_TituloVazio_ViraNewChat()
    {
        var auth = await SignUpAsync("Import", "import@edges.local", "senha123");
        UseToken(auth.Token);

        var imported = await _client.PostAsJsonAsync("/api/v1/chats/import",
            new List<ChatUpsertRequest> { new(" ", [], []) });
        Assert.That(imported.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>("/api/v1/chats/");
        Assert.That(list, Has.Count.EqualTo(1));
        Assert.That(list![0].Title, Is.EqualTo("New Chat"));
    }

    // ---------- TaskEndpoints ----------

    [Test]
    public async Task Titulo_SemMensagens_UsaSoTemplate()
    {
        var auth = await SignUpAsync("Empty", "empty@edges.local", "senha123");
        UseToken(auth.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/tasks/title/completions",
            new TaskGenerationRequest("fake:1", [], null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var title = (await response.Content.ReadFromJsonAsync<TaskTitleResponse>())!;
        Assert.That(title.Title, Is.EqualTo("Titulo Mock"));
    }

    [Test]
    public async Task Titulo_RespostaCitadaEMultiLinha_LimpaETrunca()
    {
        var auth = await SignUpAsync("Cleaner", "cleaner@edges.local", "senha123");
        UseToken(auth.Token);

        var quoted = await _client.PostAsJsonAsync("/api/v1/tasks/title/completions",
            TaskRequest("MARK_QUOTED"));
        var title = (await quoted.Content.ReadFromJsonAsync<TaskTitleResponse>())!;
        Assert.That(title.Title, Is.EqualTo("Titulo Citado"));

        var multi = await _client.PostAsJsonAsync("/api/v1/tasks/title/completions",
            TaskRequest("MARK_MULTI"));
        var titleMulti = (await multi.Content.ReadFromJsonAsync<TaskTitleResponse>())!;
        Assert.That(titleMulti.Title, Is.EqualTo("Titulo Um"));

        var longTitle = await _client.PostAsJsonAsync("/api/v1/tasks/title/completions",
            TaskRequest("MARK_LONG"));
        var titleLong = (await longTitle.Content.ReadFromJsonAsync<TaskTitleResponse>())!;
        Assert.That(titleLong.Title, Has.Length.EqualTo(80));
    }

    [Test]
    public async Task FollowUps_JsonMalformado_RetornaListaVazia()
    {
        var auth = await SignUpAsync("Fu", "fu@edges.local", "senha123");
        UseToken(auth.Token);

        var noArray = await _client.PostAsJsonAsync("/api/v1/tasks/follow_up/completions",
            TaskRequest("MARK_BADJSON"));
        var followUps = (await noArray.Content.ReadFromJsonAsync<TaskFollowUpsResponse>())!;
        Assert.That(followUps.FollowUps, Is.Empty);

        var broken = await _client.PostAsJsonAsync("/api/v1/tasks/follow_up/completions",
            TaskRequest("MARK_BROKEN"));
        var followUpsBroken = (await broken.Content.ReadFromJsonAsync<TaskFollowUpsResponse>())!;
        Assert.That(followUpsBroken.FollowUps, Is.Empty);

        var queries = await _client.PostAsJsonAsync("/api/v1/tasks/queries/completions",
            TaskRequest("MARK_BADJSON"));
        var json = await queries.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(json.GetProperty("queries").GetArrayLength(), Is.EqualTo(0));
    }

    [Test]
    public async Task Tags_FiltraVaziasNormalizaEDeduplica()
    {
        var auth = await SignUpAsync("TagFil", "tagfil@edges.local", "senha123");
        UseToken(auth.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/tasks/tags/completions",
            TaskRequest("MARK_TAGS"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var tags = (await response.Content.ReadFromJsonAsync<TaskTagsResponse>())!;
        Assert.That(tags.Tags, Is.EqualTo(new[] { "tag", "outra" }));
    }

    [Test]
    public async Task Titulo_ProviderFora_Retorna404()
    {
        var auth = await SignUpAsync("Down", "down@edges.local", "senha123");

        UseToken(_adminToken);
        var broken = await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig(["http://localhost:1"], [], []));
        Assert.That(broken.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        try
        {
            UseToken(auth.Token);
            var response = await _client.PostAsJsonAsync("/api/v1/tasks/title/completions",
                TaskRequest("qualquer"));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        finally
        {
            UseToken(_adminToken);
            await _client.PostAsJsonAsync("/api/v1/configs/connections",
                new ConnectionsConfig([_mockBaseUrl], [], []));
        }
    }

    [Test]
    public async Task Titulo_SemConexao_Retorna404()
    {
        var auth = await SignUpAsync("NoConn", "noconn@edges.local", "senha123");

        UseToken(_adminToken);
        var empty = await _client.PostAsJsonAsync("/api/v1/configs/connections",
            new ConnectionsConfig([], [], []));
        Assert.That(empty.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        try
        {
            UseToken(auth.Token);
            var response = await _client.PostAsJsonAsync("/api/v1/tasks/title/completions",
                TaskRequest("qualquer"));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        }
        finally
        {
            UseToken(_adminToken);
            await _client.PostAsJsonAsync("/api/v1/configs/connections",
                new ConnectionsConfig([_mockBaseUrl], [], []));
        }
    }

    // ---------- AuthEndpoints ----------

    [Test]
    public async Task Token_Invalido_Retorna401()
    {
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "token.invalido.qualquer");

        var response = await _client.GetAsync("/api/v1/auths/");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Token_Expirado_Retorna401()
    {
        var auth = await SignUpAsync("Expired", "expired@edges.local", "senha123");

        string secret;
        await using (var db = CreateContext())
        {
            var entry = await db.ConfigEntries.FindAsync("webui.jwt.secret");
            secret = JsonSerializer.Deserialize<string>(entry!.ValueJson)!;
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var expired = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims:
            [
                new Claim(ClaimTypes.NameIdentifier, auth.User.Id),
                new Claim(ClaimTypes.Role, auth.User.Role),
            ],
            expires: DateTime.UtcNow.AddMinutes(-30),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));

        UseToken(expired);
        var response = await _client.GetAsync("/api/v1/auths/");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Token_UsuarioDeletado_Retorna401()
    {
        var auth = await SignUpAsync("Ghost", "ghost@edges.local", "senha123");

        await using (var db = CreateContext())
        {
            var user = await db.Users.FindAsync(auth.User.Id);
            db.Users.Remove(user!);
            await db.SaveChangesAsync();
        }

        UseToken(auth.Token);
        var response = await _client.GetAsync("/api/v1/auths/");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Signup_PapelPending_NaoEmiteTokenESigninFalha()
    {
        UseToken(_adminToken);
        await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "pending" });
        try
        {
            var pending = await SignUpAsync("Pending", "pending@edges.local", "senha123");
            Assert.Multiple(() =>
            {
                Assert.That(pending.Token, Is.Empty);
                Assert.That(pending.User.Role, Is.EqualTo("pending"));
            });

            _client.DefaultRequestHeaders.Authorization = null;
            var signin = await _client.PostAsJsonAsync("/api/v1/auths/signin",
                new SignInRequest("pending@edges.local", "senha123"));
            Assert.That(signin.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }
        finally
        {
            UseToken(_adminToken);
            await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
                AdminConfig.Default with { DefaultUserRole = "user" });
        }
    }

    // ---------- GroupEndpoints ----------

    [Test]
    public async Task Grupo_Inexistente_Operacoes_Retornam404()
    {
        UseToken(_adminToken);
        const string id = "grupo-inexistente";

        Assert.Multiple(async () =>
        {
            Assert.That((await _client.GetAsync($"/api/v1/groups/{id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PutAsJsonAsync($"/api/v1/groups/{id}",
                new UpdateGroupRequest("Novo", null, null))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.DeleteAsync($"/api/v1/groups/{id}")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PostAsJsonAsync($"/api/v1/groups/{id}/members",
                new UpdateGroupMembersRequest(["u1"]))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.DeleteAsync($"/api/v1/groups/{id}/members/u1")).StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That((await _client.PutAsJsonAsync($"/api/v1/groups/{id}/members/u1",
                new UpdateGroupMembersRequest([], "admin"))).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task Grupo_DeletePorNaoAdmin_Retorna403()
    {
        var comum = await SignUpAsync("Comum", "comum@edges.local", "senha123");

        UseToken(_adminToken);
        var created = await _client.PostAsJsonAsync("/api/v1/groups",
            new CreateGroupRequest("Protegido", null, null));
        var group = (await created.Content.ReadFromJsonAsync<GroupResponse>())!;

        UseToken(comum.Token);
        var denied = await _client.DeleteAsync($"/api/v1/groups/{group.Id}");
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        var deniedGet = await _client.GetAsync($"/api/v1/groups/{group.Id}");
        Assert.That(deniedGet.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    // ---------- ImageEndpoints ----------

    [Test]
    public async Task Imagens_ConfigIncompleta_Retorna501()
    {
        var auth = await SignUpAsync("Img", "img@edges.local", "senha123");

        UseToken(_adminToken);
        var saved = await _client.PostAsJsonAsync("/api/v1/images/config",
            new ImagesConfig(true, "openai", " ", "", "gpt-image-1", "1024x1024", 30, "{}"));
        Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        try
        {
            UseToken(auth.Token);
            var response = await _client.PostAsJsonAsync("/api/v1/images/generations",
                new ImageGenerationRequest("um gato", 1, null));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotImplemented));
        }
        finally
        {
            UseToken(_adminToken);
            await _client.PostAsJsonAsync("/api/v1/images/config", ImagesConfig.Default);
        }
    }

    [Test]
    public async Task Imagens_PromptVazio_Retorna400()
    {
        var auth = await SignUpAsync("ImgEmpty", "imgempty@edges.local", "senha123");
        UseToken(auth.Token);

        var response = await _client.PostAsJsonAsync("/api/v1/images/generations",
            new ImageGenerationRequest("   ", 1, null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        UseToken(_adminToken);
        var badEngine = await _client.PostAsJsonAsync("/api/v1/images/config",
            ImagesConfig.Default with { Engine = "stable-diffusion" });
        Assert.That(badEngine.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---------- ToolExecutor (serviço direto) ----------

    private static string SpecFor(string name) =>
        $"{{\"type\":\"function\",\"function\":{{\"name\":\"{name}\",\"parameters\":{{}}}}}}";

    private static Tool NewTool(string name, string url, bool enabled = true, string? spec = null) =>
        new()
        {
            UserId = "u1",
            Name = name,
            SpecJson = spec ?? SpecFor(name),
            Url = url,
            Enabled = enabled,
        };

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static PythonToolExecutor NewPythonExecutor() =>
        new(new ConfigurationBuilder().Build());

    private McpClientService NewMcp(AppDbContext db) =>
        new(db, mc1);

    private static BuiltinToolRegistry EmptyRegistry() =>
        new([], new ConfigurationBuilder().Build());

    [Test]
    public async Task Executor_ToolDesconhecida_OuSpecQuebrada_RetornaErro()
    {
        await using var db = CreateContext();
        var executor = new ToolExecutor(db, new StubHttpClientFactory(), NewPythonExecutor(), NewMcp(db), EmptyRegistry(), new ConfigService(db, TestCache.Create()));
        var tools = new List<Tool>
        {
            NewTool("outra", $"{_mockBaseUrl}/tool-ok"),
            NewTool("quebrada", $"{_mockBaseUrl}/tool-ok", spec: "{nao e json"),
        };

        var unknown = (await executor.ExecuteAsync(tools, "minha_tool", "{}")).Text;
        Assert.That(unknown, Does.Contain("minha_tool").And.Contain("habilitada"));

        var brokenSpec = (await executor.ExecuteAsync(tools, "quebrada", "{}")).Text;
        Assert.That(brokenSpec, Does.Contain("quebrada").And.Contain("habilitada"));
    }

    [Test]
    public async Task Executor_UrlInacessivel_OuCancelada_RetornaErro()
    {
        await using var db = CreateContext();
        var executor = new ToolExecutor(db, new StubHttpClientFactory(), NewPythonExecutor(), NewMcp(db), EmptyRegistry(), new ConfigService(db, TestCache.Create()));
        var tools = new List<Tool> { NewTool("minha_tool", "http://localhost:1/x") };

        var down = (await executor.ExecuteAsync(tools, "minha_tool", "{}")).Text;
        Assert.That(down, Does.Contain("Erro ao executar tool 'minha_tool'"));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = (await executor.ExecuteAsync(tools, "minha_tool", "{}", cts.Token)).Text;
        Assert.That(cancelled, Does.Contain("Erro ao executar tool 'minha_tool'"));
    }

    [Test]
    public async Task Executor_Respostas_TextoErroETruncamento()
    {
        await using var db = CreateContext();
        var executor = new ToolExecutor(db, new StubHttpClientFactory(), NewPythonExecutor(), NewMcp(db), EmptyRegistry(), new ConfigService(db, TestCache.Create()));
        var tools = new List<Tool>
        {
            NewTool("minha_tool", $"{_mockBaseUrl}/tool-ok"),
            NewTool("errada", $"{_mockBaseUrl}/tool-500"),
            NewTool("grande", $"{_mockBaseUrl}/tool-big"),
        };

        // Resposta não-JSON volta verbatim (o modelo interpreta o conteúdo).
        var text = (await executor.ExecuteAsync(tools, "minha_tool", "{}")).Text;
        Assert.That(text, Is.EqualTo("resultado em texto puro"));

        var httpError = (await executor.ExecuteAsync(tools, "errada", "{}")).Text;
        Assert.That(httpError, Does.Contain("respondeu 500"));

        var big = (await executor.ExecuteAsync(tools, "grande", "{}")).Text;
        Assert.That(big, Has.Length.EqualTo(4000));
    }

    [Test]
    public async Task Executor_LoadEnabled_FiltraVaziasDesabilitadasEAlheias()
    {
        await using var db = CreateContext();
        var executor = new ToolExecutor(db, new StubHttpClientFactory(), NewPythonExecutor(), NewMcp(db), EmptyRegistry(), new ConfigService(db, TestCache.Create()));

        var empty = await executor.LoadEnabledAsync("u1", []);
        Assert.That(empty, Is.Empty);

        var dono = await SignUpAsync("ToolOwner", "toolowner@edges.local", "senha123");
        var outro = await SignUpAsync("ToolOther", "toolother@edges.local", "senha123");

        var own = NewTool("minha_tool", "http://x");
        own.UserId = dono.User.Id;
        var disabled = NewTool("off", "http://x", enabled: false);
        disabled.UserId = dono.User.Id;
        var foreign = NewTool("alheia", "http://x");
        foreign.UserId = outro.User.Id;
        db.Tools.AddRange(own, disabled, foreign);
        await db.SaveChangesAsync();

        var loaded = await executor.LoadEnabledAsync(dono.User.Id, [own.Id, disabled.Id, foreign.Id]);
        Assert.That(loaded, Has.Count.EqualTo(1));
        Assert.That(loaded[0].Id, Is.EqualTo(own.Id));
    }
}
