using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do SPEC-20261013-run-advisor: allowlist read-only do advisor
/// (RF-002), normalização do conselho (RF-003 — <c>OK</c>/vazio descartado,
/// cap ~500 chars), gates de flag (RF-005 — global + por chat), papel de
/// modelo <c>advisor</c> com fallback e o mini-loop interno de tools.
/// </summary>
[TestFixture]
public class RunAdvisorTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp() => _dbPath = TestInfra.NewDbPath("run-advisor");

    [TearDown]
    public void TearDown() => TestInfra.DeleteDb(_dbPath);

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>Tool fake para o registry do advisor.</summary>
    private sealed class StubTool(string name) : IBuiltinChatTool
    {
        public string Name => name;
        public string Description => "stub";
        public string ParametersJson => "{\"type\":\"object\"}";
        public ApprovalTier Tier => ApprovalTier.Read;
        public bool RequiresApproval => false;

        public Task<BuiltinToolResult> ExecuteAsync(
            JsonElement args, BuiltinToolContext context, CancellationToken ct) =>
            Task.FromResult(new BuiltinToolResult("ok"));
    }

    private static BuiltinToolRegistry Registry(params IBuiltinChatTool[] tools) =>
        new(tools, new ConfigurationBuilder().Build());

    private static ChatRun RunFor(Chat chat) =>
        new() { ChatId = chat.Id, UserId = chat.UserId, Model = "modelo-chat" };

    private static async Task<Chat> SeedChatAsync(
        AppDbContext db, bool advisorEnabled)
    {
        var user = new User
        {
            Name = "U", Email = "u@x", PasswordHash = "x", Role = "user",
        };
        var chat = new Chat
        {
            UserId = user.Id, Title = "t",
            AdvisorEnabled = advisorEnabled,
        };
        chat.Messages.Add(new ChatMessage
        {
            ChatId = chat.Id, Role = "user", Content = "faça algo",
            Timestamp = 1,
        });
        db.Users.Add(user);
        db.Chats.Add(chat);
        await db.SaveChangesAsync();
        return chat;
    }

    private RunAdvisorService Advisor(
        AppDbContext db,
        Func<ChatCompletionRequest, CancellationToken, Task<ProviderCompletion>>? complete = null,
        Func<IReadOnlyList<Tool>, string, string, BuiltinToolContext?, CancellationToken, Task<ToolExecutionOutcome>>? exec = null,
        params IBuiltinChatTool[] tools) =>
        new(db,
            Registry(tools),
            new ModelRoleResolver(
                db, _ => Task.FromResult(new List<ModelInfo>()),
                NullLogger<ModelRoleResolver>.Instance),
            new ConfigService(db, TestCache.Create()),
            complete ?? ((_, _) => Task.FromResult(new ProviderCompletion("OK", [], "[]"))),
            exec ?? ((_, _, _, _, _) => Task.FromResult(new ToolExecutionOutcome("ran"))));

    // ---- allowlist read-only (RF-002) ----

    [Test]
    public void IsAdvisorTool_SomenteLeitura()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RunAdvisorService.IsAdvisorTool("file_read"), Is.True);
            Assert.That(RunAdvisorService.IsAdvisorTool("file_grep"), Is.True);
            Assert.That(RunAdvisorService.IsAdvisorTool("file_glob"), Is.True);
            Assert.That(RunAdvisorService.IsAdvisorTool("lsp_definition"), Is.True);
            Assert.That(RunAdvisorService.IsAdvisorTool("file_write"), Is.False);
            Assert.That(RunAdvisorService.IsAdvisorTool("shell_exec"), Is.False);
            Assert.That(RunAdvisorService.IsAdvisorTool("file_list"), Is.False);
        });
    }

    [Test]
    public void BareName_StripPrefixos()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RunAdvisorService.BareName("builtin_file_read"),
                Is.EqualTo("file_read"));
            Assert.That(RunAdvisorService.BareName("builtin:file_grep"),
                Is.EqualTo("file_grep"));
            Assert.That(RunAdvisorService.BareName("file_glob"),
                Is.EqualTo("file_glob"));
        });
    }

    // ---- conselho normalizado (RF-003) ----

    [Test]
    public void TruncateAdvice_OkOuVazioViraNull()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RunAdvisorService.TruncateAdvice("OK"), Is.Null);
            Assert.That(RunAdvisorService.TruncateAdvice("  ok  "), Is.Null);
            Assert.That(RunAdvisorService.TruncateAdvice("   "), Is.Null);
            Assert.That(RunAdvisorService.TruncateAdvice(null), Is.Null);
        });
    }

    [Test]
    public void TruncateAdvice_Capa500Chars()
    {
        var advice = RunAdvisorService.TruncateAdvice(new string('x', 900));
        Assert.That(advice, Has.Length.EqualTo(RunAdvisorService.AdviceChars));
    }

    // ---- gates de flag (RF-005) ----

    [Test]
    public async Task IsEnabled_ChatDesligado_RetornaFalse()
    {
        await using var db = NewDb();
        var chat = await SeedChatAsync(db, advisorEnabled: false);
        Assert.That(await Advisor(db).IsEnabledAsync(chat.Id, default), Is.False);
    }

    [Test]
    public async Task IsEnabled_ChatLigado_RetornaTrue()
    {
        await using var db = NewDb();
        var chat = await SeedChatAsync(db, advisorEnabled: true);
        Assert.That(await Advisor(db).IsEnabledAsync(chat.Id, default), Is.True);
    }

    [Test]
    public async Task IsEnabled_GateGlobalDesligado_RetornaFalse()
    {
        await using var db = NewDb();
        var chat = await SeedChatAsync(db, advisorEnabled: true);
        await new ConfigService(db, TestCache.Create())
            .SetAsync("advisor.enabled", false);
        Assert.That(await Advisor(db).IsEnabledAsync(chat.Id, default), Is.False);
    }

    // ---- revisão (AdviseAsync) ----

    [Test]
    public async Task Advise_ChatDesligado_NaoChamaProvider()
    {
        await using var db = NewDb();
        var chat = await SeedChatAsync(db, advisorEnabled: false);
        var called = false;
        var advice = await Advisor(db,
            complete: (_, _) =>
            {
                called = true;
                return Task.FromResult(new ProviderCompletion("dica", [], "[]"));
            }).AdviseAsync(RunFor(chat), "user_message", [], null, default);
        Assert.Multiple(() =>
        {
            Assert.That(advice, Is.Null);
            Assert.That(called, Is.False);
        });
    }

    [Test]
    public async Task Advise_OkDoModelo_DevolveNull()
    {
        await using var db = NewDb();
        var chat = await SeedChatAsync(db, advisorEnabled: true);
        var advice = await Advisor(db).AdviseAsync(
            RunFor(chat), "user_message", [], null, default);
        Assert.That(advice, Is.Null);
    }

    [Test]
    public async Task Advise_ConselhoDevolvidoETruncado()
    {
        await using var db = NewDb();
        var chat = await SeedChatAsync(db, advisorEnabled: true);
        var longo = new string('x', 600);
        var advice = await Advisor(db,
            complete: (_, _) => Task.FromResult(
                new ProviderCompletion(longo, [], "[]")))
            .AdviseAsync(RunFor(chat), "tool_burst", [], null, default);
        Assert.That(advice, Has.Length.EqualTo(RunAdvisorService.AdviceChars));
    }

    [Test]
    public async Task Advise_UsaModeloDoPapelAdvisor()
    {
        await using var db = NewDb();
        var chat = await SeedChatAsync(db, advisorEnabled: true);
        var user = await db.Users.FirstAsync();
        user.SettingsJson = JsonSerializer.Serialize(new
        {
            modelRoles = new { advisor = new[] { "modelo-advisor" } },
        });
        await db.SaveChangesAsync();

        string? modeloUsado = null;
        var advisor = new RunAdvisorService(
            db,
            Registry(),
            new ModelRoleResolver(
                db,
                _ => Task.FromResult(new List<ModelInfo>
                {
                    new("modelo-advisor", "adv", "ollama", null, null),
                }),
                NullLogger<ModelRoleResolver>.Instance),
            new ConfigService(db, TestCache.Create()),
            (req, _) =>
            {
                modeloUsado = req.Model;
                return Task.FromResult(new ProviderCompletion("OK", [], "[]"));
            },
            (_, _, _, _, _) => Task.FromResult(new ToolExecutionOutcome("ran")));
        await advisor.AdviseAsync(RunFor(chat), "user_message", [], null, default);
        Assert.That(modeloUsado, Is.EqualTo("modelo-advisor"));
    }

    [Test]
    public async Task Advise_ToolForaDoSubset_ENegadaSemExecutar()
    {
        await using var db = NewDb();
        var chat = await SeedChatAsync(db, advisorEnabled: true);
        var executou = false;
        var calls = 0;
        var bctx = new BuiltinToolContext("u", chat.Id, "r", "/tmp", "/tmp");
        var advice = await Advisor(db,
            complete: (req, _) =>
            {
                calls++;
                // Primeira rodada pede uma tool mutável — deve ser negada
                // sem chegar no executor; segunda devolve o conselho.
                return Task.FromResult(calls == 1
                    ? new ProviderCompletion("",
                        [new ProviderToolCall("c1", "builtin_file_write", "{}")],
                        "[]")
                    : new ProviderCompletion("pare o loop", [], "[]"));
            },
            exec: (_, _, _, _, _) =>
            {
                executou = true;
                return Task.FromResult(new ToolExecutionOutcome("ran"));
            },
            new StubTool("file_read"), new StubTool("file_write"))
            .AdviseAsync(RunFor(chat), "pause", [], bctx, default);
        Assert.Multiple(() =>
        {
            Assert.That(executou, Is.False, "tool mutável não pode executar");
            Assert.That(advice, Is.EqualTo("pare o loop"));
        });
    }

    [Test]
    public async Task Advise_ToolDoSubset_ExecutaERetorna()
    {
        await using var db = NewDb();
        var chat = await SeedChatAsync(db, advisorEnabled: true);
        var chamadas = new List<string>();
        var calls = 0;
        var bctx = new BuiltinToolContext("u", chat.Id, "r", "/tmp", "/tmp");
        var advice = await Advisor(db,
            complete: (req, _) =>
            {
                calls++;
                return Task.FromResult(calls == 1
                    ? new ProviderCompletion("",
                        [new ProviderToolCall("c1", "builtin_file_grep", "{\"q\":\"x\"}")],
                        "[]")
                    : new ProviderCompletion("nada de errado", [], "[]"));
            },
            exec: (_, name, _, _, _) =>
            {
                chamadas.Add(name);
                return Task.FromResult(new ToolExecutionOutcome("hit"));
            },
            new StubTool("file_grep"))
            .AdviseAsync(RunFor(chat), "tool_burst", [], bctx, default);
        Assert.Multiple(() =>
        {
            Assert.That(chamadas, Is.EqualTo(new[] { "builtin_file_grep" }));
            Assert.That(advice, Is.EqualTo("nada de errado"));
        });
    }
}
