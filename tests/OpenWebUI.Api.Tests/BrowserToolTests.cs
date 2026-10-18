using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Browser;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261017-browser-visual-reasoning — flag, SSRF, budgets e healing
/// sem lançar browser real (o launch só ocorre depois de flag+URL válidas).
/// </summary>
[TestFixture]
public class BrowserToolTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp() => _dbPath = TestInfra.NewDbPath("browser-tools");

    [TearDown]
    public void TearDown() => TestInfra.DeleteDb(_dbPath);

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static IConfiguration Config(Dictionary<string, string?>? kv = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv ?? []).Build();

    private static BuiltinToolContext Ctx() => new("u1", "c1", "r1", "/tmp/ws", "/tmp/up");

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    private BrowserSessionToolBase Navigate(AppDbContext db, IConfiguration cfg) =>
        new BrowserNavigateBuiltinTool(
            Sessions(cfg), Ocr(), new ConfigService(db, TestCache.Create()), cfg, db);

    private BrowserSessionToolBase Act(AppDbContext db, IConfiguration cfg) =>
        new BrowserActBuiltinTool(
            Sessions(cfg), Ocr(), new ConfigService(db, TestCache.Create()), cfg, db);

    private BrowserSessionToolBase Close(AppDbContext db, IConfiguration cfg) =>
        new BrowserCloseBuiltinTool(
            Sessions(cfg), Ocr(), new ConfigService(db, TestCache.Create()), cfg, db);

    private static TesseractOcr Ocr() => new(NullLogger<TesseractOcr>.Instance);

    private static BrowserSessionService Sessions(IConfiguration cfg) =>
        new(cfg, NullLogger<BrowserSessionService>.Instance);

    [Test]
    public async Task Navigate_desabilitada_quando_flag_off()
    {
        var result = await Navigate(NewDb(), Config())
            .ExecuteAsync(Args("""{"url":"http://localhost:8080"}"""), Ctx(), default);
        Assert.That(result.Text, Does.Contain("desabilitada"));
    }

    [Test]
    public async Task Navigate_bloqueia_host_privado_lan()
    {
        var cfg = Config(new() { ["BrowserTools:Enabled"] = "true" });
        var result = await Navigate(NewDb(), cfg)
            .ExecuteAsync(Args("""{"url":"http://192.168.0.10/admin"}"""), Ctx(), default);
        Assert.That(result.Text, Does.Contain("bloqueado"));
    }

    [Test]
    public async Task Navigate_rejeita_url_invalida()
    {
        var cfg = Config(new() { ["BrowserTools:Enabled"] = "true" });
        var result = await Navigate(NewDb(), cfg)
            .ExecuteAsync(Args("""{"url":"ftp://x"}"""), Ctx(), default);
        Assert.That(result.Text, Does.Contain("URL inválida"));
    }

    [Test]
    public async Task Act_tem_tier_exec_e_desabilitada_com_flag_off()
    {
        var tool = Act(NewDb(), Config());
        Assert.That(tool.Tier, Is.EqualTo(ApprovalTier.Exec));
        var result = await tool.ExecuteAsync(
            Args("""{"action":"click","index":0}"""), Ctx(), default);
        Assert.That(result.Text, Does.Contain("desabilitada"));
    }

    [Test]
    public async Task Act_rejeita_acao_desconhecida()
    {
        var cfg = Config(new() { ["BrowserTools:Enabled"] = "true" });
        var result = await Act(NewDb(), cfg).ExecuteAsync(
            Args("""{"action":"hack"}"""), Ctx(), default);
        Assert.That(result.Text, Does.Contain("Ação inválida"));
    }

    [Test]
    public async Task Close_encerra_sem_exigir_flag()
    {
        var result = await Close(NewDb(), Config())
            .ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(result.Text, Does.Contain("encerrada"));
    }

    [Test]
    public void Budget_esgota_apos_max_actions()
    {
        var sessions = Sessions(Config(new() { ["BrowserTools:MaxActionsPerRun"] = "2" }));
        Assert.That(sessions.TryConsumeAction("r1"), Is.True);
        Assert.That(sessions.TryConsumeAction("r1"), Is.True);
        Assert.That(sessions.TryConsumeAction("r1"), Is.False);
        // Outra run tem budget próprio.
        Assert.That(sessions.TryConsumeAction("r2"), Is.True);
    }

    [Test]
    public void Elements_rank_prioriza_nome_parecido()
    {
        var elements = new List<BrowserElement>
        {
            new(0, "a", "", "Home", "/", ""),
            new(1, "button", "", "Cancel", "", ""),
            new(2, "button", "", "Save changes", "", ""),
        };
        var ranked = BrowserElements.Rank(elements, "save");
        Assert.That(ranked, Has.Count.GreaterThan(0));
        Assert.That(ranked[0].Index, Is.EqualTo(2));
    }

    [Test]
    public void Elements_parse_e_format()
    {
        var json = JsonDocument.Parse(
            """[{"i":0,"tag":"button","role":"","name":"Login","href":"","type":"submit"}]""");
        var elements = BrowserElements.Parse(json.RootElement);
        Assert.That(elements, Has.Count.EqualTo(1));
        Assert.That(BrowserElements.Format(elements), Does.Contain("[0] button \"Login\""));
    }

    [Test]
    public void Tsv_find_text_box_retorna_centro()
    {
        var tsv = string.Join('\n',
            "level\tpage\tblock\tpar\tline\tword\tleft\ttop\twidth\theight\tconf\ttext",
            "5\t1\t1\t1\t1\t1\t100\t50\t60\t20\t96\tSave",
            "5\t1\t1\t1\t1\t2\t170\t50\t80\t20\t96\tchanges");
        var box = TesseractOcr.FindTextBox(tsv, "Save changes");
        Assert.That(box, Is.Not.Null);
        Assert.That(box!.Value.X, Is.InRange(150, 210));
        Assert.That(box!.Value.Y, Is.EqualTo(60));
    }

    [Test]
    public void Tsv_find_text_box_null_sem_match()
    {
        var tsv = "5\t1\t1\t1\t1\t1\t10\t10\t40\t15\t95\tHello";
        Assert.That(TesseractOcr.FindTextBox(tsv, "xyz"), Is.Null);
    }

    /// <summary>
    /// Smoke E2E do RF-001/RF-003: lança o Chromium real (quando instalado),
    /// navega numa data: URL e extrai os elementos indexados. Ignorado em
    /// ambientes sem o bundle do Playwright (CI pode não ter).
    /// </summary>
    [Test]
    public async Task Session_navega_e_indexa_elementos()
    {
        var root = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "ms-playwright");
        if (!Directory.Exists(root)
            || !Directory.GetDirectories(root)
                .Any(d => Path.GetFileName(d).StartsWith("chromium", StringComparison.Ordinal)))
        {
            Assert.Ignore("Chromium do Playwright não instalado neste host");
        }

        await using var sessions = Sessions(Config());
        var page = await sessions.GetPageAsync("smoke", default);
        await page.GotoAsync("data:text/html,<button id=\"b\">Save</button>");
        var json = await page.EvaluateAsync<JsonElement>(
            BrowserElements.ExtractScript, BrowserElements.MaxElements);
        var elements = BrowserElements.Parse(json);
        Assert.That(elements.Any(e => e.Name == "Save" && e.Tag == "button"), Is.True);
        await sessions.CloseChatAsync("smoke");
    }
}
