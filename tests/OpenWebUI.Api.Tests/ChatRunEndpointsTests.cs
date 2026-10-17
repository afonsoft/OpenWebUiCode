using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura das rotas de runs desacopladas (SPEC-20261007-chat-detached-runs):
/// enqueue persiste a mensagem do usuário, isolamento por usuário, attach SSE
/// com replay e stop. Sem provider configurado a run vai a <c>failed</c> — o que
/// já prova que o dispatcher executou em background sem a conexão do cliente.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ChatRunEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-runs-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        // Sem provider real nos testes deste fixture: as runs usam o Ollama
        // default (localhost:11434) e falham por transporte. Uma tentativa só
        // mantém o comportamento de falha rápida — sem o backoff de ~35s da
        // política de resiliência (SPEC-20261016-provider-resilience).
        Environment.SetEnvironmentVariable("Providers__MaxAttempts", "1");
        Environment.SetEnvironmentVariable("Providers__MaxConsecutiveTimeouts", "1");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        // O primeiro usuário vira admin e habilita "user" como papel padrão —
        // sem isso os signups seguintes ficam pendentes e a API devolve 401.
        var admin = await SignUpAsync("Admin", "admin@runs.local");
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("Providers__MaxAttempts", null);
        Environment.SetEnvironmentVariable("Providers__MaxConsecutiveTimeouts", null);
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
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
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<ChatResponse> CriarChatAsync(List<ChatMessageModel> mensagens)
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Chat runs", ["llama3"], mensagens));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    private async Task<ChatRunResponse> EnfileirarAsync(
        string chatId, string? content, string model = "llama3")
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chatId}/messages",
            new EnqueueChatRunRequest(content, model));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChatRunResponse>())!;
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


    [Test]
    public async Task SubRun_Terminal_AnexaMarcadorNoChatPai()
    {
        // Run filha (ParentRunId) ao chegar em status terminal anexa uma
        // mensagem "Subtarefa concluída" no chat pai
        // (SPEC-20261010-subrun-parent-notify).
        var auth = await SignUpAsync("PN", "pn@runs.local");
        UseToken(auth.Token);
        var pai = await CriarChatAsync([]);
        var filho = await CriarChatAsync([]);

        string childRunId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var parentRun = new ChatRun
            {
                ChatId = pai.Id, UserId = auth.User.Id, Model = "llama3",
                RequestJson = "{}", Status = ChatRunStatus.Completed,
                CreatedAt = 1, CompletedAt = 2,
            };
            var childRun = new ChatRun
            {
                ChatId = filho.Id, UserId = auth.User.Id, Model = "llama3",
                RequestJson = "{}", ParentRunId = parentRun.Id, CreatedAt = 3,
            };
            db.ChatRuns.AddRange(parentRun, childRun);
            db.SaveChanges();
            childRunId = childRun.Id;
            scope.ServiceProvider.GetRequiredService<IChatRunDispatcher>()
                .Enqueue(childRunId);
        }

        // A run filha executa (request vazio → failed rápido) e a notificação
        // ao pai sai no término — polling até o marcador aparecer.
        var limite = DateTime.UtcNow.AddSeconds(60);
        ChatResponse? detalhe = null;
        while (DateTime.UtcNow < limite)
        {
            detalhe = await _client.GetFromJsonAsync<ChatResponse>(
                $"/api/v1/chats/{pai.Id}");
            if (detalhe!.Messages.Any(m =>
                    m.Content.Contains("Subtarefa concluída")))
            {
                break;
            }
            await Task.Delay(300);
        }
        Assert.That(detalhe!.Messages.Any(m =>
                m.Content.Contains("Subtarefa concluída")), Is.True,
            "chat pai sem marcador de sub-session concluída");
    }

    [Test]
    public async Task RunsConsole_ListaConsolidadaDeTodasAsRunsDoUsuario()
    {
        // GET /api/v1/chats/runs devolve as runs de TODOS os chats do
        // usuário numa visão só (SPEC-20261010-parallel-runs-console) —
        // título do chat + status, isolado por usuário.
        var auth = await SignUpAsync("RC", "rc@runs.local");
        UseToken(auth.Token);
        var c1 = await CriarChatAsync([]);
        var c2 = await CriarChatAsync([]);
        var r1 = await EnfileirarAsync(c1.Id, "a");
        var r2 = await EnfileirarAsync(c2.Id, "b");

        var runs = await _client.GetFromJsonAsync<List<ParallelRunResponse>>(
            "/api/v1/chats/runs");
        Assert.That(runs, Is.Not.Null);
        var mine = runs!.Where(r => r.Id == r1.Id || r.Id == r2.Id).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(mine, Has.Count.EqualTo(2), "faltou run na lista consolidada");
            Assert.That(mine.All(r => r.ChatTitle == "Chat runs"), Is.True);
            Assert.That(mine.Any(r => r.ChatId == c1.Id && r.Id == r1.Id), Is.True);
            Assert.That(mine.Any(r => r.ChatId == c2.Id && r.Id == r2.Id), Is.True);
        });

        // Isolamento: outro usuário não vê as runs do primeiro.
        var outro = await SignUpAsync("RC2", "rc2@runs.local");
        UseToken(outro.Token);
        var runsOutro = await _client.GetFromJsonAsync<List<ParallelRunResponse>>(
            "/api/v1/chats/runs");
        Assert.That(runsOutro!.Any(r => r.Id == r1.Id || r.Id == r2.Id), Is.False);
    }

    [Test]
    public async Task Enqueue_PersisteMensagemDoUsuarioEDespachaRun()
    {
        var auth = await SignUpAsync("RunA", "runa@runs.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync([]);

        var run = await EnfileirarAsync(chat.Id, "olá");

        Assert.Multiple(() =>
        {
            Assert.That(run.ChatId, Is.EqualTo(chat.Id));
            Assert.That(run.Status, Is.AnyOf("queued", "running", "failed"));
        });

        // A mensagem do usuário foi persistida no servidor no enqueue.
        var detalhe = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{chat.Id}");
        Assert.That(detalhe!.Messages.Select(m => (m.Role, m.Content)),
            Contains.Item(("user", "olá")));

        // Sem provider configurado o dispatcher leva a run a failed — prova que
        // a execução aconteceu em background, independente da conexão do cliente.
        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("failed"));
        Assert.That(final.Error, Is.Not.Null.And.Not.Empty);
        Assert.That(final.CompletedAt, Is.Not.Null);
    }

    [Test]
    public async Task Enqueue_SemContent_ExigeHistoricoTerminandoEmUsuario()
    {
        var auth = await SignUpAsync("RunB", "runb@runs.local");
        UseToken(auth.Token);

        // Histórico termina em assistant → regeneração rejeitada.
        var chat = await CriarChatAsync([
            new ChatMessageModel("m1", "user", "pergunta", null, 100),
            new ChatMessageModel("m2", "assistant", "resposta", "llama3", 101),
        ]);
        var bad = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/messages", new EnqueueChatRunRequest(null, "llama3"));
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Histórico terminando em user → aceita e enfileira.
        var chatOk = await CriarChatAsync([
            new ChatMessageModel("m1", "user", "pergunta", null, 100),
        ]);
        var ok = await EnfileirarAsync(chatOk.Id, null);
        Assert.That(ok.Status, Is.AnyOf("queued", "running", "failed"));
        await AguardarFinalAsync(chatOk.Id, ok.Id);
    }

    [Test]
    public async Task Runs_IsolamentoPorUsuario()
    {
        var dono = await SignUpAsync("RunC", "runc@runs.local");
        UseToken(dono.Token);
        var chat = await CriarChatAsync([]);
        var run = await EnfileirarAsync(chat.Id, "segredo");

        var outro = await SignUpAsync("RunD", "rund@runs.local");
        UseToken(outro.Token);

        var get = await _client.GetAsync($"/api/v1/chats/{chat.Id}/runs/{run.Id}");
        var list = await _client.GetFromJsonAsync<List<ChatRunResponse>>(
            $"/api/v1/chats/{chat.Id}/runs");
        var stream = await _client.GetAsync($"/api/v1/chats/{chat.Id}/runs/{run.Id}/stream");
        var stop = await _client.PostAsync($"/api/v1/chats/{chat.Id}/runs/{run.Id}/stop", null);

        Assert.Multiple(() =>
        {
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(list, Is.Empty);
            Assert.That(stream.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(stop.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task Stop_RunEnfileiradaOuRodando_Finaliza()
    {
        var auth = await SignUpAsync("RunE", "rune@runs.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync([]);
        var run = await EnfileirarAsync(chat.Id, "stop me");

        var stop = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/stop", null);

        // Se a run ainda estava queued/running → 200; se já falhou (sem provider) → 409.
        Assert.That(stop.StatusCode,
            Is.AnyOf(HttpStatusCode.OK, HttpStatusCode.Conflict));

        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.AnyOf("stopped", "failed"));

        // Run já finalizada → stop devolve 409.
        var again = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/stop", null);
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task Stream_RunFinalizada_RetornaEFecha()
    {
        var auth = await SignUpAsync("RunF", "runf@runs.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync([]);
        var run = await EnfileirarAsync(chat.Id, "conteúdo");
        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("failed"));

        // Attach a run finalizada: o backlog (erro + DONE) sai e o stream fecha.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/chats/{chat.Id}/runs/{run.Id}/stream");
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType,
            Is.EqualTo("text/event-stream"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("id: "));
            Assert.That(body, Does.Contain("data: [DONE]"));
        });
    }

    [Test]
    public async Task RunsActive_SemRunEmAndamento_RetornaVazio()
    {
        var auth = await SignUpAsync("RunG", "rung@runs.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync([]);

        var response = await _client.GetAsync($"/api/v1/chats/{chat.Id}/runs/active");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty.Or.EqualTo("null"));
    }

    [Test]
    public async Task Steer_RunViva_PersisteInboxDuravel()
    {
        // SPEC-20261010-steer-queue: POST em run viva grava linha pending.
        // Run seeded direto (running) — enfileirar de verdade tornaria o teste
        // flaky: o provider mock pode finalizar a run antes do POST chegar.
        var auth = await SignUpAsync("SteerA", "steera@runs.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync(
            [new ChatMessageModel("m1", "user", "oi", null, 100)]);

        var runId = Guid.NewGuid().ToString();
        using (var seed = _factory.Services.CreateScope())
        {
            var seedDb = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            seedDb.ChatRuns.Add(new ChatRun
            {
                Id = runId, ChatId = chat.Id, UserId = auth.User.Id,
                Model = "llama3", Status = ChatRunStatus.Running,
                RequestJson = "{}",
            });
            await seedDb.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/runs/{runId}/steer",
            new SteerRunRequest("também faz X", "steer"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await response.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ChatRunSteers
            .Where(s => s.RunId == runId)
            .SingleOrDefaultAsync();
        Assert.That(row, Is.Not.Null);
        Assert.That(row!.Mode, Is.EqualTo("steer"));
        Assert.That(row.Content, Is.EqualTo("também faz X"));
    }

    [Test]
    public async Task Steer_RunFinalizada_Conflict()
    {
        var auth = await SignUpAsync("SteerB", "steerb@runs.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync(
            [new ChatMessageModel("m1", "user", "oi", null, 100)]);
        var run = await EnfileirarAsync(chat.Id, null);
        await AguardarFinalAsync(chat.Id, run.Id);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/steer",
            new SteerRunRequest("tarde demais"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task Steer_MensagemVazia_BadRequest()
    {
        var auth = await SignUpAsync("SteerC", "steerc@runs.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync(
            [new ChatMessageModel("m1", "user", "oi", null, 100)]);
        var run = await EnfileirarAsync(chat.Id, null);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/steer",
            new SteerRunRequest("   "));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Steer_RunDeOutroUsuario_NotFound()
    {
        var dono = await SignUpAsync("SteerD1", "steerd1@runs.local");
        UseToken(dono.Token);
        var chat = await CriarChatAsync(
            [new ChatMessageModel("m1", "user", "oi", null, 100)]);
        var run = await EnfileirarAsync(chat.Id, null);

        var outro = await SignUpAsync("SteerD2", "steerd2@runs.local");
        UseToken(outro.Token);
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/steer",
            new SteerRunRequest("alheio"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    /// <summary>Seed de run num status arbitrário sem passar pelo dispatcher.</summary>
    private async Task<string> SeedRunAsync(
        string chatId, string userId, string status)
    {
        var runId = $"run-{Guid.NewGuid():N}";
        using var seed = _factory.Services.CreateScope();
        var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ChatRuns.Add(new ChatRun
        {
            Id = runId, ChatId = chatId, UserId = userId,
            Model = "llama3", Status = status, RequestJson = "{}",
            CreatedAt = 1,
            StartedAt = status == ChatRunStatus.Queued ? null : 2,
        });
        await db.SaveChangesAsync();
        return runId;
    }

    [Test]
    public async Task PauseResume_EstadosEIsolamento()
    {
        var auth = await SignUpAsync("PR1", "pr1@runs.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync(
            [new ChatMessageModel("m1", "user", "oi", null, 100)]);

        // Run inexistente → 404 em ambos.
        var pause404 = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/nope/pause", null);
        var resume404 = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/nope/resume", null);
        Assert.Multiple(() =>
        {
            Assert.That(pause404.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(resume404.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });

        // Run finalizada → pausar/resumir conflitam.
        var done = await SeedRunAsync(chat.Id, auth.User.Id, ChatRunStatus.Completed);
        var pauseDone = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/{done}/pause", null);
        var resumeDone = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/{done}/resume", null);
        Assert.Multiple(() =>
        {
            Assert.That(pauseDone.StatusCode,
                Is.AnyOf(HttpStatusCode.Conflict, HttpStatusCode.BadRequest));
            Assert.That(resumeDone.StatusCode,
                Is.AnyOf(HttpStatusCode.Conflict, HttpStatusCode.BadRequest));
        });

        // Run queued → pause 200; paused (nunca iniciou) → resume volta a queued.
        var queued = await SeedRunAsync(chat.Id, auth.User.Id, ChatRunStatus.Queued);
        var pause = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/{queued}/pause", null);
        Assert.That(pause.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await pause.Content.ReadAsStringAsync());
        var pausedBody = await pause.Content
            .ReadFromJsonAsync<ChatRunResponse>();
        Assert.That(pausedBody!.Status, Is.EqualTo("paused"));

        // Resume de run que nunca iniciou → volta a queued (re-enfileira;
        // o dispatcher falha a run "{}" rápido — sem provider).
        var resume = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/{queued}/resume", null);
        Assert.That(resume.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await resume.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task DecideApproval_ValidacoesEStatus()
    {
        var auth = await SignUpAsync("DA1", "da1@runs.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync(
            [new ChatMessageModel("m1", "user", "oi", null, 100)]);
        var runId = await SeedRunAsync(chat.Id, auth.User.Id, ChatRunStatus.Running);

        // Decision inválida → 400.
        var badDecision = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/runs/{runId}/approvals/c1",
            new { decision = "talvez" });
        Assert.That(badDecision.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Mensagem acima de 2KB → 400.
        var tooLong = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/runs/{runId}/approvals/c1",
            new { decision = "deny", message = new string('x', 2100) });
        Assert.That(tooLong.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // Run inexistente → 404 (decision válida para passar do primeiro gate).
        var notFound = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/runs/nope/approvals/c1",
            new { decision = "approve" });
        Assert.That(notFound.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Run finalizada → 410 Gone.
        var done = await SeedRunAsync(chat.Id, auth.User.Id, ChatRunStatus.Failed);
        var gone = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/runs/{done}/approvals/c1",
            new { decision = "approve" });
        Assert.That((int)gone.StatusCode, Is.EqualTo(410));

        // Chat de outro usuário → 404 mesmo com run viva.
        var outro = await SignUpAsync("DA2", "da2@runs.local");
        UseToken(outro.Token);
        var alien = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/runs/{runId}/approvals/c1",
            new { decision = "approve" });
        Assert.That(alien.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task RunDiff_RunInexistente_NotFound()
    {
        var auth = await SignUpAsync("RD1", "rd1@runs.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync([]);

        var diff = await _client.GetAsync(
            $"/api/v1/chats/{chat.Id}/runs/nope/diff");
        Assert.That(diff.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Run sem worktree vinculado → resposta de erro/vazio, nunca 500.
        var runId = await SeedRunAsync(chat.Id, auth.User.Id, ChatRunStatus.Running);
        var noTree = await _client.GetAsync(
            $"/api/v1/chats/{chat.Id}/runs/{runId}/diff");
        Assert.That((int)noTree.StatusCode, Is.LessThan(500));
    }
}
