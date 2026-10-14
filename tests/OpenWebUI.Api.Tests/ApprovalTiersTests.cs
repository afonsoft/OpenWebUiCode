using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do SPEC-20261013-approval-tiers: tiers declarados por tool
/// (RF-001 — <see cref="ApprovalTier"/> substitui o booleano), derivação
/// de tier para tools externas (MCP=write, código=arbitrário=exec,
/// builtin desconhecida=exec fail-closed), resolução de overrides do
/// usuário (RF-002 — strip dos prefixos, case-insensitive) e a
/// persistência em kv <c>user:{id}:tool-approvals</c>. O mapa real das
/// built-ins registradas em DI também é conferido (RF-006 — sem
/// regressão do gate).
/// </summary>
[TestFixture]
public class ApprovalTiersTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp() => _dbPath = TestInfra.NewDbPath("approval-tiers");

    [TearDown]
    public void TearDown() => TestInfra.DeleteDb(_dbPath);

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>Tool fake para exercitar o registry/tier sem serviços.</summary>
    private sealed class StubTool(string name, ApprovalTier tier)
        : IBuiltinChatTool
    {
        public string Name => name;
        public string Description => "stub";
        public string ParametersJson => "{\"type\":\"object\"}";
        public ApprovalTier Tier => tier;

        public Task<BuiltinToolResult> ExecuteAsync(
            JsonElement args, BuiltinToolContext context, CancellationToken ct) =>
            Task.FromResult(new BuiltinToolResult("ok"));
    }

    private static BuiltinToolRegistry Registry(params IBuiltinChatTool[] tools) =>
        new(tools, new ConfigurationBuilder().Build());

    private static Tool ToolOf(string url, string? code = null,
        bool requiresApproval = false) =>
        new() { Id = url, Name = url, SpecJson = "{}", Url = url,
            Code = code, RequiresApproval = requiresApproval };

    // ---- TierOf — derivação do tier efetivo (RF-003) ----

    [Test]
    public void TierOf_BuiltinUsaTierDeclarado()
    {
        var registry = Registry(
            new StubTool("leitor", ApprovalTier.Read),
            new StubTool("escritor", ApprovalTier.Write));

        Assert.Multiple(() =>
        {
            Assert.That(ToolApprovalPolicy.TierOf(
                ToolOf("builtin://leitor"), registry), Is.EqualTo(ApprovalTier.Read));
            Assert.That(ToolApprovalPolicy.TierOf(
                ToolOf("builtin://escritor"), registry), Is.EqualTo(ApprovalTier.Write));
        });
    }

    [Test]
    public void TierOf_BuiltinDesconhecidaFalhaFechada() =>
        // builtin://ghost: URL diz que é built-in mas o registry não a
        // conhece — fail-closed em Exec, nunca Read (RF-003).
        Assert.That(ToolApprovalPolicy.TierOf(
            ToolOf("builtin://ghost"), Registry()),
            Is.EqualTo(ApprovalTier.Exec));

    [Test]
    public void TierOf_CodigoEhExecMcpEhWrite() =>
        Assert.Multiple(() =>
        {
            Assert.That(ToolApprovalPolicy.TierOf(
                ToolOf("http://x/t", code: "class Tools: pass")),
                Is.EqualTo(ApprovalTier.Exec));
            Assert.That(ToolApprovalPolicy.TierOf(
                ToolOf("mcp://srv-1/minha_tool")),
                Is.EqualTo(ApprovalTier.Write));
        });

    [Test]
    public void TierOf_HttpSegueRequiresApproval() =>
        Assert.Multiple(() =>
        {
            Assert.That(ToolApprovalPolicy.TierOf(
                ToolOf("https://api.ex/ler", requiresApproval: false)),
                Is.EqualTo(ApprovalTier.Read));
            Assert.That(ToolApprovalPolicy.TierOf(
                ToolOf("https://api.ex/mutar", requiresApproval: true)),
                Is.EqualTo(ApprovalTier.Exec));
        });

    // ---- OverrideFor — resolução do override do usuário (RF-002) ----

    [Test]
    public void OverrideFor_CasaPorPrefixoECase()
    {
        var overrides = new Dictionary<string, string>
        { ["shell_exec"] = "deny", ["file_read"] = "PROMPT" };

        Assert.Multiple(() =>
        {
            Assert.That(ToolApprovalPolicy.OverrideFor(
                overrides, "builtin:shell_exec"), Is.EqualTo(ToolOverride.Deny));
            Assert.That(ToolApprovalPolicy.OverrideFor(
                overrides, "builtin_shell_exec"), Is.EqualTo(ToolOverride.Deny));
            Assert.That(ToolApprovalPolicy.OverrideFor(
                overrides, "SHELL_EXEC"), Is.EqualTo(ToolOverride.Deny));
            Assert.That(ToolApprovalPolicy.OverrideFor(
                overrides, "builtin_file_read"), Is.EqualTo(ToolOverride.Prompt));
            Assert.That(ToolApprovalPolicy.OverrideFor(
                overrides, "web_search"), Is.EqualTo(ToolOverride.None));
        });
    }

    [Test]
    public void OverrideFor_AcaoInvalidaIgnorada() =>
        Assert.That(ToolApprovalPolicy.OverrideFor(
            new Dictionary<string, string> { ["x"] = "talvez" }, "x"),
            Is.EqualTo(ToolOverride.None));

    // ---- ToolApprovalOverrideService — persistência em kv ----

    [Test]
    public async Task OverrideService_MergeGetERemove()
    {
        await using var db = NewDb();
        var svc = new ToolApprovalOverrideService(
            new ConfigService(db, TestCache.Create()));

        await svc.MergeAsync("u1", new Dictionary<string, string?>
        { ["shell_exec"] = "deny", ["file_read"] = "prompt" }, default);
        var map = await svc.GetAsync("u1", default);
        Assert.Multiple(() =>
        {
            Assert.That(map["shell_exec"], Is.EqualTo("deny"));
            Assert.That(map["file_read"], Is.EqualTo("prompt"));
        });

        // null remove; ação inválida é ignorada sem derrubar o resto.
        await svc.MergeAsync("u1", new Dictionary<string, string?>
        { ["shell_exec"] = null, ["file_read"] = "bogus" }, default);
        map = await svc.GetAsync("u1", default);
        Assert.Multiple(() =>
        {
            Assert.That(map.ContainsKey("shell_exec"), Is.False);
            Assert.That(map["file_read"], Is.EqualTo("prompt"));
        });
    }

    [Test]
    public async Task OverrideService_IsoladoPorUsuario()
    {
        await using var db = NewDb();
        var svc = new ToolApprovalOverrideService(
            new ConfigService(db, TestCache.Create()));

        await svc.MergeAsync("u1", new Dictionary<string, string?>
        { ["shell_exec"] = "allow" }, default);
        Assert.Multiple(() =>
        {
            Assert.That(svc.GetAsync("u2", default).Result, Is.Empty);
            Assert.That(svc.GetAsync("u1", default).Result["shell_exec"],
                Is.EqualTo("allow"));
        });
    }

    // ---- Mapa real: cada built-in registrada tem o tier certo (RF-006) ----

    /// <summary>
    /// Resolve as tools built-in reais do container de DI da API e
    /// confere o mapa nome→tier contra a classificação esperada —
    /// regressão do gate: execução arbitrária = <see cref="ApprovalTier.Exec"/>,
    /// mutação confinada = <see cref="ApprovalTier.Write"/>, resto =
    /// <see cref="ApprovalTier.Read"/> (incl. <c>ask_user</c>, que é
    /// interação e não mutação).
    /// </summary>
    [Test]
    public void MapaDeTiers_DasBuiltinsRegistradas()
    {
        var dbPath = TestInfra.NewDbPath("approval-tiers-e2e");
        var saved = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Default", $"Data Source={dbPath}");
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var scope = factory.Services.CreateScope();
            var tools = scope.ServiceProvider
                .GetServices<IBuiltinChatTool>().ToDictionary(t => t.Name);

            var esperado = new Dictionary<string, ApprovalTier>
            {
                ["shell_exec"] = ApprovalTier.Exec,
                ["code_interpreter"] = ApprovalTier.Exec,
                ["delegate_task"] = ApprovalTier.Exec,
                ["plan_exit"] = ApprovalTier.Exec,
                ["job_kill"] = ApprovalTier.Exec,
                ["n8n_trigger"] = ApprovalTier.Exec,
                ["worktree_merge"] = ApprovalTier.Exec,
                ["file_write"] = ApprovalTier.Write,
                ["file_edit"] = ApprovalTier.Write,
                ["apply_patch"] = ApprovalTier.Write,
                ["file_read"] = ApprovalTier.Read,
                ["file_list"] = ApprovalTier.Read,
                ["ask_user"] = ApprovalTier.Read,
                ["web_search"] = ApprovalTier.Read,
                ["memory_save"] = ApprovalTier.Read,
                ["todo_write"] = ApprovalTier.Read,
            };
            foreach (var (nome, tier) in esperado)
            {
                Assert.That(tools, Does.ContainKey(nome),
                    $"tool '{nome}' não registrada");
                Assert.That(tools[nome].Tier, Is.EqualTo(tier),
                    $"tier de '{nome}'");
            }
            // RequiresApproval (membro default) sempre ≡ tier != Read.
            Assert.That(tools.Values.All(t =>
                t.RequiresApproval == (t.Tier != ApprovalTier.Read)), Is.True);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__Default", saved);
            TestInfra.DeleteDb(dbPath);
        }
    }
}
