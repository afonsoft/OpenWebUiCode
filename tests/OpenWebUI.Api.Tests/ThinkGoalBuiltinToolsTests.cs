using System.Text.Json;
using NUnit.Framework;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261128-think-goal-tools: tools <c>builtin think</c> e
/// <c>builtin goal</c> — nota de raciocínio privada e objetivo declarado
/// da run (estilo Devin/opencode), ambas read-only e sem aprovação.
/// </summary>
[TestFixture]
public sealed class ThinkGoalBuiltinToolsTests
{
    private static readonly BuiltinToolContext Ctx =
        new("u1", "c1", "r1", "/tmp/ws", "/tmp/up");

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task Think_RegistraNotaEDevolvePayload()
    {
        var tool = new ThinkBuiltinTool();
        var r = await tool.ExecuteAsync(
            Args("""{"thought":"analisar antes de mutar"}"""), Ctx, default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("registrada"));
            Assert.That(r.Result, Is.Not.Null);
            Assert.That(r.Refused, Is.False);
        });
        var json = JsonSerializer.Serialize(r.Result);
        Assert.That(json, Does.Contain("analisar antes de mutar"));
    }

    [Test]
    public async Task Think_SemThought_PedeParametro()
    {
        var tool = new ThinkBuiltinTool();
        var r = await tool.ExecuteAsync(Args("{}"), Ctx, default);
        Assert.That(r.Text, Does.Contain("thought"));
    }

    [Test]
    public async Task Think_Capa2000Chars()
    {
        var tool = new ThinkBuiltinTool();
        var r = await tool.ExecuteAsync(
            Args($$"""{"thought":"{{new string('x', 3000)}}"}"""), Ctx, default);
        var json = JsonSerializer.Serialize(r.Result);
        Assert.That(JsonDocument.Parse(json).RootElement
            .GetProperty("thought").GetString()!.Length, Is.LessThan(2100));
    }

    [Test]
    public async Task Goal_DefineObjetivoEDevolvePayload()
    {
        var tool = new GoalBuiltinTool();
        var r = await tool.ExecuteAsync(
            Args("""{"goal":"migrar o layout mobile"}"""), Ctx, default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("migrar o layout mobile"));
            Assert.That(r.Result, Is.Not.Null);
        });
    }

    [Test]
    public async Task Goal_SemGoal_PedeParametro()
    {
        var tool = new GoalBuiltinTool();
        var r = await tool.ExecuteAsync(Args("""{"goal":"  "}"""), Ctx, default);
        Assert.That(r.Text, Does.Contain("goal"));
    }

    [Test]
    public void Ambas_SaoReadOnlyTier()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new ThinkBuiltinTool().Tier, Is.EqualTo(ApprovalTier.Read));
            Assert.That(new GoalBuiltinTool().Tier, Is.EqualTo(ApprovalTier.Read));
            Assert.That(new ThinkBuiltinTool().Name, Is.EqualTo("think"));
            Assert.That(new GoalBuiltinTool().Name, Is.EqualTo("goal"));
        });
    }
}
