using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenWebUI.Api.DevPipeline;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261121-dev-pipeline: detecção de stack do
/// <see cref="ProjectValidation"/>, execução do
/// <c>builtin:agent_validate</c>, validação de args do
/// <c>builtin:dev_pipeline</c>, helpers git do <see cref="WorktreeService"/>
/// e anúncio por modo do <see cref="PermissionRuleset"/>.
/// </summary>
[TestFixture]
public class DevPipelineUnitTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Join(Path.GetTempPath(), $"owui-dp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_dir)) { Directory.Delete(_dir, true); } }
        catch { /* processo/git pode segurar handle — ignore */ }
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static BuiltinToolContext Ctx(string workdir) =>
        new("u1", null, null, workdir, workdir);

    private string NewGitRepo()
    {
        var repo = Path.Join(_dir, $"repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        Run(repo, "init");
        Run(repo, "config", "user.email", "t@t.local");
        Run(repo, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(repo, "f.txt"), "x");
        Run(repo, "add", ".");
        Run(repo, "commit", "-m", "init");
        return repo;
    }

    private static void Run(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = cwd,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(30_000);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {args[0]} falhou: {p.StandardError.ReadToEnd()}");
        }
    }

    // ---------- ProjectValidation.Detect ----------

    [Test]
    public void Detect_Csproj_RetornaDotnetTest()
    {
        File.WriteAllText(Path.Combine(_dir, "App.csproj"), "<Project/>");
        Assert.That(ProjectValidation.Detect(_dir),
            Is.EqualTo("dotnet test --nologo"));
    }

    [Test]
    public void Detect_PackageJson_RetornaNpmTest()
    {
        File.WriteAllText(Path.Combine(_dir, "package.json"), "{}");
        Assert.That(ProjectValidation.Detect(_dir), Is.EqualTo("npm test"));
    }

    [Test]
    public void Detect_Pyproject_RetornaPytest()
    {
        File.WriteAllText(Path.Combine(_dir, "pyproject.toml"), "[project]");
        Assert.That(ProjectValidation.Detect(_dir), Is.EqualTo("pytest -q"));
    }

    [Test]
    public void Detect_GoMod_RetornaGoTest()
    {
        File.WriteAllText(Path.Combine(_dir, "go.mod"), "module x");
        Assert.That(ProjectValidation.Detect(_dir),
            Is.EqualTo("go test ./..."));
    }

    [Test]
    public void Detect_CargoToml_RetornaCargoTest()
    {
        File.WriteAllText(Path.Combine(_dir, "Cargo.toml"), "[package]");
        Assert.That(ProjectValidation.Detect(_dir), Is.EqualTo("cargo test"));
    }

    [Test]
    public void Detect_VazioOuInexistente_RetornaNull()
    {
        Assert.That(ProjectValidation.Detect(_dir), Is.Null);
        Assert.That(ProjectValidation.Detect(
            Path.Combine(_dir, "nao-existe")), Is.Null);
    }

    // ---------- ProjectValidation.RunAsync ----------

    [Test]
    public async Task RunAsync_ComandoOk_PassaSemRepoGit()
    {
        var result = await ProjectValidation.RunAsync(
            _dir, "echo ok", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.That(result.Passed, Is.True);
        Assert.That(result.Outcome.ExitCode, Is.EqualTo(0));
        Assert.That(result.GitPorcelain, Is.Null,
            "fora de repo git, porcelain é null");
        Assert.That(result.Uncommitted, Is.EqualTo(0));
    }

    [Test]
    public async Task RunAsync_EmRepo_ExpoePorcelain()
    {
        var repo = NewGitRepo();
        File.WriteAllText(Path.Combine(repo, "novo.txt"), "y");
        var result = await ProjectValidation.RunAsync(
            repo, "echo ok", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.That(result.Passed, Is.True);
        Assert.That(result.GitPorcelain, Does.Contain("novo.txt"));
        Assert.That(result.Uncommitted, Is.GreaterThanOrEqualTo(1));
    }

    // ---------- agent_validate ----------

    [Test]
    public async Task AgentValidate_SemStack_PedeComandoExplicito()
    {
        var tool = new AgentValidateBuiltinTool();
        var result = await tool.ExecuteAsync(
            Args("{}"), Ctx(_dir), CancellationToken.None);
        Assert.That(result.Text, Does.Contain("Nenhum stack reconhecido"));
        Assert.That(result.Refused, Is.False);
    }

    [Test]
    public async Task AgentValidate_ComandoExplicito_Exit0Validado()
    {
        var tool = new AgentValidateBuiltinTool();
        var result = await tool.ExecuteAsync(
            Args("""{"command":"echo ok"}"""), Ctx(_dir),
            CancellationToken.None);
        Assert.That(result.Text, Does.Contain("passed"));
        Assert.That(result.Text, Does.Contain("exit 0"));
    }

    [Test]
    public async Task AgentValidate_ComandoFalho_ReportaFailed()
    {
        var tool = new AgentValidateBuiltinTool();
        var result = await tool.ExecuteAsync(
            Args("""{"command":"ls /definitivamente-ausente-xyz"}"""),
            Ctx(_dir), CancellationToken.None);
        Assert.That(result.Text, Does.Contain("FAILED"));
        Assert.That(result.Refused, Is.False);
    }

    [Test]
    public async Task AgentValidate_ComandoProibido_Recusa()
    {
        var tool = new AgentValidateBuiltinTool();
        var result = await tool.ExecuteAsync(
            Args("""{"command":"rm -rf /"}"""), Ctx(_dir),
            CancellationToken.None);
        Assert.That(result.Refused, Is.True);
        Assert.That(result.Text, Does.Contain("negado"));
    }

    [Test]
    public async Task AgentValidate_Timeout_ReportaTimedOut()
    {
        var tool = new AgentValidateBuiltinTool();
        var result = await tool.ExecuteAsync(
            Args("""{"command":"sleep 5","timeout_seconds":1}"""),
            Ctx(_dir), CancellationToken.None);
        Assert.That(result.Text, Does.Contain("timeout"));
    }

    // ---------- dev_pipeline (validação de args — sem escopo real) ----------

    private static DevPipelineBuiltinTool NewPipelineTool() =>
        new(new ServiceCollection().BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>());

    [Test]
    public async Task DevPipeline_OpInvalido_Erro()
    {
        var result = await NewPipelineTool().ExecuteAsync(
            Args("""{"op":"bogus"}"""), Ctx(_dir), CancellationToken.None);
        Assert.That(result.Text, Does.Contain("inválido"));
    }

    [Test]
    public async Task DevPipeline_StartSemTask_Erro()
    {
        var result = await NewPipelineTool().ExecuteAsync(
            Args("""{"op":"start"}"""), Ctx(_dir), CancellationToken.None);
        Assert.That(result.Text, Does.Contain("'task' é obrigatório"));
    }

    [Test]
    public async Task DevPipeline_StatusSemPipelineId_Erro()
    {
        var result = await NewPipelineTool().ExecuteAsync(
            Args("""{"op":"status"}"""), Ctx(_dir), CancellationToken.None);
        Assert.That(result.Text, Does.Contain("'pipeline_id' é obrigatório"));
    }

    [Test]
    public async Task DevPipeline_ResumeSemPipelineId_Erro()
    {
        var result = await NewPipelineTool().ExecuteAsync(
            Args("""{"op":"resume"}"""), Ctx(_dir), CancellationToken.None);
        Assert.That(result.Text, Does.Contain("'pipeline_id' é obrigatório"));
    }

    // ---------- ConventionalSubject ----------

    [Test]
    public void ConventionalSubject_PrimeiraLinhaComFeat()
    {
        Assert.That(DevPipelineService.ConventionalSubject("adiciona proxy"),
            Is.EqualTo("feat: adiciona proxy"));
    }

    [Test]
    public void ConventionalSubject_MultiLinha_PegaPrimeira()
    {
        Assert.That(DevPipelineService.ConventionalSubject(
                "tarefa A\ndetalhe extra"),
            Is.EqualTo("feat: tarefa A"));
    }

    [Test]
    public void ConventionalSubject_Longa_Trunca72()
    {
        var subject = DevPipelineService.ConventionalSubject(new string('x', 200));
        Assert.That(subject.Length, Is.EqualTo(72));
        Assert.That(subject, Does.StartWith("feat: "));
    }

    [Test]
    public void ConventionalSubject_Vazia_Fallback()
    {
        Assert.That(DevPipelineService.ConventionalSubject("  \n "),
            Is.EqualTo("feat: pipeline changes"));
    }

    // ---------- WorktreeService git helpers ----------

    [Test]
    public async Task GitOutputAsync_RevParse_RetornaSha()
    {
        var repo = NewGitRepo();
        var sha = await WorktreeService.GitOutputAsync(
            repo, CancellationToken.None, "rev-parse", "--short", "HEAD");
        Assert.That(sha?.Trim(), Is.Not.Null.And.Length.GreaterThanOrEqualTo(7));
    }

    [Test]
    public async Task GitErrorAsync_SubcomandoRuim_RetornaErro()
    {
        var repo = NewGitRepo();
        var error = await WorktreeService.GitErrorAsync(
            repo, CancellationToken.None, "subcomando-inexistente");
        Assert.That(error, Is.Not.Null);
    }

    // ---------- ParsePhaseResult / LlmPhases (RF-006/010) ----------

    private static DevPipelineService.State NewState(bool enablePlan = true) =>
        new()
        {
            Id = "dp-test", UserId = "u1", ParentChatId = "c1",
            ParentRunId = "r1", Task = "tarefa",
            EnablePlan = enablePlan, CreatedAt = 1, UpdatedAt = 1,
        };

    [Test]
    public void LlmPhases_ComPlan_PlanPrimeiro()
    {
        var phases = DevPipelineService.LlmPhases(NewState(enablePlan: true))
            .Select(p => p.Phase).ToList();
        Assert.That(phases, Is.EqualTo(new[] { "plan", "implement", "test" }));
    }

    [Test]
    public void LlmPhases_SemPlan_ComecaNoImplement()
    {
        var phases = DevPipelineService.LlmPhases(NewState(enablePlan: false))
            .Select(p => p.Phase).ToList();
        Assert.That(phases, Is.EqualTo(new[] { "implement", "test" }));
    }

    [Test]
    public void LlmPhases_Implement_CarregaPlanoNoPrompt()
    {
        var state = NewState();
        state.Plan = "1. arquivo a.cs; 2. teste";
        var prompt = DevPipelineService.LlmPhases(state)
            .First(p => p.Phase == "implement").Prompt;
        Assert.That(prompt, Does.Contain("Plano aprovado"));
        Assert.That(prompt, Does.Contain("arquivo a.cs"));
    }

    [Test]
    public void ParsePhaseResult_BlocoValido_ExtraiCampos()
    {
        var text = "trabalho feito.\n<pipeline_result>"
            + """{"files":["a.cs","b.cs"],"summary":"ok","verdict":"issues","issues":["x","y"],"plan":"1. passo"}"""
            + "</pipeline_result>";
        var r = DevPipelineService.ParsePhaseResult(text);
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Files, Is.EqualTo(new[] { "a.cs", "b.cs" }));
        Assert.That(r.Verdict, Is.EqualTo("issues"));
        Assert.That(r.Issues, Is.EqualTo(new[] { "x", "y" }));
        Assert.That(r.Plan, Is.EqualTo("1. passo"));
        Assert.That(r.Summary, Is.EqualTo("ok"));
    }

    [Test]
    public void ParsePhaseResult_SemBlocoOuMalformado_Null()
    {
        Assert.That(DevPipelineService.ParsePhaseResult(null), Is.Null);
        Assert.That(DevPipelineService.ParsePhaseResult("sem bloco"), Is.Null);
        Assert.That(DevPipelineService.ParsePhaseResult(
            "<pipeline_result>{invalido}</pipeline_result>"), Is.Null);
    }

    // ---------- TryModeSlash (RF-004) ----------

    [Test]
    public void TryModeSlash_ModosReconhecidos()
    {
        Assert.Multiple(() =>
        {
            Assert.That(OpenWebUI.Api.Endpoints.ChatRunEndpoints
                .TryModeSlash("/plan faça o plano")!.Value.Target,
                Is.EqualTo("plan"));
            Assert.That(OpenWebUI.Api.Endpoints.ChatRunEndpoints
                .TryModeSlash("/build implemente")!.Value.Target,
                Is.EqualTo("build"));
            Assert.That(OpenWebUI.Api.Endpoints.ChatRunEndpoints
                .TryModeSlash("/code implemente")!.Value.Target,
                Is.EqualTo("build"), "/code é alias de build");
            Assert.That(OpenWebUI.Api.Endpoints.ChatRunEndpoints
                .TryModeSlash("/chat conversa")!.Value.Target,
                Is.EqualTo("chat"));
        });
    }

    [Test]
    public void TryModeSlash_RestanteViraConteudo_VazioViraDiretiva()
    {
        var comArgs = OpenWebUI.Api.Endpoints.ChatRunEndpoints
            .TryModeSlash("/build   implemente x  ")!.Value;
        Assert.That(comArgs.Remainder, Is.EqualTo("implemente x"));

        var semArgs = OpenWebUI.Api.Endpoints.ChatRunEndpoints
            .TryModeSlash("/plan")!.Value;
        Assert.That(semArgs.Remainder, Does.Contain("modo plan"));
    }

    [Test]
    public void TryModeSlash_NaoComandoOuDesconhecido_Null()
    {
        Assert.Multiple(() =>
        {
            Assert.That(OpenWebUI.Api.Endpoints.ChatRunEndpoints
                .TryModeSlash(null), Is.Null);
            Assert.That(OpenWebUI.Api.Endpoints.ChatRunEndpoints
                .TryModeSlash("mensagem comum"), Is.Null);
            Assert.That(OpenWebUI.Api.Endpoints.ChatRunEndpoints
                .TryModeSlash("/planx não é modo"), Is.Null);
            Assert.That(OpenWebUI.Api.Endpoints.ChatRunEndpoints
                .TryModeSlash("/deploy prod"), Is.Null,
                "comando de repo não é interceptado pelo slash de modo");
        });
    }

    // ---------- PermissionRuleset.ShouldAnnounce ----------

    [Test]
    public void ShouldAnnounce_DevTools_NoChatNaoAparecem()
    {
        var validate = new Tool { Url = "builtin://agent_validate" };
        var pipeline = new Tool { Url = "builtin://dev_pipeline" };
        Assert.That(PermissionRuleset.ShouldAnnounce("chat", validate), Is.False);
        Assert.That(PermissionRuleset.ShouldAnnounce("chat", pipeline), Is.False);
    }

    [Test]
    public void ShouldAnnounce_DevTools_NoBuildAparecem()
    {
        var validate = new Tool { Url = "builtin://agent_validate" };
        var pipeline = new Tool { Url = "builtin://dev_pipeline" };
        Assert.That(PermissionRuleset.ShouldAnnounce("build", validate), Is.True);
        Assert.That(PermissionRuleset.ShouldAnnounce("build", pipeline), Is.True);
    }

    [Test]
    public void ShouldAnnounce_Plan_AnunciaValidateMasNaoPipeline()
    {
        var validate = new Tool { Url = "builtin://agent_validate" };
        var pipeline = new Tool { Url = "builtin://dev_pipeline" };
        // plan pode validar para análise (gated pelo preset) mas não
        // dispara pipeline mutante — Deny → fora do spec anunciado.
        Assert.That(PermissionRuleset.ShouldAnnounce("plan", validate), Is.True);
        Assert.That(PermissionRuleset.ShouldAnnounce("plan", pipeline), Is.False);
    }
}

/// <summary>
/// SPEC-20261121-dev-pipeline: <see cref="DevPipelineService"/> end-to-end
/// com a factory real — <c>StartAsync</c> persiste kv + chat filho build;
/// <c>RunAsync</c> sem provider falha na fase implement e reporta no chat
/// pai; <c>status</c>/<c>resume</c> da tool refletem o kv.
/// </summary>
[TestFixture, IsolateEnvironment]
public class DevPipelineIntegrationTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-dp-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Default", $"Data Source={_dbPath}");
        // Sem provider real: runs falham por transporte rápido
        // (MaxAttempts=1 — mesmo padrão do fixture de runs).
        Environment.SetEnvironmentVariable("Providers__MaxAttempts", "1");
        Environment.SetEnvironmentVariable(
            "Providers__MaxConsecutiveTimeouts", "1");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        var admin = await SignUpAsync("DPAdmin", "dpadmin@pipeline.local");
        Assert.That(admin.Token, Is.Not.Null.And.Not.Empty);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("Providers__MaxAttempts", null);
        Environment.SetEnvironmentVariable(
            "Providers__MaxConsecutiveTimeouts", null);
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, "senha123"));
        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    /// <summary>Semeia chat pai + run concluída para o contexto da tool.</summary>
    private async Task<(string UserId, string ChatId, string RunId)> SeedParentAsync(
        string email)
    {
        var auth = await SignUpAsync("U", email);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var chat = new Chat
        {
            UserId = auth.User.Id, Title = "pai",
            ModelsJson = "[\"llama3\"]", CreatedAt = now, UpdatedAt = now,
        };
        db.Chats.Add(chat);
        var run = new ChatRun
        {
            ChatId = chat.Id, UserId = auth.User.Id, Model = "llama3",
            RequestJson = """{"model":"llama3","messages":[]}""",
            Status = ChatRunStatus.Completed,
            CreatedAt = now, CompletedAt = now,
        };
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync();
        return (auth.User.Id, chat.Id, run.Id);
    }

    private DevPipelineService NewService() =>
        _factory.Services.CreateScope().ServiceProvider
            .GetRequiredService<DevPipelineService>();

    [Test]
    public async Task StartAsync_CriaChatFilhoBuildEKv()
    {
        var (userId, chatId, runId) = await SeedParentAsync("s1@pipeline.local");
        var ctx = new BuiltinToolContext(
            userId, chatId, runId, Path.GetTempPath(), Path.GetTempPath(),
            "llama3");

        var state = await NewService().StartAsync(
            ctx, "tarefa de teste", "ctx extra", push: false,
            maxFixAttempts: 1, CancellationToken.None);

        Assert.That(state.Id, Does.StartWith("dp-"));
        Assert.That(state.ChildChatId, Is.Not.Null);
        Assert.That(state.Status, Is.EqualTo("running"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var filho = await db.Chats.AsNoTracking()
            .FirstAsync(c => c.Id == state.ChildChatId);
        Assert.That(filho.ParentChatId, Is.EqualTo(chatId));
        Assert.That(filho.Mode, Is.EqualTo(PermissionRuleset.BuildMode));
        Assert.That(filho.Title, Does.Contain("pipeline:"));

        // kv round-trip — o estado sobrevive a um scope novo.
        var reload = await NewService().GetAsync(state.Id, CancellationToken.None);
        Assert.That(reload, Is.Not.Null);
        Assert.That(reload!.Task, Is.EqualTo("tarefa de teste"));
        Assert.That(reload.Phase, Is.EqualTo("implement"));
        Assert.That(reload.MaxFixAttempts, Is.EqualTo(1));
    }

    // ---------- RF-007: defaults fusion (leader→plan/fix, sidekick→workers) ----------

    /// <summary>Semeia as kv de par fusion da run pai (o executor grava
    /// no boot de uma run <c>fusion:L&gt;S</c>).</summary>
    private async Task SeedFusionKvAsync(
        string runId, string leader, string sidekick)
    {
        using var scope = _factory.Services.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
        await config.SetAsync($"r:{runId}:fusion.leader", leader, CancellationToken.None);
        await config.SetAsync($"r:{runId}:fusion.sidekick", sidekick, CancellationToken.None);
    }

    [Test]
    public async Task StartAsync_PaiFusion_PapeisHerdadosDoPar()
    {
        var (userId, chatId, runId) = await SeedParentAsync("fu1@pipeline.local");
        await SeedFusionKvAsync(runId, "gpt-lead", "chain:sk-1,gpt-lead");
        var ctx = new BuiltinToolContext(
            userId, chatId, runId, Path.GetTempPath(), Path.GetTempPath(),
            "fusion:gpt-lead>sk-1");

        var state = await NewService().StartAsync(
            ctx, "tarefa fusion", null, push: false,
            maxFixAttempts: 1, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(state.Models["implement"], Is.EqualTo("chain:sk-1,gpt-lead"));
            Assert.That(state.Models["test"], Is.EqualTo("chain:sk-1,gpt-lead"));
            Assert.That(state.Models["verify"], Is.EqualTo("chain:sk-1,gpt-lead"));
            Assert.That(state.Models["plan"], Is.EqualTo("gpt-lead"),
                "planner roda no leader — não no sidekick dos workers");
            Assert.That(state.Models["fix"], Is.EqualTo("gpt-lead"),
                "diagnoser roda no leader");
        });
    }

    [Test]
    public async Task StartAsync_OverridesExplicitos_VencemDefaultsFusion()
    {
        var (userId, chatId, runId) = await SeedParentAsync("fu2@pipeline.local");
        await SeedFusionKvAsync(runId, "gpt-lead", "sk-1");
        var ctx = new BuiltinToolContext(
            userId, chatId, runId, Path.GetTempPath(), Path.GetTempPath(),
            "fusion:gpt-lead>sk-1");

        var state = await NewService().StartAsync(
            ctx, "tarefa fusion", null, push: false,
            maxFixAttempts: 1, CancellationToken.None,
            workerModel: "w-custom", fixModel: "f-custom");

        Assert.Multiple(() =>
        {
            Assert.That(state.Models["implement"], Is.EqualTo("w-custom"));
            Assert.That(state.Models["plan"], Is.EqualTo("w-custom"),
                "worker_model explícito cobre o plan (semântica documentada)");
            Assert.That(state.Models["fix"], Is.EqualTo("f-custom"));
        });
    }

    [Test]
    public async Task StartAsync_SemFusion_ModelsVazioHerdaDoPai()
    {
        var (userId, chatId, runId) = await SeedParentAsync("fu3@pipeline.local");
        var ctx = new BuiltinToolContext(
            userId, chatId, runId, Path.GetTempPath(), Path.GetTempPath(),
            "llama3");

        var state = await NewService().StartAsync(
            ctx, "tarefa comum", null, push: false,
            maxFixAttempts: 1, CancellationToken.None);

        Assert.That(state.Models, Is.Empty,
            "sem fusion nem overrides as fases herdam o modelo do pai");
    }

    // ---------- mode_switch + preset de entrada (SPEC-20261122) ----------

    /// <summary>Semeia um chat simples do usuário e devolve ids + userId.</summary>
    private async Task<(string UserId, string ChatId)> SeedChatAsync(
        string email, string mode = "chat")
    {
        var auth = await SignUpAsync("MU", email);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var chat = new Chat
        {
            UserId = auth.User.Id, Title = "modes",
            ModelsJson = "[]", Mode = mode,
            ApprovalPreset = "approve-mutations",
            CreatedAt = now, UpdatedAt = now,
        };
        chat.Id = Guid.NewGuid().ToString("N");
        db.Chats.Add(chat);
        await db.SaveChangesAsync();
        return (auth.User.Id, chat.Id);
    }

    private OpenWebUI.Infrastructure.ChatTools.Tools.ModeSwitchBuiltinTool ModeTool() =>
        new(_factory.Services.GetRequiredService<IServiceScopeFactory>());

    private static JsonElement ModeArgs(string mode) =>
        JsonDocument.Parse($"{{\"mode\":\"{mode}\"}}").RootElement;

    [Test]
    public async Task ModeSwitch_ChatParaPlan_TrocaModoESobePreset()
    {
        var (userId, chatId) = await SeedChatAsync("ms-plan@test.local");
        var result = await ModeTool().ExecuteAsync(
            ModeArgs("plan"),
            new BuiltinToolContext(userId, chatId, null, "/tmp", "/tmp"),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Does.Contain("'plan'"));
            Assert.That(result.Text, Does.Contain("smart"));
        });
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var chat = await db.Chats.FindAsync(chatId);
        Assert.That(chat!.Mode, Is.EqualTo("plan"));
        Assert.That(chat.ApprovalPreset, Is.EqualTo("smart"));
    }

    [Test]
    public async Task ModeSwitch_ChatParaBuild_TrocaParaBuild()
    {
        var (userId, chatId) = await SeedChatAsync("ms-build@test.local");
        var result = await ModeTool().ExecuteAsync(
            ModeArgs("build"),
            new BuiltinToolContext(userId, chatId, null, "/tmp", "/tmp"),
            CancellationToken.None);

        Assert.That(result.Text, Does.Contain("'build'"));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var chat = await db.Chats.FindAsync(chatId);
        Assert.That(chat!.Mode, Is.EqualTo("build"));
        Assert.That(chat.ApprovalPreset, Is.EqualTo("smart"));
    }

    [Test]
    public async Task ModeSwitch_PlanParaBuild_RecusaEApontaPlanExit()
    {
        var (userId, chatId) = await SeedChatAsync(
            "ms-planbuild@test.local", mode: "plan");
        var result = await ModeTool().ExecuteAsync(
            ModeArgs("build"),
            new BuiltinToolContext(userId, chatId, null, "/tmp", "/tmp"),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Does.Contain("plan_exit"),
                "plan→build exige a aprovação humana do plan_exit");
        });
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var chat = await db.Chats.FindAsync(chatId);
        Assert.That(chat!.Mode, Is.EqualTo("plan"), "modo não mudou");
    }

    [Test]
    public async Task ModeSwitch_MesmoAlvo_NoOp()
    {
        var (userId, chatId) = await SeedChatAsync("ms-noop@test.local");
        var result = await ModeTool().ExecuteAsync(
            ModeArgs("chat"),
            new BuiltinToolContext(userId, chatId, null, "/tmp", "/tmp"),
            CancellationToken.None);
        Assert.That(result.Text, Does.Contain("já está no modo chat"));
    }

    [Test]
    public async Task ModeSwitch_PresetPinado_NaoSobrescreve()
    {
        var (userId, chatId) = await SeedChatAsync("ms-pin@test.local");
        using (var scope = _factory.Services.CreateScope())
        {
            var config = scope.ServiceProvider
                .GetRequiredService<ConfigService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var chat = await db.Chats.FindAsync(chatId);
            chat!.ApprovalPreset = "allow-readonly";
            await db.SaveChangesAsync();
            await ChatModeTransitions.PinPresetAsync(
                config, chatId, CancellationToken.None);
        }

        var result = await ModeTool().ExecuteAsync(
            ModeArgs("build"),
            new BuiltinToolContext(userId, chatId, null, "/tmp", "/tmp"),
            CancellationToken.None);

        Assert.That(result.Text, Does.Not.Contain("smart"));
        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var chat2 = await db2.Chats.FindAsync(chatId);
        Assert.That(chat2!.ApprovalPreset, Is.EqualTo("allow-readonly"),
            "preset pinado pelo dono nunca é sobrescrito");
    }

    [Test]
    public async Task RunAsync_SemProvider_FalhaImplementEReportaNoPai()
    {
        var (userId, chatId, runId) = await SeedParentAsync("s2@pipeline.local");
        var ctx = new BuiltinToolContext(
            userId, chatId, runId, Path.GetTempPath(), Path.GetTempPath(),
            "llama3");
        var service = NewService();
        var state = await service.StartAsync(
            ctx, "implementar x", null, push: false,
            maxFixAttempts: 1, CancellationToken.None);

        // A run da fase plan (primeira LLM do ciclo) é despachada e falha
        // sem provider — o orquestrador marca a pipeline failed e reporta
        // no chat pai.
        await service.RunAsync(state.Id);

        var final = await service.GetAsync(state.Id, CancellationToken.None);
        Assert.That(final, Is.Not.Null);
        Assert.That(final!.Status, Is.EqualTo("failed"));
        Assert.That(final.Error, Does.Contain("plan"));
        Assert.That(final.Runs, Contains.Key("plan"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Report-back: mensagem [dev_pipeline] no chat pai + run de report.
        var reportMsgs = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatId == chatId && m.Content.Contains("[dev_pipeline"))
            .ToListAsync();
        Assert.That(reportMsgs, Has.Count.EqualTo(1));
        Assert.That(reportMsgs[0].Content, Does.Contain("failed"));

        var reportRun = await db.ChatRuns.AsNoTracking()
            .Where(r => r.ChatId == chatId && r.MetaJson != null
                && r.MetaJson.Contains("report"))
            .ToListAsync();
        Assert.That(reportRun, Has.Count.EqualTo(1));

        // Resume em pipeline failed → nada a retomar.
        var tool = new DevPipelineBuiltinTool(
            _factory.Services.GetRequiredService<IServiceScopeFactory>());
        var resume = await tool.ExecuteAsync(
            JsonDocument.Parse(
                $"{{\"op\":\"resume\",\"pipeline_id\":\"{state.Id}\"}}").RootElement,
            new BuiltinToolContext(userId, chatId, runId,
                Path.GetTempPath(), Path.GetTempPath()),
            CancellationToken.None);
        Assert.That(resume.Text, Does.Contain("nada a retomar"));

        // Status via tool reflete o kv.
        var status = await tool.ExecuteAsync(
            JsonDocument.Parse(
                $"{{\"op\":\"status\",\"pipeline_id\":\"{state.Id}\"}}").RootElement,
            new BuiltinToolContext(userId, chatId, runId,
                Path.GetTempPath(), Path.GetTempPath()),
            CancellationToken.None);
        Assert.That(status.Text, Does.Contain("status=failed"));
    }

    [Test]
    public async Task DevPipelineTool_StatusInexistente_NaoEncontrada()
    {
        var tool = new DevPipelineBuiltinTool(
            _factory.Services.GetRequiredService<IServiceScopeFactory>());
        var result = await tool.ExecuteAsync(
            JsonDocument.Parse(
                """{"op":"status","pipeline_id":"dp-inexistente"}""").RootElement,
            new BuiltinToolContext("u1", null, null,
                Path.GetTempPath(), Path.GetTempPath()),
            CancellationToken.None);
        Assert.That(result.Text, Does.Contain("não encontrada"));
    }
}
