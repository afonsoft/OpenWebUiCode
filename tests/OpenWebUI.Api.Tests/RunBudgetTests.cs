using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes das políticas de budget (SPEC-20261023-run-cost-budgets
/// RF-002/003/004): precedência user→grupo→global, gate pré-run por
/// mês/tier e classificação de tier por preço blended.
/// </summary>
[TestFixture, IsolateEnvironment]
public class RunBudgetTests
{
    private string _dbPath = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-budget-{Guid.NewGuid():N}.db");
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

    private static BudgetPolicyService Budgets(AppDbContext db) =>
        new(db, new ConfigService(db, TestCache.Create()),
            new RunUsageLedgerService(
                db, NullLogger<RunUsageLedgerService>.Instance),
            NullLogger<BudgetPolicyService>.Instance);

    private static BudgetPolicy Policy(
        string subject, double? usdRun = null, double? usdMonth = null,
        long? tokensMonth = null, int? maxTier = null) => new()
        {
            Subject = subject, UsdPerRun = usdRun,
            UsdPerMonth = usdMonth, TokensPerMonth = tokensMonth,
            MaxModelTier = maxTier,
        };

    private void SeedUsage(string userId, double costUsd, long tokens)
    {
        using var db = CreateContext();
        var start = new DateTimeOffset(
            DateTimeOffset.UtcNow.Year, DateTimeOffset.UtcNow.Month, 1,
            0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        db.RunUsageRecords.Add(new RunUsageRecord
        {
            RunId = Guid.NewGuid().ToString(), ChatId = "c",
            UserId = userId, PromptTokens = tokens / 2,
            CompletionTokens = tokens / 2, CostUsd = costUsd,
            CreatedAt = start + 60,
        });
        db.SaveChanges();
    }

    // Covers RF-002: sem policy = sem teto (comportamento atual).
    [Test]
    public async Task CheckStart_NoPolicy_Allows()
    {
        await using var db = CreateContext();
        var gate = await Budgets(db).CheckStartAsync(
            "u1", "gpt-5", CancellationToken.None);

        Assert.That(gate.Allowed, Is.True);
    }

    // Covers RF-002: mês já acima do cap de USD → nega no gate.
    [Test]
    public async Task CheckStart_MonthUsdExceeded_Denies()
    {
        await using var db = CreateContext();
        db.BudgetPolicies.Add(Policy("user:u1", usdMonth: 5.0));
        await db.SaveChangesAsync();
        SeedUsage("u1", 6.0, 1000);

        var gate = await Budgets(db).CheckStartAsync(
            "u1", "model-x", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(gate.Allowed, Is.False);
            Assert.That(gate.Code, Is.EqualTo("budget_exceeded"));
        });
    }

    // Covers RF-002: mês acima do cap de tokens → nega no gate.
    [Test]
    public async Task CheckStart_MonthTokensExceeded_Denies()
    {
        await using var db = CreateContext();
        db.BudgetPolicies.Add(Policy("user:u1", tokensMonth: 100));
        await db.SaveChangesAsync();
        SeedUsage("u1", 0, 200);

        var gate = await Budgets(db).CheckStartAsync(
            "u1", "model-x", CancellationToken.None);

        Assert.That(gate.Allowed, Is.False);
    }

    // Covers RF-002: policy de grupo vale quando não há de usuário; a de
    // usuário vence a do grupo (precedência por campo).
    [Test]
    public async Task Effective_UserOverridesGroup_GroupBeatsGlobal()
    {
        await using var db = CreateContext();
        db.Users.Add(new User { Id = "u1", Email = "u1@t.dev" });
        db.Groups.Add(new Group { Id = "g1", Name = "Time" });
        db.GroupMembers.Add(new GroupMember
        { GroupId = "g1", UserId = "u1" });
        db.BudgetPolicies.AddRange(
            Policy("global", usdMonth: 50, maxTier: 3),
            Policy("group:g1", usdMonth: 20),
            Policy("user:u1", usdRun: 1.5));
        await db.SaveChangesAsync();

        var limits = await Budgets(db).EffectiveAsync(
            "u1", CancellationToken.None);

        Assert.Multiple(() =>
        {
            // usdPerRun vem da policy do usuário.
            Assert.That(limits.UsdPerRun, Is.EqualTo(1.5));
            // usdPerMonth: usuário não define → grupo (20) vence global.
            Assert.That(limits.UsdPerMonth, Is.EqualTo(20));
            // maxTier: só o global define.
            Assert.That(limits.MaxModelTier, Is.EqualTo(3));
        });
    }

    // Covers RF-002: menor valor entre grupos quando o usuário está em
    // vários com caps distintos.
    [Test]
    public async Task Effective_MultipleGroups_TakesMinimum()
    {
        await using var db = CreateContext();
        db.Users.Add(new User { Id = "u1", Email = "u1@t.dev" });
        db.Groups.AddRange(
            new Group { Id = "g1", Name = "A" },
            new Group { Id = "g2", Name = "B" });
        db.GroupMembers.AddRange(
            new GroupMember { GroupId = "g1", UserId = "u1" },
            new GroupMember { GroupId = "g2", UserId = "u1" });
        db.BudgetPolicies.AddRange(
            Policy("group:g1", usdMonth: 30),
            Policy("group:g2", usdMonth: 10));
        await db.SaveChangesAsync();

        var limits = await Budgets(db).EffectiveAsync(
            "u1", CancellationToken.None);

        Assert.That(limits.UsdPerMonth, Is.EqualTo(10));
    }

    // Covers RF-004: modelo com preço blended ≥ limiar alto → tier 3;
    // barato → tier 0; sem preço → null (elegível em qualquer tier).
    [Test]
    public async Task ModelTier_ClassifiesByBlendedCost()
    {
        await using var db = CreateContext();
        db.ModelEntries.AddRange(
            new ModelEntry
            {
                Id = "cheap", UserId = "public",
                MetaJson = """{"cost":{"input":0.1,"output":0.2}}""",
            },
            new ModelEntry
            {
                Id = "pricey", UserId = "public",
                MetaJson = """{"cost":{"input":30,"output":60}}""",
            },
            new ModelEntry { Id = "local", UserId = "public" });
        await db.SaveChangesAsync();

        var budgets = Budgets(db);
        var cheap = await budgets.ModelTierAsync(
            "cheap", null, CancellationToken.None);
        var pricey = await budgets.ModelTierAsync(
            "pricey", null, CancellationToken.None);
        var local = await budgets.ModelTierAsync(
            "local", null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(cheap, Is.EqualTo(0));
            Assert.That(pricey, Is.EqualTo(3));
            Assert.That(local, Is.Null);
        });
    }

    // Covers RF-004: policy com maxTier=0 nega modelo caro no gate;
    // modelo sem preço passa.
    [Test]
    public async Task CheckStart_MaxTier_DeniesPriceyModel()
    {
        await using var db = CreateContext();
        db.BudgetPolicies.Add(Policy("user:u1", maxTier: 0));
        db.ModelEntries.AddRange(
            new ModelEntry
            {
                Id = "pricey", UserId = "public",
                MetaJson = """{"cost":{"input":30,"output":60}}""",
            },
            new ModelEntry { Id = "local", UserId = "public" });
        await db.SaveChangesAsync();

        var budgets = Budgets(db);
        var denied = await budgets.CheckStartAsync(
            "u1", "pricey", CancellationToken.None);
        var allowedLocal = await budgets.CheckStartAsync(
            "u1", "local", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(denied.Allowed, Is.False);
            Assert.That(denied.Code, Is.EqualTo("no_model_in_tier"));
            Assert.That(allowedLocal.Allowed, Is.True);
        });
    }

    // Covers RF-004: tier herdado do pai restringe além da policy — min
    // dos dois vence.
    [Test]
    public async Task CheckStart_InheritedTier_NarrowsPolicy()
    {
        await using var db = CreateContext();
        db.BudgetPolicies.Add(Policy("user:u1", maxTier: 2));
        db.ModelEntries.Add(new ModelEntry
        {
            Id = "mid", UserId = "public",
            MetaJson = """{"cost":{"input":10,"output":10}}""",
        });
        await db.SaveChangesAsync();

        // Policy permite tier ≤2, mas o herdado (0) afunila → nega mid(1?2).
        var gate = await Budgets(db).CheckStartAsync(
            "u1", "mid", CancellationToken.None, inheritedMaxTier: 0);

        Assert.Multiple(() =>
        {
            Assert.That(gate.Allowed, Is.False);
            Assert.That(gate.Code, Is.EqualTo("no_model_in_tier"));
        });
    }

    // Covers RF-002/RF-004: kill-switch desligado libera tudo.
    [Test]
    public async Task CheckStart_Disabled_Allows()
    {
        await using var db = CreateContext();
        db.BudgetPolicies.Add(Policy("user:u1", usdMonth: 0.001));
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("agent.budgets.enabled", false,
            CancellationToken.None);
        SeedUsage("u1", 999, 999999);

        var gate = await Budgets(db).CheckStartAsync(
            "u1", "any", CancellationToken.None);

        Assert.That(gate.Allowed, Is.True);
    }

    private static ModelRoleResolver Resolver(AppDbContext db,
        BudgetPolicyService budgets, params string[] catalogIds) =>
        new(db, new ConfigService(db, TestCache.Create()),
            _ => Task.FromResult(catalogIds
                .Select(id => new Application.Contracts.ModelInfo(
                    id, id, "ollama", "x")).ToList()),
            NullLogger<ModelRoleResolver>.Instance, budgets);

    private void SeedPriced(string id, double input, double output)
    {
        using var db = CreateContext();
        db.ModelEntries.Add(new ModelEntry
        {
            Id = id, UserId = "public",
            MetaJson = $"{{\"cost\":{{\"input\":{input},\"output\":{output}}}}}",
        });
        db.SaveChanges();
    }

    // Covers RF-004/AC: maxTier=1 na resolução escolhe o melhor ≤1 da
    // cadeia do papel — o caro é pulado, não o primeiro.
    [Test]
    public async Task ResolveForTier_PicksBestWithinTier()
    {
        await using var db = CreateContext();
        db.Users.Add(new User
        {
            Id = "u1", Email = "u1@t.dev",
            SettingsJson = """{"modelRoles":{"tasks":["pricey","cheap"]}}""",
        });
        await db.SaveChangesAsync();
        SeedPriced("pricey", 30, 60);   // blended 45 → tier 3
        SeedPriced("cheap", 0.1, 0.2);  // blended 0.15 → tier 0

        var budgets = Budgets(db);
        var resolver = Resolver(db, budgets, "pricey", "cheap");

        var resolved = await resolver.ResolveForTierAsync(
            "tasks", "u1", "fallback", 1, CancellationToken.None);

        Assert.That(resolved, Is.EqualTo("cheap"));
    }

    // Covers RF-004/AC: nenhum candidato no tier → null (o caller emite
    // no_model_in_tier, nunca upgrade silencioso).
    [Test]
    public async Task ResolveForTier_NoneEligible_ReturnsNull()
    {
        await using var db = CreateContext();
        db.Users.Add(new User
        {
            Id = "u1", Email = "u1@t.dev",
            SettingsJson = """{"modelRoles":{"tasks":["pricey"]}}""",
        });
        await db.SaveChangesAsync();
        SeedPriced("pricey", 30, 60);
        SeedPriced("fallback", 50, 50);

        var budgets = Budgets(db);
        var resolver = Resolver(db, budgets, "pricey", "fallback");

        var resolved = await resolver.ResolveForTierAsync(
            "tasks", "u1", "fallback", 0, CancellationToken.None);

        Assert.That(resolved, Is.Null);
    }

    // Covers RF-004: maxTier null preserva o comportamento original da
    // resolução (primeiro da cadeia conhecido).
    [Test]
    public async Task ResolveForTier_NoTier_BehavesLikeResolve()
    {
        await using var db = CreateContext();
        db.Users.Add(new User
        {
            Id = "u1", Email = "u1@t.dev",
            SettingsJson = """{"modelRoles":{"tasks":["pricey"]}}""",
        });
        await db.SaveChangesAsync();

        var budgets = Budgets(db);
        var resolver = Resolver(db, budgets, "pricey");

        var resolved = await resolver.ResolveForTierAsync(
            "tasks", "u1", "fallback", null, CancellationToken.None);

        Assert.That(resolved, Is.EqualTo("pricey"));
    }

    // Covers RF-002: warnAt default 80 e clamped; mês vazio soma zero.
    [Test]
    public async Task WarnAndMonthSafe_Defaults()
    {
        await using var db = CreateContext();
        var budgets = Budgets(db);

        var warn = await budgets.WarnAtPercentAsync(CancellationToken.None);
        var month = await budgets.MonthTotalsSafeAsync(
            "nobody", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(warn, Is.EqualTo(80));
            Assert.That(month.CostUsd, Is.EqualTo(0));
            Assert.That(month.Runs, Is.EqualTo(0));
        });
    }
}
