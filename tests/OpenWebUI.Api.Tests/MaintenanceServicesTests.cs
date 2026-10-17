using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do auto-sync de memória e da retenção
/// (SPEC-20261010-memory-autosync-retention): serviços invocados via
/// <c>RunOnceAsync</c> — o agendamento em background não é exercitado aqui.
/// </summary>
[TestFixture]
public class MaintenanceServicesTests
{
    private string _root = null!;
    private string _dbPath = null!;
    private ServiceProvider _provider = null!;
    private OllamaStubHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-maint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, "test.db");
        lock (ListLogger.Messages)
        {
            ListLogger.Messages.Clear();
        }

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(
            o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddLogging(b => b.AddProvider(ListLogger.Provider)
            .SetMinimumLevel(LogLevel.Debug));
        services.AddHybridCache();
        services.AddSingleton<IHostEnvironment>(new StubEnvLocal(_root));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        _handler = new OllamaStubHandler();
        services.AddSingleton<IHttpClientFactory>(
            new StubHttpClientFactory(_handler));
        services.AddScoped<ConfigService>();
        services.AddScoped<WorkspaceRepoService>();
        services.AddScoped<ProviderService>();
        services.AddScoped<NotificationService>();
        services.AddSingleton<WorktreeService>();
        services.AddScoped<UserConfigOverlayService>();
        services.AddScoped(sp => new ModelRoleResolver(
            sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<ConfigService>(),
            ct => sp.GetRequiredService<ProviderService>().ListModelsAsync(ct),
            sp.GetRequiredService<ILogger<ModelRoleResolver>>()));
        services.AddScoped<MemorySyncService>();
        services.AddScoped<RetentionService>();
        _provider = services.BuildServiceProvider();

        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        _handler.Dispose();
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private MemorySyncService NewMemSync() =>
        _provider.GetRequiredService<MemorySyncService>();

    private RetentionService NewRetention() =>
        _provider.GetRequiredService<RetentionService>();

    private ConfigService Config() => _provider.GetRequiredService<ConfigService>();

    private async Task SeedChatAsync(string id, string title, long updatedAt)
    {
        await using var db = NewDb();
        var chat = new Chat
        {
            Id = id, UserId = "u1", Title = title,
            ModelsJson = """["fake:1"]""",
            CreatedAt = updatedAt, UpdatedAt = updatedAt,
        };
        chat.Messages.Add(new ChatMessage
        {
            ChatId = id, Role = "user", Content = "o deploy usa fly.io",
            Position = 0, Timestamp = updatedAt,
        });
        chat.Messages.Add(new ChatMessage
        {
            ChatId = id, Role = "assistant", Content = "anotado",
            Position = 1, Timestamp = updatedAt,
        });
        db.Chats.Add(chat);
        await db.SaveChangesAsync();
    }

    // ---------------- Memory sync ----------------

    [Test]
    public async Task MemSync_DestilaChat_GravaMemoriaAuto()
    {
        await Config().SetAsync("connections",
            new ConnectionsConfig(["http://stub.local"], [], []));
        await SeedChatAsync("c1", "deploy notes", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await NewMemSync().RunOnceAsync(default);

        await using var db = NewDb();
        var memory = await db.AgentMemories.SingleOrDefaultAsync(m => m.UserId == "u1");
        Assert.That(memory, Is.Not.Null,
            "logs: " + string.Join(" | ", ListLogger.Messages));
        Assert.Multiple(() =>
        {
            Assert.That(memory!.Title, Is.EqualTo("auto:deploy notes"));
            Assert.That(memory.Scope, Is.EqualTo("global"));
            Assert.That(memory.Content, Does.Contain("fato1"));
        });
    }

    [Test]
    public async Task MemSync_Watermark_NaoReprocessa()
    {
        await Config().SetAsync("connections",
            new ConnectionsConfig(["http://stub.local"], [], []));
        await SeedChatAsync("c1", "t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var sync = NewMemSync();
        await sync.RunOnceAsync(default);
        await sync.RunOnceAsync(default);

        await using var db = NewDb();
        Assert.That(await db.AgentMemories.CountAsync(m => m.UserId == "u1"), Is.EqualTo(1));
    }

    [Test]
    public async Task MemSync_OptOut_NaoSincroniza()
    {
        await Config().SetAsync("u:u1:memsync.enabled", false);
        await SeedChatAsync("c1", "t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await NewMemSync().RunOnceAsync(default);

        await using var db = NewDb();
        Assert.That(await db.AgentMemories.CountAsync(m => m.UserId == "u1"), Is.Zero);
    }

    [Test]
    public async Task MemSync_Poda_Mantem25Auto()
    {
        await Config().SetAsync("connections",
            new ConnectionsConfig(["http://stub.local"], [], []));
        await using (var db = NewDb())
        {
            for (var i = 0; i < 27; i++)
            {
                db.AgentMemories.Add(new AgentMemory
                {
                    UserId = "u1", Title = $"auto:old{i}", Content = "x",
                    UpdatedAt = i,
                });
            }
            await db.SaveChangesAsync();
        }
        await SeedChatAsync("c1", "nova", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await NewMemSync().RunOnceAsync(default);

        await using var check = NewDb();
        var count = await check.AgentMemories
            .CountAsync(m => m.UserId == "u1" && m.Title.StartsWith("auto:"));
        Assert.That(count, Is.EqualTo(25));
    }

    [Test]
    public async Task Config_Get_Miss_NaoGrudaFallbackNoCache()
    {
        var cfg = Config();
        // Chave inexistente: leituras com fallbacks diferentes não podem
        // contaminar o slot compartilhado bool/bool? do cache.
        var a = await cfg.GetAsync("zzz.missing", false);
        var b = await cfg.GetAsync<bool?>("zzz.missing", null);
        var c = await cfg.GetAsync("zzz.missing", true);
        TestContext.Out.WriteLine($"a={a} b={(b?.ToString() ?? "null")} c={c}");
        Assert.Multiple(() =>
        {
            Assert.That(a, Is.False);
            Assert.That(b, Is.Null);
            Assert.That(c, Is.True);
        });
    }

    [Test]
    public async Task MemSync_UsaPapelMemory_QuandoConfigurado()
    {
        // SPEC-20261014-memory-pipeline AC: cadeia do papel `memory` do
        // usuário resolve o modelo da destilação em vez do modelo do chat.
        await Config().SetAsync("connections",
            new ConnectionsConfig(["http://stub.local"], [], []));
        var resolver = _provider.GetRequiredService<ModelRoleResolver>();
        await resolver.SetRolesAsync("u1",
            new Dictionary<string, List<string>> { ["memory"] = ["fake:mem"] },
            default);
        await SeedChatAsync("c1", "deploy notes", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await NewMemSync().RunOnceAsync(default);

        Assert.That(_handler.LastChatModel, Is.EqualTo("fake:mem"));
    }

    [Test]
    public async Task MemSync_AdminGlobalOff_EhAbsoluto()
    {
        // RF-004: `memsync.enabled=false` global é negativa absoluta — nem
        // opt-in explícito do usuário reabilita (custo de provider).
        await Config().SetAsync("connections",
            new ConnectionsConfig(["http://stub.local"], [], []));
        await Config().SetAsync("memsync.enabled", false);
        await Config().SetAsync("u:u1:memsync.enabled", true);
        await SeedChatAsync("c1", "t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await NewMemSync().RunOnceAsync(default);

        await using var db = NewDb();
        Assert.That(await db.AgentMemories.CountAsync(m => m.UserId == "u1"), Is.Zero);
    }

    [Test]
    public async Task MemSync_MaxAgeDays_PulaChatVelho()
    {
        await Config().SetAsync("connections",
            new ConnectionsConfig(["http://stub.local"], [], []));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Chat de 40 dias atrás — fora da janela default de 30d.
        await SeedChatAsync("c-old", "antigo", now - 40L * 86400);
        await SeedChatAsync("c-new", "recente", now);

        await NewMemSync().RunOnceAsync(default);

        await using var db = NewDb();
        var titles = await db.AgentMemories
            .Where(m => m.UserId == "u1").Select(m => m.Title).ToListAsync();
        Assert.That(titles, Is.EqualTo(new[] { "auto:recente" }));
    }

    [Test]
    public async Task MemSync_Intervalo_NaoRodaAntesDaHora()
    {
        // last = agora-1h, intervalo de 6h → mesmo com chat novo, não roda.
        await Config().SetAsync("connections",
            new ConnectionsConfig(["http://stub.local"], [], []));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await Config().SetAsync("u:u1:memsync.last", now - 3600);
        await SeedChatAsync("c1", "t", now - 1800);

        await NewMemSync().RunOnceAsync(default);

        Assert.That(_handler.LastChatModel, Is.Null);
        await using var db = NewDb();
        Assert.That(await db.AgentMemories.CountAsync(m => m.UserId == "u1"), Is.Zero);
    }

    [Test]
    public async Task MemSync_IntervaloOverride_RodaQuandoVencido()
    {
        // Intervalo de 1h do usuário + last de 2h atrás → roda.
        await Config().SetAsync("connections",
            new ConnectionsConfig(["http://stub.local"], [], []));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await Config().SetAsync("u:u1:memsync.intervalHours", 1);
        await Config().SetAsync("u:u1:memsync.last", now - 7200);
        await SeedChatAsync("c1", "t", now - 3600);

        await NewMemSync().RunOnceAsync(default);

        Assert.That(_handler.LastChatModel, Is.Not.Null);
    }

    [Test]
    public async Task MemoryLearn_GravaEDedupa()
    {
        var tool = new MemoryLearnBuiltinTool(NewDb());
        var ctx = new OpenWebUI.Infrastructure.ChatTools.BuiltinToolContext(
            "u1", "c1", null, _root, _root);
        var args = System.Text.Json.JsonDocument.Parse(
            """{"lesson":"rodar testes exige docker","context":"deploy","tags":["tests","ci"]}""")
            .RootElement;

        var first = await tool.ExecuteAsync(args, ctx, default);
        var second = await tool.ExecuteAsync(args, ctx, default);

        Assert.Multiple(() =>
        {
            Assert.That(first.Text, Does.Contain("Lição registrada"));
            Assert.That(second.Text, Does.Contain("Lição atualizada"));
        });
        await using var db = NewDb();
        var mems = await db.AgentMemories
            .Where(m => m.UserId == "u1" && m.Title.StartsWith("learn:")).ToListAsync();
        Assert.That(mems, Has.Count.EqualTo(1));
        Assert.That(mems[0].Content, Does.Contain("tags: tests, ci"));
    }

    // ---------------- Retention ----------------

    [Test]
    public async Task Retention_ExpurgaRegistrosVelhos_MantemNovos()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var dia = 24 * 3600L;
        await using (var db = NewDb())
        {
            db.Automations.Add(new Automation
            {
                Id = "a1", UserId = "u1", Name = "a", Prompt = "p",
                ModelId = "m", CreatedAt = now,
            });
            db.AutomationRuns.AddRange(
                new AutomationRun
                {
                    AutomationId = "a1", Status = "ok",
                    StartedAt = now - 91 * dia, FinishedAt = now - 91 * dia,
                },
                new AutomationRun
                {
                    AutomationId = "a1", Status = "ok",
                    StartedAt = now - 1 * dia, FinishedAt = now - 1 * dia,
                });
            db.Notifications.AddRange(
                new Notification
                {
                    UserId = "u1", Kind = "k", Title = "velha",
                    CreatedAt = now - 61 * dia,
                },
                new Notification
                {
                    UserId = "u1", Kind = "k", Title = "nova", CreatedAt = now,
                });
            db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
            db.ChatRuns.Add(new ChatRun
            {
                Id = "r1", ChatId = "c1", UserId = "u1", Model = "m",
                Status = ChatRunStatus.Completed,
                CompletedAt = now - 40 * dia, RequestJson = "{}",
            });
            db.ChatRunSteers.AddRange(
                new ChatRunSteer
                {
                    RunId = "r1", ChatId = "c1", Content = "old",
                    Timestamp = now - 31 * dia,
                },
                new ChatRunSteer
                {
                    RunId = "r1", ChatId = "c1", Content = "recente",
                    Timestamp = now - 1 * dia,
                });
            await db.SaveChangesAsync();
        }

        await NewRetention().RunOnceAsync(default);

        await using var check = NewDb();
        Assert.Multiple(() =>
        {
            Assert.That(check.AutomationRuns.Count(), Is.EqualTo(1));
            Assert.That(check.Notifications.Count(), Is.EqualTo(1));
            Assert.That(check.ChatRunSteers.Single().Content, Is.EqualTo("recente"));
        });
    }

    [Test]
    public async Task Retention_Vacuum_RespeitaIntervaloSemanal()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var recent = now - 24 * 3600;
        await Config().SetAsync("sys:vacuum.last", recent);

        await NewRetention().RunOnceAsync(default);

        // Vacuum era recente — timestamp não avança.
        Assert.That(await Config().GetAsync("sys:vacuum.last", 0L), Is.EqualTo(recent));

        // Força last antigo → roda e atualiza.
        await Config().SetAsync("sys:vacuum.last", now - 8L * 24 * 3600);
        await NewRetention().RunOnceAsync(default);
        Assert.That(await Config().GetAsync("sys:vacuum.last", 0L),
            Is.GreaterThanOrEqualTo(now));
    }

    // ---- session-maintenance (#411): poda de chats com destilação ----

    [Test]
    public async Task Poda_ChatVelho_DestilaEDeleta()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-40).ToUnixTimeSeconds();
        await SeedChatAsync("old-chat", "deploy fly.io", old);
        await Config().SetAsync(RetentionService.ChatDaysKey, 30, CancellationToken.None);

        var result = await NewRetention().RunCycleAsync(dryRun: false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ChatsPurged, Is.EqualTo(1));
            Assert.That(result.MemoriesSynthesized, Is.EqualTo(1));
        });
        await using var db = NewDb();
        Assert.That(await db.Chats.FindAsync("old-chat"), Is.Null);
        Assert.That(await db.AgentMemories.AnyAsync(
            m => m.UserId == "u1" && m.Title.StartsWith("auto:")), Is.True);
    }

    [Test]
    public async Task Poda_DryRun_ContaSemDeletar()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-40).ToUnixTimeSeconds();
        await SeedChatAsync("dry-chat", "dry", old);
        await Config().SetAsync(RetentionService.ChatDaysKey, 30, CancellationToken.None);

        var result = await NewRetention().RunCycleAsync(dryRun: true, CancellationToken.None);

        Assert.That(result.ChatsPurged, Is.EqualTo(1));
        await using var db = NewDb();
        Assert.That(await db.Chats.FindAsync("dry-chat"), Is.Not.Null);
        Assert.That(await db.AgentMemories.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task Poda_PinadoERecente_EBinding_NaoPodam()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-40).ToUnixTimeSeconds();
        await SeedChatAsync("pinned-chat", "pinned", old);
        await SeedChatAsync("bound-chat", "bound", old);
        await SeedChatAsync("new-chat", "new", DateTimeOffset.UtcNow.AddDays(-5).ToUnixTimeSeconds());
        await Config().SetAsync(RetentionService.ChatDaysKey, 30, CancellationToken.None);
        await using (var db = NewDb())
        {
            (await db.Chats.FindAsync("pinned-chat"))!.Pinned = true;
            await db.SaveChangesAsync();
        }
        // Binding de workspace por chat — nunca podado por idade.
        await Config().SetAsync("chat:bound-chat:workspace.repo",
            new { slug = "a/b" }, CancellationToken.None);

        var result = await NewRetention().RunCycleAsync(dryRun: false, CancellationToken.None);

        Assert.That(result.ChatsPurged, Is.Zero);
        await using var check = NewDb();
        Assert.That(await check.Chats.CountAsync(), Is.EqualTo(3));
    }

    [Test]
    public async Task Poda_ProviderFalha_ChatNaoEhDeletado()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-40).ToUnixTimeSeconds();
        await SeedChatAsync("fragile-chat", "fragile", old);
        await Config().SetAsync(RetentionService.ChatDaysKey, 30, CancellationToken.None);
        _handler.FailChat = true;

        var result = await NewRetention().RunCycleAsync(dryRun: false, CancellationToken.None);

        Assert.That(result.ChatsPurged, Is.Zero);
        await using var db = NewDb();
        Assert.That(await db.Chats.FindAsync("fragile-chat"), Is.Not.Null);
    }

    [Test]
    public async Task Status_RefleteUltimoCiclo()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-40).ToUnixTimeSeconds();
        await SeedChatAsync("status-chat", "st", old);
        await Config().SetAsync(RetentionService.ChatDaysKey, 30, CancellationToken.None);

        await NewRetention().RunCycleAsync(dryRun: false, CancellationToken.None);
        var status = await NewRetention().GetStatusAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(status.LastRun, Is.Not.Null);
            Assert.That(status.LastRun!.ChatsPurged, Is.EqualTo(1));
            Assert.That(status.ChatDays, Is.EqualTo(30));
            Assert.That(status.NextRunAt, Is.GreaterThan(0));
        });
    }

    // ---------------- Stubs ----------------

    private sealed class OllamaStubHandler : HttpMessageHandler
    {
        /// <summary>Último modelo pedido em /api/chat (asserção de papel).</summary>
        public string? LastChatModel;

        /// <summary>Quando true, /api/chat responde 500 (provider transitório).</summary>
        public bool FailChat;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (FailChat && request.RequestUri!.AbsolutePath == "/api/chat")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            var json = request.RequestUri!.AbsolutePath switch
            {
                "/api/tags" => """{"models":[{"model":"fake:1","name":"fake:1"},{"model":"fake:mem","name":"fake:mem"}]}""",
                "/api/chat" => await ChatReplyAsync(request),
                _ => "{}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        private async Task<string> ChatReplyAsync(HttpRequestMessage request)
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            LastChatModel = doc.RootElement.TryGetProperty("model", out var m)
                ? m.GetString() : null;
            return """{"message":{"content":"fato1\nfato2"}}""";
        }
    }

    /// <summary>Captura logs do memsync para inspeção de falhas do sync.</summary>
    private sealed class ListLogger(string category) : ILogger
    {
        public static readonly ListLoggerProvider Provider = new();
        public static readonly List<string> Messages = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) =>
            category.Contains("MemorySync")
                || (level >= LogLevel.Warning && !category.Contains("EntityFramework"));
        public void Log<TState>(LogLevel level, EventId id, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level))
            {
                lock (Messages)
                {
                    Messages.Add(formatter(state, exception) + " " + exception);
                }
            }
        }
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName);
        public void Dispose() { }
    }

    private sealed class StubEnvLocal(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
