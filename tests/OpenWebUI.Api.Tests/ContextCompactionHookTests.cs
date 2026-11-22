using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Context;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261124-compaction-hook: evento <c>SessionCompacting</c> —
/// additionalContext anexa ao prompt do summarizer, updatedInput de hook
/// <c>prompt</c> substitui, command hook nunca substitui, falha cai no
/// prompt padrão e o payload carrega trigger/chatId.
/// </summary>
[TestFixture]
public class ContextCompactionHookTests
{
    private string _dir = string.Empty;
    private string _home = string.Empty;
    private string _dbPath = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"chk-{Guid.NewGuid():N}");
        _home = Path.Combine(Path.GetTempPath(), $"chkhome-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(Path.GetTempPath(), $"chk-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_home);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private void WriteHooks(string json)
    {
        var d = Path.Combine(_dir, ".openwebui");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "hooks.json"), json);
    }

    private void Seed(AppDbContext db)
    {
        db.Users.Add(new User
        { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        db.ChatRuns.Add(new ChatRun
        {
            Id = "r1",
            ChatId = "c1",
            UserId = "u1",
            Model = "m",
            Status = ChatRunStatus.Running,
        });
        db.SaveChanges();
    }

    private async Task<ContextCompactionService> NewService(
        AppDbContext db, CaptureSummarizer sum,
        LifecycleHookService? hooks, ConfigService? config = null)
    {
        var cfg = config ?? new ConfigService(db, TestCache.Create());
        // tail=2 garante head não-vazio nos históricos curtos dos testes.
        await cfg.SetAsync("compaction:tail_messages", 2);
        var roles = new ModelRoleResolver(db, cfg,
            _ => Task.FromResult(new List<ModelInfo>()),
            NullLogger<ModelRoleResolver>.Instance);
        return new ContextCompactionService(db, cfg, sum, roles,
            NullLogger<ContextCompactionService>.Instance,
            new CompactionDeps(Hooks: hooks),
            new AsyncCompactionScheduler());
    }

    private LifecycleHookService CommandOnlyHooks() =>
        new(new CommandHookRunner(), prompts: null!, events: null);

    private static List<ChatCompletionMessage> Msgs(int n, int chars = 10) =>
        [.. Enumerable.Range(0, n).Select(i =>
            new ChatCompletionMessage(i % 2 == 0 ? "user" : "assistant",
                new string('x', chars)))];

    // O loader reconhece o evento novo — um hooks.json que só tenha
    // SessionCompacting resolve normalmente (e não é ignorado como
    // evento desconhecido).
    [Test]
    public void Loader_SessionCompacting_Reconhecido()
    {
        WriteHooks("""
            {"hooks": {"SessionCompacting": [
              {"hooks": [{"type": "command", "command": "echo ok"}]}]}}
            """);
        var hooks = HookConfigLoader.Load(_dir, home: _home);
        Assert.Multiple(() =>
        {
            Assert.That(hooks, Has.Count.EqualTo(1));
            Assert.That(hooks[0].Event, Is.EqualTo("SessionCompacting"));
            Assert.That(hooks[0].Type, Is.EqualTo("command"));
        });
    }

    // additionalContext de command hook anexa ao prompt do summarizer;
    // promptOverride segue null (command nunca substitui).
    [Test]
    public async Task Hook_ContextAdicional_AnexaAoSummarizer()
    {
        WriteHooks("""
            {"hooks": {"SessionCompacting": [{"hooks": [{"type": "command",
              "command": "echo '{\"decision\":\"approve\",\"hookSpecificOutput\":{\"additionalContext\":\"CTX-HOOK\"}}'"}]}]}}
            """);
        await using var db = NewDb();
        Seed(db);
        var sum = new CaptureSummarizer("RESUMO");
        var svc = await NewService(db, sum, CommandOnlyHooks());

        var ok = await svc.MaybeCompactAsync("c1", Msgs(8), "m",
            CancellationToken.None, "u1", force: true, workdir: _dir);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(sum.Calls, Is.EqualTo(1));
            Assert.That(sum.LastContextAppend, Is.EqualTo("CTX-HOOK"));
            Assert.That(sum.LastPromptOverride, Is.Null);
        });
    }

    // Command hook até emite updatedInput, mas a trilha da SPEC exige:
    // só hook do tipo prompt pode trocar o prompt do summarizer.
    [Test]
    public async Task Hook_Command_UpdatedInput_NaoSubstitui()
    {
        WriteHooks("""
            {"hooks": {"SessionCompacting": [{"hooks": [{"type": "command",
              "command": "echo '{\"decision\":\"approve\",\"hookSpecificOutput\":{\"updatedInput\":{\"prompt\":\"X\"},\"additionalContext\":\"CTX2\"}}'"}]}]}}
            """);
        await using var db = NewDb();
        Seed(db);
        var sum = new CaptureSummarizer("RESUMO");
        var svc = await NewService(db, sum, CommandOnlyHooks());

        var ok = await svc.MaybeCompactAsync("c1", Msgs(8), "m",
            CancellationToken.None, "u1", force: true, workdir: _dir);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(sum.LastPromptOverride, Is.Null);
            Assert.That(sum.LastContextAppend, Is.EqualTo("CTX2"));
        });
    }

    // Hook do tipo prompt: updatedInput da resposta do modelo substitui
    // o prompt do summarizer (override só é honrado nesse caso).
    [Test]
    public async Task Hook_Prompt_UpdatedInput_SubstituiPrompt()
    {
        WriteHooks("""
            {"hooks": {"SessionCompacting": [{"hooks": [
              {"type": "prompt", "prompt": "customize o prompt"}]}]}}
            """);
        await using var db = NewDb();
        Seed(db);
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("hooks.promptModel", "m");
        var roles = new ModelRoleResolver(db, config,
            _ => Task.FromResult(new List<ModelInfo>()),
            NullLogger<ModelRoleResolver>.Instance);
        var prompts = new PromptHookEvaluator(roles, config,
            (req, uid, ct) => Task.FromResult(new ProviderCompletion(
                "{\"pass\":true,\"updatedInput\":\"PROMPT-CUSTOM\",\"additionalContext\":\"CTX-P\"}",
                [], "[]")));
        var hooks = new LifecycleHookService(
            new CommandHookRunner(), prompts, events: null);
        var sum = new CaptureSummarizer("RESUMO");
        var svc = await NewService(db, sum, hooks, config);

        var ok = await svc.MaybeCompactAsync("c1", Msgs(8), "m",
            CancellationToken.None, "u1", force: true, workdir: _dir);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(sum.LastPromptOverride, Is.EqualTo("PROMPT-CUSTOM"));
            Assert.That(sum.LastContextAppend, Is.EqualTo("CTX-P"));
        });
    }

    // Hook que falha (exit≠0) não impede a compactação — o summarizer
    // roda com o prompt padrão.
    [Test]
    public async Task Hook_Falha_CaiNoPromptPadrao()
    {
        WriteHooks("""
            {"hooks": {"SessionCompacting": [{"hooks": [{"type": "command",
              "command": "exit 2"}]}]}}
            """);
        await using var db = NewDb();
        Seed(db);
        var sum = new CaptureSummarizer("RESUMO");
        var svc = await NewService(db, sum, CommandOnlyHooks());

        var ok = await svc.MaybeCompactAsync("c1", Msgs(8), "m",
            CancellationToken.None, "u1", force: true, workdir: _dir);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(sum.Calls, Is.EqualTo(1));
            Assert.That(sum.LastPromptOverride, Is.Null);
            Assert.That(sum.LastContextAppend, Is.Null);
        });
    }

    // O payload do evento carrega trigger/chatId/transcriptChars —
    // command hook captura o stdin no workdir (cwd = workdir).
    [Test]
    public async Task Hook_Payload_TemTriggerManualEChatId()
    {
        WriteHooks("""
            {"hooks": {"SessionCompacting": [{"hooks": [{"type": "command",
              "command": "cat > got.json"}]}]}}
            """);
        await using var db = NewDb();
        Seed(db);
        var sum = new CaptureSummarizer("RESUMO");
        var svc = await NewService(db, sum, CommandOnlyHooks());

        await svc.MaybeCompactAsync("c1", Msgs(8), "m",
            CancellationToken.None, "u1", force: true, workdir: _dir);

        var got = Path.Combine(_dir, "got.json");
        Assert.That(File.Exists(got), Is.True);
        var payload = File.ReadAllText(got);
        Assert.Multiple(() =>
        {
            Assert.That(payload, Does.Contain("\"trigger\":\"manual\""));
            Assert.That(payload, Does.Contain("\"chatId\":\"c1\""));
            Assert.That(payload, Does.Contain("\"transcriptChars\":"));
        });
    }

    private sealed class CaptureSummarizer : IContextSummarizer
    {
        private readonly ContextSummary? _result;

        public CaptureSummarizer(string result) =>
            _result = new ContextSummary(result, [], [], []);

        public int Calls { get; private set; }
        public string? LastPromptOverride { get; private set; }
        public string? LastContextAppend { get; private set; }

        public Task<ContextSummary?> SummarizeAsync(
            string model, string transcript, string? userId,
            CancellationToken ct, string? promptOverride = null,
            string? contextAppend = null)
        {
            Calls++;
            LastPromptOverride = promptOverride;
            LastContextAppend = contextAppend;
            return Task.FromResult(_result);
        }
    }
}
