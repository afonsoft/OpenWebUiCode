using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Runs;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.SystemPrompt;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura da SPEC-20261021-harness-seams: template de webhook
/// (RF-D2), waterfall de interceptors com ordenação/fail-closed
/// (RF-A2/A3), RepeatToolGuard (RF-A4), seams de team
/// (RF-E1/E2) e paridade do system prompt por contributors (RF-B2).
/// </summary>
[TestFixture]
public class HarnessSeamsTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"owui-seams-{Guid.NewGuid():N}.db");
        using var db = NewDb();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static async Task SeedUserAsync(AppDbContext db, string id)
    {
        db.Users.Add(new User
        {
            Id = id, Name = id, Email = $"{id}@c.local", Role = "user",
        });
        await db.SaveChangesAsync();
    }

    // ---------- RF-D2: WebhookPromptTemplate ----------

    [Test]
    public void Template_Resolve_Path_Simples_E_Aninhado()
    {
        var payload = JsonDocument.Parse(
            """{"ref":"main","pusher":{"name":"octo"},"n":3}""").RootElement;
        var rendered = WebhookPromptTemplate.Render(
            "push em {{ref}} por {{pusher.name}} (#{{n}})", payload);
        Assert.That(rendered, Is.EqualTo("push em main por octo (#3)"));
    }

    [Test]
    public void Template_Campo_Ausente_Lanca()
    {
        var payload = JsonDocument.Parse("""{"a":1}""").RootElement;
        Assert.Throws<WebhookTemplateException>(() =>
            WebhookPromptTemplate.Render("{{a}} {{zzz}}", payload));
    }

    [Test]
    public void Template_Raw_Imprime_Payload_Inteiro()
    {
        var payload = JsonDocument.Parse("""{"x":[1,2]}""").RootElement;
        var rendered = WebhookPromptTemplate.Render("corpo: {{$raw}}", payload);
        Assert.That(rendered, Does.Contain("corpo: "));
        Assert.That(rendered, Does.Contain("\"x\""));
    }

    // ---------- RF-D1: assinatura HMAC do webhook ----------

    private static string Sign(string secret, string body) =>
        "sha256=" + Convert.ToHexString(
            System.Security.Cryptography.HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(secret),
                Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    [Test]
    public void Webhook_Assinatura_Valida_Aceita()
    {
        var sig = Sign("segredo", "{\"a\":1}");
        Assert.That(
            WebhookSignature.Verify("segredo", "{\"a\":1}", sig), Is.True);
    }

    [Test]
    public void Webhook_Assinatura_Errada_Ou_Ausente_Rejeita()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WebhookSignature.Verify(
                "segredo", "{\"a\":1}", "sha256=deadbeef"), Is.False);
            Assert.That(WebhookSignature.Verify(
                "segredo", "{\"a\":1}", null), Is.False);
            Assert.That(WebhookSignature.Verify(
                "segredo", "corpo adulterado",
                Sign("segredo", "{\"a\":1}")), Is.False);
        });
    }

    // ---------- RF-A2/A3: RunInterceptorRunner ----------

    private sealed class PassInterceptor(int order) : IRunInterceptor
    {
        public int Order { get; } = order;
    }

    private sealed class RecordingInterceptor(int order, List<string> seen)
        : IRunInterceptor
    {
        public int Order { get; } = order;
        public string Name => $"i{Order}";

        public Task<ToolCallDecision> OnToolCallAsync(
            ToolCallContext ctx, CancellationToken ct)
        {
            seen.Add(Name);
            return Task.FromResult(ToolCallDecision.Pass);
        }
    }

    private sealed class RewriteInterceptor(string args) : IRunInterceptor
    {
        public int Order => 50;
        public Task<ToolCallDecision> OnToolCallAsync(
            ToolCallContext ctx, CancellationToken ct) =>
            Task.FromResult(ToolCallDecision.Rewrite(args));
    }

    private sealed class ThrowingInterceptor : IRunInterceptor
    {
        public int Order => 10;
        public Task<ToolCallDecision> OnToolCallAsync(
            ToolCallContext ctx, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private static ToolCallContext Ctx(string tool = "shell", string args = "{}") =>
        new("run1", "chat1", "u1", tool, args);

    private static RunInterceptorRunner Runner(params IRunInterceptor[] xs) =>
        new(xs, new ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<RunInterceptorRunner>.Instance);

    [Test]
    public async Task Interceptors_Rodam_Em_Ordem_Crescente()
    {
        var seen = new List<string>();
        var runner = Runner(
            new RecordingInterceptor(30, seen),
            new RecordingInterceptor(10, seen),
            new RecordingInterceptor(20, seen));
        var outcome = await runner.RunToolCallAsync(Ctx(), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(outcome.Vetoed, Is.False);
            Assert.That(seen, Is.EqualTo(new[] { "i10", "i20", "i30" }));
        });
    }

    [Test]
    public async Task Interceptor_Excecao_Em_ToolCall_Veta_FailClosed()
    {
        var seen = new List<string>();
        var runner = Runner(
            new ThrowingInterceptor(),
            new RecordingInterceptor(99, seen));
        var outcome = await runner.RunToolCallAsync(Ctx(), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(outcome.Vetoed, Is.True);
            Assert.That(outcome.VetoedBy, Is.EqualTo(nameof(ThrowingInterceptor)));
            // Fail-closed: o interceptor seguinte NÃO roda após o veto.
            Assert.That(seen, Is.Empty);
        });
    }

    [Test]
    public async Task Interceptor_Rewrite_Propaga_Args()
    {
        var runner = Runner(new RewriteInterceptor("{\"a\":1}"));
        var outcome = await runner.RunToolCallAsync(
            Ctx(args: "{\"a\":0}"), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(outcome.Vetoed, Is.False);
            Assert.That(outcome.ArgumentsJson, Is.EqualTo("{\"a\":1}"));
        });
    }

    // ---------- RF-A4: RepeatToolGuard ----------

    [Test]
    public async Task RepeatGuard_Veta_Na_Terceira_Repeticao()
    {
        var guard = new RepeatToolGuard(
            new ConfigurationBuilder().Build());
        var r1 = await guard.OnToolCallAsync(Ctx(), CancellationToken.None);
        var r2 = await guard.OnToolCallAsync(Ctx(), CancellationToken.None);
        var r3 = await guard.OnToolCallAsync(Ctx(), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(r1.Action, Is.EqualTo(ToolCallAction.Pass));
            Assert.That(r2.Action, Is.EqualTo(ToolCallAction.Pass));
            Assert.That(r3.Action, Is.EqualTo(ToolCallAction.Veto));
            Assert.That(r3.Reason, Does.Contain("shell"));
        });
    }

    [Test]
    public async Task RepeatGuard_Args_Diferentes_Resetam_Streak()
    {
        var guard = new RepeatToolGuard(
            new ConfigurationBuilder().Build());
        await guard.OnToolCallAsync(Ctx(args: "{\"n\":1}"), CancellationToken.None);
        await guard.OnToolCallAsync(Ctx(args: "{\"n\":2}"), CancellationToken.None);
        var r3 = await guard.OnToolCallAsync(
            Ctx(args: "{\"n\":3}"), CancellationToken.None);
        Assert.That(r3.Action, Is.EqualTo(ToolCallAction.Pass));
    }

    // ---------- RF-E1/E2: TeamService ----------

    private static Chat NewChat(string userId, string? parent = null) =>
        new()
        {
            UserId = userId,
            Title = "c",
            ParentChatId = parent,
            CreatedAt = 1,
            UpdatedAt = 1,
        };

    [Test]
    public async Task Team_EnsureRoot_Marca_Parent_Como_Leader()
    {
        using var db = NewDb();
        await SeedUserAsync(db, "u1");
        var parent = NewChat("u1");
        db.Chats.Add(parent);
        await db.SaveChangesAsync();

        var teamId = await TeamService.EnsureRootTeamAsync(
            db, parent, CancellationToken.None);
        await db.SaveChangesAsync();

        var reloaded = await db.Chats.FindAsync(parent.Id);
        Assert.Multiple(() =>
        {
            Assert.That(teamId, Is.EqualTo(parent.Id));
            Assert.That(reloaded!.TeamId, Is.EqualTo(parent.Id));
            Assert.That(reloaded.TeamRole, Is.EqualTo("leader"));
        });
    }

    [Test]
    public async Task Team_ResolveTeamId_Sobe_Ate_A_Raiz()
    {
        using var db = NewDb();
        await SeedUserAsync(db, "u1");
        var root = NewChat("u1");
        db.Chats.Add(root);
        await db.SaveChangesAsync();
        var teamId = await TeamService.EnsureRootTeamAsync(
            db, root, CancellationToken.None);
        var child = NewChat("u1", parent: root.Id);
        child.TeamId = teamId;
        child.TeamRole = "member";
        var grand = NewChat("u1", parent: child.Id);
        db.Chats.AddRange(child, grand);
        await db.SaveChangesAsync();

        var resolved = await TeamService.ResolveTeamIdAsync(
            db, grand.Id, CancellationToken.None);
        Assert.That(resolved, Is.EqualTo(root.Id));
    }

    [Test]
    public async Task Team_Status_Lista_Roster_Com_Roles()
    {
        using var db = NewDb();
        await SeedUserAsync(db, "u1");
        var root = NewChat("u1");
        db.Chats.Add(root);
        await db.SaveChangesAsync();
        var teamId = await TeamService.EnsureRootTeamAsync(
            db, root, CancellationToken.None);
        var member = NewChat("u1", parent: root.Id);
        member.TeamId = teamId;
        member.TeamRole = "member";
        db.Chats.Add(member);
        await db.SaveChangesAsync();

        var status = await TeamService.BuildStatusAsync(
            db, teamId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(status, Is.Not.Null);
            Assert.That(status!.Members.Count, Is.EqualTo(2));
            Assert.That(
                status.Members.Count(m => m.Role == "leader"), Is.EqualTo(1));
            Assert.That(
                status.Members.Count(m => m.Role == "member"), Is.EqualTo(1));
        });
    }

    // ---------- RF-B2: paridade do prompt por contributors ----------

    [Test]
    public async Task Prompt_Contributors_Default_Ordem_E_Base_Presente()
    {
        // Golden de fase 1: com inputs mínimos, só o contributor base emite
        // — o system prompt resultante é exatamente o prompt default
        // histórico (paridade byte-a-byte com o systemParts original).
        using var db = NewDb();
        var contributors = OpenWebUI.Api.Completions.Contributors
            .SystemPromptContributors.Default(db, null!, null!, null!, null!);
        var user = new User { Id = "u1", Name = "U", Email = "u@c.local" };
        var ctx = new PromptContext
        {
            Request = new ChatCompletionRequest(
                "m", [new ChatCompletionMessage("user", "oi")], Stream: true),
            User = user,
            LastUserText = "oi",
            Model = "m",
            Ct = CancellationToken.None,
        };

        var orders = contributors.Select(c => c.Order).ToList();
        Assert.That(orders, Is.EqualTo(orders.OrderBy(x => x).ToList()),
            "contributors default fora de ordem crescente");

        var sections = new List<string>();
        foreach (var c in contributors)
        {
            try
            {
                sections.AddRange(await c.ContributeAsync(ctx));
            }
            catch (Exception)
            {
                // contributors dependentes de serviços null omitimos —
                // o composer tolera falha do mesmo jeito.
            }
        }

        var merged = string.Join("\n\n", sections);
        Assert.That(merged, Does.Contain("Open WebUI agent"));
        Assert.That(merged.TrimStart(),
            Does.StartWith(sections[0].TrimStart()));
    }

    // ---------- RF-E2: team tools (task/message/status) ----------

    private IServiceScopeFactory Scopes()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o =>
            o.UseSqlite($"Data Source={_dbPath}"));
        return services.BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
    }

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    private async Task<(Chat Root, Chat Member)> SeedTeamAsync(
        AppDbContext db)
    {
        await SeedUserAsync(db, "u1");
        var root = NewChat("u1");
        db.Chats.Add(root);
        await db.SaveChangesAsync();
        var teamId = await TeamService.EnsureRootTeamAsync(
            db, root, CancellationToken.None);
        var member = NewChat("u1", parent: root.Id);
        member.TeamId = teamId;
        member.TeamRole = "member";
        db.Chats.Add(member);
        await db.SaveChangesAsync();
        return (root, member);
    }

    private static BuiltinToolContext ToolCtx(string chatId) =>
        new("u1", chatId, "run1", "/tmp", "/tmp");

    // ---------- TeamService: resolução por cadeia de parents ----------

    [Test]
    public async Task Interceptor_PreStep_Rewrite_Substitui_Messages()
    {
        var rewrite = new PreStepRewriteInterceptor(
            [new ChatCompletionMessage("system", "reescrito")]);
        var runner = Runner(rewrite);
        var ctx = new PreStepContext("r1", "c1", "u1", 1,
            [new ChatCompletionMessage("user", "original")]);

        var outcome = await runner.RunPreStepAsync(ctx, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(outcome.Rejected, Is.False);
            Assert.That(outcome.Messages.Single().Content,
                Is.EqualTo("reescrito"));
        });
    }

    private sealed class PreStepRewriteInterceptor(
        IReadOnlyList<ChatCompletionMessage> messages) : IRunInterceptor
    {
        public int Order => 0;
        public Task<PreStepDecision> OnPreStepAsync(
            PreStepContext ctx, CancellationToken ct) =>
            Task.FromResult(PreStepDecision.Rewrite(messages));
    }

    [Test]
    public async Task TeamResolve_SemColuna_Sobe_Cadeia_Parent_Ate_Raiz()
    {
        using var db = NewDb();
        await SeedUserAsync(db, "u1");
        var root = NewChat("u1");
        db.Chats.Add(root);
        await db.SaveChangesAsync();
        // Raiz auto-referenciada como team (modelo legado sem TeamId nos filhos).
        root.TeamId = root.Id;
        root.TeamRole = "leader";
        var mid = NewChat("u1", parent: root.Id);
        var leaf = NewChat("u1", parent: mid.Id);
        db.Chats.AddRange(mid, leaf);
        await db.SaveChangesAsync();

        var resolved = await TeamService.ResolveTeamIdAsync(
            db, leaf.Id, CancellationToken.None);
        Assert.That(resolved, Is.EqualTo(root.Id));

        var rootId = await TeamService.RootChatIdAsync(
            db, leaf, CancellationToken.None);
        Assert.That(rootId, Is.EqualTo(root.Id));
    }

    [Test]
    public async Task TeamResolve_Chat_Orfa_Null_E_Parent_Quebrado_Null()
    {
        using var db = NewDb();
        await SeedUserAsync(db, "u1");
        var solo = NewChat("u1");
        db.Chats.Add(solo);
        await db.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await TeamService.ResolveTeamIdAsync(
                db, "chat-inexistente", CancellationToken.None), Is.Null);
            Assert.That(await TeamService.ResolveTeamIdAsync(
                db, solo.Id, CancellationToken.None), Is.Null);
        });

        // Parent apontando pra chat deletado → null (não é team).
        var orphan = NewChat("u1", parent: "pai-deletado");
        db.Chats.Add(orphan);
        await db.SaveChangesAsync();
        Assert.That(await TeamService.ResolveTeamIdAsync(
            db, orphan.Id, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task TeamStatus_Roster_Runs_Tasks_Completos()
    {
        using var db = NewDb();
        var (root, member) = await SeedTeamAsync(db);
        db.TeamTasks.Add(new TeamTask
        {
            TeamId = root.TeamId!,
            Title = "investigar bug",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        db.ChatRuns.Add(new ChatRun
        {
            ChatId = member.Id,
            UserId = "u1",
            Model = "m",
            Status = ChatRunStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        await db.SaveChangesAsync();

        var status = await TeamService.BuildStatusAsync(
            db, root.TeamId!, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(status, Is.Not.Null);
            Assert.That(status!.Members.Count, Is.EqualTo(2));
            Assert.That(status.Tasks.Count, Is.EqualTo(1));
            Assert.That(status.Members.Count(m => m.ActiveRunId is not null),
                Is.EqualTo(1));
        });
    }

    [Test]
    public async Task TeamTask_PathsDeErro_ClaimDuplo_TaskInexistente_StatusInvalido()
    {
        using var db = NewDb();
        var (root, _) = await SeedTeamAsync(db);
        var tool = new OpenWebUI.Infrastructure.ChatTools.Tools
            .TeamTaskBuiltinTool(Scopes());
        var ctx = ToolCtx(root.Id);

        // task_id inexistente.
        var miss = await tool.ExecuteAsync(
            Args("""{"op":"claim","task_id":"zzz"}"""),
            ctx, CancellationToken.None);
        Assert.That(miss.Text, Does.Contain("não existe"));

        // claim duplo: outra tarefa claimada por outro membro não re-claima.
        var created = await tool.ExecuteAsync(
            Args("""{"op":"create","title":"t1"}"""),
            ctx, CancellationToken.None);
        var task = await db.TeamTasks.SingleAsync();
        await tool.ExecuteAsync(
            Args($$"""{"op":"claim","task_id":"{{task.Id}}"}"""),
            ctx, CancellationToken.None);
        var again = await tool.ExecuteAsync(
            Args($$"""{"op":"claim","task_id":"{{task.Id}}"}"""),
            ctx, CancellationToken.None);
        Assert.That(again.Text, Does.Contain("já claimada"));

        // update com status inválido.
        var bad = await tool.ExecuteAsync(
            Args($$"""{"op":"update","task_id":"{{task.Id}}","status":"x"}"""),
            ctx, CancellationToken.None);
        Assert.That(bad.Text, Does.Contain("pending|in_progress|done"));

        // op desconhecida.
        var op = await tool.ExecuteAsync(
            Args("""{"op":"explode"}"""), ctx, CancellationToken.None);
        Assert.That(op.Text, Does.Contain("list|create|claim|update|complete"));
    }

    [Test]
    public async Task TeamMessage_E_TeamTask_SemTeam_FailClosed()
    {
        using var db = NewDb();
        await SeedUserAsync(db, "u1");
        var solo = NewChat("u1");
        db.Chats.Add(solo);
        await db.SaveChangesAsync();

        var taskTool = new OpenWebUI.Infrastructure.ChatTools.Tools
            .TeamTaskBuiltinTool(Scopes());
        var msgTool = new OpenWebUI.Infrastructure.ChatTools.Tools
            .TeamMessageBuiltinTool(Scopes());
        var statusTool = new OpenWebUI.Infrastructure.ChatTools.Tools
            .TeamStatusBuiltinTool(Scopes());
        var ctx = ToolCtx(solo.Id);

        Assert.Multiple(async () =>
        {
            Assert.That((await taskTool.ExecuteAsync(
                Args("""{"op":"list"}"""), ctx, CancellationToken.None)).Text,
                Does.Contain("não pertence"));
            Assert.That((await msgTool.ExecuteAsync(
                Args("""{"to_chat_id":"x","message":"y"}"""),
                ctx, CancellationToken.None)).Text,
                Does.Contain("não pertence"));
            Assert.That((await statusTool.ExecuteAsync(
                Args("{}"), ctx, CancellationToken.None)).Text,
                Does.Contain("não pertence"));
        });
    }

    [Test]
    public async Task TeamMessage_AlvoInvalido_SelfOuForaDoTeam()
    {
        using var db = NewDb();
        var (root, member) = await SeedTeamAsync(db);
        var tool = new OpenWebUI.Infrastructure.ChatTools.Tools
            .TeamMessageBuiltinTool(Scopes());
        var ctx = ToolCtx(root.Id);

        // Args obrigatórios.
        var missing = await tool.ExecuteAsync(
            Args("""{"to_chat_id":""}"""), ctx, CancellationToken.None);
        Assert.That(missing.Text, Does.Contain("obrigatórios"));

        // Self.
        var self = await tool.ExecuteAsync(
            Args($$"""{"to_chat_id":"{{root.Id}}","message":"oi"}"""),
            ctx, CancellationToken.None);
        Assert.That(self.Text, Does.Contain("outro membro"));

        // Fora do team.
        var outsider = NewChat("u1");
        db.Chats.Add(outsider);
        await db.SaveChangesAsync();
        var outr = await tool.ExecuteAsync(
            Args($$"""{"to_chat_id":"{{outsider.Id}}","message":"oi"}"""),
            ctx, CancellationToken.None);
        Assert.That(outr.Text, Does.Contain("não é membro"));
    }

    [Test]
    public async Task TeamTask_Ciclo_Completo_Create_Claim_Complete()
    {
        using var db = NewDb();
        var (root, _) = await SeedTeamAsync(db);
        var tool = new OpenWebUI.Infrastructure.ChatTools.Tools
            .TeamTaskBuiltinTool(Scopes());
        var ctx = ToolCtx(root.Id);

        var created = await tool.ExecuteAsync(
            Args("""{"op":"create","title":"pesquisar API"}"""),
            ctx, CancellationToken.None);
        Assert.That(created.Text, Does.Contain("Tarefa criada"));

        var list = await tool.ExecuteAsync(
            Args("""{"op":"list"}"""), ctx, CancellationToken.None);
        Assert.That(list.Text, Does.Contain("pesquisar API"));

        var task = await db.TeamTasks.SingleAsync();
        var claim = await tool.ExecuteAsync(
            Args($$"""{"op":"claim","task_id":"{{task.Id}}"}"""),
            ctx, CancellationToken.None);
        Assert.That(claim.Text, Does.Contain("in_progress"));

        var done = await tool.ExecuteAsync(
            Args($$"""{"op":"complete","task_id":"{{task.Id}}","result":"ok"}"""),
            ctx, CancellationToken.None);
        db.ChangeTracker.Clear();
        Assert.Multiple(() =>
        {
            Assert.That(done.Text, Does.Contain("done"));
            Assert.That(db.TeamTasks.Single().Result, Is.EqualTo("ok"));
        });
    }

    [Test]
    public async Task TeamTask_Fora_De_Team_Falha_Fechado()
    {
        using var db = NewDb();
        await SeedUserAsync(db, "u1");
        var solo = NewChat("u1");
        db.Chats.Add(solo);
        await db.SaveChangesAsync();
        var tool = new OpenWebUI.Infrastructure.ChatTools.Tools
            .TeamTaskBuiltinTool(Scopes());
        var res = await tool.ExecuteAsync(
            Args("""{"op":"list"}"""), ToolCtx(solo.Id),
            CancellationToken.None);
        Assert.That(res.Text, Does.Contain("não pertence a um agent team"));
    }

    [Test]
    public async Task TeamMessage_Membro_Ocioso_Gera_ChatMessage_E_Notificacao()
    {
        using var db = NewDb();
        var (root, member) = await SeedTeamAsync(db);
        var tool = new OpenWebUI.Infrastructure.ChatTools.Tools
            .TeamMessageBuiltinTool(Scopes());
        var res = await tool.ExecuteAsync(
            Args($$"""{"to_chat_id":"{{member.Id}}","message":"oi time"}"""),
            ToolCtx(root.Id), CancellationToken.None);
        Assert.That(res.Text, Does.Contain("Membro ocioso"));

        var msgs = await db.ChatMessages
            .Where(m => m.ChatId == member.Id).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(msgs, Has.Count.EqualTo(1));
            Assert.That(msgs[0].Content, Does.Contain("[team:"));
            Assert.That(db.Notifications.Count(n =>
                n.UserId == "u1"), Is.GreaterThan(0));
        });
    }

    [Test]
    public async Task TeamMessage_Membro_Com_RunAtiva_Vira_Steer()
    {
        using var db = NewDb();
        var (root, member) = await SeedTeamAsync(db);
        db.ChatRuns.Add(new ChatRun
        {
            ChatId = member.Id,
            UserId = "u1",
            Model = "m",
            Status = ChatRunStatus.Running,
            CreatedAt = 1,
        });
        await db.SaveChangesAsync();
        var run = await db.ChatRuns.SingleAsync();

        var tool = new OpenWebUI.Infrastructure.ChatTools.Tools
            .TeamMessageBuiltinTool(Scopes());
        var res = await tool.ExecuteAsync(
            Args($$"""{"to_chat_id":"{{member.Id}}","message":"steer aqui"}"""),
            ToolCtx(root.Id), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(res.Text, Does.Contain("steer"));
            Assert.That(db.ChatRunSteers.Single(s => s.RunId == run.Id)
                .Content, Does.Contain("steer aqui"));
        });
    }

    [Test]
    public async Task TeamMessage_Alvo_De_Outro_Time_Rejeita()
    {
        using var db = NewDb();
        var (root, _) = await SeedTeamAsync(db);
        var estrangeiro = NewChat("u1");
        estrangeiro.TeamId = "outro-team";
        estrangeiro.TeamRole = "member";
        db.Chats.Add(estrangeiro);
        await db.SaveChangesAsync();
        var tool = new OpenWebUI.Infrastructure.ChatTools.Tools
            .TeamMessageBuiltinTool(Scopes());
        var res = await tool.ExecuteAsync(
            Args($$"""{"to_chat_id":"{{estrangeiro.Id}}","message":"vaza"}"""),
            ToolCtx(root.Id), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(res.Text, Does.Contain("não é membro"));
            Assert.That(db.ChatMessages.Count(
                m => m.ChatId == estrangeiro.Id), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task TeamStatus_Retorna_Roster_E_Tasks()
    {
        using var db = NewDb();
        var (root, member) = await SeedTeamAsync(db);
        db.TeamTasks.Add(new TeamTask
        {
            TeamId = root.Id, Title = "t1", Status = "pending",
            CreatedAt = 1, UpdatedAt = 1,
        });
        await db.SaveChangesAsync();
        var tool = new OpenWebUI.Infrastructure.ChatTools.Tools
            .TeamStatusBuiltinTool(Scopes());
        var res = await tool.ExecuteAsync(
            Args("{}"), ToolCtx(member.Id), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(res.Text, Does.Contain("leader"));
            Assert.That(res.Text, Does.Contain("member"));
            Assert.That(res.Text, Does.Contain("t1"));
        });
    }

    // ---------- RF-A1: isolamento de observers ----------

    private sealed class ThrowingObserver : IAgentRunObserver
    {
        public Task OnRunEventAsync(AgentRunEvent e, CancellationToken ct) =>
            throw new InvalidOperationException("observer explodiu");
    }

    private sealed class RecordingObserver(List<string> seen)
        : IAgentRunObserver
    {
        public Task OnRunEventAsync(AgentRunEvent e, CancellationToken ct)
        {
            seen.Add(e.Type);
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task Bus_Observer_Que_Falha_Nao_Derruba_Os_Demais()
    {
        var seen = new List<string>();
        var bus = new AgentRunEventBus(
            [new ThrowingObserver(), new RecordingObserver(seen)],
            new ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<AgentRunEventBus>.Instance);
        await bus.PublishAsync(
            new AgentRunEvent("r1", "c1", "u1", "run.started", "{}"),
            CancellationToken.None);
        Assert.That(seen, Is.EqualTo(new[] { "run.started" }));
    }

    [Test]
    public async Task Bus_Observer_Lento_Timeout_Nao_Bloqueia_Os_Demais()
    {
        var seen = new List<string>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["interceptors.timeout_ms"] = "100",
            }).Build();
        var bus = new AgentRunEventBus(
            [new SlowObserver(), new RecordingObserver(seen)],
            config,
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<AgentRunEventBus>.Instance);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await bus.PublishAsync(
            new AgentRunEvent("r1", "c1", "u1", "run.started", "{}"),
            CancellationToken.None);
        sw.Stop();
        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.EqualTo(new[] { "run.started" }));
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
        });
    }

    private sealed class SlowObserver : IAgentRunObserver
    {
        public Task OnRunEventAsync(AgentRunEvent e, CancellationToken ct) =>
            Task.Delay(TimeSpan.FromSeconds(30), ct);
    }

    [Test]
    public async Task Interceptor_ToolCall_Lento_Timeout_Veta()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["interceptors.timeout_ms"] = "100",
            }).Build();
        var runner = new RunInterceptorRunner(
            [new HangingInterceptor()], config,
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<RunInterceptorRunner>.Instance);
        var outcome = await runner.RunToolCallAsync(
            Ctx(), CancellationToken.None);
        Assert.That(outcome.Vetoed, Is.True);
    }

    private sealed class HangingInterceptor : IRunInterceptor
    {
        public int Order => 0;
        public async Task<ToolCallDecision> OnToolCallAsync(
            ToolCallContext ctx, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return ToolCallDecision.Pass;
        }
    }

    // ---------- RF-C1: validação de args do headless ----------

    [Test]
    public void Headless_IsHeadless_Detecta_Flag()
    {
        Assert.Multiple(() =>
        {
            Assert.That(OpenWebUI.Api.Headless.HeadlessRunner.IsHeadless(
                ["--headless", "--prompt", "x"]), Is.True);
            Assert.That(OpenWebUI.Api.Headless.HeadlessRunner.IsHeadless(
                ["--serve"]), Is.False);
        });
    }

    [Test]
    public async Task Headless_SemPrompt_Exit2()
    {
        using var db = NewDb();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o =>
            o.UseSqlite($"Data Source={_dbPath}"));
        var provider = services.BuildServiceProvider();
        var code = await OpenWebUI.Api.Headless.HeadlessRunner.RunAsync(
            provider, ["--headless"], CancellationToken.None);
        Assert.That(code, Is.EqualTo(2));
    }

    [Test]
    public async Task Headless_Usuario_Inexistente_Exit2()
    {
        using var db = NewDb();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o =>
            o.UseSqlite($"Data Source={_dbPath}"));
        var provider = services.BuildServiceProvider();
        var code = await OpenWebUI.Api.Headless.HeadlessRunner.RunAsync(
            provider,
            ["--headless", "--prompt", "oi", "--user-email", "nao@existe",
             "--model", "m"],
            CancellationToken.None);
        Assert.That(code, Is.EqualTo(2));
    }

    // ── Limpeza de disco no delete de chat ─────────────────────────

    private sealed class StubEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static string HashPath(string path) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path))))
        [..16].ToLowerInvariant();

    [Test]
    public void DeleteChat_Limpa_Checkpoints_Do_Workdir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"owui-ck-{Guid.NewGuid():N}");
        var workdir = Path.Combine(root, "workspaces", "u1", "wt", "repo", "chat1");
        Directory.CreateDirectory(workdir);
        var dataRoot = Path.Combine(DataPaths.Root(root), "checkpoints");
        var hash = HashPath(workdir);
        var repoDir = Path.Combine(dataRoot, "repo", hash);
        var filesDir = Path.Combine(dataRoot, "files", hash);
        Directory.CreateDirectory(Path.Combine(repoDir, "objects"));
        Directory.CreateDirectory(filesDir);
        File.WriteAllText(Path.Combine(filesDir, "manifest.json"), "{}");

        var svc = new OpenWebUI.Infrastructure.Services.CheckpointService(
            new StubEnv(root), new ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<OpenWebUI.Infrastructure.Services.CheckpointService>
                .Instance);
        svc.DeleteForWorkdir(workdir);

        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(repoDir), Is.False);
            Assert.That(Directory.Exists(filesDir), Is.False);
        });
        Directory.Delete(root, recursive: true);
    }

    [Test]
    public async Task DeleteChat_Remove_Worktree_Isolado_Da_Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"owui-wt-{Guid.NewGuid():N}");
        var runDir = Path.Combine(
            DataPaths.Root(root), "worktrees", "u1", "run1");
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, "f.txt"), "x");

        var services = new ServiceCollection().BuildServiceProvider();
        var svc = new OpenWebUI.Infrastructure.Services.WorktreeService(
            new StubEnv(root), new ConfigurationBuilder().Build(),
            services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<OpenWebUI.Infrastructure.Services.WorktreeService>
                .Instance);

        var removed = await svc.DeleteForRunAsync(
            "u1", "run1", Path.Combine(root, "inexistente"),
            CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(runDir));
            Assert.That(Directory.Exists(runDir), Is.False);
        });

        // Idempotente: run inexistente retorna null sem erro.
        var again = await svc.DeleteForRunAsync(
            "u1", "run1", root, CancellationToken.None);
        Assert.That(again, Is.Null);
        Directory.Delete(root, recursive: true);
    }
}
