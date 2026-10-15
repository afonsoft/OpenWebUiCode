using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Caminho ativo e leafs da árvore de conversa
/// (SPEC-20261013-chat-branching RF-001/RF-002/RF-003/RF-005):
/// walk leaf→raiz com fallback legado por Position, enumeração de
/// leafs divergentes, marcação de ativo e resumo do ramo abandonado.
/// </summary>
[TestFixture]
public class ChatBranchingTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"owui-branch-{Guid.NewGuid():N}.db");
        using var db = NewDb();
        db.Database.EnsureCreated();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private static ChatMessage Msg(
        string chatId, int pos, string role = "user", string? parentId = null) =>
        new()
        {
            Id = $"m{pos:D2}-{Guid.NewGuid():N}",
            ChatId = chatId,
            Role = role,
            Content = $"conteúdo {pos}",
            Position = pos,
            Timestamp = 1000 + pos,
            ParentId = parentId,
        };

    /// <summary>Semeia um chat com <paramref name="count"/> mensagens lineares.</summary>
    private static Chat SeedLinearChat(string id, int count)
    {
        var chat = new Chat
        {
            Id = id, UserId = "u1", Title = "t",
            ModelsJson = """["fake:1"]""", CreatedAt = 1, UpdatedAt = 1,
        };
        for (var i = 0; i < count; i++)
        {
            chat.Messages.Add(Msg(id, i, i % 2 == 0 ? "user" : "assistant"));
        }
        return chat;
    }

    private BranchService NewService(
        AppDbContext db, IContextSummarizer? summarizer = null) =>
        new(
            db,
            new ConfigService(db, TestCache.Create()),
            summarizer ?? new StubSummarizer(),
            new ModelRoleResolver(
                db, new ConfigService(db, TestCache.Create()),
                _ => Task.FromResult(new List<Application.Contracts.ModelInfo>()),
                NullLogger<ModelRoleResolver>.Instance),
            NullLogger<BranchService>.Instance);

    // ---------------- WalkPath / ResolveLeaf ----------------

    [Test]
    public void WalkPath_LinearLegado_SeguePositionSemParentId()
    {
        // Chat legado: nenhum ParentId — a cadeia implícita por Position
        // reproduz a conversa linear inteira (RF-001 compat).
        var chat = SeedLinearChat("c1", 6);

        var path = BranchService.WalkPath(chat);

        Assert.That(path.Select(m => m.Position), Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5 }));
        Assert.That(BranchService.ResolveLeaf(chat)!.Position, Is.EqualTo(5));
    }

    [Test]
    public void WalkPath_RamoAtivo_ExcluiIrmaosForaDoCaminho()
    {
        // Ramo criado a partir da msg 2: o walk segue o ParentId explícito e
        // nunca vê as mensagens 3-5 do caminho antigo (RF-002).
        var chat = SeedLinearChat("c1", 6);
        var parent = chat.Messages[2];
        var alt1 = Msg("c1", 6, "user", parent.Id);
        var alt2 = Msg("c1", 7, "assistant", alt1.Id);
        chat.Messages.Add(alt1);
        chat.Messages.Add(alt2);
        chat.LeafMessageId = alt2.Id;

        var path = BranchService.WalkPath(chat);

        Assert.That(path.Select(m => m.Id),
            Is.EqualTo(new[]
            {
                chat.Messages[0].Id, chat.Messages[1].Id, parent.Id, alt1.Id, alt2.Id,
            }));
        Assert.That(path.Any(m => m.Position is 3 or 4 or 5), Is.False,
            "mensagens do ramo antigo vazaram para o caminho ativo");
    }

    [Test]
    public void WalkPath_LeafApontaParaRamoLegado_UsaCadeiaMista()
    {
        // Leaf em nó legado: o walk usa o leaf explícito e cai na aresta
        // implícita por Position a partir dele (misto, sem backfill).
        var chat = SeedLinearChat("c1", 6);
        var mid = chat.Messages[3];
        chat.LeafMessageId = mid.Id;

        var path = BranchService.WalkPath(chat);

        Assert.That(path.Select(m => m.Position), Is.EqualTo(new[] { 0, 1, 2, 3 }));
    }

    [Test]
    public void WalkPath_LeafInvalido_CaiNoMaxPosition()
    {
        // LeafMessageId pendurado (mensagem deletada): cai no leaf implícito
        // em vez de falhar — o caminho não pode ficar vazio com msgs vivas.
        var chat = SeedLinearChat("c1", 4);
        chat.LeafMessageId = "nao-existe";

        var path = BranchService.WalkPath(chat);

        Assert.That(path, Has.Count.EqualTo(4));
    }

    [Test]
    public void WalkPath_CicloNaoTrava()
    {
        // Dado corrompido: A→B→A. O walk precisa terminar (visited set).
        var chat = new Chat
        {
            Id = "cyc", UserId = "u1", ModelsJson = "[]", CreatedAt = 1, UpdatedAt = 1,
        };
        var a = Msg("cyc", 0);
        var b = Msg("cyc", 1, parentId: a.Id);
        a.ParentId = b.Id; // ciclo
        chat.Messages.Add(a);
        chat.Messages.Add(b);
        chat.LeafMessageId = b.Id;

        var path = BranchService.WalkPath(chat);

        Assert.That(path.Count, Is.EqualTo(2));
    }

    // ---------------- ListLeaves / ListBranchesAsync ----------------

    [Test]
    public void ListLeaves_Linear_TemUmSoLeaf()
    {
        var chat = SeedLinearChat("c1", 5);

        var leaves = BranchService.ListLeaves(chat);

        Assert.That(leaves, Has.Count.EqualTo(1));
        Assert.That(leaves[0].Position, Is.EqualTo(4));
    }

    [Test]
    public async Task ListBranches_Divergentes_MarcamAtivo()
    {
        var chat = SeedLinearChat("c1", 6);
        var parent = chat.Messages[2];
        var alt = Msg("c1", 6, "user", parent.Id);
        chat.Messages.Add(alt);
        chat.LeafMessageId = alt.Id;
        await using var db = NewDb();
        db.Chats.Add(chat);
        await db.SaveChangesAsync();

        var tracked = await db.Chats.Include(c => c.Messages)
            .FirstAsync(c => c.Id == "c1");
        var branches = await NewService(db).ListBranchesAsync(tracked, default);

        Assert.That(branches, Has.Count.EqualTo(2));
        var ativo = branches.Single(b => b.IsActive);
        Assert.That(ativo.LeafId, Is.EqualTo(alt.Id));
        Assert.That(ativo.MessageCount, Is.EqualTo(4));
        var antigo = branches.Single(b => !b.IsActive);
        Assert.That(antigo.MessageCount, Is.EqualTo(6));
        Assert.That(antigo.Preview, Is.Not.Empty);
    }

    // ---------------- ActivateLeafAsync + resumo (RF-005) ----------------

    [Test]
    public async Task ActivateLeaf_TrocaLeafERenderizaCaminho()
    {
        var chat = SeedLinearChat("c1", 6);
        var parent = chat.Messages[2];
        var alt = Msg("c1", 6, "user", parent.Id);
        chat.Messages.Add(alt);
        await using var db = NewDb();
        db.Chats.Add(chat);
        await db.SaveChangesAsync();

        var tracked = await db.Chats.Include(c => c.Messages)
            .FirstAsync(c => c.Id == "c1");
        var ok = await NewService(db).ActivateLeafAsync(tracked, alt.Id, "fake:1", default);

        Assert.That(ok, Is.True);
        Assert.That(tracked.LeafMessageId, Is.EqualTo(alt.Id));
        Assert.That(BranchService.WalkPath(tracked).Select(m => m.Position),
            Is.EqualTo(new[] { 0, 1, 2, 6 }));
    }

    [Test]
    public async Task ActivateLeaf_RamoAbandonadoLongo_GeraResumo()
    {
        // RF-005: ao sair de um caminho com ≥4 mensagens, o resumo vai pro
        // kv chat:{id}:branch:{leafId} no modelo do papel memory.
        var summarizer = new StubSummarizer { Result = new ContextSummary("resumo do ramo", [], [], []) };
        var chat = SeedLinearChat("c1", 6); // 6 msgs no ramo abandonado
        chat.LeafMessageId = chat.Messages[^1].Id; // leaf na ponta legada
        var alvo = chat.Messages[2];
        var alt = Msg("c1", 6, "user", alvo.Id);
        chat.Messages.Add(alt);
        await using var db = NewDb();
        db.Chats.Add(chat);
        await db.SaveChangesAsync();

        var tracked = await db.Chats.Include(c => c.Messages)
            .FirstAsync(c => c.Id == "c1");
        var oldLeaf = BranchService.ResolveLeaf(tracked)!.Id;
        var service = NewService(db, summarizer);

        await service.ActivateLeafAsync(tracked, alt.Id, "fake:1", default);

        Assert.That(summarizer.Calls, Is.EqualTo(1));
        var resumo = await service.GetBranchSummaryAsync("c1", oldLeaf, default);
        Assert.That(resumo, Is.EqualTo("resumo do ramo"));
    }

    [Test]
    public async Task ActivateLeaf_SummarizerFalha_TrocaDoMesmoJeito()
    {
        // RF-005: falha do resumo nunca bloqueia a troca de ramo.
        var summarizer = new StubSummarizer { Throws = true };
        var chat = SeedLinearChat("c1", 6);
        chat.LeafMessageId = chat.Messages[^1].Id;
        var alt = Msg("c1", 6, "user", chat.Messages[2].Id);
        chat.Messages.Add(alt);
        await using var db = NewDb();
        db.Chats.Add(chat);
        await db.SaveChangesAsync();

        var tracked = await db.Chats.Include(c => c.Messages)
            .FirstAsync(c => c.Id == "c1");
        var ok = await NewService(db, summarizer)
            .ActivateLeafAsync(tracked, alt.Id, "fake:1", default);

        Assert.That(ok, Is.True);
        Assert.That(tracked.LeafMessageId, Is.EqualTo(alt.Id));
    }

    [Test]
    public async Task ActivateLeaf_RamoCurto_NaoResume()
    {
        // Caminho abandonado <4 mensagens: sem chamada ao summarizer.
        var summarizer = new StubSummarizer { Result = new ContextSummary("x", [], [], []) };
        var chat = SeedLinearChat("c1", 3);
        chat.LeafMessageId = chat.Messages[^1].Id;
        var alt = Msg("c1", 3, "user", chat.Messages[0].Id);
        chat.Messages.Add(alt);
        await using var db = NewDb();
        db.Chats.Add(chat);
        await db.SaveChangesAsync();

        var tracked = await db.Chats.Include(c => c.Messages)
            .FirstAsync(c => c.Id == "c1");
        await NewService(db, summarizer).ActivateLeafAsync(tracked, alt.Id, "fake:1", default);

        Assert.That(summarizer.Calls, Is.EqualTo(0));
    }

    [Test]
    public async Task ActivateLeaf_LeafDeOutroChat_Rejeita()
    {
        var chat = SeedLinearChat("c1", 4);
        var outro = SeedLinearChat("c2", 4);
        await using var db = NewDb();
        db.Chats.AddRange(chat, outro);
        await db.SaveChangesAsync();

        var tracked = await db.Chats.Include(c => c.Messages)
            .FirstAsync(c => c.Id == "c1");
        var foreignLeaf = outro.Messages[^1].Id;
        var ok = await NewService(db).ActivateLeafAsync(tracked, foreignLeaf, "fake:1", default);

        Assert.That(ok, Is.False);
        Assert.That(tracked.LeafMessageId, Is.Not.EqualTo(foreignLeaf));
    }

    // ---------------- Bordas de cobertura ----------------

    [Test]
    public void WalkPath_ParentDangling_CaiNaArestaLegada()
    {
        // ParentId apontando pra id inexistente — o walk usa a aresta
        // legada por Position em vez de quebrar.
        var chat = SeedLinearChat("c1", 4);
        var solto = Msg("c1", 4, parentId: "id-fantasma");
        chat.Messages.Add(solto);
        chat.LeafMessageId = solto.Id;

        var path = BranchService.WalkPath(chat);

        Assert.That(path.Last().Id, Is.EqualTo(solto.Id));
        Assert.That(path, Has.Count.EqualTo(5),
            "pai dangling não interrompe — desce pela cadeia legada");
    }

    [Test]
    public void ListLeaves_ChatVazio_Vazio()
    {
        var chat = new Chat
        {
            Id = "vazio", UserId = "u1", ModelsJson = "[]", CreatedAt = 1, UpdatedAt = 1,
        };

        Assert.That(BranchService.ListLeaves(chat), Is.Empty);
        Assert.That(BranchService.WalkPath(chat), Is.Empty);
        Assert.That(BranchService.ResolveLeaf(chat), Is.Null);
    }

    [Test]
    public async Task ListBranches_ResumoPersistido_ApareceNoLeaf()
    {
        var chat = SeedLinearChat("c1", 5);
        var alt = Msg("c1", 5, "user", chat.Messages[2].Id);
        chat.Messages.Add(alt);
        chat.LeafMessageId = alt.Id;
        await using var db = NewDb();
        db.Chats.Add(chat);
        await db.SaveChangesAsync();
        var leafAntigo = chat.Messages[^2].Id; // ponta legada (pos 4)
        await new ConfigService(db, TestCache.Create())
            .SetAsync(BranchService.SummaryKey("c1", leafAntigo), "resumo salvo", default);

        var tracked = await db.Chats.Include(c => c.Messages)
            .FirstAsync(c => c.Id == "c1");
        var branches = await NewService(db).ListBranchesAsync(tracked, default);

        var antigo = branches.Single(b => b.LeafId == leafAntigo);
        Assert.That(antigo.Summary, Is.EqualTo("resumo salvo"));
        Assert.That(branches.Single(b => b.LeafId == alt.Id).Summary, Is.Null);
    }

    [Test]
    public async Task ActivateLeaf_ReativarMesmoLeaf_SemResumo()
    {
        // Alvo já é o leaf ativo — nada abandonado, summarizer não roda.
        var summarizer = new StubSummarizer { Result = new ContextSummary("x", [], [], []) };
        var chat = SeedLinearChat("c1", 6);
        chat.LeafMessageId = chat.Messages[^1].Id;
        await using var db = NewDb();
        db.Chats.Add(chat);
        await db.SaveChangesAsync();

        var tracked = await db.Chats.Include(c => c.Messages)
            .FirstAsync(c => c.Id == "c1");
        var ok = await NewService(db, summarizer)
            .ActivateLeafAsync(tracked, chat.LeafMessageId, "fake:1", default);

        Assert.That(ok, Is.True);
        Assert.That(summarizer.Calls, Is.EqualTo(0));
    }

    [Test]
    public async Task ActivateLeaf_ResumoVazio_NaoPersiste()
    {
        var summarizer = new StubSummarizer { Result = new ContextSummary("   ", [], [], []) };
        var chat = SeedLinearChat("c1", 6);
        chat.LeafMessageId = chat.Messages[^1].Id;
        var alt = Msg("c1", 6, "user", chat.Messages[2].Id);
        chat.Messages.Add(alt);
        await using var db = NewDb();
        db.Chats.Add(chat);
        await db.SaveChangesAsync();

        var tracked = await db.Chats.Include(c => c.Messages)
            .FirstAsync(c => c.Id == "c1");
        var oldLeaf = chat.LeafMessageId;
        var service = NewService(db, summarizer);
        await service.ActivateLeafAsync(tracked, alt.Id, "fake:1", default);

        Assert.That(summarizer.Calls, Is.EqualTo(1));
        Assert.That(await service.GetBranchSummaryAsync("c1", oldLeaf!, default), Is.Null);
    }

    [Test]
    public async Task ActivateLeaf_ResumoLongo_TruncaTranscript()
    {
        // Transcript >12k chars é cortado antes de ir ao summarizer.
        var captor = new StubSummarizer { Result = new ContextSummary("ok", [], [], []) };
        var chat = new Chat
        {
            Id = "c1", UserId = "u1", ModelsJson = "[]", CreatedAt = 1, UpdatedAt = 1,
        };
        for (var i = 0; i < 6; i++)
        {
            var m = Msg("c1", i);
            m.Content = new string('x', 4_000);
            chat.Messages.Add(m);
        }
        chat.LeafMessageId = chat.Messages[^1].Id;
        var alt = Msg("c1", 6, "user", chat.Messages[2].Id);
        chat.Messages.Add(alt);
        await using var db = NewDb();
        db.Chats.Add(chat);
        await db.SaveChangesAsync();

        var tracked = await db.Chats.Include(c => c.Messages)
            .FirstAsync(c => c.Id == "c1");
        await NewService(db, captor).ActivateLeafAsync(tracked, alt.Id, "fake:1", default);

        Assert.That(captor.LastTranscript!.Length,
            Is.LessThanOrEqualTo(12_000));
    }

    // ---------------- history:// sobre o caminho ativo ----------------

    [Test]
    public async Task History_CaminhoAtivo_NaoMostraRamoIrmao()
    {
        var chat = SeedLinearChat("c1", 6);
        var alt = Msg("c1", 6, "user", chat.Messages[2].Id);
        chat.Messages.Add(alt);
        chat.LeafMessageId = alt.Id;
        await using var db = NewDb();
        db.Chats.Add(chat);
        await db.SaveChangesAsync();

        var svc = new HistoryResourceService(db);
        var (text, err) = await svc.ResolveAsync(
            "history://c1", "u1", null, default);

        Assert.That(err, Is.Null);
        Assert.That(text, Does.Contain("conteúdo 6"));
        Assert.That(text, Does.Not.Contain("conteúdo 5"),
            "transcript vazou mensagem do ramo inativo");

        var (tail, err2) = await svc.ResolveAsync(
            "history://c1/tail:1", "u1", null, default);
        Assert.That(err2, Is.Null);
        Assert.That(tail, Does.Contain("conteúdo 6"));

        // msg: segue global — recupera mensagem do ramo inativo.
        var idFora = chat.Messages[4].Id;
        var (msg, err3) = await svc.ResolveAsync(
            $"history://c1/msg:{idFora}", "u1", null, default);
        Assert.That(err3, Is.Null);
        Assert.That(msg, Does.Contain("conteúdo 4"));
    }

    /// <summary>Summarizer fake: captura chamadas e pode falhar/retornar texto.</summary>
    private sealed class StubSummarizer : IContextSummarizer
    {
        public ContextSummary? Result { get; set; }
        public bool Throws { get; set; }
        public int Calls { get; private set; }
        public string? LastTranscript { get; private set; }
        public string? LastModel { get; private set; }

        public Task<ContextSummary?> SummarizeAsync(
            string model, string transcript, string? userId, CancellationToken ct)
        {
            Calls++;
            LastModel = model;
            LastTranscript = transcript;
            if (Throws)
            {
                throw new InvalidOperationException("summarizer quebrou");
            }
            return Task.FromResult(Result);
        }
    }
}

/// <summary>
/// E2E dos endpoints de ramificação sobre o pipeline real
/// (SPEC-20261013-chat-branching RF-002/RF-003/RF-004): enqueue por caminho
/// ativo, listagem de leafs, ativação com isolamento de dono.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ChatBranchingE2ETests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-branch-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("Admin", "admin@branch.local");
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, "senha123"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    /// <summary>Chat linear de <paramref name="pairs"/> pares user+assistant.</summary>
    private async Task<ChatResponse> CriarChatLinearAsync(int pairs)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var mensagens = new List<ChatMessageModel>();
        for (var i = 0; i < pairs; i++)
        {
            mensagens.Add(new ChatMessageModel(
                $"u-{tag}-{i}", "user", $"pergunta {i}", null, 1000 + i * 2));
            mensagens.Add(new ChatMessageModel(
                $"a-{tag}-{i}", "assistant", $"resposta {i}", "llama3", 1001 + i * 2));
        }
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Chat ramos", ["llama3"], mensagens));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    private async Task<ChatRunResponse> EnfileirarAsync(
        string chatId, string? content, string? forkFrom = null)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chatId}/messages",
            new EnqueueChatRunRequest(content, "llama3", ForkFromMessageId: forkFrom));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChatRunResponse>())!;
    }

    [Test]
    public async Task Ramificar_NaMsg5_EnqueueEnviaApenasCaminhoAoModelo()
    {
        // AC central: chat de 10 msgs (5 pares), ativa a msg do meio,
        // envia alternativa — o histórico serializado na run só pode ter
        // as msgs até a ativada + a alternativa (RF-002/RF-004).
        var auth = await SignUpAsync("BrA", "bra@branch.local");
        UseToken(auth.Token);
        var chat = await CriarChatLinearAsync(5);
        var pivot = chat.Messages[4]; // user "pergunta 2"

        var ativado = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/branches/activate",
            new ActivateBranchRequest(pivot.Id));
        Assert.That(ativado.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await ativado.Content.ReadAsStringAsync());
        var render = (await ativado.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.That(render.Messages.Select(m => m.Id),
            Is.EqualTo(chat.Messages.Take(5).Select(m => m.Id)),
            "caminho ativado deveria ser só até o pivot");
        Assert.That(render.ActiveLeafId, Is.EqualTo(pivot.Id));
        Assert.That(render.BranchCount, Is.EqualTo(1));

        var run = await EnfileirarAsync(chat.Id, "alternativa");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.ChatRuns.AsNoTracking()
            .FirstAsync(r => r.Id == run.Id);
        var payload = JsonSerializer.Deserialize<ChatCompletionRequest>(
            stored.RequestJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var historico = payload.Messages.Where(m => m.Role != "system")
            .Select(m => m.Content).ToList();
        Assert.That(historico, Has.Count.EqualTo(6),
            "caminho (5 msgs) + alternativa");
        Assert.That(historico.Last(), Is.EqualTo("alternativa"));
        Assert.That(historico, Has.None.Contains("pergunta 3").Or.Contains("resposta 4"),
            "mensagens do ramo abandonado vazaram pro modelo");

        // O leaf avançou pra nova mensagem e há 2 ramos divergentes.
        var detalhe = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{chat.Id}");
        Assert.That(detalhe!.BranchCount, Is.EqualTo(2));
        Assert.That(detalhe.ActiveLeafId, Is.EqualTo(detalhe.Messages[^1].Id));
        Assert.That(detalhe.Messages[^1].ParentId, Is.EqualTo(pivot.Id));
    }

    [Test]
    public async Task ForkAtomico_EnqueueComForkFrom_PenduraNaOrigem()
    {
        // forkFromMessageId: ramifica + envia num request só (RF-004).
        var auth = await SignUpAsync("BrB", "brb@branch.local");
        UseToken(auth.Token);
        var chat = await CriarChatLinearAsync(3);
        var pivot = chat.Messages[1];

        var run = await EnfileirarAsync(chat.Id, "fork direto", pivot.Id);

        var detalhe = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{chat.Id}");
        Assert.That(detalhe!.Messages.Select(m => m.Id),
            Is.EqualTo(chat.Messages.Take(2).Select(m => m.Id)
                .Concat([detalhe.Messages[^1].Id])));
        Assert.That(detalhe.BranchCount, Is.EqualTo(2));
    }

    [Test]
    public async Task Branches_ListaLeafsComPreviewEAtivo()
    {
        var auth = await SignUpAsync("BrC", "brc@branch.local");
        UseToken(auth.Token);
        var chat = await CriarChatLinearAsync(2);
        var pivot = chat.Messages[0];
        await EnfileirarAsync(chat.Id, "ramo B", pivot.Id);

        var branches = await _client.GetFromJsonAsync<List<BranchLeafResponse>>(
            $"/api/v1/chats/{chat.Id}/branches");

        Assert.That(branches, Is.Not.Null);
        Assert.That(branches!, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(branches.Count(b => b.IsActive), Is.EqualTo(1));
            Assert.That(branches.All(b => b.Preview.Length > 0), Is.True);
            Assert.That(branches.All(b => b.MessageCount >= 1), Is.True);
            Assert.That(branches.All(b => b.UpdatedAt > 0), Is.True);
        });
    }

    [Test]
    public async Task Ativar_LeafDeOutroUsuarioOuChat_NotFound()
    {
        var dono = await SignUpAsync("BrD1", "brd1@branch.local");
        UseToken(dono.Token);
        var chat = await CriarChatLinearAsync(2);

        // Outro usuário: chat nem existe pra ele → 404.
        var outro = await SignUpAsync("BrD2", "brd2@branch.local");
        UseToken(outro.Token);
        var r1 = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/branches/activate",
            new ActivateBranchRequest(chat.Messages[^1].Id));
        Assert.That(r1.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Dono: leaf de OUTRO chat → 404 (leaf não pertence ao chat).
        UseToken(dono.Token);
        var outroChat = await CriarChatLinearAsync(1);
        var r2 = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/branches/activate",
            new ActivateBranchRequest(outroChat.Messages[^1].Id));
        Assert.That(r2.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Legado_SemRamos_ComportamentoIdentico()
    {
        // Chat sem branching: path == ordem por Position, leaf implícito no fim.
        var auth = await SignUpAsync("BrE", "bre@branch.local");
        UseToken(auth.Token);
        var chat = await CriarChatLinearAsync(4);

        var detalhe = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{chat.Id}");

        Assert.That(detalhe!.Messages.Select(m => m.Id),
            Is.EqualTo(chat.Messages.Select(m => m.Id)));
        Assert.That(detalhe.BranchCount, Is.EqualTo(1));
        Assert.That(detalhe.ActiveLeafId, Is.EqualTo(chat.Messages[^1].Id));

        // Enqueue normal encadeia no leaf implícito (ParentId explícito novo).
        var run = await EnfileirarAsync(chat.Id, "mais uma");
        var depois = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{chat.Id}");
        Assert.That(depois!.Messages, Has.Count.EqualTo(9));
        Assert.That(depois.Messages[^1].ParentId, Is.EqualTo(chat.Messages[^1].Id));
    }

    [Test]
    public async Task Delete_Subarvore_PreservaRamoIrmao()
    {
        // Deletar a alternativa não toca no ramo antigo; deletar o pivot
        // remove a subárvore inteira dele.
        var auth = await SignUpAsync("BrF", "brf@branch.local");
        UseToken(auth.Token);
        var chat = await CriarChatLinearAsync(3);
        var pivot = chat.Messages[1];
        var run = await EnfileirarAsync(chat.Id, "alternativa", pivot.Id);

        var detalhe = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{chat.Id}");
        var altMsg = detalhe!.Messages[^1];
        Assert.That(altMsg.ParentId, Is.EqualTo(pivot.Id));

        // Deleta a alternativa — o caminho antigo continua intacto.
        var del = await _client.DeleteAsync(
            $"/api/v1/chats/{chat.Id}/messages/{altMsg.Id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = (await del.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.That(body.BranchCount, Is.EqualTo(1));
        Assert.That(body.Messages.Select(m => m.Id),
            Is.EqualTo(chat.Messages.Take(2).Select(m => m.Id)),
            "depois de deletar o leaf alternativo o caminho recua ao pivot");
    }

    [Test]
    public async Task UpdateChat_MergePreservaRamoForaDoCaminho()
    {
        // O upsert do cliente só descreve o caminho ativo — o merge do
        // servidor precisa preservar os leafs irmãos (senão editar título
        // apagaria ramos).
        var auth = await SignUpAsync("BrG", "brg@branch.local");
        UseToken(auth.Token);
        var chat = await CriarChatLinearAsync(3);
        var pivot = chat.Messages[1];
        await EnfileirarAsync(chat.Id, "alternativa", pivot.Id);

        // Renomeia via upsert mandando só o caminho ativo (4 msgs).
        var ativo = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{chat.Id}");
        var upsert = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}",
            new ChatUpsertRequest("renomeado", ["llama3"], ativo!.Messages));
        Assert.That(upsert.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await upsert.Content.ReadAsStringAsync());

        // O ramo antigo (msgs 2..5 do caminho original) sobreviveu.
        var branches = await _client.GetFromJsonAsync<List<BranchLeafResponse>>(
            $"/api/v1/chats/{chat.Id}/branches");
        Assert.That(branches!.Count, Is.EqualTo(2), "upsert apagou o ramo irmão");
    }

    [Test]
    public async Task Clone_PreservaRamosComIdsRemapeados()
    {
        // Clone: a árvore vai toda — ParentId/leaf remapeados pros ids novos.
        var auth = await SignUpAsync("BrH", "brh@branch.local");
        UseToken(auth.Token);
        var chat = await CriarChatLinearAsync(3);
        var pivot = chat.Messages[1];
        await EnfileirarAsync(chat.Id, "alternativa", pivot.Id);

        var cloneResp = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/clone", null);
        Assert.That(cloneResp.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await cloneResp.Content.ReadAsStringAsync());
        var clone = (await cloneResp.Content.ReadFromJsonAsync<ChatResponse>())!;

        Assert.That(clone.BranchCount, Is.EqualTo(2));
        Assert.That(clone.Messages.All(m => m.Id != null
            && !chat.Messages.Any(o => o.Id == m.Id)),
            Is.True, "ids do clone devem ser novos");
        // O caminho ativo do clone espelha o do original: msg0 → pivot → alt.
        Assert.That(clone.Messages, Has.Count.EqualTo(3));
        Assert.That(clone.Messages[^1].ParentId, Is.EqualTo(clone.Messages[1].Id));
        Assert.That(clone.ActiveLeafId, Is.EqualTo(clone.Messages[^1].Id));
    }

    [Test]
    public async Task Delete_Pivot_RemoveSubarvoreERecuaLeaf()
    {
        // Deletar o pivot remove ele + o descendente explícito e a
        // continuação legada — sobra só a raiz.
        var auth = await SignUpAsync("BrI", "bri@branch.local");
        UseToken(auth.Token);
        var chat = await CriarChatLinearAsync(3);
        var pivot = chat.Messages[1];
        await EnfileirarAsync(chat.Id, "alternativa", pivot.Id);

        var del = await _client.DeleteAsync(
            $"/api/v1/chats/{chat.Id}/messages/{pivot.Id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = (await del.Content.ReadFromJsonAsync<ChatResponse>())!;

        Assert.That(body.Messages, Has.Count.EqualTo(1));
        Assert.That(body.BranchCount, Is.EqualTo(1));
        Assert.That(body.ActiveLeafId, Is.EqualTo(chat.Messages[0].Id));
    }

    [Test]
    public async Task UpdateChat_ClienteLegado_PreservaParentIdArmazenado()
    {
        // Cliente antigo não conhece ParentId: envia o campo nulo — o
        // merge preserva o ParentId que já estava gravado (RF-compat).
        var auth = await SignUpAsync("BrJ", "brj@branch.local");
        UseToken(auth.Token);
        var chat = await CriarChatLinearAsync(2);
        var pivot = chat.Messages[0];
        await EnfileirarAsync(chat.Id, "alternativa", pivot.Id);
        var ativo = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{chat.Id}");
        var alt = ativo!.Messages[^1];
        Assert.That(alt.ParentId, Is.EqualTo(pivot.Id));

        // Reenvia o mesmo caminho mas com ParentId nulo (cliente legado).
        var legado = ativo.Messages
            .Select(m => m with { ParentId = null }).ToList();
        var upsert = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}",
            new ChatUpsertRequest("chat", ["llama3"], legado));
        Assert.That(upsert.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var depois = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{chat.Id}");
        Assert.That(depois!.Messages[^1].ParentId, Is.EqualTo(pivot.Id),
            "ParentId armazenado sumiu num upsert de cliente legado");
    }

    [Test]
    public async Task Enqueue_SemConteudo_RegeneraSobreCaminho()
    {
        // Regenerar (content=null) exige o caminho terminando em user.
        // Ativa o pivot assistant → 400; ativa o pivot user → Accepted.
        var auth = await SignUpAsync("BrK", "brk@branch.local");
        UseToken(auth.Token);
        var chat = await CriarChatLinearAsync(3);
        var assistant = chat.Messages[1];
        var user = chat.Messages[2];

        await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/branches/activate",
            new ActivateBranchRequest(assistant.Id));
        var ruim = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/messages",
            new EnqueueChatRunRequest(null, "llama3"));
        Assert.That(ruim.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/branches/activate",
            new ActivateBranchRequest(user.Id));
        var bom = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/messages",
            new EnqueueChatRunRequest(null, "llama3"));
        Assert.That(bom.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
    }

    [Test]
    public async Task SharedChat_RenderizaApenasCaminhoAtivo()
    {
        // Chat compartilhado: o GET público também vê só o caminho ativo.
        var auth = await SignUpAsync("BrL", "brl@branch.local");
        UseToken(auth.Token);
        var chat = await CriarChatLinearAsync(3);
        var pivot = chat.Messages[0];
        await EnfileirarAsync(chat.Id, "alternativa", pivot.Id);

        var share = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/share", new { });
        Assert.That(share.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await share.Content.ReadAsStringAsync());
        var shareId = (await share.Content
            .ReadFromJsonAsync<ChatResponse>())!.ShareId;
        Assert.That(shareId, Is.Not.Null.And.Not.Empty);

        _client.DefaultRequestHeaders.Authorization = null;
        var shared = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/chats/share/{shareId}");
        var msgs = shared.GetProperty("messages").EnumerateArray().ToList();
        Assert.That(msgs, Has.Count.EqualTo(2),
            "shared view deve conter só o caminho ativo (raiz + alternativa)");
        Assert.That(msgs[^1].GetProperty("content").GetString(),
            Is.EqualTo("alternativa"));
    }
}
