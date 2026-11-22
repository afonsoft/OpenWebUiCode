using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenWebUI.Api.DevPipeline;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// E2E do dev_pipeline/run_workflow com provider programável
/// (SPEC-20261122-programmable-provider): runs reais do ChatRunExecutor
/// dentro do WebApplicationFactory, respondidas pelo
/// <see cref="ScriptedLlmMock"/> — exercita RunStepsAsync, EnqueuePhaseRunAsync,
/// WaitRunAsync, ParsePhaseResultAsync, GateStepAsync/AnswerGateAsync e
/// MarkFailedAsync ponta-a-ponta.
/// </summary>
[TestFixture]
public sealed class DevPipelineE2ETests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private ScriptedLlmMock _mock = null!;
    private string _dbPath = null!;
    private string _userId = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-dpe2e-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _mock = new ScriptedLlmMock();

        var admin = await SignUpAsync("Admin", "admin@dpe2e.local");
        _userId = admin.User.Id;
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());

        var connResponse = await _client.PostAsJsonAsync(
            "/api/v1/configs/connections", new ConnectionsConfig([_mock.BaseUrl], [], []));
        Assert.That(connResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mock.Dispose();
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new SignUpRequest(name, email, "senha-forte-123"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    /// <summary>Chat em modo build + preset (sem binding — steps llm
    /// não precisam de workdir git).</summary>
    private async Task<ChatResponse> CriarChatBuildAsync(
        string preset = "always-allow")
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Chat e2e", ["fake:1"], []));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
        var patched = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}", new ChatPatchRequest(preset, Mode: "build"));
        Assert.That(patched.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await patched.Content.ReadAsStringAsync());
        return (await patched.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    /// <summary>Run pai real completada — dá o RequestJson (model fake:1)
    /// que a pipeline herda nas runs de fase.</summary>
    private async Task<ChatRun> EnfileirarPaiAsync(string chatId)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chatId}/messages",
            new EnqueueChatRunRequest("msg inicial", "fake:1", ToolIds: []));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await response.Content.ReadAsStringAsync());
        var run = (await response.Content.ReadFromJsonAsync<ChatRunResponse>())!;
        var limite = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < limite)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var atual = await db.ChatRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == run.Id);
            if (atual?.Status is ChatRunStatus.Completed or ChatRunStatus.Failed
                or ChatRunStatus.Stopped or ChatRunStatus.Interrupted)
            {
                return atual;
            }
            await Task.Delay(150);
        }
        Assert.Fail("Run pai não terminou em 60s.");
        return null!;
    }

    private async Task<DevPipelineService.State> IniciarAsync(
        ChatResponse chat, string parentRunId,
        List<DevPipelineService.WorkflowStep> steps)
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider
            .GetRequiredService<DevPipelineService>();
        var ctx = new BuiltinToolContext(
            _userId, chat.Id, parentRunId, "/tmp/workdir", "/tmp/uploads",
            "fake:1");
        return await svc.StartAsync(
            ctx, "tarefa e2e", null, push: false, maxFixAttempts: 0,
            ct: CancellationToken.None, enablePlan: false,
            enableFanout: false, steps: steps);
    }

    /// <summary>Dirige a pipeline com teto — devolve o estado final.</summary>
    private async Task<DevPipelineService.State> DirigirAsync(
        string pipelineId, int segundos = 90)
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider
            .GetRequiredService<DevPipelineService>();
        var drive = svc.RunAsync(pipelineId);
        var teto = Task.Delay(TimeSpan.FromSeconds(segundos));
        if (await Task.WhenAny(drive, teto) == teto)
        {
            Assert.Fail($"pipeline {pipelineId} não estabilizou em {segundos}s");
        }
        return (await svc.GetAsync(pipelineId, CancellationToken.None))!;
    }

    // ---- e2e: steps llm encadeados com interpolação de resultados ----

    [Test]
    public async Task Workflow_LlmSteps_CompletamEInterpolam()
    {
        _mock.OnContains("faz a primeira parte", _mock.Text(
            "implementado <pipeline_result>{\"files\":[\"a.cs\"],"
                + "\"summary\":\"impl ok\",\"verdict\":\"pass\"}"
                + "</pipeline_result>"));
        _mock.OnContains("revise usando", _mock.Text(
            "revisto <pipeline_result>{\"files\":[\"b.cs\"],"
                + "\"summary\":\"rev ok\",\"verdict\":\"pass\"}"
                + "</pipeline_result>"));

        var chat = await CriarChatBuildAsync();
        var pai = await EnfileirarPaiAsync(chat.Id);
        var state = await IniciarAsync(chat, pai.Id,
            [
                new() { Kind = "llm", Key = "s1", Prompt = "faz a primeira parte" },
                new() { Kind = "llm", Key = "s2", Prompt = "revise usando {results:s1}" },
            ]);

        var final = await DirigirAsync(state.Id);

        Assert.Multiple(() =>
        {
            Assert.That(final.Status, Is.EqualTo("done"), final.Error);
            Assert.That(final.PhaseResults.ContainsKey("s1"), Is.True);
            Assert.That(final.PhaseResults.ContainsKey("s2"), Is.True);
            Assert.That(final.PhaseResults["s1"].Verdict, Is.EqualTo("pass"));
            Assert.That(final.PhaseResults["s1"].Files,
                Is.EqualTo(new[] { "a.cs" }));
            Assert.That(final.Runs, Has.Count.EqualTo(2));
        });
        // {results:s1} interpola o Verdict da fase — prova real de que a
        // saída de s1 alimentou o prompt de s2.
        Assert.That(_mock.Bodies.Any(b => b.Contains(
            "revise usando pass", StringComparison.Ordinal)), Is.True,
            "interpolação {results:s1} não chegou ao provider em s2");
    }

    // ---- e2e: gate humano — estaciona, resposta retoma ----

    [Test]
    public async Task Workflow_Gate_AnswerCompleta()
    {
        _mock.OnContains("fecha tudo", _mock.Text(
            "fechado <pipeline_result>{\"files\":[],"
                + "\"summary\":\"done\",\"verdict\":\"pass\"}"
                + "</pipeline_result>"));

        var chat = await CriarChatBuildAsync();
        var pai = await EnfileirarPaiAsync(chat.Id);
        var state = await IniciarAsync(chat, pai.Id,
            [
                new() { Kind = "gate", Key = "g1", Prompt = "posso seguir?" },
                new() { Kind = "llm", Key = "s2", Prompt = "fecha tudo" },
            ]);

        var estacionado = await DirigirAsync(state.Id, segundos: 30);
        Assert.Multiple(() =>
        {
            Assert.That(estacionado.AwaitingGate, Is.EqualTo("g1"));
            Assert.That(estacionado.Status, Is.EqualTo("running"));
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider
                .GetRequiredService<DevPipelineService>();
            var key = await svc.AnswerGateAsync(
                state.Id, "approved", "segue", CancellationToken.None);
            Assert.That(key, Is.EqualTo("g1"));
        }

        var final = await DirigirAsync(state.Id);
        Assert.Multiple(() =>
        {
            Assert.That(final.Status, Is.EqualTo("done"), final.Error);
            Assert.That(final.PhaseResults["g1"].Verdict, Is.EqualTo("pass"));
            Assert.That(final.PhaseResults["g1"].Summary,
                Does.Contain("segue"));
            Assert.That(final.PhaseResults.ContainsKey("s2"), Is.True);
        });
    }

    // ---- e2e negativo: run de fase falha → pipeline marca failed ----

    [Test]
    public async Task Workflow_RunFalha_MarcaFailed()
    {
        _mock.OnContains("fase que explode",
            ScriptedLlmMock.Fail(500, "{\"error\":{\"message\":\"boom\"}}"));

        var chat = await CriarChatBuildAsync();
        var pai = await EnfileirarPaiAsync(chat.Id);
        var state = await IniciarAsync(chat, pai.Id,
            [
                new() { Kind = "llm", Key = "s1", Prompt = "fase que explode" },
            ]);

        var final = await DirigirAsync(state.Id);
        Assert.That(final.Status, Is.EqualTo("failed"));
        Assert.That(final.Error, Is.Not.Null.And.Not.Empty);
    }
}
