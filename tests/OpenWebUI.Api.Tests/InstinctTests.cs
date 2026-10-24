using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenWebUI.Api.Completions.Contributors;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Application.Interfaces;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Runs;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.SystemPrompt;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261023-instincts-learning: núcleo do InstinctService
/// (dedup/reforço, lifecycle, ações humanas, evolve), filtros do
/// InstinctExtractor e injeção rankeada do InstinctContributor.
/// </summary>
[TestFixture]
public class InstinctTests
{
    private string _root = null!;
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-instinct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, "test.db");
        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Users.Add(new User
        {
            Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user",
        });
        db.Users.Add(new User
        {
            Id = "u2", Name = "U2", Email = "u2@t.local", Role = "user",
        });
        db.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private InstinctService NewService(AppDbContext db) =>
        new(db,
            new ConfigService(db, TestCache.Create()),
            new UserConfigOverlayService(db, new ConfigService(db, TestCache.Create())),
            NullLogger<InstinctService>.Instance);

    private static Instinct Seed(
        string userId = "u1", string domain = "dotnet",
        string trigger = "when reviewing a PR", string body = "check tests",
        double conf = 0.8, string status = "active", string scope = "user",
        string? workspace = null, long? lastSeen = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new Instinct
        {
            UserId = userId, Scope = scope, WorkspaceId = workspace,
            Domain = domain, Trigger = trigger, Body = body,
            Confidence = conf, Status = status, HitCount = 1,
            SourceRefs = "[]",
            CreatedAt = now, UpdatedAt = now,
            LastSeenAt = lastSeen ?? now,
        };
    }

    // ---------- upsert / dedup ----------

    [Test]
    public async Task Upsert_Cria_Pending_Com_Conf_Inicial()
    {
        using var db = NewDb();
        var svc = NewService(db);
        var i = await svc.UpsertCandidateAsync(
            "u1", "user", null, "dotnet", "when building", "run dotnet test",
            "run-1", default);

        Assert.That(i, Is.Not.Null);
        Assert.That(i!.Status, Is.EqualTo("pending"));
        Assert.That(i.Confidence, Is.EqualTo(0.5).Within(0.001));
        Assert.That(i.HitCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Upsert_Dedup_Reforca_Existente()
    {
        using var db = NewDb();
        var svc = NewService(db);
        await svc.UpsertCandidateAsync(
            "u1", "user", null, "dotnet", "when building!", "run tests",
            "run-1", default);
        var second = await svc.UpsertCandidateAsync(
            "u1", "user", null, "dotnet", "  When   building ", "run tests again",
            "run-2", default);

        var all = await db.Instincts.Where(i => i.UserId == "u1").ToListAsync();
        Assert.That(all, Has.Count.EqualTo(1),
            "trigger normalizado igual + mesmo domain/scope = reforço, não duplica");
        Assert.That(second!.HitCount, Is.EqualTo(2));
        Assert.That(second.Confidence, Is.EqualTo(0.55).Within(0.001));
        Assert.That(second.SourceRefs, Does.Contain("run-1")
            .And.Contain("run-2"));
    }

    [Test]
    public async Task Upsert_Domain_Diferente_Cria_Novo()
    {
        using var db = NewDb();
        var svc = NewService(db);
        await svc.UpsertCandidateAsync(
            "u1", "user", null, "dotnet", "when building", "x", "r1", default);
        await svc.UpsertCandidateAsync(
            "u1", "user", null, "git", "when building", "x", "r1", default);
        Assert.That(
            await db.Instincts.CountAsync(i => i.UserId == "u1"),
            Is.EqualTo(2));
    }

    [Test]
    public async Task Upsert_Invalido_Devolve_Null()
    {
        using var db = NewDb();
        var svc = NewService(db);
        Assert.That(await svc.UpsertCandidateAsync(
            "u1", "user", null, "", "t", "b", "r", default), Is.Null);
        Assert.That(await svc.UpsertCandidateAsync(
            "u1", "user", null, "d", " ", "b", "r", default), Is.Null);
        Assert.That(await svc.UpsertCandidateAsync(
            "u1", "user", null, "d", "t", "", "r", default), Is.Null);
    }

    // ---------- lifecycle ----------

    [Test]
    public async Task Lifecycle_Promove_Pending_No_Threshold()
    {
        using var db = NewDb();
        db.Instincts.Add(Seed(conf: 0.75, status: "pending"));
        db.Instincts.Add(Seed(conf: 0.5, status: "pending",
            trigger: "other trigger"));
        db.SaveChanges();

        await NewService(db).ApplyLifecycleAsync("u1", default);
        var items = await db.Instincts.OrderBy(i => i.Trigger).ToListAsync();
        Assert.That(items[1].Status, Is.EqualTo("active"),
            "conf ≥ threshold (0.7) promove");
        Assert.That(items[0].Status, Is.EqualTo("pending"));
    }

    [Test]
    public async Task Lifecycle_Expira_Sem_Hit_30d()
    {
        using var db = NewDb();
        var old = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 31L * 86400;
        db.Instincts.Add(Seed(conf: 0.9, status: "active", lastSeen: old));
        db.Instincts.Add(Seed(conf: 0.9, status: "active",
            trigger: "fresh one"));
        db.SaveChanges();

        await NewService(db).ApplyLifecycleAsync("u1", default);
        var items = await db.Instincts.OrderBy(i => i.Trigger).ToListAsync();
        Assert.That(items[1].Status, Is.EqualTo("expired"));
        Assert.That(items[0].Status, Is.EqualTo("active"));
    }

    // ---------- ações humanas ----------

    [Test]
    public async Task Approve_Promove_Pending_Abaixo_Threshold()
    {
        using var db = NewDb();
        var i = Seed(conf: 0.5, status: "pending");
        db.Instincts.Add(i);
        db.SaveChanges();

        var result = await NewService(db).ApplyActionAsync(
            "u1", i.Id, "approve", default);
        Assert.That(result!.Status, Is.EqualTo("active"));
        Assert.That(result.Confidence,
            Is.GreaterThanOrEqualTo(0.71).Within(0.001),
            "aprovação fixa confiança em threshold+ε");
    }

    [Test]
    public async Task Reject_E_Delete_Funcionam()
    {
        using var db = NewDb();
        var a = Seed(trigger: "t1");
        var b = Seed(trigger: "t2");
        db.Instincts.AddRange(a, b);
        db.SaveChanges();
        var svc = NewService(db);

        Assert.That((await svc.ApplyActionAsync("u1", a.Id, "reject", default))!
            .Status, Is.EqualTo("rejected"));
        await svc.ApplyActionAsync("u1", b.Id, "delete", default);
        Assert.That(await db.Instincts.FindAsync(b.Id), Is.Null);
    }

    [Test]
    public async Task Action_De_Outro_Usuario_Devolve_Null()
    {
        using var db = NewDb();
        var i = Seed();
        db.Instincts.Add(i);
        db.SaveChanges();
        Assert.That(await NewService(db).ApplyActionAsync(
            "u2", i.Id, "approve", default), Is.Null);
    }

    // ---------- candidatos de injeção ----------

    [Test]
    public async Task Candidates_Auto_Filtra_Threshold_E_Scope()
    {
        using var db = NewDb();
        db.Instincts.AddRange(
            Seed(trigger: "t1", conf: 0.8),                          // user+active+.8 ✓
            Seed(trigger: "t2", conf: 0.6),                          // abaixo threshold
            Seed(trigger: "t3", conf: 0.9, status: "pending"),       // não active
            Seed(trigger: "t4", conf: 0.9, scope: "workspace",
                workspace: "o/a"),                                   // workspace≠repo
            Seed(trigger: "t5", conf: 0.9, scope: "workspace",
                workspace: "o/b"));                                  // workspace=repo ✓
        db.SaveChanges();

        var svc = NewService(db);
        var hits = await svc.InjectionCandidatesAsync("u1", "o/b", default);
        Assert.That(hits.Select(i => i.Trigger),
            Is.EquivalentTo(new[] { "t1", "t5" }));
    }

    [Test]
    public async Task Candidates_Mode_Off_Zera()
    {
        using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("instincts.mode", "off", default);
        db.Instincts.Add(Seed(conf: 0.95));
        db.SaveChanges();
        Assert.That(
            await NewService(db).InjectionCandidatesAsync("u1", null, default),
            Is.Empty);
    }

    [Test]
    public async Task Candidates_Mode_On_Ignora_Threshold()
    {
        using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("instincts.mode", "on", default);
        db.Instincts.Add(Seed(conf: 0.2));
        db.SaveChanges();
        Assert.That(
            await NewService(db).InjectionCandidatesAsync("u1", null, default),
            Has.Count.EqualTo(1));
    }

    // ---------- reforço / penalidade ----------

    [Test]
    public async Task RecordHits_Reforca_Elegiveis()
    {
        using var db = NewDb();
        var i = Seed(conf: 0.8);
        db.Instincts.Add(i);
        db.SaveChanges();
        var bumped = await NewService(db).RecordInjectionHitsAsync(
            "u1", null, default);
        Assert.That(bumped, Is.EqualTo(1));
        Assert.That((await db.Instincts.FindAsync(i.Id))!.Confidence,
            Is.EqualTo(0.85).Within(0.001));
    }

    [Test]
    public async Task RecordHits_Cap_095()
    {
        using var db = NewDb();
        var i = Seed(conf: 0.94);
        db.Instincts.Add(i);
        db.SaveChanges();
        await NewService(db).RecordInjectionHitsAsync("u1", null, default);
        Assert.That((await db.Instincts.FindAsync(i.Id))!.Confidence,
            Is.EqualTo(0.95).Within(0.001));
    }

    [Test]
    public async Task RecordFailure_Penaliza()
    {
        using var db = NewDb();
        var i = Seed(conf: 0.8);
        db.Instincts.Add(i);
        db.SaveChanges();
        await NewService(db).RecordFailureAsync("u1", null, default);
        Assert.That((await db.Instincts.FindAsync(i.Id))!.Confidence,
            Is.EqualTo(0.65).Within(0.001));
    }

    // ---------- evolve ----------

    [Test]
    public async Task Evolve_Gera_Draft_Com_3_Ativos()
    {
        using var db = NewDb();
        db.Instincts.AddRange(
            Seed(trigger: "t1", body: "b1"),
            Seed(trigger: "t2", body: "b2"),
            Seed(trigger: "t3", body: "b3"),
            Seed(trigger: "t4", body: "b4", status: "pending"));
        db.SaveChanges();

        var draft = await NewService(db).EvolveAsync(
            "u1", "dotnet", null, default);
        Assert.That(draft, Is.Not.Null);
        Assert.That(draft!.Value.Draft, Does.Contain("name: learned-dotnet"));
        Assert.That(draft.Value.Draft, Does.Contain("not policy"));
        Assert.That(draft.Value.Sources, Has.Count.EqualTo(3),
            "pending não entra no draft");
    }

    [Test]
    public async Task Evolve_Massa_Insuficiente_Devolve_Null()
    {
        using var db = NewDb();
        db.Instincts.AddRange(Seed(trigger: "t1"), Seed(trigger: "t2"));
        db.SaveChanges();
        Assert.That(await NewService(db).EvolveAsync(
            "u1", "dotnet", null, default), Is.Null);
    }

    // ---------- extractor: parse ----------

    [Test]
    public void ParseCandidates_Json_Puro()
    {
        var cands = InstinctExtractor.ParseCandidates(
            """[{"domain":"dotnet","trigger":"when t","body":"do x"}]""");
        Assert.That(cands, Has.Count.EqualTo(1));
        Assert.That(cands[0].Domain, Is.EqualTo("dotnet"));
    }

    [Test]
    public void ParseCandidates_Com_Prosa_E_Fence()
    {
        var reply = "Sure!\n```json\n[{\"domain\":\"git\",\"trigger\":\"t\",\"body\":\"b\"}]\n```\nDone.";
        Assert.That(InstinctExtractor.ParseCandidates(reply),
            Has.Count.EqualTo(1));
    }

    [Test]
    public void ParseCandidates_Lixo_Devolve_Vazio()
    {
        Assert.That(InstinctExtractor.ParseCandidates("no json at all"), Is.Empty);
        Assert.That(InstinctExtractor.ParseCandidates(""), Is.Empty);
        Assert.That(InstinctExtractor.ParseCandidates(
            """[{"domain":1}]"""), Is.Empty);
    }

    [Test]
    public void ParseCandidates_Cap_3()
    {
        var reply = "[" + string.Join(",",
            Enumerable.Range(0, 5).Select(i =>
                $"{{\"domain\":\"d{i}\",\"trigger\":\"t\",\"body\":\"b\"}}")) + "]";
        Assert.That(InstinctExtractor.ParseCandidates(reply),
            Has.Count.EqualTo(3));
    }

    // ---------- extractor: observer ----------

    [Test]
    public async Task Observer_Chat_Efemero_Ignora()
    {
        using var db = NewDb();
        db.Chats.Add(new Chat
        {
            Id = "c-eph", UserId = "u1", Title = "t",
            IsEphemeral = true, CreatedAt = 1, UpdatedAt = 1,
        });
        db.SaveChanges();

        var extractor = NewExtractor(_dbPath);
        await extractor.OnRunEventAsync(new AgentRunEvent(
            "r1", "c-eph", "u1", "run.completed", "{}"), default);
        using var check = NewDb();
        Assert.That(await check.Instincts.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task Observer_Chat_Gateway_Ignora_Sem_OptIn()
    {
        using var db = NewDb();
        db.Chats.Add(new Chat
        {
            Id = "c-gw", UserId = "u1", Title = "gateway:telegram:x",
            CreatedAt = 1, UpdatedAt = 1,
        });
        db.GatewayChannels.Add(new GatewayChannel
        {
            Id = "gwch", OwnerUserId = "u1",
            Platform = GatewayPlatforms.Telegram,
        });
        db.GatewayPeers.Add(new GatewayPeer
        {
            Id = "gwp", ChannelId = "gwch", ExternalChatId = "1",
            ExternalUserId = "2", PairedUserId = "u1", CreatedAt = 1,
        });
        db.GatewaySessions.Add(new GatewaySession
        {
            PeerId = "gwp", ChatId = "c-gw", CreatedAt = 1,
        });
        db.SaveChanges();

        var extractor = NewExtractor(_dbPath);
        await extractor.OnRunEventAsync(new AgentRunEvent(
            "r1", "c-gw", "u1", "run.completed", "{}"), default);
        using var check = NewDb();
        Assert.That(await check.Instincts.CountAsync(), Is.EqualTo(0),
            "conversa de terceiros não alimenta instincts sem opt-in");
    }

    [Test]
    public async Task Observer_Chat_Gateway_Com_OptIn_Passa_Filtro()
    {
        using var db = NewDb();
        db.Chats.Add(new Chat
        {
            Id = "c-gw2", UserId = "u1", Title = "gateway:telegram:x",
            CreatedAt = 1, UpdatedAt = 1,
        });
        db.GatewaySessions.Add(new GatewaySession
        {
            PeerId = "gwp2", ChatId = "c-gw2", CreatedAt = 1,
        });
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("instincts.includeGateway", true, default);
        db.SaveChanges();

        // Opt-in ligado → o filtro não corta; a extração segue até a
        // checagem de elegibilidade (sem steps → termina em silêncio).
        var extractor = NewExtractor(_dbPath);
        await extractor.OnRunEventAsync(new AgentRunEvent(
            "r1", "c-gw2", "u1", "run.completed", "{}"), default);
        using var check = NewDb();
        Assert.That(await check.Instincts.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task Observer_RunFailed_Penaliza_Elegiveis()
    {
        using var db = NewDb();
        db.Chats.Add(new Chat
        {
            Id = "c1", UserId = "u1", Title = "t",
            CreatedAt = 1, UpdatedAt = 1,
        });
        var i = Seed(conf: 0.8);
        db.Instincts.Add(i);
        db.SaveChanges();

        var extractor = NewExtractor(_dbPath);
        await extractor.OnRunEventAsync(new AgentRunEvent(
            "r1", "c1", "u1", "run.failed", "{}"), default);

        using var check = NewDb();
        Assert.That((await check.Instincts.FindAsync(i.Id))!.Confidence,
            Is.EqualTo(0.65).Within(0.001));
    }

    private static InstinctExtractor NewExtractor(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o =>
            o.UseSqlite($"Data Source={dbPath}"));
        services.AddLogging();
        services.AddHybridCache();
        services.AddScoped<ConfigService>();
        services.AddScoped<UserConfigOverlayService>();
        services.AddScoped<InstinctService>();
        services.AddScoped<WorkspaceRepoService>();
        services.AddScoped<ProviderService>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(
            new StubEnv());
        services.AddSingleton<IHttpClientFactory>(new StubHttpFactory());
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddScoped(sp => new ModelRoleResolver(
            sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<ConfigService>(),
            ct => sp.GetRequiredService<ProviderService>().ListModelsAsync(ct),
            Microsoft.Extensions.Logging.Abstractions
                .NullLogger<ModelRoleResolver>.Instance));
        var provider = services.BuildServiceProvider();
        return new InstinctExtractor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<InstinctExtractor>.Instance);
    }

    private sealed class StubEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider
        { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // ---------- contributor ----------

    [Test]
    public async Task Contributor_Injeta_Bloco_Com_Aviso()
    {
        using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        db.Instincts.Add(Seed(conf: 0.8, trigger: "when reviewing",
            body: "check coverage"));
        db.SaveChanges();
        var svc = NewService(db);

        var sections = await new InstinctContributor(svc, config)
            .ContributeAsync(Ctx());
        Assert.That(sections, Has.Count.EqualTo(1));
        Assert.That(sections[0], Does.Contain("<learned_instincts"));
        Assert.That(sections[0], Does.Contain("not policy"));
        Assert.That(sections[0], Does.Contain("[dotnet] when reviewing")
            .Or.Contain("[dotnet] when"));
    }

    [Test]
    public async Task Contributor_Off_Nao_Injeta()
    {
        using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("instincts.mode", "off", default);
        db.Instincts.Add(Seed(conf: 0.95));
        db.SaveChanges();
        var sections = await new InstinctContributor(
                NewService(db), config)
            .ContributeAsync(Ctx());
        Assert.That(sections, Is.Empty);
    }

    [Test]
    public async Task Contributor_Boost_Stack_Sobe_Rank()
    {
        using var db = NewDb();
        var workdir = Path.Join(_root, "wd");
        Directory.CreateDirectory(workdir);
        await File.WriteAllTextAsync(Path.Join(workdir, "a.cs"), "//");
        // dotnet .75 (com boost .1 → .85) vs python .80 (sem boost → .80)
        db.Instincts.Add(Seed(domain: "dotnet", conf: 0.75,
            trigger: "t-dotnet"));
        db.Instincts.Add(Seed(domain: "python", conf: 0.80,
            trigger: "t-python"));
        db.SaveChanges();
        var config = new ConfigService(db, TestCache.Create());

        var sections = await new InstinctContributor(NewService(db), config)
            .ContributeAsync(Ctx(workdir));
        Assert.That(sections, Has.Count.EqualTo(1));
        var dotnetIdx = sections[0].IndexOf("t-dotnet", StringComparison.Ordinal);
        var pythonIdx = sections[0].IndexOf("t-python", StringComparison.Ordinal);
        Assert.That(dotnetIdx, Is.GreaterThanOrEqualTo(0));
        Assert.That(dotnetIdx, Is.LessThan(pythonIdx),
            "boost de stack sobe dotnet .75 acima de python .80");
    }

    [Test]
    public async Task Contributor_Cap_Bloco_1200()
    {
        using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        for (var n = 0; n < 6; n++)
        {
            db.Instincts.Add(Seed(
                trigger: $"trigger {n} " + new string('x', 300),
                body: new string('y', 200), conf: 0.9));
        }
        db.SaveChanges();
        var sections = await new InstinctContributor(
                NewService(db), config)
            .ContributeAsync(Ctx());
        Assert.That(sections[0].Length,
            Is.LessThanOrEqualTo(InstinctContributor.MaxBlockChars));
        Assert.That(sections[0], Does.EndWith("</learned_instincts>"));
    }

    [Test]
    public async Task Contributor_StackDetection_Cobre_Markers_E_Extensoes()
    {
        using var db = NewDb();
        var workdir = Path.Join(_root, "wd-all");
        Directory.CreateDirectory(workdir);
        // Todos os marker files e extensões do switch de DetectStackAsync.
        string[] files =
        [
            "package.json", "tsconfig.json", "pyproject.toml",
            "requirements.txt", "pipfile", "go.mod", "cargo.toml",
            "pom.xml", "build.gradle", "build.gradle.kts", "gemfile",
            "composer.json", "dockerfile", "docker-compose.yaml",
            "docker-compose.yml",
            "a.cs", "a.razor", "a.csproj", "a.sln", "a.slnx",
            "a.py", "a.ts", "a.tsx", "a.js", "a.jsx", "a.vue",
            "a.svelte", "a.go", "a.rs", "a.java", "a.kt", "a.scala",
            "a.rb", "a.php", "a.sql", "a.tf", "a.unknown",
        ];
        foreach (var f in files)
        {
            await File.WriteAllTextAsync(Path.Join(workdir, f), "x");
        }
        // Um instinct por domínio, conf igual — todos entram (maxInjected
        // ampliado); o switch inteiro é exercitado pelo scan.
        var domains = new[]
        {
            "web", "python", "go", "rust", "jvm", "ruby", "php",
            "docker", "dotnet", "sql", "terraform",
        };
        foreach (var d in domains)
        {
            db.Instincts.Add(Seed(domain: d, trigger: $"t-{d}", conf: 0.8));
        }
        db.SaveChanges();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("instincts.maxInjected", 20, default);

        var sections = await new InstinctContributor(NewService(db), config)
            .ContributeAsync(Ctx(workdir));
        Assert.That(sections, Has.Count.EqualTo(1));
        foreach (var d in domains)
        {
            Assert.That(sections[0], Does.Contain($"t-{d}"));
        }
    }

    [Test]
    public async Task Contributor_Workdir_Ausente_Injeta_Por_Confianca()
    {
        using var db = NewDb();
        db.Instincts.Add(Seed(trigger: "t-solo", conf: 0.9));
        db.SaveChanges();
        var config = new ConfigService(db, TestCache.Create());

        // Workdir inexistente → DetectStack retorna vazio, sem boost.
        var sections = await new InstinctContributor(NewService(db), config)
            .ContributeAsync(Ctx(Path.Join(_root, "nao-existe")));
        Assert.That(sections, Has.Count.EqualTo(1));
        Assert.That(sections[0], Does.Contain("t-solo"));
    }

    [Test]
    public async Task Contributor_ScanCap_Respeitado_E_Injeta()
    {
        using var db = NewDb();
        var workdir = Path.Join(_root, "wd-many");
        Directory.CreateDirectory(workdir);
        for (var n = 0; n < 210; n++)
        {
            await File.WriteAllTextAsync(
                Path.Join(workdir, $"f{n}.cs"), "//");
        }
        db.Instincts.Add(Seed(trigger: "t-cap", conf: 0.9));
        db.SaveChanges();
        var config = new ConfigService(db, TestCache.Create());

        var sections = await new InstinctContributor(NewService(db), config)
            .ContributeAsync(Ctx(workdir));
        Assert.That(sections, Has.Count.EqualTo(1));
        Assert.That(sections[0], Does.Contain("t-cap"));
    }

    [Test]
    public async Task Contributor_MaxInjected_Clampado()
    {
        using var db = NewDb();
        for (var n = 0; n < 4; n++)
        {
            db.Instincts.Add(Seed(trigger: $"t-{n}", conf: 0.8));
        }
        db.SaveChanges();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("instincts.maxInjected", 2, default);

        var sections = await new InstinctContributor(NewService(db), config)
            .ContributeAsync(Ctx());
        // Só 2 linhas de instinct sob o clamp.
        var count = sections[0].Split('\n')
            .Count(l => l.StartsWith("- [", StringComparison.Ordinal));
        Assert.That(count, Is.EqualTo(2));
    }

    private static PromptContext Ctx(string? workdir = null) => new()
    {
        Request = new ChatCompletionRequest(
            "m", [new ChatCompletionMessage("user", "oi")], Stream: true),
        User = new User { Id = "u1", Name = "U", Email = "u@t.local" },
        LastUserText = "oi",
        Model = "m",
        Ct = CancellationToken.None,
        WorkdirFactory = _ => Task.FromResult(workdir ?? string.Empty),
    };
}
