using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do SPEC-20261013-agent-context-management: ciclo de vida
/// checkpoint→rewind (RF-001), filtragem do contexto sem deletar mensagens
/// (RF-002), notebook persistente com cap e replace (RF-003), flag de
/// compactação a pedido (RF-004) e o gate admin <c>contexttools.enabled</c>
/// (RF-005). Erros de tool voltam ao modelo sem quebrar a run.
/// </summary>
[TestFixture]
public class ContextBoundaryTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp() => _dbPath = TestInfra.NewDbPath("context-boundary");

    [TearDown]
    public void TearDown() => TestInfra.DeleteDb(_dbPath);

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static ContextBoundaryService Boundaries(AppDbContext db) =>
        BoundariesAndConfig(db).Svc;

    /// <summary>
    /// Serviço + o <see cref="ConfigService"/> que alimenta o mesmo cache —
    /// caches separadas tornariam invisível o <c>SetAsync</c> do teste.
    /// </summary>
    private static (ContextBoundaryService Svc, ConfigService Config) BoundariesAndConfig(
        AppDbContext db)
    {
        var config = new ConfigService(db, TestCache.Create());
        return (new ContextBoundaryService(db, config), config);
    }

    private static BuiltinToolContext Ctx(string? chatId, string? runId = "r1") =>
        new("u1", chatId, runId, "/tmp/ws", "/tmp/up");

    /// <summary>Semeia chat com <paramref name="count"/> mensagens pos 0..n-1.</summary>
    private static async Task<Chat> SeedChatAsync(AppDbContext db, int count)
    {
        if (!await db.Users.AnyAsync(u => u.Id == "u1"))
        {
            db.Users.Add(new User
            {
                Id = "u1", Name = "U", Email = "u@x",
                PasswordHash = "x", Role = "user",
            });
            await db.SaveChangesAsync();
        }
        var chat = new Chat { UserId = "u1", Title = "t" };
        for (var i = 0; i < count; i++)
        {
            chat.Messages.Add(new ChatMessage
            {
                ChatId = chat.Id,
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"m{i}",
                Position = i,
                Timestamp = i,
            });
        }
        db.Chats.Add(chat);
        await db.SaveChangesAsync();
        return chat;
    }

    private static JsonElement Args(params (string k, string v)[] props)
    {
        var dict = props.ToDictionary(p => p.k, p => (object)p.v);
        return JsonSerializer.SerializeToElement(dict);
    }

    // ---- checkpoint (RF-001) ----

    [Test]
    public async Task Mark_AbreNaUltimaPosicaoMaisUm()
    {
        using var db = NewDb();
        var chat = await SeedChatAsync(db, 3);
        var svc = Boundaries(db);

        var from = await svc.MarkAsync(chat.Id, "r1", "explorar", default);

        Assert.Multiple(() =>
        {
            Assert.That(from, Is.EqualTo(3));
            var open = svc.ListAsync(chat.Id, default).Result.Single();
            Assert.That(open.Open, Is.True);
            Assert.That(open.FromPos, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Mark_DoubleOpenNaMesmaRun_RetornaNull()
    {
        using var db = NewDb();
        var chat = await SeedChatAsync(db, 1);
        var svc = Boundaries(db);

        Assert.That(await svc.MarkAsync(chat.Id, "r1", "a", default), Is.Not.Null);
        Assert.That(await svc.MarkAsync(chat.Id, "r1", "b", default), Is.Null);
        Assert.That((await svc.ListAsync(chat.Id, default)).Count(b => b.Open), Is.EqualTo(1));
    }

    [Test]
    public async Task Mark_OrfaodeOutraRun_EhDescartado()
    {
        using var db = NewDb();
        var chat = await SeedChatAsync(db, 1);
        var svc = Boundaries(db);

        // Run "r-morta" morreu com checkpoint aberto.
        await svc.MarkAsync(chat.Id, "r-morta", "velho", default);
        var from = await svc.MarkAsync(chat.Id, "r1", "novo", default);

        Assert.Multiple(() =>
        {
            Assert.That(from, Is.EqualTo(1));
            var boundaries = svc.ListAsync(chat.Id, default).Result;
            Assert.That(boundaries.Count(b => b.Open), Is.EqualTo(1));
            Assert.That(boundaries.Single(b => b.Open).RunId, Is.EqualTo("r1"));
        });
    }

    // ---- rewind (RF-001/RF-002) ----

    [Test]
    public async Task Commit_SemCheckpointAberto_RetornaNull()
    {
        using var db = NewDb();
        var chat = await SeedChatAsync(db, 2);
        Assert.That(await Boundaries(db).CommitAsync(chat.Id, "r1", "relato", default), Is.Null);
    }

    [Test]
    public async Task Commit_IntervaloVazio_FechaSemBoundary()
    {
        using var db = NewDb();
        var chat = await SeedChatAsync(db, 2);
        var svc = Boundaries(db);
        await svc.MarkAsync(chat.Id, "r1", "nada", default); // from=2; lastPos=1

        var result = await svc.CommitAsync(chat.Id, "r1", "relato", default);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Value.Covered, Is.EqualTo(-1));
            Assert.That(svc.ListAsync(chat.Id, default).Result, Is.Empty);
            // Nenhum marcador persistido.
            Assert.That(db.ChatMessages.Count(m => m.ChatId == chat.Id), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Commit_FechaIntervalo_EGravaMarcador()
    {
        using var db = NewDb();
        var chat = await SeedChatAsync(db, 2);            // pos 0,1
        var svc = Boundaries(db);
        await svc.MarkAsync(chat.Id, "r1", "pesquisa", default); // from=2
        // Run persiste 3 mensagens exploradas.
        for (var i = 0; i < 3; i++)
        {
            db.ChatMessages.Add(new ChatMessage
            {
                ChatId = chat.Id, Role = "assistant", Content = $"expl{i}",
                Position = 2 + i, Timestamp = 10 + i,
            });
        }
        await db.SaveChangesAsync();

        var result = await svc.CommitAsync(chat.Id, "r1", "achei X", default);

        Assert.Multiple(() =>
        {
            Assert.That(result!.Value.Covered, Is.EqualTo(3));
            var closed = svc.ListAsync(chat.Id, default).Result.Single();
            Assert.That(closed.Open, Is.False);
            Assert.That((closed.FromPos, closed.ToPos), Is.EqualTo((2, 4)));
        });
        var marker = await db.ChatMessages
            .SingleAsync(m => m.ChatId == chat.Id && m.Role == "compaction");
        Assert.Multiple(() =>
        {
            Assert.That(marker.Position, Is.EqualTo(5));
            Assert.That(marker.Content, Does.Contain("achei X"));
        });
    }

    [Test]
    public async Task FilterMessages_DropaIntervaloFechado_MantemMarcador()
    {
        using var db = NewDb();
        var chat = await SeedChatAsync(db, 6); // pos 0..5
        var svc = Boundaries(db);
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync($"chat:{chat.Id}:context-boundaries",
            new List<ContextBoundary>
            {
                new(1, 3, "x", Open: false),
                new(5, -1, "aberto", Open: true),
            }, default);

        var msgs = await db.ChatMessages.Where(m => m.ChatId == chat.Id)
            .OrderBy(m => m.Position).ToListAsync();
        var filtered = ContextBoundaryService.FilterMessages(msgs, await svc.ListAsync(chat.Id, default));

        // Boundary fechada dropa pos 1..3; a aberta (run morta) não filtra nada.
        Assert.That(filtered.Select(m => m.Position), Is.EqualTo(new[] { 0, 4, 5 }));
        // Persistido intacto — nada deletado.
        Assert.That(await db.ChatMessages.CountAsync(m => m.ChatId == chat.Id), Is.EqualTo(6));
    }

    [Test]
    public async Task Commit_BoundaryCobreOutraFechada_UneReports()
    {
        using var db = NewDb();
        var chat = await SeedChatAsync(db, 8); // pos 0..7
        var (svc, config) = BoundariesAndConfig(db);
        // Boundary fechada B1 cobre 5..7; o marcador fica em 8 (report "relatoA"),
        // depois o histórico segue em 9 e 10.
        db.ChatMessages.Add(new ChatMessage
        {
            ChatId = chat.Id, Role = "compaction", Content = "relatoA",
            Position = 8, Timestamp = 8,
        });
        for (var i = 0; i < 2; i++)
        {
            db.ChatMessages.Add(new ChatMessage
            {
                ChatId = chat.Id, Role = "assistant", Content = $"expl{i}",
                Position = 9 + i, Timestamp = 9 + i,
            });
        }
        await db.SaveChangesAsync();
        // Boundary aberta B2 (da run atual) desde 4 — vai cobrir B1 toda.
        await config.SetAsync($"chat:{chat.Id}:context-boundaries",
            new List<ContextBoundary>
            {
                new(5, 7, "a", Open: false),
                new(4, -1, "b", Open: true, RunId: "r1"),
            }, default);

        var result = await svc.CommitAsync(chat.Id, "r1", "relatoB", default);

        Assert.Multiple(() =>
        {
            Assert.That(result!.Value.Report, Does.Contain("relatoA").And.Contain("relatoB"));
            var boundaries = svc.ListAsync(chat.Id, default).Result;
            Assert.That(boundaries.Count, Is.EqualTo(1));
            Assert.That((boundaries[0].FromPos, boundaries[0].ToPos), Is.EqualTo((4, 10)));
        });
    }

    // ---- notebook (RF-003) ----

    [Test]
    public async Task Notes_ReadWrite_RoundTrip()
    {
        using var db = NewDb();
        var svc = Boundaries(db);
        const string chatId = "c1";

        Assert.That(await svc.GetNotesAsync(chatId, default), Is.Null);

        await svc.SetNotesAsync(chatId, "objetivo: migrar p/ postgres", default);
        Assert.That(await svc.GetNotesAsync(chatId, default),
            Is.EqualTo("objetivo: migrar p/ postgres"));

        // Replace, não append.
        await svc.SetNotesAsync(chatId, "novo estado", default);
        Assert.That(await svc.GetNotesAsync(chatId, default), Is.EqualTo("novo estado"));

        // Vazio remove.
        await svc.SetNotesAsync(chatId, "", default);
        Assert.That(await svc.GetNotesAsync(chatId, default), Is.Null);
    }

    [Test]
    public async Task Notes_Cap8k_Recusa()
    {
        using var db = NewDb();
        var svc = Boundaries(db);
        var big = new string('x', ContextBoundaryService.MaxNoteChars + 1);

        Assert.That(await svc.SetNotesAsync("c1", big, default), Is.False);
        Assert.That(await svc.GetNotesAsync("c1", default), Is.Null);
        // Exatamente no cap passa.
        Assert.That(await svc.SetNotesAsync("c1",
            new string('x', ContextBoundaryService.MaxNoteChars), default), Is.True);
    }

    // ---- new_context (RF-004) ----

    [Test]
    public async Task NewContext_Flag_ConsumidaUmaVez()
    {
        using var db = NewDb();
        var svc = Boundaries(db);

        Assert.That(await svc.ConsumeNewContextAsync("r1", default), Is.False);
        await svc.RequestNewContextAsync("r1", default);
        Assert.That(await svc.ConsumeNewContextAsync("r1", default), Is.True);
        Assert.That(await svc.ConsumeNewContextAsync("r1", default), Is.False);
    }

    // ---- gate admin (RF-005) ----

    [Test]
    public async Task Gate_DefaultLigado_FalseDesliga()
    {
        using var db = NewDb();
        var (svc, config) = BoundariesAndConfig(db);

        Assert.That(await svc.EnabledAsync(default), Is.True);
        await config.SetAsync("contexttools.enabled", false, default);
        Assert.That(await svc.EnabledAsync(default), Is.False);
    }

    // ---- erros de tool (voltam ao modelo sem quebrar a run) ----

    [Test]
    public async Task Checkpoint_SemChatPersistido_ErroPlayground()
    {
        using var db = NewDb();
        var tool = new CheckpointBuiltinTool(Boundaries(db));
        var result = await tool.ExecuteAsync(
            Args(("goal", "x")), Ctx(null), default);
        Assert.That(result.Text, Does.Contain("requer chat persistido"));
    }

    [Test]
    public async Task Rewind_SemCheckpoint_ErroSuave()
    {
        using var db = NewDb();
        var chat = await SeedChatAsync(db, 1);
        var tool = new RewindBuiltinTool(Boundaries(db));

        var result = await tool.ExecuteAsync(
            Args(("report", "x")), Ctx(chat.Id), default);

        Assert.That(result.Text, Does.Contain("Nenhum checkpoint aberto"));
    }

    [Test]
    public async Task Rewind_ReportVazio_Erro()
    {
        using var db = NewDb();
        var chat = await SeedChatAsync(db, 1);
        var tool = new RewindBuiltinTool(Boundaries(db));

        var result = await tool.ExecuteAsync(
            Args(("report", "  ")), Ctx(chat.Id), default);

        Assert.That(result.Text, Does.Contain("obrigatório"));
    }

    [Test]
    public async Task Tools_GateDesligado_DevolvemIndisponivel()
    {
        using var db = NewDb();
        var chat = await SeedChatAsync(db, 1);
        var (svc, config) = BoundariesAndConfig(db);
        await config.SetAsync("contexttools.enabled", false, default);

        IBuiltinChatTool[] tools =
        [
            new CheckpointBuiltinTool(svc),
            new RewindBuiltinTool(svc),
            new ContextNotesBuiltinTool(svc),
            new NewContextBuiltinTool(svc),
        ];
        foreach (var tool in tools)
        {
            var result = await tool.ExecuteAsync(
                Args(("goal", "g"), ("report", "r"), ("text", "t")),
                Ctx(chat.Id), default);
            Assert.That(result.Text, Does.Contain("desabilitadas"), tool.Name);
        }
    }

    [Test]
    public async Task CheckpointRewind_ViaTools_FluxoCompleto()
    {
        using var db = NewDb();
        var chat = await SeedChatAsync(db, 1);
        var svc = Boundaries(db);
        var checkpoint = new CheckpointBuiltinTool(svc);
        var rewind = new RewindBuiltinTool(svc);

        var opened = await checkpoint.ExecuteAsync(
            Args(("goal", "investigar")), Ctx(chat.Id, "r1"), default);
        Assert.That(opened.Text, Does.Contain("Checkpoint aberto"));

        // Segundo checkpoint na mesma run → erro de double-open.
        var dup = await checkpoint.ExecuteAsync(
            Args(("goal", "de novo")), Ctx(chat.Id, "r1"), default);
        Assert.That(dup.Text, Does.Contain("Já existe um checkpoint aberto"));

        db.ChatMessages.Add(new ChatMessage
        {
            ChatId = chat.Id, Role = "assistant", Content = "exploração",
            Position = 1, Timestamp = 2,
        });
        await db.SaveChangesAsync();

        var closed = await rewind.ExecuteAsync(
            Args(("report", "conclusão")), Ctx(chat.Id, "r1"), default);
        Assert.That(closed.Text, Does.Contain("Contexto colapsado"));
    }

    [Test]
    public async Task ContextNotes_ViaTool_ReadWriteCap()
    {
        using var db = NewDb();
        var chat = await SeedChatAsync(db, 1);
        var tool = new ContextNotesBuiltinTool(Boundaries(db));

        var read = await tool.ExecuteAsync(JsonDocument.Parse("{}").RootElement,
            Ctx(chat.Id), default);
        Assert.That(read.Text, Is.EqualTo("(notebook vazio)"));

        var wrote = await tool.ExecuteAsync(
            Args(("text", "estado X")), Ctx(chat.Id), default);
        Assert.That(wrote.Text, Does.Contain("Notebook atualizado"));

        var reread = await tool.ExecuteAsync(JsonDocument.Parse("{}").RootElement,
            Ctx(chat.Id), default);
        Assert.That(reread.Text, Is.EqualTo("estado X"));

        var over = await tool.ExecuteAsync(
            Args(("text", new string('x', ContextBoundaryService.MaxNoteChars + 1))),
            Ctx(chat.Id), default);
        Assert.That(over.Text, Does.Contain("cap"));
    }

    [Test]
    public async Task NewContext_ViaTool_SemRun_Erro()
    {
        using var db = NewDb();
        var chat = await SeedChatAsync(db, 1);
        var tool = new NewContextBuiltinTool(Boundaries(db));

        var ok = await tool.ExecuteAsync(JsonDocument.Parse("{}").RootElement,
            Ctx(chat.Id, "r9"), default);
        Assert.That(ok.Text, Does.Contain("agendada"));
        Assert.That(await Boundaries(db).ConsumeNewContextAsync("r9", default), Is.True);

        var noRun = await tool.ExecuteAsync(JsonDocument.Parse("{}").RootElement,
            Ctx(chat.Id, null), default);
        Assert.That(noRun.Text, Does.Contain("requer run"));
    }
}
