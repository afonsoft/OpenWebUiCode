using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using NUnit.Framework;

using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261123-context-usage-report: relatório por seção do request
/// persistido da run + aviso acima de 80% do threshold efetivo.
/// </summary>
[TestFixture]
public sealed class ContextUsageReportTests
{
    private static AppDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"ctxusage-{Guid.NewGuid():N}")
            .Options;
        var db = new AppDbContext(opts);
        db.Database.EnsureCreated();
        return db;
    }

    private static BuiltinToolContext Ctx(string runId) => new(
        UserId: "u1", ChatId: "c1", RunId: runId,
        WorkspacePath: "/tmp/ws", UploadDir: "/tmp/up", Model: "m1");

    private static async Task<(AppDbContext Db, ContextUsageBuiltinTool Tool, ConfigService Config)>
        NewToolAsync(string requestJson, string model = "m1")
    {
        var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U", Email = "u@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        db.ChatRuns.Add(new ChatRun
        {
            Id = "run-1", ChatId = "c1", UserId = "u1", Model = model,
            RequestJson = requestJson,
        });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        return (db, new ContextUsageBuiltinTool(db, config), config);
    }

    [Test]
    public async Task Usage_RetornaSecoesCorretas()
    {
        var request = """
            {"model":"m1","messages":[
              {"role":"system","content":"AAAA"},
              {"role":"user","content":"BBBB"},
              {"role":"assistant","content":"CCCC"},
              {"role":"tool","content":"DDDD"}
            ],"tools":[{"type":"function","function":{"name":"x"}}]}
            """;
        var (db, tool, _) = await NewToolAsync(request);
        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("{}").RootElement, Ctx("run-1"), CancellationToken.None);
        using var payload = JsonDocument.Parse(result.Text);
        var root = payload.RootElement;
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("estimating").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("totalTokens").GetInt32(), Is.GreaterThan(0));
            var sections = root.GetProperty("sections").EnumerateArray()
                .ToDictionary(s => s.GetProperty("name").GetString()!,
                    s => s.GetProperty("tokens").GetInt32());
            // Cada seção recebeu os chars certos (4 chars → 1 token).
            Assert.That(sections["system_prompt"], Is.EqualTo(1));
            Assert.That(sections["history"], Is.EqualTo(2)); // user+assistant
            Assert.That(sections["pending_tool_results"], Is.EqualTo(1));
            Assert.That(sections["tools"], Is.GreaterThan(0));
            // Soma das seções == total reportado.
            Assert.That(sections.Values.Sum(),
                Is.EqualTo(root.GetProperty("totalTokens").GetInt32()));
        });
        await db.DisposeAsync();
    }

    [Test]
    public async Task Usage_Threshold80_Avisa()
    {
        // system com 800k chars → 200k tokens ≈ 89% do threshold 224k.
        var big = new string('A', 800_000);
        var request = $$"""
            {"model":"m1","messages":[{"role":"system","content":"{{big}}"}]}
            """;
        var (db, tool, _) = await NewToolAsync(request);
        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("{}").RootElement, Ctx("run-1"), CancellationToken.None);
        using var payload = JsonDocument.Parse(result.Text);
        Assert.Multiple(() =>
        {
            Assert.That(payload.RootElement.GetProperty("percentUsed").GetInt32(),
                Is.GreaterThan(80));
            Assert.That(payload.RootElement.GetProperty("warning").GetString(),
                Does.Contain("80%"));
        });
        await db.DisposeAsync();
    }

    [Test]
    public async Task Usage_ThresholdPorModelo_Vence()
    {
        var (db, tool, config) = await NewToolAsync(
            """{"model":"m1","messages":[{"role":"user","content":"AAAA"}]}""");
        await config.SetAsync("compaction:m1:threshold_tokens", 64_000L);
        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("{}").RootElement, Ctx("run-1"), CancellationToken.None);
        using var payload = JsonDocument.Parse(result.Text);
        Assert.That(payload.RootElement.GetProperty("maxTokens").GetInt32(),
            Is.EqualTo(64_000));
        await db.DisposeAsync();
    }

    [Test]
    public async Task Usage_SemRun_FallbackEstimating()
    {
        var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        var tool = new ContextUsageBuiltinTool(db, config);
        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("{}").RootElement,
            Ctx("run-inexistente"), CancellationToken.None);
        Assert.That(result.Text, Does.Contain("\"estimating\":true"));
        await db.DisposeAsync();
    }
}
