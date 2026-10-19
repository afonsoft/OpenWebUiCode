using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do PolicyService (SPEC-20261018-policies-admin): match glob,
/// ordenação last-match-wins, auditoria de deny e enforcement nos
/// pontos tool.use / provider.use / model.use / integration.use.
/// </summary>
[TestFixture, IsolateEnvironment]
public class PolicyServiceTests
{
    private string _dbPath = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-policies-{Guid.NewGuid():N}.db");
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private static AdminPolicyStatement Rule(
        string action, string resource, int order = 0) => new()
        { Action = action, Resource = resource, Order = order };

    // Covers RF-001: match glob sobre o recurso concreto.
    [Test]
    public async Task MatchAsync_GlobMatch_DeniesMatchingResource()
    {
        await using var db = CreateContext();
        db.AdminPolicyStatements.Add(Rule(PolicyService.ActionToolUse, "shell:rm *"));
        await db.SaveChangesAsync();
        var policies = new PolicyService(db);

        var denied = await policies.MatchAsync(
            PolicyService.ActionToolUse, ["shell", "shell:rm -rf *"]);

        Assert.That(denied, Is.Not.Null);
        Assert.That(denied!.Resource, Is.EqualTo("shell:rm *"));
    }

    // Covers RF-001: glob não casa → permitido.
    [Test]
    public async Task MatchAsync_NoMatch_Allows()
    {
        await using var db = CreateContext();
        db.AdminPolicyStatements.Add(Rule(PolicyService.ActionToolUse, "shell:rm *"));
        await db.SaveChangesAsync();
        var policies = new PolicyService(db);

        var denied = await policies.MatchAsync(
            PolicyService.ActionToolUse, ["shell", "shell:ls *"]);

        Assert.That(denied, Is.Null);
    }

    // Covers RF-001: avaliação ordenada — último match vence.
    [Test]
    public async Task MatchAsync_Ordered_LastMatchWins()
    {
        await using var db = CreateContext();
        db.AdminPolicyStatements.Add(Rule(PolicyService.ActionToolUse, "shell:*", 1));
        db.AdminPolicyStatements.Add(Rule(PolicyService.ActionToolUse, "shell:git *", 2));
        await db.SaveChangesAsync();
        var policies = new PolicyService(db);

        var denied = await policies.MatchAsync(
            PolicyService.ActionToolUse, ["shell:git push*"]);

        Assert.That(denied!.Resource, Is.EqualTo("shell:git *"));
    }

    // Covers RF-001: match case-insensitive em ids.
    [Test]
    public void GlobMatch_CaseInsensitive()
    {
        Assert.That(PolicyService.GlobMatch("OpenAI/*", "openai/gpt-5"), Is.True);
        Assert.That(PolicyService.GlobMatch("mcp:files*", "mcp:filesystem"), Is.True);
        Assert.That(PolicyService.GlobMatch("exact", "exactly"), Is.False);
    }

    // Covers RF-002/AC: deny escreve auditoria com usuário e statement.
    [Test]
    public async Task DenyAsync_OnMatch_WritesAudit()
    {
        await using var db = CreateContext();
        var rule = Rule(PolicyService.ActionToolUse, "shell:*");
        db.AdminPolicyStatements.Add(rule);
        await db.SaveChangesAsync();
        var policies = new PolicyService(db);

        var denied = await policies.DenyAsync(
            PolicyService.ActionToolUse, ["shell", "shell:ls *"], "user-1");

        Assert.That(denied, Is.Not.Null);
        var audit = await db.PolicyDenials.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(audit.UserId, Is.EqualTo("user-1"));
            Assert.That(audit.Action, Is.EqualTo(PolicyService.ActionToolUse));
            Assert.That(audit.StatementId, Is.EqualTo(rule.Id));
            Assert.That(audit.Resource, Is.EqualTo("shell:ls *"));
        });
    }

    // Covers RF-002: sem match não escreve auditoria.
    [Test]
    public async Task DenyAsync_NoMatch_NoAudit()
    {
        await using var db = CreateContext();
        var policies = new PolicyService(db);
        var denied = await policies.DenyAsync(
            PolicyService.ActionToolUse, ["shell"], "user-1");
        Assert.That(denied, Is.Null);
        Assert.That(await db.PolicyDenials.CountAsync(), Is.Zero);
    }

    // Covers RF-003: provider.use nega provider inteiro; model.use nega modelo.
    [Test]
    public async Task ProviderAndModelDeny_MatchCompositeResources()
    {
        await using var db = CreateContext();
        db.AdminPolicyStatements.Add(Rule(PolicyService.ActionProviderUse, "openai/*"));
        db.AdminPolicyStatements.Add(Rule(PolicyService.ActionModelUse, "claude-opus*"));
        await db.SaveChangesAsync();
        var policies = new PolicyService(db);

        var p = await policies.MatchAsync(
            PolicyService.ActionProviderUse, ["openai", "openai/gpt-5"]);
        var m = await policies.MatchAsync(
            PolicyService.ActionModelUse, ["claude-opus-4.5", "anthropic/claude-opus-4.5"]);
        var ok = await policies.MatchAsync(
            PolicyService.ActionProviderUse, ["anthropic", "anthropic/claude"]);

        Assert.That(p, Is.Not.Null);
        Assert.That(m, Is.Not.Null);
        Assert.That(ok, Is.Null);
    }

    // Covers RF-004: integration.use por servidor específico.
    [Test]
    public async Task IntegrationDeny_PerServerScope()
    {
        await using var db = CreateContext();
        db.AdminPolicyStatements.Add(
            Rule(PolicyService.ActionIntegrationUse, "mcp:filesystem"));
        await db.SaveChangesAsync();
        var policies = new PolicyService(db);

        var denied = await policies.MatchAsync(
            PolicyService.ActionIntegrationUse, ["mcp:abc", "mcp:filesystem"]);
        var allowed = await policies.MatchAsync(
            PolicyService.ActionIntegrationUse, ["mcp:abc", "mcp:github"]);

        Assert.That(denied, Is.Not.Null);
        Assert.That(allowed, Is.Null);
    }

    // Covers AC: DeniedMessage nomeia a policy.
    [Test]
    public void DeniedMessage_NamesStatement()
    {
        var s = Rule(PolicyService.ActionToolUse, "shell:rm *");
        s.Note = "comando destrutivo";
        var msg = PolicyService.DeniedMessage(s, "shell");
        Assert.That(msg, Does.Contain("shell:rm *").And.Contain("comando destrutivo"));
    }
}
