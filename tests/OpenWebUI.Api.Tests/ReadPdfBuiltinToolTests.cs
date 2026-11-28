using System.Text.Json;
using NUnit.Framework;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261128-read-pdf: <c>read_pdf</c> extrai texto de PDFs do
/// workspace com limites determinísticos e o mesmo sandbox do file_read.
/// </summary>
[TestFixture]
public sealed class ReadPdfBuiltinToolTests
{
    private string _workspace = null!;
    private BuiltinToolContext _ctx = null!;

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    [SetUp]
    public void SetUp()
    {
        _workspace = Path.Join(Path.GetTempPath(), $"owui-pdf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        _ctx = new BuiltinToolContext("u1", "c1", "r1", _workspace, "/tmp/up");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_workspace, true);

    private void MakePdf(string name, params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(PageSize.A4);
            var y = page.PageSize.Top - 40;
            foreach (var line in text.Split('\n'))
            {
                page.AddText(line, 10, new PdfPoint(40, y), font);
                y -= 14;
            }
        }
        File.WriteAllBytes(Path.Join(_workspace, name), builder.Build());
    }

    [Test]
    public async Task ReadPdf_ExtraiTextoDasPaginas()
    {
        // Covers RF-001: extrai texto do PDF informado.
        MakePdf("doc.pdf", "primeira pagina", "segunda pagina");
        var r = await new ReadPdfBuiltinTool().ExecuteAsync(
            Args("""{"path":"doc.pdf"}"""), _ctx, default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("primeira pagina"));
            Assert.That(r.Text, Does.Contain("segunda pagina"));
            Assert.That(r.Text, Does.Contain("2 página"));
        });
    }

    [Test]
    public async Task ReadPdf_StartPagePaginaDocumento()
    {
        // Covers RF-002: startPage 1-based permite paginar o documento.
        MakePdf("doc.pdf", "pagina um", "pagina dois", "pagina tres");
        var r = await new ReadPdfBuiltinTool().ExecuteAsync(
            Args("""{"path":"doc.pdf","startPage":2}"""), _ctx, default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Not.Contain("pagina um"));
            Assert.That(r.Text, Does.Contain("pagina dois"));
            Assert.That(r.Text, Does.Contain("pagina tres"));
        });
    }

    [Test]
    public async Task ReadPdf_LimitaVintePaginasEInformaRestantes()
    {
        // Covers RF-002: cap de 20 páginas com indicação de truncamento.
        MakePdf("doc.pdf", Enumerable.Range(1, 23)
            .Select(i => $"conteudo pagina {i}").ToArray());
        var r = await new ReadPdfBuiltinTool().ExecuteAsync(
            Args("""{"path":"doc.pdf"}"""), _ctx, default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("conteudo pagina 20"));
            Assert.That(r.Text, Does.Not.Contain("conteudo pagina 21"));
            Assert.That(r.Text, Does.Contain("3 página(s) restante"));
        });
    }

    [Test]
    public async Task ReadPdf_TruncaTextoEm64KiB()
    {
        // Covers RF-002: cap de texto de 64 KiB por chamada.
        var longLine = new string('x', 70 * 1024);
        MakePdf("big.pdf", longLine);
        var r = await new ReadPdfBuiltinTool().ExecuteAsync(
            Args("""{"path":"big.pdf"}"""), _ctx, default);
        Assert.That(r.Text, Does.Contain("truncado"));
        Assert.That(r.Text.Length, Is.LessThan(70 * 1024));
    }

    [Test]
    public async Task ReadPdf_ArquivoInexistente_RetornaErroDeTool()
    {
        // Covers RF-003: arquivo ausente vira erro de tool.
        var r = await new ReadPdfBuiltinTool().ExecuteAsync(
            Args("""{"path":"missing.pdf"}"""), _ctx, default);
        Assert.That(r.Text, Does.Contain("não encontrado"));
    }

    [Test]
    public async Task ReadPdf_StartPageAlemDoFim_RetornaErroDeTool()
    {
        // Covers RF-002/RF-003: paginação além do documento é erro explícito.
        MakePdf("doc.pdf", "pagina");
        var r = await new ReadPdfBuiltinTool().ExecuteAsync(
            Args("""{"path":"doc.pdf","startPage":2}"""), _ctx, default);
        Assert.That(r.Text, Does.Contain("além do fim"));
    }

    [Test]
    public async Task ReadPdf_PathNaoString_RetornaErroDeTool()
    {
        // Covers RF-003: path malformado do modelo não derruba a run.
        var r = await new ReadPdfBuiltinTool().ExecuteAsync(
            Args("""{"path":42}"""), _ctx, default);
        Assert.That(r.Text, Does.StartWith("Erro"));
    }

    [Test]
    public async Task ReadPdf_StartPageNaoInteiro_RetornaErroDeTool()
    {
        // Covers RF-003: args malformados do modelo não derrubam a run.
        MakePdf("doc.pdf", "pagina");
        var r = await new ReadPdfBuiltinTool().ExecuteAsync(
            Args("""{"path":"doc.pdf","startPage":1.5}"""), _ctx, default);
        Assert.That(r.Text, Does.StartWith("Erro"));
    }

    [Test]
    public async Task ReadPdf_ForaDoSandbox_Recusa()
    {
        // Covers RF-003: path fora do workspace vira erro de tool.
        var r = await new ReadPdfBuiltinTool().ExecuteAsync(
            Args("""{"path":"../etc.pdf"}"""), _ctx, default);
        Assert.That(r.Text, Does.StartWith("Erro"));
    }

    [Test]
    public async Task ReadPdf_PdfInvalido_RetornaErroDeTool()
    {
        // Covers RF-003: PDF inválido não derruba a run.
        await File.WriteAllTextAsync(Path.Join(_workspace, "bad.pdf"), "not a pdf");
        var r = await new ReadPdfBuiltinTool().ExecuteAsync(
            Args("""{"path":"bad.pdf"}"""), _ctx, default);
        Assert.That(r.Text, Does.StartWith("Erro"));
    }

    [Test]
    public void ReadPdf_MetadadosReadPlanAllowedESafeInParallel()
    {
        // Covers RF-004: tier read, schema e presença na allowlist do plan mode.
        var tool = new ReadPdfBuiltinTool();
        Assert.Multiple(() =>
        {
            Assert.That(tool.Name, Is.EqualTo("read_pdf"));
            Assert.That(tool.Tier, Is.EqualTo(ApprovalTier.Read));
            Assert.That(tool.SafeInParallel, Is.True);
            Assert.That(tool.ParametersJson, Does.Contain("startPage"));
        });

        var field = typeof(PermissionRuleset).GetField("PlanAllowed",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var set = field!.GetValue(null) as HashSet<string>;
        Assert.That(set, Does.Contain("read_pdf"));
    }
}
