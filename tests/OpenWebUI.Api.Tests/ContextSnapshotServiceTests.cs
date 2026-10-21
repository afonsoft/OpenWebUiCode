using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261020-context-snapshots RF-003/RF-004/RF-006: persistência de
/// snapshots por chat (versão monotônica, retenção, RepoKey do binding),
/// validação de payload (kinds, tamanhos, control chars, segredos) e a
/// compactação de itens sob budget (elision → summary → corte ordenado).
/// </summary>
[TestFixture]
public class ContextSnapshotServiceTests : SnapshotTestBase
{
    private ContextSnapshotService NewService(AppDbContext db, ConfigService config) =>
        Snapshots(db, config);

    private static ContextSnapshotService.SnapshotItem Item(
        string kind, string content, string? title = null, double? score = null) =>
        new(kind, title, content, score);

    // ---------- RF-003: persistência ----------

    [Test]
    public async Task SaveAsync_CriaVersao1_LatestDevolve()
    {
        // AC: save e recovery no mesmo chat — version 1 com os itens.
        await using var db = NewDb();
        var svc = NewService(db, new ConfigService(db, TestCache.Create()));

        var (snap, err) = await svc.SaveAsync("c1", "u1", "lbl",
            [Item("decision", "usar tokens"), Item("note", "nota")],
            "none", compacted: false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(err, Is.Null);
            Assert.That(snap, Is.Not.Null);
            Assert.That(snap!.Version, Is.EqualTo(1));
            Assert.That(snap.ChatId, Is.EqualTo("c1"));
            Assert.That(snap.RepoKey, Is.Null);
            Assert.That(snap.CreatedByUserId, Is.EqualTo("u1"));
        });

        var items = ContextSnapshotService.DeserializeItems(snap.PayloadJson);
        Assert.That(items, Has.Count.EqualTo(2));
        Assert.That(items[0].Kind, Is.EqualTo("decision"));

        var latest = await svc.LatestAsync("c1", CancellationToken.None);
        Assert.That(latest!.Id, Is.EqualTo(snap.Id));
    }

    [Test]
    public async Task SaveAsync_VersoesIncrementaisPorChat()
    {
        await using var db = NewDb();
        var svc = NewService(db, new ConfigService(db, TestCache.Create()));
        var items = new[] { Item("note", "n") };

        var (a, _) = await svc.SaveAsync("c1", "u1", null, items, "none", false, CancellationToken.None);
        var (b, _) = await svc.SaveAsync("c1", "u1", null, items, "none", false, CancellationToken.None);
        var (other, _) = await svc.SaveAsync("c2", "u1", null, items, "none", false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(a!.Version, Is.EqualTo(1));
            Assert.That(b!.Version, Is.EqualTo(2));
            // Chat diferente tem sequência própria.
            Assert.That(other!.Version, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SaveAsync_Retencao_RemoveMaisAntigo()
    {
        // AC: max=2 e 3 saves → total fica 2, mais antigo removido.
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:max_snapshots_per_chat", 2);
        var svc = NewService(db, config);
        var items = new[] { Item("note", "n") };

        await svc.SaveAsync("c1", "u1", null, items, "none", false, CancellationToken.None);
        await svc.SaveAsync("c1", "u1", null, items, "none", false, CancellationToken.None);
        var (third, _) = await svc.SaveAsync("c1", "u1", null, items, "none", false, CancellationToken.None);

        var all = await svc.ListAsync("c1", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(all, Has.Count.EqualTo(2));
            Assert.That(all.Select(s => s.Version), Is.EquivalentTo(new[] { 2, 3 }));
            Assert.That(third!.Version, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task SaveAsync_RepoKeyDoBinding_Lowercase()
    {
        // AC: chat com binding → RepoKey = slug owner/repo normalizado.
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        var repos = new WorkspaceRepoService(config, new TestEnv(Dir));
        await repos.SetChatBindingAsync("c1",
            new WorkspaceRepoBinding("Afonsoft/KnowledgeRAG", "main", "d"), CancellationToken.None);
        var svc = new ContextSnapshotService(db, config, repos);

        var (snap, _) = await svc.SaveAsync("c1", "u1", null,
            [Item("note", "n")], "none", false, CancellationToken.None);

        Assert.That(snap!.RepoKey, Is.EqualTo("afonsoft/knowledgerag"));
    }

    [Test]
    public async Task Latest_IsolamentoEntreChats()
    {
        // AC: snapshot no chat A → recovery no chat B não acha nada.
        await using var db = NewDb();
        var svc = NewService(db, new ConfigService(db, TestCache.Create()));
        await svc.SaveAsync("a", "u1", null, [Item("note", "n")], "none", false, CancellationToken.None);

        Assert.That(await svc.LatestAsync("b", CancellationToken.None), Is.Null);
        Assert.That(await svc.GetAsync("b", 1, CancellationToken.None), Is.Null);
    }

    // ---------- RF-004: validação ----------

    [Test]
    public async Task SaveAsync_SecretDetectado_NadaGravado()
    {
        // AC: conteúdo com "aft_" + 32 hex → secret_detected, nada gravado.
        await using var db = NewDb();
        var svc = NewService(db, new ConfigService(db, TestCache.Create()));
        var secret = "aft_" + new string('a', 32);

        var (snap, err) = await svc.SaveAsync("c1", "u1", null,
            [Item("note", $"token: {secret}")], "none", false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(snap, Is.Null);
            Assert.That(err!.Code, Is.EqualTo("secret_detected"));
            Assert.That(db.ContextSnapshots.Count(), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task SaveAsync_PayloadMuitoGrande_Recusa()
    {
        // Edge: serializado > compaction:max_payload_bytes → payload_too_large.
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:max_payload_bytes", 64_000); // mínimo da faixa
        var svc = NewService(db, config);

        var big = new string('x', 16_000);
        var items = Enumerable.Range(0, 5).Select(_ => Item("note", big)).ToList();
        var (snap, err) = await svc.SaveAsync("c1", "u1", null, items, "none", false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(snap, Is.Null);
            Assert.That(err!.Code, Is.EqualTo("payload_too_large"));
        });
    }

    [Test]
    public async Task SaveAsync_ContentAcimaDoCap_InvalidPayload()
    {
        await using var db = NewDb();
        var svc = NewService(db, new ConfigService(db, TestCache.Create()));

        var (snap, err) = await svc.SaveAsync("c1", "u1", null,
            [Item("note", new string('x', 16_001))], "none", false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(snap, Is.Null);
            Assert.That(err!.Code, Is.EqualTo("invalid_payload"));
        });
    }

    [Test]
    public async Task SaveAsync_ControlCharsRejeitados()
    {
        await using var db = NewDb();
        var svc = NewService(db, new ConfigService(db, TestCache.Create()));

        var (snap, err) = await svc.SaveAsync("c1", "u1", null,
            [Item("note", "a\u0007b")], "none", false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(snap, Is.Null);
            Assert.That(err!.Code, Is.EqualTo("invalid_payload"));
        });
    }

    [Test]
    public async Task SaveAsync_MaxItemsRespeitado()
    {
        await using var db = NewDb();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("compaction:max_items_per_snapshot", 2);
        var svc = NewService(db, config);

        var (snap, err) = await svc.SaveAsync("c1", "u1", null,
            [Item("note", "a"), Item("note", "b"), Item("note", "c")],
            "none", false, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(snap, Is.Null);
            Assert.That(err!.Code, Is.EqualTo("invalid_payload"));
        });
    }

    [Test]
    public void ParseItems_CampoDesconhecido_InvalidPayload()
    {
        // Edge: {"foo":1} no item → invalid_payload.
        var doc = JsonDocument.Parse("""[{"kind":"note","content":"x","foo":1}]""");
        var (items, err) = ContextSnapshotService.ParseItems(doc.RootElement, 200);
        Assert.Multiple(() =>
        {
            Assert.That(items, Is.Null);
            Assert.That(err!.Code, Is.EqualTo("invalid_payload"));
        });
    }

    [Test]
    public void ParseItems_KindInvalido_InvalidPayload()
    {
        var doc = JsonDocument.Parse("""[{"kind":"weird","content":"x"}]""");
        var (items, err) = ContextSnapshotService.ParseItems(doc.RootElement, 200);
        Assert.Multiple(() =>
        {
            Assert.That(items, Is.Null);
            Assert.That(err!.Code, Is.EqualTo("invalid_payload"));
        });
    }

    [Test]
    public void ParseItems_Roundtrip()
    {
        var doc = JsonDocument.Parse(
            """[{"kind":"decision","title":"t","content":"c","score":0.5,"elided":true}]""");
        var (items, err) = ContextSnapshotService.ParseItems(doc.RootElement, 200);
        Assert.That(err, Is.Null);
        Assert.Multiple(() =>
        {
            Assert.That(items![0].Kind, Is.EqualTo("decision"));
            Assert.That(items[0].Score, Is.EqualTo(0.5));
            Assert.That(items[0].Elided, Is.True);
        });

        var back = ContextSnapshotService.ParseItems(
            JsonDocument.Parse(ContextSnapshotService.SerializeItems(items!)).RootElement, 200);
        Assert.That(back.Items![0].Kind, Is.EqualTo("decision"));
        Assert.That(back.Items[0].Elided, Is.True);
    }

    // ---------- RF-005/RF-006: compactor ----------

    [Test]
    public async Task ApplyBudget_Elision_StubToolOutput_EntradaIntacta()
    {
        // AC: recovery com elision → tool_output vira stub, decision/note
        // permanecem, e a lista original (o snapshot) não muda.
        var items = new List<ContextSnapshotService.SnapshotItem>
        {
            Item("decision", "decisão"),
            Item("tool_output", new string('y', 5_000)),
            Item("note", "nota"),
        };
        var originalContent = items[1].Content;

        var (result, stats) = await ContextSnapshotCompactor.ApplyBudgetAsync(
            items, "elision", 512, summarizer: null, model: null,
            userId: null, "c1", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result[1].Content, Does.Contain("elidido"));
            Assert.That(result[1].Elided, Is.True);
            Assert.That(result[0].Content, Is.EqualTo("decisão"));
            Assert.That(result[2].Content, Is.EqualTo("nota"));
            Assert.That(items[1].Content, Is.EqualTo(originalContent)); // entrada intacta
            Assert.That(stats.ElidedCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ApplyBudget_SummarizerFalha_FallbackSemErro()
    {
        // AC: summarizer lança → summaryFallback=true, sem erro.
        var items = new List<ContextSnapshotService.SnapshotItem>
        {
            Item("decision", "d"),
            Item("passage", new string('p', 9_000)),
            Item("tool_output", new string('o', 9_000)),
        };

        var (result, stats) = await ContextSnapshotCompactor.ApplyBudgetAsync(
            items, "elision+summary", 512, new ThrowingSummarizer(), "m",
            "u1", "c1", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(stats.SummaryFallback, Is.True);
            Assert.That(stats.SummaryUsed, Is.False);
            Assert.That(result, Is.Not.Null);
        });
    }

    [Test]
    public async Task ApplyBudget_SummarizerOk_ViraItemSummary()
    {
        var items = new List<ContextSnapshotService.SnapshotItem>
        {
            Item("decision", "manter"),
            Item("passage", new string('p', 9_000)),
            Item("tool_output", new string('o', 9_000)),
        };

        var (result, stats) = await ContextSnapshotCompactor.ApplyBudgetAsync(
            items, "elision+summary", 512, new FakeSummarizer("RESUMO X"), "m",
            "u1", "c1", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(stats.SummaryUsed, Is.True);
            Assert.That(result.Any(i => i.Kind == "summary"
                && i.Content.Contains("RESUMO X")), Is.True);
            Assert.That(result[0].Kind, Is.EqualTo("decision"));
            Assert.That(result.Count(i => i.Kind == "passage"
                || i.Kind == "tool_output"), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task ApplyBudget_CorteOrdenado_PassageMenorScoreDepoisToolOutput()
    {
        // RF-006: acima do budget após elision → remove passage de menor
        // score, depois tool_output mais antigo; decision/note nunca saem.
        // Cada item ~200 chars (50 tok); budget 120 tok (480 chars) remove
        // os 2 passages + o tool_output mais antigo e para — "recente" e
        // "fica" sobrevivem.
        var items = new List<ContextSnapshotService.SnapshotItem>
        {
            Item("decision", "fica" + new string('f', 196)),
            Item("tool_output", "antigo" + new string('a', 194)),
            Item("passage", "score-baixo" + new string('b', 189), score: 0.1),
            Item("passage", "score-alto" + new string('c', 190), score: 0.9),
            Item("tool_output", "recente" + new string('r', 193)),
        };

        var (result, stats) = await ContextSnapshotCompactor.ApplyBudgetAsync(
            items, "none", 120, summarizer: null, model: null,
            userId: null, "c1", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Multiple(() =>
        {
            // strategy none → elidedCount 0 e só cortes.
            Assert.That(stats.ElidedCount, Is.EqualTo(0));
            // "score-baixo" sai antes de "score-alto"; "antigo" antes de
            // "recente"; "fica" (decision) nunca sai.
            var contents = result.Select(i => i.Content).ToList();
            Assert.That(contents, Does.Not.Contain("score-baixo" + new string('b', 189)));
            Assert.That(contents, Does.Not.Contain("antigo" + new string('a', 194)));
            Assert.That(contents.Any(c => c.StartsWith("fica")), Is.True);
            Assert.That(contents.Any(c => c.StartsWith("recente")), Is.True);
            Assert.That(stats.WithinBudget, Is.True);
            Assert.That(ContextSnapshotService.EstimateTokens(result), Is.LessThanOrEqualTo(120));
        });
    }

    [Test]
    public async Task ApplyBudget_SoProtegidosAcimaBudget_WithinFalse()
    {
        // RF-006: só decision/note restam e ainda excede → withinBudget=false.
        var items = new List<ContextSnapshotService.SnapshotItem>
        {
            Item("decision", new string('d', 2_000)),
            Item("note", new string('n', 2_000)),
        };

        var (_, stats) = await ContextSnapshotCompactor.ApplyBudgetAsync(
            items, "none", 256, summarizer: null, model: null,
            userId: null, "c1", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.That(stats.WithinBudget, Is.False);
    }
}
