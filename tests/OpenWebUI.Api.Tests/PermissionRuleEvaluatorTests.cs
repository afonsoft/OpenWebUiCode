using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// RF-001/RF-002 do SPEC-20261018-permissions-v2: avaliador de regras
/// ordenadas do usuário (last-match-wins) e consulta às aprovações
/// duráveis por workdir sobre SQLite.
/// </summary>
[TestFixture, IsolateEnvironment]
public class PermissionRuleEvaluatorTests
{
    private string _dbPath = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-rules-{Guid.NewGuid():N}.db");
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

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        return new AppDbContext(options);
    }

    private PermissionRuleEvaluator CreateEvaluator(AppDbContext db) => new(db);

    private static UserPermissionRule Rule(
        string action, string resource, string effect, int pos) => new()
        {
            UserId = "u1", Action = action, Resource = resource,
            Effect = effect, Position = pos,
        };

    [Test]
    public async Task EvaluateAsync_EmptyRules_ReturnsNull()
    {
        await using var db = CreateContext();
        Assert.That(await CreateEvaluator(db)
            .EvaluateAsync("u1", "shell", ["shell_exec"]), Is.Null);
    }

    [Test]
    public async Task EvaluateAsync_OnlyOthersRules_ReturnsNull()
    {
        await using var db = CreateContext();
        db.UserPermissionRules.Add(Rule("shell", "*", "deny", 1));
        db.UserPermissionRules.Add(new UserPermissionRule
        {
            UserId = "u2", Action = "read", Resource = "*", Effect = "deny",
        });
        await db.SaveChangesAsync();
        Assert.That(await CreateEvaluator(db)
            .EvaluateAsync("u1", "read", ["file_read"]), Is.Null);
    }

    [Test]
    public async Task EvaluateAsync_LastMatchWins()
    {
        await using var db = CreateContext();
        db.UserPermissionRules.AddRange(
            Rule("shell", "shell_exec:*", "allow", 1),
            Rule("shell", "shell_exec:rm *", "deny", 2));
        await db.SaveChangesAsync();

        var eval = CreateEvaluator(db);
        // rm casa a deny depois da allow → deny.
        Assert.That(await eval.EvaluateAsync("u1", "shell",
            ["shell_exec", "shell_exec:rm -rf"]), Is.EqualTo("deny"));
        // ls casa só a allow.
        Assert.That(await eval.EvaluateAsync("u1", "shell",
            ["shell_exec", "shell_exec:ls"]), Is.EqualTo("allow"));
    }

    [Test]
    public async Task EvaluateAsync_DenyRuleBeatsSavedApproval_OrderIndependente()
    {
        await using var db = CreateContext();
        db.UserPermissionRules.Add(Rule("shell", "shell_exec:git push*", "deny", 1));
        db.SavedApprovals.Add(new SavedApproval
        {
            UserId = "u1", Workdir = "/repo", Tool = "shell_exec",
            ArgPattern = "git push*", Decision = "allow",
        });
        await db.SaveChangesAsync();

        var eval = CreateEvaluator(db);
        Assert.That(await eval.EvaluateAsync("u1", "shell",
            ["shell_exec", "shell_exec:git push origin"]), Is.EqualTo("deny"));
        Assert.That(await eval.SavedDecisionAsync(
            "u1", "/repo", "shell_exec", "git push origin"), Is.True);
    }

    [Test]
    public async Task EvaluateAsync_GlobPatterns()
    {
        await using var db = CreateContext();
        db.UserPermissionRules.AddRange(
            Rule("read", "*", "allow", 1),
            Rule("edit", "file_write:src/**", "ask", 2),
            Rule("external_directory", "/etc/**", "deny", 3),
            Rule("mcp", "mcp_fs*", "allow", 4));
        await db.SaveChangesAsync();

        var eval = CreateEvaluator(db);
        Assert.That(await eval.EvaluateAsync("u1", "read", ["file_read:main.cs"]),
            Is.EqualTo("allow"));
        Assert.That(await eval.EvaluateAsync("u1", "edit",
            ["file_write", "file_write:src/x.cs"]), Is.EqualTo("ask"));
        Assert.That(await eval.EvaluateAsync("u1", "external_directory",
            ["/etc/passwd"]), Is.EqualTo("deny"));
        Assert.That(await eval.EvaluateAsync("u1", "mcp",
            ["mcp_fs_read"]), Is.EqualTo("allow"));
    }

    [Test]
    public async Task SavedDecisionAsync_ScopePorWorkdirETool()
    {
        await using var db = CreateContext();
        db.SavedApprovals.AddRange(
            new SavedApproval
            {
                UserId = "u1", Workdir = "/repo", Tool = "file_write",
                ArgPattern = "*", Decision = "allow",
            },
            new SavedApproval
            {
                UserId = "u1", Workdir = "/repo", Tool = "shell_exec",
                ArgPattern = "cargo build*", Decision = "deny",
            });
        await db.SaveChangesAsync();

        var eval = CreateEvaluator(db);
        // allow global da file_write aplica no workdir.
        Assert.That(await eval.SavedDecisionAsync(
            "u1", "/repo", "file_write", "x.cs"), Is.True);
        // deny de shell_exec: cargo build.
        Assert.That(await eval.SavedDecisionAsync(
            "u1", "/repo", "shell_exec", "cargo build"), Is.False);
        // Tool sem regra → null.
        Assert.That(await eval.SavedDecisionAsync(
            "u1", "/repo", "file_read", "x"), Is.Null);
        // Outro workdir → null.
        Assert.That(await eval.SavedDecisionAsync(
            "u1", "/outro", "shell_exec", "cargo build"), Is.Null);
        // Outro user → null.
        Assert.That(await eval.SavedDecisionAsync(
            "u2", "/repo", "file_write", "x"), Is.Null);
        // Arg glob casa prefixo.
        Assert.That(await eval.SavedDecisionAsync(
            "u1", "/repo", "shell_exec", "cargo build --release"), Is.False);
    }

    [Test]
    public void ActionOf_MapasDeFerramenta()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PermissionRuleEvaluator.ActionOf("file_read"), Is.EqualTo("read"));
            Assert.That(PermissionRuleEvaluator.ActionOf("file_list"), Is.EqualTo("read"));
            Assert.That(PermissionRuleEvaluator.ActionOf("file_write"), Is.EqualTo("edit"));
            Assert.That(PermissionRuleEvaluator.ActionOf("file_edit"), Is.EqualTo("edit"));
            Assert.That(PermissionRuleEvaluator.ActionOf("apply_patch"), Is.EqualTo("edit"));
            Assert.That(PermissionRuleEvaluator.ActionOf("file_grep"), Is.EqualTo("grep"));
            Assert.That(PermissionRuleEvaluator.ActionOf("file_glob"), Is.EqualTo("glob"));
            Assert.That(PermissionRuleEvaluator.ActionOf("shell_exec"), Is.EqualTo("shell"));
            Assert.That(PermissionRuleEvaluator.ActionOf("code_interpreter"), Is.EqualTo("shell"));
            Assert.That(PermissionRuleEvaluator.ActionOf("delegate_task"), Is.EqualTo("subagent"));
            Assert.That(PermissionRuleEvaluator.ActionOf("ask_user"), Is.EqualTo("question"));
            Assert.That(PermissionRuleEvaluator.ActionOf("fetch_url"), Is.EqualTo("webfetch"));
            Assert.That(PermissionRuleEvaluator.ActionOf("web_search"), Is.EqualTo("websearch"));
            Assert.That(PermissionRuleEvaluator.ActionOf("mcp_fs_read"), Is.EqualTo("mcp"));
            Assert.That(PermissionRuleEvaluator.ActionOf("xqualquer"), Is.EqualTo("xqualquer"));
        });
    }
}
