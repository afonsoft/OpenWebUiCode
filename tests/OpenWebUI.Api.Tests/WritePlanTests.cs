using System.Text.Json;
using NUnit.Framework;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261031-plan-mode-v2: write_plan (RF-001/RF-006) — arquivo por
/// sessão, slug sanitizado, fallback ~/.openwebui/plans, warnings de
/// seção; listagem (RF-005); plan_exit referencia o path (RF-004).
/// </summary>
[TestFixture]
public class WritePlanTests
{
    private string _dir = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"plans-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        WritePlanTests_SessionReset();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // O estado por sessão é estático — reset entre testes via reflexão
    // não; usa-se chave de sessão única por teste.
    private static void WritePlanTests_SessionReset() { }

    private static BuiltinToolContext Ctx(string ws, string run) =>
        new("u1", $"chat-{run}", run, ws, ws);

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static string FullPlan => """
        # My Plan
        ## Summary
        s
        ## Implementation Steps
        1. x
        ## Files to Modify
        - a.cs
        ## Verification
        dotnet test
        ## Risks
        none
        """;

    [Test]
    public async Task WritePlan_CriaArquivoNaPlansDir_ERetornaPath()
    {
        var svc = new PlanFileService();
        var tool = new WritePlanBuiltinTool(svc);
        var result = await tool.ExecuteAsync(
            Args("""{"content":"# T\n\n## Summary\nx"}"""),
            Ctx(_dir, "r1"), default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Refused, Is.False);
            Assert.That(result.Text, Does.Contain("plans/"));
        });
        var files = Directory.GetFiles(Path.Combine(_dir, "plans"), "*.md");
        Assert.That(files, Has.Length.EqualTo(1));
        Assert.That(Path.GetFileName(files[0]),
            Does.Match(@"^\d{4}-\d{2}-\d{2}-t\.md$"));
    }

    [Test]
    public async Task WritePlan_SegundaChamadaNaSessao_AtualizaMesmoArquivo()
    {
        var svc = new PlanFileService();
        var tool = new WritePlanBuiltinTool(svc);
        var ctx = Ctx(_dir, "r2");
        await tool.ExecuteAsync(Args("""{"content":"# A\nx"}"""), ctx, default);
        var result = await tool.ExecuteAsync(
            Args("""{"content":"# Renamed\ny"}"""), ctx, default);

        var files = Directory.GetFiles(Path.Combine(_dir, "plans"), "*.md");
        Assert.Multiple(() =>
        {
            Assert.That(files, Has.Length.EqualTo(1),
                "mesma sessão sobrescreve");
            Assert.That(File.ReadAllText(files[0]), Does.Contain("Renamed"));
            Assert.That(PlanFileService.LatestOf("r2"),
                Is.EqualTo("plans/" + Path.GetFileName(files[0])));
        });
    }

    [Test]
    public async Task WritePlan_SessoesConcorrentes_SufixoDistinto()
    {
        var svc = new PlanFileService();
        var tool = new WritePlanBuiltinTool(svc);
        await tool.ExecuteAsync(Args("""{"content":"# Same Title\n1"}"""),
            Ctx(_dir, "ra"), default);
        await tool.ExecuteAsync(Args("""{"content":"# Same Title\n2"}"""),
            Ctx(_dir, "rb"), default);

        var files = Directory.GetFiles(Path.Combine(_dir, "plans"), "*.md")
            .Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.That(files, Has.Count.EqualTo(2));
        Assert.That(files.Any(f => f!.Contains("-2.md")), Is.True);
    }

    [Test]
    public async Task WritePlan_SecoesFaltando_GravamComAviso()
    {
        var svc = new PlanFileService();
        var tool = new WritePlanBuiltinTool(svc);
        var result = await tool.ExecuteAsync(
            Args("""{"content":"# Partial\nsó summary"}"""),
            Ctx(_dir, "r4"), default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Refused, Is.False);
            Assert.That(result.Text, Does.Contain("seções do template ausentes"));
            Assert.That(result.Text, Does.Contain("Implementation Steps"));
        });
    }

    [Test]
    public async Task WritePlan_SemWorkspace_FallbackHome()
    {
        var fake = Path.Combine(_dir, "home");
        var prev = PlanFileService.HomeResolver;
        PlanFileService.HomeResolver = () => fake;
        try
        {
            var svc = new PlanFileService();
            var tool = new WritePlanBuiltinTool(svc);
            var result = await tool.ExecuteAsync(
                Args("""{"content":"# H\nx"}"""), Ctx("", "r5"), default);
            Assert.That(result.Refused, Is.False);
            Assert.That(Directory.Exists(
                Path.Combine(fake, ".openwebui", "plans")), Is.True);
        }
        finally
        {
            PlanFileService.HomeResolver = prev;
        }
    }

    [Test]
    public void List_RetornaOrdenadoPorModificacao()
    {
        var svc = new PlanFileService();
        var dir = Path.Combine(_dir, "plans");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "2026-01-01-old.md"), "# Old");
        var newer = Path.Combine(dir, "2026-01-02-new.md");
        File.WriteAllText(newer, "# New Title\nbody");
        File.SetLastWriteTimeUtc(
            Path.Combine(dir, "2026-01-01-old.md"),
            DateTime.UtcNow.AddDays(-1));

        var list = svc.List(_dir);
        Assert.Multiple(() =>
        {
            Assert.That(list, Has.Count.EqualTo(2));
            Assert.That(list[0].Name, Is.EqualTo("2026-01-02-new.md"));
            Assert.That(list[0].Title, Is.EqualTo("New Title"));
            Assert.That(list[0].Path, Is.EqualTo("plans/2026-01-02-new.md"));
        });
    }

    [Test]
    public async Task PlanExit_ReferenciaArquivoDoWritePlan()
    {
        var svc = new PlanFileService();
        var ctx = Ctx(_dir, "r6");
        svc.Write(_dir, "chat-r6", FullPlan, null, "r6");

        var exit = new PlanExitBuiltinTool();
        var spec = """
            # SPEC-20261101-x
            ## 0. Metadata
            x
            ## 1. User Story
            x
            ## 2. Scope
            x
            ## 4. Requirements
            RF-001 y
            ## 6. Acceptance Criteria
            x
            ## 7. Task Plan
            x
            """;
        var result = await exit.ExecuteAsync(
            Args(JsonSerializer.Serialize(new { plan = spec })),
            ctx, default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Does.Contain("plans/"));
        });
    }
}
