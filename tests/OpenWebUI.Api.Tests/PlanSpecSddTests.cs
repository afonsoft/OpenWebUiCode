using NUnit.Framework;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Contrato SPEC SDD do modo plan (SPEC-20261015-plan-spec-sdd):
/// <see cref="PlanSpecTemplate.Validate"/> barra planos sem a estrutura
/// exigida e <see cref="PlanExitBuiltinTool"/> recusa a submissão com a
/// lista do que falta — a SPEC válida é gravada sem heading duplicado.
/// </summary>
public class PlanSpecSddTests
{
    private string _workspace = null!;

    private const string SpecValida = """
        # SPEC-20261015-login

        ## 0. Metadata
        | Field | Value |
        | Status | Draft |

        ## 1. User Story
        **As a** usuário **I want** login **So that** acesso.

        ## 2. Scope
        - in: endpoint

        ## 3. Technical Context
        src/

        ## 4. Requirements
        ### RF-001: login
        - **Description:** autenticar.

        ## 6. Acceptance Criteria
        - Given credencial quando login então token.

        ## 7. Task Plan
        - [ ] T1 implementar

        ## Open Questions / Pending Ambiguity
        - nenhuma
        """;

    [SetUp]
    public void SetUp()
    {
        _workspace = Path.Join(Path.GetTempPath(), $"owui-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_workspace, true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private static System.Text.Json.JsonElement Args(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement;

    private BuiltinToolContext Ctx() =>
        new("u1", "c1", "run-1", _workspace, _workspace);

    // ---------------- PlanSpecTemplate.Validate ----------------

    [Test]
    public void Validate_SpecCompleta_Aceita()
    {
        Assert.That(PlanSpecTemplate.Validate(SpecValida), Is.Null);
    }

    [Test]
    public void Validate_SecoesCaixaBaixa_Aceita()
    {
        var lower = SpecValida.ToLowerInvariant();
        Assert.That(PlanSpecTemplate.Validate(lower), Is.Null);
    }

    [Test]
    public void Validate_PlanoSolto_ListaSecoesFaltantes()
    {
        var err = PlanSpecTemplate.Validate("1. Ler\n2. Escrever");
        Assert.Multiple(() =>
        {
            Assert.That(err, Is.Not.Null);
            Assert.That(err, Does.Contain("SPEC SDD"));
            Assert.That(err, Does.Contain("User Story"));
            Assert.That(err, Does.Contain("RF-"));
        });
    }

    [Test]
    public void Validate_SemRequisitoRf_Recusa()
    {
        var semRf = SpecValida.Replace("RF-001", "requisito um");
        var err = PlanSpecTemplate.Validate(semRf);
        Assert.That(err, Does.Contain("RF-"));
    }

    // ---------------- plan_exit ----------------

    [Test]
    public async Task PlanExit_PlanoInvalido_RecusaENaoGrava()
    {
        var tool = new PlanExitBuiltinTool();
        var r = await tool.ExecuteAsync(
            Args("{\"plan\":\"1. Ler\\n2. Escrever\"}"), Ctx(), default);

        Assert.Multiple(() =>
        {
            Assert.That(r.Refused, Is.True);
            Assert.That(r.Text, Does.Contain("SPEC SDD"));
        });
        var dir = Path.Join(_workspace, PlanExitBuiltinTool.PlansDir);
        Assert.That(Directory.Exists(dir) && Directory.GetFiles(dir).Length > 0,
            Is.False, "nada deve ser gravado quando a SPEC é recusada");
    }

    [Test]
    public async Task PlanExit_SpecValida_GravaSemHeadingDuplicado()
    {
        var tool = new PlanExitBuiltinTool();
        var planJson = System.Text.Json.JsonSerializer.Serialize(SpecValida);
        var r = await tool.ExecuteAsync(
            Args($"{{\"plan\":{planJson},\"title\":\"T\"}}"), Ctx(), default);

        Assert.That(r.Refused, Is.False);
        var file = Path.Join(_workspace, PlanExitBuiltinTool.PlansDir, "run-1.md");
        Assert.That(File.Exists(file), Is.True);
        var doc = File.ReadAllText(file);
        Assert.Multiple(() =>
        {
            Assert.That(doc, Does.StartWith("# SPEC-20261015-login"),
                "o heading da SPEC não pode ser precedido pelo título wrapper");
            Assert.That(doc, Does.Contain("## 4. Requirements"));
            Assert.That(doc, Does.Contain("run-1"));
        });
    }

    [Test]
    public async Task PlanExit_PlanoSemHeading_UsaTituloWrapper()
    {
        var semHeading = SpecValida[(SpecValida.IndexOf("## 0.", StringComparison.Ordinal))..];
        var tool = new PlanExitBuiltinTool();
        var planJson = System.Text.Json.JsonSerializer.Serialize(semHeading);
        var r = await tool.ExecuteAsync(
            Args($"{{\"plan\":{planJson},\"title\":\"Meu Plano\"}}"), Ctx(), default);

        Assert.That(r.Refused, Is.False);
        var doc = File.ReadAllText(
            Path.Join(_workspace, PlanExitBuiltinTool.PlansDir, "run-1.md"));
        Assert.That(doc, Does.StartWith("# Meu Plano"));
    }
}
