using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Infrastructure.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do slice agent-personas-channels (#413): CRUD, dispatch de canal
/// com sessão por chatKey, guardrails (strict não anuncia file_write),
/// wait=false → 202, audit por turno e isolamento por dono.
/// O mock local (HttpListener) faz de Ollama — responde "pong" fixo.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class AgentPersonaTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _mockBase = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private string _adminToken = null!;

    /// <summary>Corpos dos POSTs /api/chat recebidos pelo mock (FIFO).</summary>
    private readonly ConcurrentQueue<string> _chatBodies = new();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-persona-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _mockBase = StartMock();

        var admin = await SignUpAsync("Admin", "admin@persona.local");
        _adminToken = admin.Token;
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());

        var connResponse = await _client.PostAsJsonAsync(
            "/api/v1/configs/connections", new ConnectionsConfig([_mockBase], [], []));
        Assert.That(connResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
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
            catch (ObjectDisposedException)
            {
                return;
            }

            string body = string.Empty;
            if (ctx.Request.HasEntityBody)
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                body = await reader.ReadToEndAsync(ct);
            }

            var path = ctx.Request.Url!.AbsolutePath;
            if (path == "/api/tags")
            {
                WriteJson(ctx, "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}");
            }
            else if (path == "/api/chat")
            {
                _chatBodies.Enqueue(body);
                WriteJson(ctx,
                    "{\"message\":{\"role\":\"assistant\",\"content\":\"pong\"}}");
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
    }

    private static void WriteJson(HttpListenerContext ctx, string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    /// <summary>
    /// ToolIds serializados na run indicada (whitelist do guardrail).
    /// Buscar "a mais recente por CreatedAt" é ambíguo — CreatedAt tem
    /// resolução de segundos e a run do teste anterior do fixture pode
    /// empatar e vencer a ordenação.
    /// </summary>
    private async Task<string> RunToolIdsAsync(string runId)
    {
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={_dbPath}").Options);
        var json = await db.ChatRuns
            .Where(r => r.Id == runId)
            .Select(r => r.RequestJson)
            .SingleAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("toolIds", out var t)
            ? t.GetRawText()
            : "[]";
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, "senha123"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    private async Task<AgentPersonaResponse> CriarPersonaAsync(
        string nome, string guardrail = "basic", bool enabled = true)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/agent-personas",
            new AgentPersonaUpsertRequest(
                nome, "Você é a persona de teste.", "fake:1",
                guardrail, null, enabled));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AgentPersonaResponse>())!;
    }

    [Test]
    public async Task Crud_CriaListaAtualizaDeleta()
    {
        var persona = await CriarPersonaAsync("crud-persona", "strict");

        var lista = await _client.GetFromJsonAsync<List<AgentPersonaResponse>>(
            "/api/v1/agent-personas");
        Assert.That(lista!.Any(p => p.Id == persona.Id), Is.True);

        var put = await _client.PutAsJsonAsync(
            $"/api/v1/agent-personas/{persona.Id}",
            new AgentPersonaUpsertRequest(
                "crud-renomeada", "novo prompt", "fake:1", "none", null, true));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var atualizada = await put.Content.ReadFromJsonAsync<AgentPersonaResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(atualizada!.Name, Is.EqualTo("crud-renomeada"));
            Assert.That(atualizada.GuardrailLevel, Is.EqualTo("none"));
        });

        var del = await _client.DeleteAsync($"/api/v1/agent-personas/{persona.Id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var depois = await _client.GetFromJsonAsync<List<AgentPersonaResponse>>(
            "/api/v1/agent-personas");
        Assert.That(depois!.Any(p => p.Id == persona.Id), Is.False);
    }

    [Test]
    public async Task Message_Sincrona_CriaSessao_EGravaAudit()
    {
        var persona = await CriarPersonaAsync("sync-persona");

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/agent-personas/{persona.Id}/message",
            new PersonaMessageRequest("olá persona", "webhook:contato-1"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        var resultado = await response.Content.ReadFromJsonAsync<PersonaMessageResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(resultado!.Reply, Is.EqualTo("pong"));
            Assert.That(resultado.Status, Is.EqualTo("completed"));
            Assert.That(resultado.SessionId, Is.Not.Null.And.Not.Empty);
            Assert.That(resultado.RunId, Is.Not.Null.And.Not.Empty);
        });

        var audit = await _client.GetFromJsonAsync<List<AgentTurnResponse>>(
            $"/api/v1/agent-personas/{persona.Id}/audit");
        Assert.That(audit, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(audit![0].InText, Is.EqualTo("olá persona"));
            Assert.That(audit[0].OutText, Is.EqualTo("pong"));
            Assert.That(audit[0].Channel, Is.EqualTo("webhook"));
            Assert.That(audit[0].ChatKey, Is.EqualTo("webhook:contato-1"));
            Assert.That(audit[0].Status, Is.EqualTo("completed"));
        });
    }

    [Test]
    public async Task ChatKey_ReutilizaSessao_ESegundaVeContexto()
    {
        var persona = await CriarPersonaAsync("session-persona");
        while (_chatBodies.TryDequeue(out _)) { }

        var r1 = await _client.PostAsJsonAsync(
            $"/api/v1/agent-personas/{persona.Id}/message",
            new PersonaMessageRequest("primeira", "canal-x"));
        var s1 = (await r1.Content.ReadFromJsonAsync<PersonaMessageResponse>())!;
        var r2 = await _client.PostAsJsonAsync(
            $"/api/v1/agent-personas/{persona.Id}/message",
            new PersonaMessageRequest("segunda", "canal-x"));
        var s2 = (await r2.Content.ReadFromJsonAsync<PersonaMessageResponse>())!;

        Assert.That(s2.SessionId, Is.EqualTo(s1.SessionId));
        // O segundo request ao provider carrega o turno 1 (histórico da sessão).
        var segundo = _chatBodies.Last(b => b.Contains("segunda"));
        Assert.Multiple(() =>
        {
            Assert.That(segundo, Does.Contain("primeira"));
            Assert.That(segundo, Does.Contain("pong"));
            Assert.That(segundo, Does.Contain("segunda"));
            Assert.That(segundo, Does.Contain("persona de teste"));
        });
    }

    [Test]
    public async Task GuardrailStrict_NaoAnunciaFileWrite_EInjettaLinha()
    {
        var persona = await CriarPersonaAsync("strict-persona", "strict");
        while (_chatBodies.TryDequeue(out _)) { }

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/agent-personas/{persona.Id}/message",
            new PersonaMessageRequest("apague o arquivo x.txt", null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var resultado = await response.Content
            .ReadFromJsonAsync<PersonaMessageResponse>();

        var body = _chatBodies.Last();
        Assert.Multiple(() =>
        {
            // Anunciadas ao provider: só leitura de rede/memória — workspace
            // tools também caem no modo chat (sem repo vinculado).
            Assert.That(body, Does.Not.Contain("file_write"));
            Assert.That(body, Does.Not.Contain("shell_exec"));
            Assert.That(body, Does.Contain("builtin_web_search"));
            Assert.That(body, Does.Contain("destrutivas")); // linha strict no system
        });

        // O whitelist do guardrail é o ToolIds serializado na run — prova
        // que file_read entra no conjunto strict antes do filtro de modo.
        var toolIds = await RunToolIdsAsync(resultado!.RunId!);
        Assert.Multiple(() =>
        {
            Assert.That(toolIds, Does.Contain("builtin:file_read"));
            Assert.That(toolIds, Does.Not.Contain("builtin:file_write"));
            Assert.That(toolIds, Does.Not.Contain("builtin:shell_exec"));
        });
    }

    [Test]
    public async Task GuardrailBasic_BloqueiaExec_MasAnunciaWrite()
    {
        var persona = await CriarPersonaAsync("basic-persona", "basic");
        while (_chatBodies.TryDequeue(out _)) { }

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/agent-personas/{persona.Id}/message",
            new PersonaMessageRequest("oi", null));
        var resultado = await response.Content
            .ReadFromJsonAsync<PersonaMessageResponse>();

        // basic libera mutações no whitelist — a negação das tools de
        // execução acontece no guardrail, e a de workspace no modo chat.
        var toolIds = await RunToolIdsAsync(resultado!.RunId!);
        Assert.Multiple(() =>
        {
            Assert.That(toolIds, Does.Not.Contain("builtin:shell_exec"));
            Assert.That(toolIds, Does.Not.Contain("builtin:code_interpreter"));
            Assert.That(toolIds, Does.Not.Contain("builtin:job_kill"));
            Assert.That(toolIds, Does.Contain("builtin:file_write"));
        });
    }

    [Test]
    public async Task WaitFalse_Retorna202_EAuditFechaDepois()
    {
        var persona = await CriarPersonaAsync("async-persona");

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/agent-personas/{persona.Id}/message?wait=false",
            new PersonaMessageRequest("demorada", null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var aceite = await response.Content.ReadFromJsonAsync<PersonaMessageResponse>();
        Assert.That(aceite!.RunId, Is.Not.Null.And.Not.Empty);

        // O notifier fecha o turno ao término da run — poll até completar.
        AgentTurnResponse? turno = null;
        for (var i = 0; i < 40 && turno?.Status != "completed"; i++)
        {
            await Task.Delay(500);
            var audit = await _client.GetFromJsonAsync<List<AgentTurnResponse>>(
                $"/api/v1/agent-personas/{persona.Id}/audit");
            turno = audit?.FirstOrDefault();
        }
        Assert.That(turno, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(turno!.Status, Is.EqualTo("completed"));
            Assert.That(turno.OutText, Is.EqualTo("pong"));
        });
    }

    [Test]
    public async Task PersonaDesabilitada_Retorna404()
    {
        var persona = await CriarPersonaAsync("off-persona", "basic", enabled: false);
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/agent-personas/{persona.Id}/message",
            new PersonaMessageRequest("oi", null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Persona_DeOutroUsuario_Retorna404()
    {
        var persona = await CriarPersonaAsync("privada-persona");
        var outro = await SignUpAsync("Outro", $"outro-{Guid.NewGuid():N}@persona.local");
        UseToken(outro.Token);
        try
        {
            var response = await _client.PostAsJsonAsync(
                $"/api/v1/agent-personas/{persona.Id}/message",
                new PersonaMessageRequest("oi", null));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            var lista = await _client.GetFromJsonAsync<List<AgentPersonaResponse>>(
                "/api/v1/agent-personas");
            Assert.That(lista!.Any(p => p.Id == persona.Id), Is.False);
        }
        finally
        {
            UseToken(_adminToken);
        }
    }
}
