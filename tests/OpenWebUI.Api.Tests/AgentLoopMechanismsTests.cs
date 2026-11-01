using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using NUnit.Framework;

using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Context;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Mecanismos do agent-prompt-pack (SPEC-20261031): stall watch,
/// extend_context, instant_context e unseen_messages.
/// </summary>
[TestFixture]
public class AgentLoopMechanismsTests
{
    private static AppDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"agentloop-{Guid.NewGuid():N}")
            .Options;
        var db = new AppDbContext(opts);
        db.Database.EnsureCreated();
        return db;
    }

    private static BuiltinToolContext Ctx(
        string workspace, string runId = "run-1") =>
        new("u-1", "chat-1", runId, workspace, "/tmp/uploads",
            "m", []);

    // RF-005: stall_watch — K iterações idênticas → nudge; repete → escala.
    [Test]
    public void StallWatch_NudgeNoLimiarEEscalaNaRepeticao()
    {
        var watch = new StallWatchdog();
        string? nudge = null;
        for (var i = 0; i < 4; i++)
        {
            nudge = watch.Record("r1", "file_read", "{}", "same");
            Assert.That(nudge, Is.Null);
        }
        nudge = watch.Record("r1", "file_read", "{}", "same", threshold: 4);
        Assert.That(nudge, Does.Contain("stall watch").And.Contain("file_read"));

        // Mais 4 repetições idênticas → nudge escalado.
        for (var i = 0; i < 4; i++)
        {
            nudge = watch.Record("r1", "file_read", "{}", "same");
        }
        Assert.That(nudge, Does.Contain("stall #"));

        // Output diferente → reseta a streak.
        nudge = watch.Record("r1", "file_read", "{}", "different");
        Assert.That(nudge, Is.Null);
        Assert.That(watch.StreakOf("r1"), Is.EqualTo(0));
    }

    // RF-004: extend_context — granted até o teto, depois at_ceiling;
    // TryConsume só consome concessão pendente.
    [Test]
    public void ExtendContext_GrantAteTetoEConsomeUmaVez()
    {
        var budget = new ExtendContextBudget();
        Assert.That(budget.Request("r1", 2), Is.EqualTo(ExtendContextBudget.Status.Granted));
        Assert.That(budget.Request("r1", 2), Is.EqualTo(ExtendContextBudget.Status.Granted));
        Assert.That(budget.Request("r1", 2), Is.EqualTo(ExtendContextBudget.Status.AtCeiling));
        Assert.That(budget.TryConsume("r1"), Is.True);
        Assert.That(budget.TryConsume("r1"), Is.True);
        Assert.That(budget.TryConsume("r1"), Is.False);

        // ceiling=0 → limit (desligado).
        Assert.That(budget.Request("r2", 0), Is.EqualTo(ExtendContextBudget.Status.Limit));
    }

    // RF-004 (tool): resposta JSON com status granted/at_ceiling.
    [Test]
    public async Task ExtendContextTool_RespondeStatusJson()
    {
        var tool = new ExtendContextBuiltinTool(
            new ExtendContextBudget(),
            new ConfigurationBuilder().Build());
        var result = await tool.ExecuteAsync(
            System.Text.Json.JsonDocument.Parse("{}").RootElement,
            Ctx("/tmp"), CancellationToken.None);
        Assert.That(result.Text, Does.Contain("\"status\":\"granted\""));
    }

    // RF-003: instant_context — comandos allowlisted → XML estrito com
    // file+range; verbo fora da allowlist vai para o canal de erro.
    [Test]
    public async Task InstantContext_RgEReadfileGeramXmlRestrito()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"ic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(ws, "a.txt"), "one\ntwo needle\nthree\n");
            var tool = new InstantContextBuiltinTool();
            var args = System.Text.Json.JsonDocument.Parse(
                """{"commands":["rg needle","readfile a.txt 1 1","rm -rf /"]}""")
                .RootElement;
            var result = await tool.ExecuteAsync(args, Ctx(ws), CancellationToken.None);
            Assert.That(result.Text, Does.StartWith("<ANSWER>"));
            Assert.That(result.Text, Does.Contain("two needle"));
            Assert.That(result.Text, Does.Contain("<range a="));
            Assert.That(result.Text, Does.Contain("comando não permitido"));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    // RF-003: path fora do workspace é rejeitado (boundary do workdir).
    [Test]
    public async Task InstantContext_RejeitaPathForaDoWorkspace()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"ic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        try
        {
            var tool = new InstantContextBuiltinTool();
            var args = System.Text.Json.JsonDocument.Parse(
                """{"commands":["readfile ../outside.txt 1 2"]}""").RootElement;
            var result = await tool.ExecuteAsync(args, Ctx(ws), CancellationToken.None);
            Assert.That(result.Text, Does.Contain("erro"));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    // RF-002: unseen_messages — lista pendentes com timestamp e marca
    // como vistas (promoted); segunda chamada devolve "no new messages".
    [Test]
    public async Task UnseenMessages_ListaEMarcaComoVistas()
    {
        var db = NewDb();
        db.ChatRunSteers.AddRange(
            new ChatRunSteer
            {
                Id = "s1", RunId = "run-1", ChatId = "chat-1",
                Content = "primeira", Mode = "steer", Status = "pending",
                Timestamp = 1_700_000_000,
            },
            new ChatRunSteer
            {
                Id = "s2", RunId = "run-1", ChatId = "chat-1",
                Content = "segunda", Mode = "queue", Status = "pending",
                Timestamp = 1_700_000_100,
            });
        await db.SaveChangesAsync();

        var tool = new UnseenMessagesBuiltinTool(db);
        var args = System.Text.Json.JsonDocument.Parse("{}").RootElement;
        var result = await tool.ExecuteAsync(args, Ctx("/tmp"), CancellationToken.None);
        Assert.That(result.Text, Does.Contain("primeira").And.Contain("segunda"));
        Assert.That(result.Text, Does.Contain("(steer)"));

        var again = await tool.ExecuteAsync(args, Ctx("/tmp"), CancellationToken.None);
        Assert.That(again.Text, Is.EqualTo("no new messages"));
    }
}
