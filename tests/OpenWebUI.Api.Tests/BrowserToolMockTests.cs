using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Browser;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261017-browser-visual-reasoning — caminhos de ExecuteAsync das
/// tools de sessão com <see cref="IPage"/> mockado (NSubstitute): navegação,
/// observação, ações por índice/seletor/coordenada, self-healing por texto e
/// lista rankeada de candidatos — sem lançar Chromium real.
/// </summary>
[TestFixture]
public class BrowserToolMockTests
{
    private string _dbPath = null!;
    private string _uploadDir = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = TestInfra.NewDbPath("browser-mock");
        _uploadDir = Path.Join(Path.GetTempPath(), $"owui-up-{Guid.NewGuid():N}");
    }

    [TearDown]
    public void TearDown()
    {
        TestInfra.DeleteDb(_dbPath);
        try { Directory.Delete(_uploadDir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static IConfiguration Enabled() =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["BrowserTools:Enabled"] = "true" }).Build();

    private BuiltinToolContext Ctx() =>
        new("u1", "c1", "r1", "/tmp/ws", _uploadDir);

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static IBrowserSessions Sessions(IPage page)
    {
        var s = Substitute.For<IBrowserSessions>();
        s.GetPageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(page));
        s.TryConsumeAction(Arg.Any<string>()).Returns(true);
        s.MaxActionsPerRun.Returns(50);
        s.OcrEnabled.Returns(false);
        s.ResetChatAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        s.CloseChatAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        return s;
    }

    private static TesseractOcr Ocr() => new(NullLogger<TesseractOcr>.Instance);

    private BrowserSessionToolBase Navigate(AppDbContext db, IBrowserSessions s) =>
        new BrowserNavigateBuiltinTool(s, Ocr(), new ConfigService(db, TestCache.Create()), Enabled(), db);

    private BrowserSessionToolBase Observe(AppDbContext db, IBrowserSessions s) =>
        new BrowserObserveBuiltinTool(s, Ocr(), new ConfigService(db, TestCache.Create()), Enabled(), db);

    private BrowserSessionToolBase Act(AppDbContext db, IBrowserSessions s) =>
        new BrowserActBuiltinTool(s, Ocr(), new ConfigService(db, TestCache.Create()), Enabled(), db);

    /// <summary>Página mockada com observação saudável (elementos + png + título).</summary>
    private static IPage HealthyPage()
    {
        var page = Substitute.For<IPage>();
        var elements = JsonDocument.Parse(
            """[{"i":0,"tag":"button","role":"","name":"Save","href":"","type":"submit"}]""")
            .RootElement.Clone();
        page.EvaluateAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns(Task.FromResult(elements));
        page.ScreenshotAsync(Arg.Any<PageScreenshotOptions?>())
            .Returns(Task.FromResult(new byte[] { 1, 2, 3, 4 }));
        page.TitleAsync().Returns(Task.FromResult("My Page"));
        page.Url.Returns("http://localhost:9/pagina");
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions?>())
            .Returns(Task.FromResult<IResponse?>(null));
        page.Mouse.Returns(Substitute.For<IMouse>());
        page.Mouse.WheelAsync(Arg.Any<float>(), Arg.Any<float>())
            .Returns(Task.CompletedTask);
        page.Mouse.ClickAsync(Arg.Any<float>(), Arg.Any<float>(), Arg.Any<MouseClickOptions?>())
            .Returns(Task.CompletedTask);
        page.Keyboard.Returns(Substitute.For<IKeyboard>());
        page.Keyboard.PressAsync(Arg.Any<string>(), Arg.Any<KeyboardPressOptions?>())
            .Returns(Task.CompletedTask);
        return page;
    }

    [Test]
    public async Task Navigate_sucesso_devolve_observacao_e_salva_screenshot()
    {
        var db = NewDb();
        var page = HealthyPage();
        var result = await Navigate(db, Sessions(page)).ExecuteAsync(
            Args("""{"url":"http://localhost:9/pagina"}"""), Ctx(), default);

        Assert.That(result.Text, Does.Contain("Navigated to"));
        Assert.That(result.Text, Does.Contain("My Page"));
        Assert.That(result.Text, Does.Contain("[0] button \"Save\""));
        await page.Received(1).GotoAsync(
            "http://localhost:9/pagina", Arg.Any<PageGotoOptions?>());
        // Screenshot virou FileEntry no banco + arquivo em UploadDir.
        Assert.That(db.Files.Count(), Is.EqualTo(1));
        Assert.That(Directory.GetFiles(_uploadDir), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task Navigate_falha_playwright_reseta_sessao()
    {
        var db = NewDb();
        var page = HealthyPage();
        page.GotoAsync(Arg.Any<string>(), Arg.Any<PageGotoOptions?>())
            .Returns<Task<IResponse?>>(_ => throw new PlaywrightException("boom"));
        var sessions = Sessions(page);

        var result = await Navigate(db, sessions).ExecuteAsync(
            Args("""{"url":"http://localhost:9/pagina"}"""), Ctx(), default);

        Assert.That(result.Text, Does.Contain("browser_navigate falhou"));
        Assert.That(result.Text, Does.Contain("reiniciada"));
        await sessions.Received(1).ResetChatAsync("c1");
    }

    [Test]
    public async Task Navigate_erro_interno_nao_reseta_sessao()
    {
        var db = NewDb();
        var page = HealthyPage();
        var sessions = Sessions(page);
        sessions.GetPageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IPage>>(_ => throw new InvalidOperationException("sem browser"));

        var result = await Navigate(db, sessions).ExecuteAsync(
            Args("""{"url":"http://localhost:9/pagina"}"""), Ctx(), default);

        Assert.That(result.Text, Does.Contain("sem browser"));
        await sessions.DidNotReceive().ResetChatAsync(Arg.Any<string>());
    }

    [Test]
    public async Task Observe_sucesso_sem_screenshot_quando_print_falha()
    {
        var db = NewDb();
        var page = HealthyPage();
        page.ScreenshotAsync(Arg.Any<PageScreenshotOptions?>())
            .Returns<Task<byte[]>>(_ => throw new PlaywrightException("no shot"));

        var result = await Observe(db, Sessions(page)).ExecuteAsync(Args("{}"), Ctx(), default);

        Assert.That(result.Text, Does.Contain("My Page"));
        Assert.That(db.Files.Count(), Is.EqualTo(0));
    }

    [Test]
    public async Task Observe_tolerante_quando_extracao_de_elementos_falha()
    {
        var db = NewDb();
        var page = HealthyPage();
        page.EvaluateAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>())
            .Returns<Task<JsonElement>>(_ => throw new PlaywrightException("eval fail"));

        var result = await Observe(db, Sessions(page)).ExecuteAsync(Args("{}"), Ctx(), default);

        Assert.That(result.Text, Does.Contain("nenhum elemento interativo"));
    }

    [Test]
    public async Task Observe_falha_playwright_reseta_sessao()
    {
        var db = NewDb();
        var page = HealthyPage();
        var sessions = Sessions(page);
        sessions.GetPageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IPage>>(_ => throw new PlaywrightException("dead context"));

        var result = await Observe(db, sessions).ExecuteAsync(Args("{}"), Ctx(), default);

        Assert.That(result.Text, Does.Contain("browser_observe falhou"));
        await sessions.Received(1).ResetChatAsync("c1");
    }

    [Test]
    public async Task Act_scroll_chama_mouse_wheel()
    {
        var db = NewDb();
        var page = HealthyPage();
        var result = await Act(db, Sessions(page)).ExecuteAsync(
            Args("""{"action":"scroll","deltaY":250}"""), Ctx(), default);

        await page.Mouse.Received(1).WheelAsync(0f, 250f);
        Assert.That(result.Text, Does.Contain("Action scroll"));
    }

    [Test]
    public async Task Act_press_sem_alvo_usa_keyboard()
    {
        var db = NewDb();
        var page = HealthyPage();
        await Act(db, Sessions(page)).ExecuteAsync(
            Args("""{"action":"press","key":"Tab"}"""), Ctx(), default);

        await page.Keyboard.Received(1).PressAsync("Tab", Arg.Any<KeyboardPressOptions?>());
    }

    [Test]
    public async Task Act_click_por_coordenada()
    {
        var db = NewDb();
        var page = HealthyPage();
        await Act(db, Sessions(page)).ExecuteAsync(
            Args("""{"action":"click","x":10,"y":20}"""), Ctx(), default);

        await page.Mouse.Received(1).ClickAsync(10f, 20f, Arg.Any<MouseClickOptions?>());
    }

    [Test]
    public async Task Act_click_por_index_usa_locator()
    {
        var db = NewDb();
        var page = HealthyPage();
        var locator = Substitute.For<ILocator>();
        locator.First.Returns(locator);
        locator.ClickAsync(Arg.Any<LocatorClickOptions?>()).Returns(Task.CompletedTask);
        page.Locator(BrowserElements.SelectorForIndex(3), Arg.Any<PageLocatorOptions?>())
            .Returns(locator);

        var result = await Act(db, Sessions(page)).ExecuteAsync(
            Args("""{"action":"click","index":3}"""), Ctx(), default);

        await locator.Received(1).ClickAsync(Arg.Any<LocatorClickOptions?>());
        Assert.That(result.Text, Does.Contain("Action click"));
    }

    [Test]
    public async Task Act_healing_por_texto_quando_seletor_falha()
    {
        var db = NewDb();
        var page = HealthyPage();
        var broken = Substitute.For<ILocator>();
        broken.First.Returns(broken);
        broken.ClickAsync(Arg.Any<LocatorClickOptions?>())
            .Returns<Task>(_ => throw new PlaywrightException("not found"));
        page.Locator("#gone", Arg.Any<PageLocatorOptions?>()).Returns(broken);

        var healed = Substitute.For<ILocator>();
        healed.First.Returns(healed);
        healed.ClickAsync(Arg.Any<LocatorClickOptions?>()).Returns(Task.CompletedTask);
        page.GetByText("Save", Arg.Any<PageGetByTextOptions?>()).Returns(healed);

        var result = await Act(db, Sessions(page)).ExecuteAsync(
            Args("""{"action":"click","selector":"#gone","text":"Save"}"""), Ctx(), default);

        await healed.Received(1).ClickAsync(Arg.Any<LocatorClickOptions?>());
        Assert.That(result.Text, Does.Contain("Action click"));
    }

    [Test]
    public async Task Act_todos_alvos_falham_devolve_candidatos_rankeados()
    {
        var db = NewDb();
        var page = HealthyPage();
        var loc = Substitute.For<ILocator>();
        loc.First.Returns(loc);
        loc.ClickAsync(Arg.Any<LocatorClickOptions?>())
            .Returns<Task>(_ => throw new PlaywrightException("nf"));
        page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(loc);
        page.GetByText(Arg.Any<string>(), Arg.Any<PageGetByTextOptions?>()).Returns(loc);

        var result = await Act(db, Sessions(page)).ExecuteAsync(
            Args("""{"action":"click","index":9,"text":"save"}"""), Ctx(), default);

        Assert.That(result.Text, Does.Contain("Não achei o alvo"));
        Assert.That(result.Text, Does.Contain("elements like"));
        Assert.That(result.Text, Does.Contain("[0] button \"Save\""));
    }

    [Test]
    public async Task Act_budget_esgotado_retorna_mensagem_amigavel()
    {
        var db = NewDb();
        var page = HealthyPage();
        var sessions = Sessions(page);
        sessions.TryConsumeAction(Arg.Any<string>()).Returns(false);

        var result = await Act(db, sessions).ExecuteAsync(
            Args("""{"action":"click","index":0}"""), Ctx(), default);

        Assert.That(result.Text, Does.Contain("Budget"));
        await sessions.DidNotReceive().GetPageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Act_fill_usa_value_e_fallback_text()
    {
        var db = NewDb();
        var page = HealthyPage();
        var locator = Substitute.For<ILocator>();
        locator.First.Returns(locator);
        locator.FillAsync(Arg.Any<string>(), Arg.Any<LocatorFillOptions?>())
            .Returns(Task.CompletedTask);
        page.Locator(Arg.Any<string>(), Arg.Any<PageLocatorOptions?>()).Returns(locator);

        await Act(db, Sessions(page)).ExecuteAsync(
            Args("""{"action":"fill","index":0,"value":"oi@x.com"}"""), Ctx(), default);
        await locator.Received(1).FillAsync("oi@x.com", Arg.Any<LocatorFillOptions?>());
    }

    [Test]
    public async Task Act_falha_playwright_na_pagina_reseta_sessao()
    {
        var db = NewDb();
        var sessions = Substitute.For<IBrowserSessions>();
        sessions.TryConsumeAction(Arg.Any<string>()).Returns(true);
        sessions.MaxActionsPerRun.Returns(50);
        sessions.GetPageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IPage>>(_ => throw new PlaywrightException("crash"));
        sessions.ResetChatAsync(Arg.Any<string>()).Returns(Task.CompletedTask);

        var result = await Act(db, sessions).ExecuteAsync(
            Args("""{"action":"click","index":0}"""), Ctx(), default);

        Assert.That(result.Text, Does.Contain("browser_act falhou"));
        await sessions.Received(1).ResetChatAsync("c1");
    }

    [Test]
    public async Task Service_propriedades_e_close_noop()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["BrowserTools:MaxActionsPerRun"] = "7",
                ["BrowserTools:ActionTimeoutSeconds"] = "5",
                ["BrowserTools:ViewportWidth"] = "800",
                ["BrowserTools:ViewportHeight"] = "600",
                ["BrowserTools:Ocr"] = "true",
                ["BrowserTools:AutoInstall"] = "false",
            }).Build();
        var svc = new BrowserSessionService(cfg, NullLogger<BrowserSessionService>.Instance);

        Assert.That(svc.MaxActionsPerRun, Is.EqualTo(7));
        Assert.That(svc.ActionTimeoutMs, Is.EqualTo(5000));
        Assert.That(svc.Viewport.Width, Is.EqualTo(800));
        Assert.That(svc.Viewport.Height, Is.EqualTo(600));
        Assert.That(svc.OcrEnabled, Is.True);
        Assert.That(svc.AutoInstall, Is.False);
        // Close/Reset de chat inexistente é no-op.
        await svc.CloseChatAsync("nada");
        await svc.ResetChatAsync("nada");
        _ = svc.IsBrowserReady();
        await svc.DisposeAsync();
    }

    [Test]
    public async Task Ocr_indisponivel_devolve_null_sem_processo()
    {
        // Se tesseract existe no host, ReadTextAsync num arquivo inexistente
        // também devolve null (exit code != 0) — ambos os caminhos aceitos.
        var ocr = new TesseractOcr(NullLogger<TesseractOcr>.Instance);
        var text = await ocr.ReadTextAsync("/tmp/inexistente-owui.png", default);
        var box = await ocr.FindTextBoxAsync("/tmp/inexistente-owui.png", "x", default);
        Assert.That(text, Is.Null);
        Assert.That(box, Is.Null);
        _ = ocr.IsAvailable;
    }
}
