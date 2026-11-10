using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261031-smart-permission-llm: <see cref="SmartPermissionClassifier.ClassifyAsync"/>
/// — juiz LLM com dedup por fingerprint na sessão (RF-005), always-ask
/// sem chamar o juiz (RF-004) e fail-safe <c>ask</c> quando o juiz falha
/// ou devolve lixo (RF-006).
/// </summary>
[TestFixture, IsolateEnvironment]
public class SmartPermissionClassifierJudgeTests
{
    private string _dbPath = null!;
    private AppDbContext _db = null!;
    private ConfigService _config = null!;
    private ModelRoleResolver _roles = null!;
    private string _session = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-spj-{Guid.NewGuid():N}.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        await DatabaseMigrator.MigrateAsync(_db);
        _config = new ConfigService(_db, TestCache.Create());
        _roles = new ModelRoleResolver(_db, _config,
            _ => Task.FromResult(new List<ModelInfo>()),
            NullLogger<ModelRoleResolver>.Instance);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    [SetUp]
    public void SetUp()
    {
        _session = $"sess-{Guid.NewGuid():N}";
    }

    [TearDown]
    public void TearDown() => SmartPermissionClassifier.Clear(_session);

    private SmartPermissionClassifier NewClassifier(
        Func<ChatCompletionRequest, string?, CancellationToken, Task<ProviderCompletion>> complete) =>
        new(_roles, _config, complete);

    private static ChatRun NewRun() =>
        new() { UserId = "u1", Model = "modelo-chat" };

    private static ProviderCompletion JudgeSays(string content) =>
        new(content, [], "[]");

    /// <summary>Juiz devolve allow → verdict allow com modelo resolvido;
    /// segunda classificação idêntica sai do dedup (FromCache).</summary>
    [Test]
    public async Task ClassifyAsync_Allow_DepoisVemDoDedup()
    {
        ChatCompletionRequest? captured = null;
        var calls = 0;
        var classifier = NewClassifier((req, _, _) =>
        {
            calls++;
            captured = req;
            return Task.FromResult(JudgeSays(
                """{"decision":"allow","reason":"read-only"}"""));
        });

        var first = await classifier.ClassifyAsync(_session, NewRun(),
            "file_read", """{"path":"src/a.cs"}""",
            "regras do projeto", "contexto", CancellationToken.None);
        var second = await classifier.ClassifyAsync(_session, NewRun(),
            "file_read", """{"path":"src/a.cs"}""",
            "regras do projeto", "contexto", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first.Decision, Is.EqualTo("allow"));
            Assert.That(first.Reason, Is.EqualTo("read-only"));
            Assert.That(first.FromCache, Is.False);
            Assert.That(first.Model, Is.EqualTo("modelo-chat"),
                "sem papel advisor configurado → modelo da run");
            Assert.That(second.Decision, Is.EqualTo("allow"));
            Assert.That(second.FromCache, Is.True);
            Assert.That(calls, Is.EqualTo(1), "dedup não chama o juiz de novo");
            Assert.That(captured, Is.Not.Null);
            Assert.That(captured!.Model, Is.EqualTo("modelo-chat"));
            Assert.That(captured.Messages[0].Role, Is.EqualTo("system"));
            Assert.That(captured.Messages[1].Content,
                Does.Contain("action_to_classify"));
            Assert.That(captured.Messages[1].Content,
                Does.Contain("file_read"));
        });
    }

    /// <summary>Juiz devolve deny → verdict deny também é memorizado
    /// (dedup cobre allow e deny).</summary>
    [Test]
    public async Task ClassifyAsync_Deny_EMemorizado()
    {
        var classifier = NewClassifier((_, _, _) => Task.FromResult(
            JudgeSays("""{"decision":"deny","reason":"perigoso"}""")));

        var verdict = await classifier.ClassifyAsync(_session, NewRun(),
            "shell_exec", """{"command":"ls /tmp"}""",
            null, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Decision, Is.EqualTo("deny"));
            Assert.That(verdict.Reason, Is.EqualTo("perigoso"));
            Assert.That(SmartPermissionClassifier.Cached(
                _session, verdict.Fingerprint), Is.Not.Null);
        });
    }

    /// <summary>Categoria always-ask (npm install) curto-circuita antes do
    /// juiz — complete nunca é invocado.</summary>
    [Test]
    public async Task ClassifyAsync_AlwaysAsk_NaoChamaJuiz()
    {
        var calls = 0;
        var classifier = NewClassifier((_, _, _) =>
        {
            calls++;
            return Task.FromResult(JudgeSays("""{"decision":"allow"}"""));
        });

        var verdict = await classifier.ClassifyAsync(_session, NewRun(),
            "shell_exec", """{"command":"npm install express"}""",
            null, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Decision, Is.EqualTo("ask"));
            Assert.That(verdict.Reason, Does.Contain("always-ask"));
            Assert.That(calls, Is.EqualTo(0));
        });
    }

    /// <summary>Juiz devolve texto não-JSON → parse estrito falha → ask
    /// "unparseable verdict".</summary>
    [Test]
    public async Task ClassifyAsync_JuizDevolveLixo_Ask()
    {
        var classifier = NewClassifier((_, _, _) => Task.FromResult(
            JudgeSays("isto não é json de jeito nenhum")));

        var verdict = await classifier.ClassifyAsync(_session, NewRun(),
            "file_glob", """{"pattern":"*.cs"}""",
            null, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Decision, Is.EqualTo("ask"));
            Assert.That(verdict.Reason, Does.Contain("unparseable"));
        });
    }

    /// <summary>Falha do juiz (timeout/exceção do provider) → fail-safe
    /// ask "judge unavailable" — nunca propaga.</summary>
    [Test]
    public async Task ClassifyAsync_JuizFalha_FailSafeAsk()
    {
        var classifier = NewClassifier((_, _, _) =>
            throw new InvalidOperationException("provider fora do ar"));

        var verdict = await classifier.ClassifyAsync(_session, NewRun(),
            "file_write", """{"path":"docs/x.md"}""",
            null, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Decision, Is.EqualTo("ask"));
            Assert.That(verdict.Reason, Does.Contain("unavailable"));
            // ask não é memorizado — nova tentativa consultaria o juiz.
            Assert.That(SmartPermissionClassifier.Cached(
                _session, verdict.Fingerprint), Is.Null);
        });
    }

    /// <summary><c>smartPermission.model</c> no kv vence o resolve de
    /// papel — o request do juiz vai com o modelo configurado.</summary>
    [Test]
    public async Task ClassifyAsync_ModeloConfigurado_VencePapel()
    {
        await _config.SetAsync<string?>("smartPermission.model", "juiz-config");
        try
        {
            ChatCompletionRequest? captured = null;
            var classifier = NewClassifier((req, _, _) =>
            {
                captured = req;
                return Task.FromResult(JudgeSays(
                    """{"decision":"ask","reason":"suspeito"}"""));
            });

            var verdict = await classifier.ClassifyAsync(_session, NewRun(),
                "file_glob", """{"pattern":"*.md"}""",
                null, null, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(verdict.Decision, Is.EqualTo("ask"));
                Assert.That(verdict.Model, Is.EqualTo("juiz-config"));
                Assert.That(captured!.Model, Is.EqualTo("juiz-config"));
            });
        }
        finally
        {
            await _config.SetAsync<string?>("smartPermission.model", null);
        }
    }
}
