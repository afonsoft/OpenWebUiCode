using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Api.Runs;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura do SPEC-20261016-run-recovery: eventos duráveis por run,
/// retomada de runs stale com snapshot (RF-003), liquidação de tool calls
/// órfãs com resultado sintético (RF-004), ownership por heartbeat
/// (RF-005) e meta resumedCount/lastSeq (RF-006). O mock local
/// (HttpListener) faz de Ollama — histórico com tool_call_id devolve a
/// resposta final.
/// </summary>
[TestFixture, IsolateEnvironment]
public class RunRecoveryTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _mockBase = null!;
    private string _adminToken = null!;
    private string _adminId = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-recovery-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _mockBase = StartMock();

        var admin = await SignUpAsync("Admin", "admin@recovery.local");
        _adminToken = admin.Token;
        _adminId = admin.User.Id;
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync(
            "/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());

        var connections = new ConnectionsConfig([_mockBase], [], []);
        var connResponse = await _client.PostAsJsonAsync(
            "/api/v1/configs/connections", connections);
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
            string json;
            if (path == "/api/tags")
            {
                json = "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}";
            }
            else if (path == "/api/chat")
            {
                // Histórico reconstruído já traz role=tool → resposta final;
                // sem tool_call_id o modelo "pede" a tool eco (round 0).
                json = body.Contains("\"tool_call_id\"", StringComparison.Ordinal)
                    ? "{\"message\":{\"role\":\"assistant\",\"content\":\"resposta pós-tool\"},"
                        + "\"done\":true,\"prompt_eval_count\":10,\"eval_count\":5}"
                    : "{\"message\":{\"role\":\"assistant\",\"content\":\"resposta final\"},"
                        + "\"done\":true,\"prompt_eval_count\":10,\"eval_count\":5}";
            }
            else if (path == "/tool/eco")
            {
                json = "resultado-eco";
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
        }
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

    private async Task<ChatResponse> CriarChatAsync()
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Chat recovery", ["fake:1"], []));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;

        using var scope = _factory.Services.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
        await config.SetAsync(
            $"chat:{chat.Id}:workspace.repo",
            new WorkspaceRepoBinding("a/b", "main", "repos/a__b", null, null),
            default);
        var patch = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}", new ChatPatchRequest(null, Mode: "build"));
        Assert.That(patch.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await patch.Content.ReadAsStringAsync());
        return chat;
    }

    private async Task<string> CriarToolAsync()
    {
        var spec = "{\"type\":\"function\",\"function\":{\"name\":\"eco\","
            + "\"description\":\"Eco de teste\",\"parameters\":{\"type\":\"object\","
            + "\"properties\":{\"texto\":{\"type\":\"string\"}}}}}";
        var response = await _client.PostAsJsonAsync("/api/v1/tools/",
            new ToolUpsertRequest("Eco", "eco", spec, $"{_mockBase}/tool/eco"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var tool = await response.Content.ReadFromJsonAsync<ToolResponse>();
        return tool!.Id;
    }

    /// <summary>
    /// Semeia uma run "morta": status running, dono inexistente e
    /// heartbeat velho — o sweep deve reivindicá-la como órfã.
    /// </summary>
    private async Task<ChatRun> SemearRunMortaAsync(
        string chatId, string? toolId, string status = ChatRunStatus.Running,
        string owner = "instancia-morta", int heartbeatAgeSeconds = 3600)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var agora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var run = new ChatRun
        {
            ChatId = chatId,
            UserId = _adminId,
            Model = "fake:1",
            Status = status,
            RequestJson = JsonSerializer.Serialize(new ChatCompletionRequest(
                "fake:1",
                [new ChatCompletionMessage("user", "oi")],
                ToolIds: toolId is null ? null : [toolId]), JsonOptions),
            OwnerInstance = owner,
            HeartbeatAt = heartbeatAgeSeconds < 0
                ? null : agora - heartbeatAgeSeconds,
            StartedAt = agora - 3600,
            CreatedAt = agora - 3600,
        };
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    private async Task SemearEventosAsync(
        string runId, params (int Seq, string Type, string Payload)[] eventos)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var agora = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (seq, type, payload) in eventos)
        {
            db.ChatRunEvents.Add(new ChatRunEvent
            {
                RunId = runId, Seq = seq, Type = type,
                PayloadJson = payload, CreatedAt = agora - 1800,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task<ChatRun> CarregarRunAsync(string runId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.ChatRuns.AsNoTracking()
            .FirstAsync(r => r.Id == runId));
    }

    private async Task<ChatRunResponse> AguardarFinalAsync(string chatId, string runId)
    {
        var limite = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < limite)
        {
            var run = await _client.GetFromJsonAsync<ChatRunResponse>(
                $"/api/v1/chats/{chatId}/runs/{runId}");
            Assert.That(run, Is.Not.Null);
            if (run!.Status is not ("queued" or "running"))
            {
                return run;
            }
            await Task.Delay(150);
        }
        Assert.Fail($"Run {runId} não finalizou em 60s.");
        return null!;
    }

    private static string SnapshotJson(
        int round, IReadOnlyList<ChatCompletionMessage> messages,
        IReadOnlyList<ChatCompletionMessage> toolMessages, int stepCount) =>
        RunRecoveryPlanner.SerializeCheckpoint(new RunLoopCheckpoint(
            round, messages, toolMessages, stepCount, [], 0, 0, ""));

    // ---- RF-003/004: retomada com liquidação de órfãs ----

    [Test]
    public async Task Resume_SnapshotComOrfa_RetomaEFechaCallSemResultado()
    {
        var chat = await CriarChatAsync();
        var toolId = await CriarToolAsync();
        var run = await SemearRunMortaAsync(chat.Id, toolId);

        // Round 2 morreu depois de despachar duas calls: c1 teve resultado,
        // c2 ficou órfã → retomada no round 3 com resultado sintético.
        var assistant = new ChatCompletionMessage("assistant", "",
            ToolCallsJson: "[{\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"eco\",\"arguments\":\"{}\"}},"
                + "{\"id\":\"c2\",\"type\":\"function\",\"function\":{\"name\":\"eco\",\"arguments\":\"{}\"}}]");
        await SemearEventosAsync(run.Id,
            (1, RunEventWriter.Types.Snapshot, SnapshotJson(2,
                [new ChatCompletionMessage("system", "sys"),
                 new ChatCompletionMessage("user", "oi")],
                [], 1)),
            (2, RunEventWriter.Types.StepFinished,
                JsonSerializer.Serialize(new { round = 2, calls = new[] { "c1", "c2" } }, JsonOptions)),
            (3, RunEventWriter.Types.MessagesAdded,
                JsonSerializer.Serialize(new { messages = new[] { assistant }, toolMessages = new[] { assistant } }, JsonOptions)),
            (4, RunEventWriter.Types.ToolCall,
                JsonSerializer.Serialize(new { id = "c1", name = "eco" }, JsonOptions)),
            (5, RunEventWriter.Types.ToolResult,
                JsonSerializer.Serialize(new { callId = "c1", output = "resultado-eco" }, JsonOptions)),
            (6, RunEventWriter.Types.ToolCall,
                JsonSerializer.Serialize(new { id = "c2", name = "eco" }, JsonOptions)));

        var dispatcher = _factory.Services.GetRequiredService<ChatRunDispatcher>();
        await dispatcher.SweepOrphansAsync(default);

        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"),
            $"run retomada não concluiu: {final.Status}");
        Assert.That(final.Meta?.TryGetProperty("resumedCount", out var rc) == true
            && rc.GetInt32() == 1, Is.True, "meta.resumedCount ausente");
        Assert.That(final.Meta?.TryGetProperty("lastSeq", out _), Is.True,
            "meta.lastSeq ausente");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mensagens = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatId == chat.Id)
            .OrderBy(m => m.Position)
            .ToListAsync();
        var toolMsgs = mensagens.Where(m => m.Role == "tool").ToList();
        Assert.That(toolMsgs.Any(m => m.ToolCallId == "c1" && m.Content == "resultado-eco"),
            Is.True, "tool result real de c1 não foi persistido");
        Assert.That(toolMsgs.Any(m => m.ToolCallId == "c2"
                && m.Content.Contains("\"interrupted\":true")
                && m.Content.Contains("server restarted")),
            Is.True, "c2 órfã não ganhou resultado sintético");
        Assert.That(mensagens.Any(m => m.Role == "assistant"
                && m.Content == "resposta pós-tool"), Is.True);
    }

    [Test]
    public async Task Resume_SemStepFinished_RefazRoundDoSnapshot()
    {
        var chat = await CriarChatAsync();
        var toolId = await CriarToolAsync();
        var run = await SemearRunMortaAsync(chat.Id, toolId);

        // Morte durante o completion do round 0: snapshot existe mas o
        // texto parcial é descartado — o loop refaz o round.
        await SemearEventosAsync(run.Id,
            (1, RunEventWriter.Types.Snapshot, SnapshotJson(0,
                [new ChatCompletionMessage("user", "oi")], [], 0)),
            (2, RunEventWriter.Types.StepStarted,
                JsonSerializer.Serialize(new { round = 1 }, JsonOptions)),
            (3, RunEventWriter.Types.ContentDelta,
                JsonSerializer.Serialize(new { delta = "texto parcial perdido" }, JsonOptions)));

        var dispatcher = _factory.Services.GetRequiredService<ChatRunDispatcher>();
        await dispatcher.SweepOrphansAsync(default);

        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"));
        Assert.That(final.Meta?.TryGetProperty("resumedCount", out var rc) == true
            && rc.GetInt32() == 1, Is.True);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var assistant = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatId == chat.Id && m.Role == "assistant")
            .ToListAsync();
        // Round refeito do zero: o mock responde com tool call de novo…
        // e o pós-tool fecha a run — o delta parcial nunca aparece.
        Assert.That(assistant.Any(m => m.Content.Contains("texto parcial perdido")),
            Is.False, "delta do round morto vazou para o resultado");
    }

    [Test]
    public async Task Resume_DoisRestarts_ResumedCountAcumula()
    {
        var chat = await CriarChatAsync();
        var toolId = await CriarToolAsync();
        var run = await SemearRunMortaAsync(chat.Id, toolId);
        await SemearEventosAsync(run.Id,
            (1, RunEventWriter.Types.Snapshot, SnapshotJson(0,
                [new ChatCompletionMessage("user", "oi")], [], 0)));

        var dispatcher = _factory.Services.GetRequiredService<ChatRunDispatcher>();
        await dispatcher.SweepOrphansAsync(default);
        var primeiro = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(primeiro.Status, Is.EqualTo("completed"));

        // Segundo restart: a run volta a running com dono morto — a trilha
        // de eventos da primeira execução segue válida para replay.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tracked = await db.ChatRuns.FirstAsync(r => r.Id == run.Id);
            tracked.Status = ChatRunStatus.Running;
            tracked.CompletedAt = null;
            tracked.OwnerInstance = "outra-instancia-morta";
            tracked.HeartbeatAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600;
            await db.SaveChangesAsync();
        }
        await dispatcher.SweepOrphansAsync(default);
        var segundo = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(segundo.Status, Is.EqualTo("completed"));
        Assert.That(segundo.Meta?.TryGetProperty("resumedCount", out var rc) == true
            && rc.GetInt32() == 2, Is.True,
            $"resumedCount deveria ser 2, meta={segundo.Meta}");
    }

    // ---- RF-003/005: sweep — legado, dono vivo, pausada, corrupto ----

    [Test]
    public async Task Sweep_RunningSemEventos_ViraInterrupted()
    {
        var chat = await CriarChatAsync();
        var run = await SemearRunMortaAsync(chat.Id, null);

        var dispatcher = _factory.Services.GetRequiredService<ChatRunDispatcher>();
        await dispatcher.SweepOrphansAsync(default);

        var atual = await CarregarRunAsync(run.Id);
        Assert.That(atual.Status, Is.EqualTo(ChatRunStatus.Interrupted));
        Assert.That(atual.Error, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Sweep_HeartbeatFrescoDeOutroDono_NaoRoubaRun()
    {
        var chat = await CriarChatAsync();
        var run = await SemearRunMortaAsync(
            chat.Id, null, owner: "instancia-viva", heartbeatAgeSeconds: 5);
        await SemearEventosAsync(run.Id,
            (1, RunEventWriter.Types.Snapshot, SnapshotJson(0,
                [new ChatCompletionMessage("user", "oi")], [], 0)));

        var dispatcher = _factory.Services.GetRequiredService<ChatRunDispatcher>();
        await dispatcher.SweepOrphansAsync(default);

        var atual = await CarregarRunAsync(run.Id);
        Assert.That(atual.Status, Is.EqualTo(ChatRunStatus.Running));
        Assert.That(atual.OwnerInstance, Is.EqualTo("instancia-viva"));
    }

    [Test]
    public async Task Sweep_Pausada_NaoToca()
    {
        var chat = await CriarChatAsync();
        var run = await SemearRunMortaAsync(
            chat.Id, null, status: ChatRunStatus.Paused);
        await SemearEventosAsync(run.Id,
            (1, RunEventWriter.Types.Snapshot, SnapshotJson(0,
                [new ChatCompletionMessage("user", "oi")], [], 0)));

        var dispatcher = _factory.Services.GetRequiredService<ChatRunDispatcher>();
        await dispatcher.SweepOrphansAsync(default);

        var atual = await CarregarRunAsync(run.Id);
        Assert.That(atual.Status, Is.EqualTo(ChatRunStatus.Paused));
    }

    [Test]
    public async Task Resume_SnapshotCorrompido_ViraInterrupted()
    {
        var chat = await CriarChatAsync();
        var run = await SemearRunMortaAsync(chat.Id, null);
        await SemearEventosAsync(run.Id,
            (1, RunEventWriter.Types.Snapshot, "{isto nao é json válido"));

        var dispatcher = _factory.Services.GetRequiredService<ChatRunDispatcher>();
        await dispatcher.SweepOrphansAsync(default);

        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("interrupted"));
        Assert.That(final.Error, Does.Contain("corrompido"));
    }

    // ---- RunRecoveryPlanner (unitário) ----

    [Test]
    public void Replay_SemSnapshot_RetornaNull()
    {
        var events = new List<ChatRunEvent>
        {
            new() { RunId = "r", Seq = 1, Type = RunEventWriter.Types.Phase,
                PayloadJson = "{}" },
        };
        Assert.That(RunRecoveryPlanner.Replay(events), Is.Null);
    }

    [Test]
    public void Replay_OrfaSemToolCall_DeduzDoStepFinished()
    {
        // A call foi registrada pelo step_finished mas nem despachou —
        // não é "executada", ainda assim ganha resultado sintético.
        var baseMsgs = new List<ChatCompletionMessage>
        {
            new("user", "oi"),
        };
        var events = new List<ChatRunEvent>
        {
            new() { RunId = "r", Seq = 1, Type = RunEventWriter.Types.Snapshot,
                PayloadJson = SnapshotJson(1, baseMsgs, [], 0) },
            new() { RunId = "r", Seq = 2, Type = RunEventWriter.Types.StepFinished,
                PayloadJson = JsonSerializer.Serialize(
                    new { round = 1, calls = new[] { "x" } }, JsonOptions) },
        };
        var plan = RunRecoveryPlanner.Replay(events)!;
        Assert.That(plan.Round, Is.EqualTo(2));
        Assert.That(plan.RecoveredTools, Is.EqualTo(1));
        Assert.That(plan.Messages.Last().ToolCallId, Is.EqualTo("x"));
        Assert.That(plan.Messages.Last().Content,
            Does.Contain("interrupted"));
    }

    [Test]
    public void Replay_RespostaFinalNaoPersistida_RetomaAposUltimoStep()
    {
        var events = new List<ChatRunEvent>
        {
            new() { RunId = "r", Seq = 1, Type = RunEventWriter.Types.Snapshot,
                PayloadJson = SnapshotJson(3, [new ChatCompletionMessage("user", "oi")], [], 3) },
            new() { RunId = "r", Seq = 2, Type = RunEventWriter.Types.StepFinished,
                PayloadJson = JsonSerializer.Serialize(
                    new { round = 3, calls = Array.Empty<string>() }, JsonOptions) },
        };
        var plan = RunRecoveryPlanner.Replay(events)!;
        Assert.That(plan.Round, Is.EqualTo(4));
        Assert.That(plan.RecoveredTools, Is.EqualTo(0));
    }
}
