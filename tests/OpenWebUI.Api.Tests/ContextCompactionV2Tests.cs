using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Context;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261102-context-compactor-v2: thresholds 3-tier (spawn/apply/hard),
/// resumo em background single-flight, pending kv, apply na fronteira e
/// hard-wait bounded — cobre RF-001..RF-004.
/// </summary>
[TestFixture]
public class ContextCompactionV2Tests
{
    private string _dir = null!;
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Join(Path.GetTempPath(), $"owui-compact2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Join(_dir, "t.db");
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_dir)) { Directory.Delete(_dir, true); } }
        catch { /* ignore */ }
    }

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static List<ChatCompletionMessage> Msgs(int n, int chars = 10) =>
        [.. Enumerable.Range(0, n).Select(i =>
            new ChatCompletionMessage(i % 2 == 0 ? "user" : "assistant", new string('x', chars)))];

    // RF-002: abaixo do spawn não dispara task nem toca o summarizer.
    [Test]
    public async Task Spawn_AbaixoDoLimiar_NaoDisparaTask()
    {
        await using var db = NewDb();
        var fake = new FakeSummarizer("RESUMO");
        var svc = NewService(db, fake);
        Assert.That((await svc.MaybeSpawnAsync(
            "c1", Msgs(4), "m", null, CancellationToken.None)).Spawned, Is.False);
        Assert.That(fake.Calls, Is.EqualTo(0));
    }

    // RF-001/002: ≥ spawn e < hard → task em background grava pendente,
    // sem tocar no estado aplicado nem no chat.
    [Test]
    public async Task Spawn_AcimaDoLimiar_DisparaEGravaPendente()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        // Alavanca de chars (o piso de tokens é 32k em produção):
        // hard=100 chars, spawn=70% → 16 msgs × 5c = 80 ∈ [70,100).
        await config.SetAsync("compaction:threshold", 100);
        await config.SetAsync("compaction:tail_messages", 2);
        var fake = new FakeSummarizer("RESUMO ASYNC");
        var svc = NewService(db, fake, config);

        Assert.That((await svc.MaybeSpawnAsync(
            "c1", Msgs(16, 5), "m", null, CancellationToken.None)).Spawned, Is.True);

        // Task completa em background → pendente gravado; aplicado vazio.
        var applied = await svc.WaitPendingAsync(
            "c1", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.That(applied.Applied, Is.True,
            string.Join(" | ", ListLogger<ContextCompactionService>.Messages));
        Assert.That(fake.Calls, Is.EqualTo(1));

        var window = await svc.ApplyAsync("c1", Msgs(16, 5), CancellationToken.None);
        Assert.That(window[0].Content, Does.Contain("RESUMO ASYNC"));
    }

    // RF-002 single-flight: segundo spawn com task em voo é no-op.
    [Test]
    public async Task Spawn_EmVoo_NaoDuplicaTask()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 100);
        await config.SetAsync("compaction:tail_messages", 2);
        var slow = new SlowSummarizer("RESUMO LENTO");
        var svc = NewService(db, slow, config);

        Assert.That((await svc.MaybeSpawnAsync(
            "c1", Msgs(16, 5), "m", null, CancellationToken.None)).Spawned, Is.True);
        Assert.That((await svc.MaybeSpawnAsync(
            "c1", Msgs(16, 5), "m", null, CancellationToken.None)).Spawned, Is.False,
            string.Join(" | ", ListLogger<ContextCompactionService>.Messages));

        slow.Release();
        Assert.That((await svc.WaitPendingAsync(
            "c1", TimeSpan.FromSeconds(10), CancellationToken.None)).Applied, Is.True);
        Assert.That(slow.Calls, Is.EqualTo(1));
    }

    // RF-003: pendente gravado move para o estado aplicado na fronteira —
    // janela sai [resumo]+tail, marker no chat, pendente removido.
    [Test]
    public async Task ApplyPending_ComResumoPronto_MoveParaJanela()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("chat:c1:compaction:pending", new
        {
            state = new { cutoff = 2, summary = "PENDENTE PRONTO" },
            spawnedAt = 1L,
            estimateTokens = 10,
        });
        var svc = NewService(db, new FakeSummarizer("x"), config);

        Assert.That((await svc.ApplyPendingAsync(
            "c1", 10, CancellationToken.None)).Applied, Is.True);

        var window = await svc.ApplyAsync("c1", Msgs(10, 5), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(window[0].Content, Does.Contain("PENDENTE PRONTO"));
            Assert.That(window, Has.Count.LessThan(10));
        });
        // Pendente consumido.
        Assert.That((await svc.ApplyPendingAsync(
            "c1", 10, CancellationToken.None)).Applied, Is.False);
        // Marker no chat aponta a compactação aplicada.
        await using var db2 = NewDb();
        var marker = await db2.ChatMessages
            .Where(m => m.ChatId == "c1" && m.Role == "compaction")
            .SingleOrDefaultAsync();
        Assert.That(marker!.Content, Is.EqualTo("PENDENTE PRONTO"));
    }

    // RF-003: pendente com cutoff menor que o aplicado (revert/branch
    // antigo) é descartado em vez de regredir a janela.
    [Test]
    public async Task ApplyPending_CutoffObsoleto_Descarta()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("chat:c1:compaction", new
        {
            cutoff = 8, summary = "APLICADO NOVO",
        });
        await config.SetAsync("chat:c1:compaction:pending", new
        {
            state = new { cutoff = 3, summary = "PENDENTE VELHO" },
            spawnedAt = 1L,
            estimateTokens = 10,
        });
        var svc = NewService(db, new FakeSummarizer("x"), config);

        Assert.That((await svc.ApplyPendingAsync(
            "c1", 10, CancellationToken.None)).Applied, Is.False);

        var window = await svc.ApplyAsync("c1", Msgs(12, 5), CancellationToken.None);
        Assert.That(window[0].Content, Does.Contain("APLICADO NOVO"));
    }

    // RF-004: sem task e sem pendente o hard-wait resolve na hora.
    [Test]
    public async Task WaitPending_SemTask_DevolveFalse()
    {
        await using var db = NewDb();
        var svc = NewService(db, new FakeSummarizer("x"));
        Assert.That((await svc.WaitPendingAsync(
            "c1", TimeSpan.FromMilliseconds(50), CancellationToken.None)).Applied, Is.False);
    }

    // RF-004: task em voo mais lenta que hard_wait_ms → espera esgota e o
    // chamador cai no caminho síncrono (a task órfã pode aplicar depois).
    [Test]
    public async Task WaitPending_TaskLenta_EsgotaSemAplicar()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 100);
        await config.SetAsync("compaction:tail_messages", 2);
        await config.SetAsync("compaction:hard_wait_ms", 200);
        var slow = new SlowSummarizer("RESUMO LENTO");
        var svc = NewService(db, slow, config);

        await svc.MaybeSpawnAsync("c1", Msgs(16, 5), "m", null, CancellationToken.None);
        var applied = await svc.WaitPendingAsync(
            "c1", TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.That(applied.Applied, Is.False);
        slow.Release();
    }

    // RF-001: override por modelo — compaction:{model}:spawn_ratio
    // antecipa o spawn para esse modelo; demais seguem o global.
    [Test]
    public async Task Spawn_OverridePorModelo_Respeita()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 500);
        await config.SetAsync("compaction:tail_messages", 2);
        await config.SetAsync("compaction:spawn_ratio", 0.95);
        await config.SetAsync("compaction:gpt-mini:spawn_ratio", 0.30);
        var fake = new FakeSummarizer("RESUMO MODELO");
        var svc = NewService(db, fake, config);

        // 200 chars: ≥30% do modelo (150), <95% do global (475).
        Assert.That((await svc.MaybeSpawnAsync(
            "c1", Msgs(40, 5), "gpt-mini", null, CancellationToken.None)).Spawned, Is.True);
        Assert.That((await svc.WaitPendingAsync(
            "c1", TimeSpan.FromSeconds(10), CancellationToken.None)).Applied, Is.True);
        Assert.That((await svc.MaybeSpawnAsync(
            "c1", Msgs(40, 5), "gpt-grande", null, CancellationToken.None)).Spawned, Is.False,
            string.Join(" | ", ListLogger<ContextCompactionService>.Messages));
    }

    // RF-001: spawn_ratio inválido cai no default 0.70 (sem disparar
    // cedo demais nem tarde demais).
    [Test]
    public async Task Spawn_RatioInvalido_CaiNoDefault()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 100);
        await config.SetAsync("compaction:tail_messages", 2);
        // ratio inválido → default 0.70 → spawn=70 chars; 80 ∈ [70,100).
        await config.SetAsync("compaction:spawn_ratio", "não-é-número");
        var fake = new FakeSummarizer("RESUMO RATIO");
        var svc = NewService(db, fake, config);

        Assert.That((await svc.MaybeSpawnAsync(
            "c1", Msgs(16, 5), "m", null, CancellationToken.None)).Spawned, Is.True);
        await svc.WaitPendingAsync("c1", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.That(fake.Calls, Is.EqualTo(1),
            string.Join(" | ", ListLogger<ContextCompactionService>.Messages));
    }

    // RF-005: o parser tolerante ganha `state` e `breadcrumbs` — JSON
    // estendido preenche; JSON legado e prosa seguem intactos.
    [Test]
    public void SummarizerV2_Parse_ExtraiStateEBreadcrumbs()
    {
        var parsed = LlmContextSummarizer.Parse(
            """{"summary":"## Overview\nMigração em curso","state":"Ajustando o compactor","breadcrumbs":["[3] pedido de merge","history://c1/msg:9"],"files":[],"decisions":[],"pending":[]}""");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.State, Is.EqualTo("Ajustando o compactor"));
            Assert.That(parsed.Breadcrumbs, Has.Count.EqualTo(2));
            Assert.That(parsed.Breadcrumbs![0], Is.EqualTo("[3] pedido de merge"));
        });
    }

    [Test]
    public void SummarizerV2_Parse_LegacySemNovosCampos()
    {
        var parsed = LlmContextSummarizer.Parse(
            """{"summary":"texto","files":[],"decisions":[],"pending":[]}""");
        Assert.Multiple(() =>
        {
            Assert.That(parsed.State, Is.Null);
            Assert.That(parsed.Breadcrumbs, Is.Empty);
        });
    }

    // RF-006: mensagens role=user do trecho compactado voltam verbatim,
    // em ordem cronológica, sob a seção "Pedidos do usuário (verbatim):".
    [Test]
    public async Task Compact_UserRecords_PreservaVerbatim()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 100);
        await config.SetAsync("compaction:tail_messages", 2);
        var svc = NewService(db, new FakeSummarizer("RESUMO"), config);
        var history = Msgs(16, 5).ToList();
        history[2] = new ChatCompletionMessage("user", "PEDIDO-ALFA importante");
        history[5] = new ChatCompletionMessage("user", "PEDIDO-BETA segundo");

        await svc.MaybeCompactAsync("c1", history, "m", CancellationToken.None, force: true);
        var window = await svc.ApplyAsync("c1", history, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(window[0].Content, Does.Contain("Pedidos do usuário"));
            Assert.That(window[0].Content, Does.Contain("PEDIDO-ALFA importante"));
            Assert.That(window[0].Content, Does.Contain("PEDIDO-BETA segundo"));
        });
    }

    // RF-007: o último todo_write no trecho compactado vira seção
    // "Tarefas:" com o JSON das tarefas preservado.
    [Test]
    public async Task Compact_Todos_PreservaUltimoWrite()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 100);
        await config.SetAsync("compaction:tail_messages", 2);
        var svc = NewService(db, new FakeSummarizer("RESUMO"), config);
        var history = Msgs(16, 5).ToList();
        history[3] = new ChatCompletionMessage("assistant", string.Empty,
            ToolCallsJson:
            """[{"id":"c1","type":"function","function":{"name":"builtin:todo_write","arguments":"{\"todos\":[{\"id\":\"t1\",\"content\":\"TAREFA-UM\",\"status\":\"completed\"},{\"id\":\"t2\",\"content\":\"TAREFA-DOIS\",\"status\":\"in_progress\"}]}"}}]""");
        history[4] = new ChatCompletionMessage("tool", "Plano atualizado: 2 tarefa(s)",
            ToolCallId: "c1");

        await svc.MaybeCompactAsync("c1", history, "m", CancellationToken.None, force: true);
        var window = await svc.ApplyAsync("c1", history, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(window[0].Content, Does.Contain("Tarefas:"));
            Assert.That(window[0].Content, Does.Contain("TAREFA-DOIS"));
        });
    }

    // RF-008: a nota <context_usage> fecha a janela com a estimativa em k —
    // flag off e playground (sem chat) não injetam.
    [Test]
    public async Task ContextUsage_InjetaNotaPorPadrao()
    {
        await using var db = NewDb();
        var svc = NewService(db, new FakeSummarizer("x"));
        var msgs = Msgs(4, 1000);
        var window = await svc.WithContextUsageAsync(
            "c1", msgs, "m", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(window, Has.Count.EqualTo(msgs.Count + 1));
            Assert.That(window[^1].Role, Is.EqualTo("system"));
            Assert.That(window[^1].Content, Does.Contain("<context_usage>"));
            Assert.That(window[^1].Content, Does.Contain("1k of"));
            Assert.That(window[^1].Content, Does.Contain("tokens used"));
        });
    }

    [Test]
    public async Task ContextUsage_FlagOff_ESemChat_NaoInjetam()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:context_usage", false);
        var svc = NewService(db, new FakeSummarizer("x"), config);
        var msgs = Msgs(4);
        Assert.Multiple(async () =>
        {
            Assert.That(await svc.WithContextUsageAsync(
                "c1", msgs, "m", CancellationToken.None), Has.Count.EqualTo(msgs.Count));
            Assert.That(await svc.WithContextUsageAsync(
                null, msgs, "m", CancellationToken.None), Has.Count.EqualTo(msgs.Count));
        });
    }

    // ---- Cobertura dirigida dos caminhos novos ----

    // Infra: o scheduler garante single-flight, expõe InFlight e Clear
    // libera o slot sem matar a task em voo.
    [Test]
    public async Task Scheduler_SingleFlight_InFlight_Clear()
    {
        var sched = new AsyncCompactionScheduler();
        var gate = new TaskCompletionSource();
        Assert.Multiple(() =>
        {
            Assert.That(sched.InFlight("c1"), Is.False);
            Assert.That(sched.TryStart("c1", _ => gate.Task), Is.True);
            Assert.That(sched.InFlight("c1"), Is.True);
            Assert.That(sched.TryStart("c1", _ => Task.CompletedTask), Is.False);
        });
        sched.Clear("c1");
        Assert.That(sched.InFlight("c1"), Is.False);
        gate.SetResult();
        await Task.Delay(100);
        // Slot limpo: WaitAsync não acha task e TryStart libera novo spawn.
        Assert.That(await sched.WaitAsync(
            "c1", TimeSpan.FromMilliseconds(10), CancellationToken.None),
            Is.False);
        Assert.That(sched.TryStart("c1", _ => Task.CompletedTask), Is.True);
        await Task.Delay(100);
        Assert.That(sched.InFlight("c1"), Is.False);
    }

    // RF-004: abaixo do hard o hard-wait é no-op instantâneo.
    [Test]
    public async Task HardWait_AbaixoDoHard_NaoEspera()
    {
        await using var db = NewDb();
        var svc = NewService(db, new FakeSummarizer("x"));
        var r = await svc.HardWaitAsync(
            "c1", Msgs(4), "m", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(r.Waited, Is.False);
            Assert.That(r.Applied, Is.False);
            Assert.That(r.FailedStage, Is.Null);
        });
    }

    // RF-004: ≥ hard com pendente pronto → o wait aplica e reporta cutoff.
    [Test]
    public async Task HardWait_ComPendente_AplicaEReporta()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 100);
        await config.SetAsync("chat:c1:compaction:pending", new
        {
            state = new { cutoff = 2, summary = "PENDENTE HARD" },
            spawnedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            estimateTokens = 50,
        });
        var svc = NewService(db, new FakeSummarizer("x"), config);

        var r = await svc.HardWaitAsync(
            "c1", Msgs(40, 5), "m", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(r.Waited, Is.True);
            Assert.That(r.Applied, Is.True);
            Assert.That(r.Cutoff, Is.EqualTo(2));
            Assert.That(r.SpawnedAt, Is.Not.Null);
        });
    }

    // RF-001: ≥ hard o spawn não dispara — a compactação síncrona cuida.
    [Test]
    public async Task Spawn_AcimaDoHard_NaoDispara()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 100);
        var svc = NewService(db, new FakeSummarizer("x"), config);
        var r = await svc.MaybeSpawnAsync(
            "c1", Msgs(40, 5), "m", null, CancellationToken.None);
        Assert.That(r.Spawned, Is.False);
    }

    // RF-005/006/007/009: o estado aplicado renderiza digest (estado,
    // breadcrumbs), records verbatim, tarefas e o ponteiro history://.
    [Test]
    public async Task Apply_RenderizaSecoesV2()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("chat:c1:compaction", new
        {
            cutoff = 2,
            summary = "RESUMO BASE",
            digest = new
            {
                text = "TEXTO DIGEST",
                state = "Ajustando o loop",
                breadcrumbs = new[] { "[2] pedido de merge" },
                files = new[] { "src/a.cs" },
                decisions = Array.Empty<string>(),
                pending = Array.Empty<string>(),
            },
            userMessages = new[] { "PEDIDO-UM", "PEDIDO-DOIS" },
            todosJson = "[{\"content\":\"t1\"}]",
        });
        var svc = NewService(db, new FakeSummarizer("x"), config);

        var window = await svc.ApplyAsync(
            "c1", Msgs(12, 5), CancellationToken.None);
        var summary = window[0].Content!;
        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("TEXTO DIGEST"));
            Assert.That(summary, Does.Contain("Estado atual: Ajustando o loop"));
            Assert.That(summary, Does.Contain("[2] pedido de merge"));
            Assert.That(summary, Does.Contain("PEDIDO-UM"));
            Assert.That(summary, Does.Contain("Tarefas:"));
            Assert.That(summary, Does.Contain("history://c1"));
        });
    }

    // RF-006: mais de 20 pedidos → últimos 20 + marcador de omissão;
    // mensagens sintéticas nunca viram record.
    [Test]
    public async Task Records_Cap20_MarcadorESinteticasFora()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 100);
        await config.SetAsync("compaction:tail_messages", 2);
        var svc = NewService(db, new FakeSummarizer("RESUMO"), config);
        var history = Msgs(50, 5).ToList();
        for (var i = 0; i < 48; i += 2)
        {
            history[i] = new ChatCompletionMessage("user", $"PEDIDO-{i:D2}");
        }
        // Sintéticas dentro do head: resumo anterior + nota de contexto.
        history[0] = new ChatCompletionMessage(
            "user", ContextCompactionService.SummaryPrefix + "resumo velho");
        history[2] = new ChatCompletionMessage("user", "[contexto] nota efêmera");

        await svc.MaybeCompactAsync("c1", history, "m", CancellationToken.None, force: true);
        var window = await svc.ApplyAsync("c1", history, CancellationToken.None);
        var summary = window[0].Content!;
        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("Pedidos do usuário"));
            Assert.That(summary, Does.Contain("PEDIDO-46"));
            Assert.That(summary, Does.Contain("anteriores omitidas"));
            Assert.That(summary, Does.Not.Contain("PEDIDO-04"));
            Assert.That(summary, Does.Not.Contain("resumo velho"));
            Assert.That(summary, Does.Not.Contain("nota efêmera"));
        });
    }

    // RF-005: breadcrumbs [pos] fora do range do transcript são
    // descartados; âncoras válidas e termos livres sobrevivem.
    [Test]
    public async Task Compact_Breadcrumbs_ForaDoRange_Descartados()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 100);
        await config.SetAsync("compaction:tail_messages", 2);
        var digest = new ContextSummary("RESUMO BC", [], [], [],
            Breadcrumbs: ["[999] fora do range", "[2] valida", "termo livre"]);
        var svc = NewService(db, new FakeSummarizer(digest), config);

        var history = Msgs(16, 5).ToList();
        await svc.MaybeCompactAsync("c1", history, "m", CancellationToken.None, force: true);
        var window = await svc.ApplyAsync("c1", history, CancellationToken.None);
        var summary = window[0].Content!;
        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("[2] valida"));
            Assert.That(summary, Does.Contain("termo livre"));
            Assert.That(summary, Does.Not.Contain("[999]"));
        });
    }

    // RF-002: sem IServiceScopeFactory a task usa os deps do próprio
    // serviço — pendente grava e aplica igual.
    [Test]
    public async Task Spawn_SemScopeFactory_GravaEAplica()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 100);
        await config.SetAsync("compaction:tail_messages", 2);
        var fake = new FakeSummarizer("RESUMO SEM-SCOPE");
        var svc = NewService(db, fake, config, withScopes: false);

        Assert.That((await svc.MaybeSpawnAsync(
            "c1", Msgs(16, 5), "m", null, CancellationToken.None)).Spawned, Is.True);
        Assert.That((await svc.WaitPendingAsync(
            "c1", TimeSpan.FromSeconds(10), CancellationToken.None)).Applied, Is.True);
        Assert.That(fake.Calls, Is.EqualTo(1));
        var window = await svc.ApplyAsync("c1", Msgs(16, 5), CancellationToken.None);
        Assert.That(window[0].Content, Does.Contain("RESUMO SEM-SCOPE"));
    }

    // RF-003: pending corrompido no kv → descartado sem falhar.
    [Test]
    public async Task ApplyPending_JsonCorrompido_Descarta()
    {
        await using var db = NewDb();
        db.ConfigEntries.Add(new ConfigEntry
        {
            Key = "chat:c1:compaction:pending", ValueJson = "{lixo sem fecha",
        });
        await db.SaveChangesAsync();
        var svc = NewService(db, new FakeSummarizer("x"));
        Assert.That((await svc.ApplyPendingAsync(
            "c1", 10, CancellationToken.None)).Applied, Is.False);
    }

    // RF-007: a forma interna builtin_todo_write (underline) também casa
    // — o tool_call pode persistir nos dois prefixos.
    [Test]
    public async Task Compact_Todos_NomeUnderline_TambemCasa()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 100);
        await config.SetAsync("compaction:tail_messages", 2);
        var svc = NewService(db, new FakeSummarizer("RESUMO"), config);
        var history = Msgs(16, 5).ToList();
        history[3] = new ChatCompletionMessage("assistant", string.Empty,
            ToolCallsJson:
            """[{"id":"c1","type":"function","function":{"name":"builtin_todo_write","arguments":"{\"todos\":[{\"id\":\"t1\",\"content\":\"TAREFA-UNDERLINE\",\"status\":\"in_progress\"}]}"}}]""");

        await svc.MaybeCompactAsync("c1", history, "m", CancellationToken.None, force: true);
        var window = await svc.ApplyAsync("c1", history, CancellationToken.None);
        Assert.That(window[0].Content, Does.Contain("TAREFA-UNDERLINE"));
    }

    // Defesa: dependências indisponíveis (db disposto) viram
    // FailedStage/false — nunca propagam exceção ao executor.
    [Test]
    public async Task Catchs_DbDisposto_FailedStageEFalse()
    {
        var db = NewDb();
        var svc = NewService(db, new FakeSummarizer("x"));
        await db.DisposeAsync();

        var spawn = await svc.MaybeSpawnAsync(
            "c1", Msgs(4), "m", null, CancellationToken.None);
        var wait = await svc.HardWaitAsync(
            "c1", Msgs(4), "m", CancellationToken.None);
        var compact = await svc.MaybeCompactAsync(
            "c1", Msgs(4), "m", CancellationToken.None, force: true);
        Assert.Multiple(() =>
        {
            Assert.That(spawn.FailedStage, Is.EqualTo("spawn"));
            Assert.That(wait.FailedStage, Is.EqualTo("hard_wait"));
            Assert.That(compact, Is.False);
        });
    }

    // RF-007: arguments também chega como objeto JSON (não só string
    // encodada) e o nome curto "todo_write" casa.
    [Test]
    public async Task Compact_Todos_ArgumentosObjeto_TambemExtrai()
    {
        await using var db = NewDb();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        await db.SaveChangesAsync();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:threshold", 100);
        await config.SetAsync("compaction:tail_messages", 2);
        var svc = NewService(db, new FakeSummarizer("RESUMO"), config);
        var history = Msgs(16, 5).ToList();
        history[3] = new ChatCompletionMessage("assistant", string.Empty,
            ToolCallsJson:
            """[{"id":"c1","type":"function","function":{"name":"todo_write","arguments":{"todos":[{"content":"TAREFA-OBJ"}]}}}]""");

        await svc.MaybeCompactAsync("c1", history, "m", CancellationToken.None, force: true);
        var window = await svc.ApplyAsync("c1", history, CancellationToken.None);
        Assert.That(window[0].Content, Does.Contain("TAREFA-OBJ"));
    }

    private ContextCompactionService NewService(
        AppDbContext db, IContextSummarizer summarizer,
        ConfigService? config = null, bool withScopes = true)
    {
        var cfg = config ?? new ConfigService(db, TestCache.Create());
        var logs = new ListLogger<ContextCompactionService>();
        IServiceScopeFactory? scopes = null;
        if (withScopes)
        {
            // Scope factory real: a task de background resolve DbContext/
            // ConfigService/Summarizer frescos no mesmo arquivo SQLite —
            // sem corrida com o contexto do chamador.
            var dbPath = _dbPath;
            var services = new ServiceCollection()
                .AddScoped(_ => new AppDbContext(
                    new DbContextOptionsBuilder<AppDbContext>()
                        .UseSqlite($"Data Source={dbPath}").Options))
                .AddScoped(sp => new ConfigService(
                    sp.GetRequiredService<AppDbContext>(), TestCache.Create()))
                .AddSingleton<IContextSummarizer>(summarizer)
                .AddScoped(sp => new ModelRoleResolver(
                    sp.GetRequiredService<AppDbContext>(),
                    sp.GetRequiredService<ConfigService>(),
                    _ => Task.FromResult(new List<ModelInfo>()),
                    NullLogger<ModelRoleResolver>.Instance))
                .BuildServiceProvider();
            scopes = services.GetRequiredService<IServiceScopeFactory>();
        }
        return new(db, cfg, summarizer,
            new ModelRoleResolver(db, cfg,
                _ => Task.FromResult(new List<ModelInfo>()),
                NullLogger<ModelRoleResolver>.Instance),
            logs,
            new CompactionDeps(Scopes: scopes),
            new AsyncCompactionScheduler());
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public static readonly ConcurrentQueue<string> Messages = new();
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }

    private sealed class FakeSummarizer : IContextSummarizer
    {
        private readonly ContextSummary? _result;

        public FakeSummarizer(string? result) =>
            _result = result is null ? null
                : new ContextSummary(result, [], [], []);

        public FakeSummarizer(ContextSummary? result) => _result = result;

        public int Calls { get; private set; }

        public Task<ContextSummary?> SummarizeAsync(string model, string transcript, string? userId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class SlowSummarizer(string result) : IContextSummarizer
    {
        private readonly TaskCompletionSource _gate = new();

        public int Calls { get; private set; }

        public void Release() => _gate.TrySetResult();

        public async Task<ContextSummary?> SummarizeAsync(string model, string transcript, string? userId, CancellationToken ct)
        {
            Calls++;
            await _gate.Task.WaitAsync(ct);
            return new ContextSummary(result, [], [], []);
        }
    }
}
