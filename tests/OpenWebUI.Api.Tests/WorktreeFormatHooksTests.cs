using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura da SPEC-20261009-worktree-format-hooks (E16 S9): worktree
/// por run (criação, isolamento entre runs, merge com conflitos, prune
/// de órfãos, fallback shared sem git), tool <c>apply_patch</c> (formato
/// OpenAI: add/update/delete/move + jail + atomicidade) e format hook
/// <c>Format:Command</c> (interpolação {files}, timeout, warning-only).
/// </summary>
[TestFixture, IsolateEnvironment]
public class WorktreeFormatHooksTests
{
    private string _root = null!;
    private AppDbContext _db = null!;
    private HybridCache _cache = null!;
    private ConfigService _config = null!;
    private WorkspaceRepoService _repos = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-wt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Join(_root, "t.db")}").Options);
        DatabaseMigrator.MigrateAsync(_db).GetAwaiter().GetResult();
        _cache = TestCache.Create();
        _config = new ConfigService(_db, _cache);
        _repos = new WorkspaceRepoService(_config, new StubEnv(_root));
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
                try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private WorktreeService Worktrees(Dictionary<string, string?>? config = null) =>
        new(new StubEnv(_root), Configuration(config),
            ScopeFactory(), NullLogger<WorktreeService>.Instance);

    private FormatHookService FormatHook(Dictionary<string, string?>? config = null) =>
        new(_repos, Configuration(config), NullLogger<FormatHookService>.Instance);

    private IConfiguration Configuration(Dictionary<string, string?>? config) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            config ?? new Dictionary<string, string?>()).Build();

    private IServiceScopeFactory ScopeFactory()
    {
        var provider = new ServiceCollection()
            .AddSingleton(_db)
            .AddSingleton(_repos)
            .BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>Liga isolamento + cria o repo do usuário vinculado.</summary>
    private async Task<(WorktreeService Worktrees, string MainWorkdir)> SetupRepoAsync()
    {
        var worktrees = Worktrees(new() { ["Workspace:RunIsolation"] = "worktree" });
        var origin = CriarOrigem("main");
        var (binding, error) = await _repos.OpenAsync("u1", "a/b", "main", origin, null, default);
        Assert.That(error, Is.Null);
        Assert.That(binding, Is.Not.Null);
        return (worktrees, await _repos.ResolveWorkdirAsync("u1", default));
    }

    // ---------------- WorktreeService ----------------

    [Test]
    public async Task Worktree_Create_Isolado()
    {
        RequireGit();
        var (worktrees, main) = await SetupRepoAsync();
        var (wt, warning) = await worktrees.TryCreateForRunAsync("u1", "run-1", main, default);

        Assert.Multiple(() =>
        {
            Assert.That(warning, Is.Null);
            Assert.That(wt, Is.EqualTo(Path.Join(_root, "data", "worktrees", "u1", "run-1")));
            Assert.That(Directory.Exists(wt), Is.True);
            // Worktree ligado: .git é arquivo, não diretório.
            Assert.That(File.Exists(Path.Join(wt!, ".git")), Is.True);
            Assert.That(File.Exists(Path.Join(wt!, "readme.md")), Is.True);
        });
        // Detached no HEAD do main.
        Assert.That(Git(wt, "rev-parse", "--abbrev-ref", "HEAD"), Is.EqualTo("HEAD"));
    }

    [Test]
    public async Task Worktree_Disabled_Shared()
    {
        RequireGit();
        var worktrees = Worktrees(); // sem Workspace:RunIsolation
        var origin = CriarOrigem("main");
        await _repos.OpenAsync("u1", "a/b", "main", origin, null, default);
        var main = await _repos.ResolveWorkdirAsync("u1", default);

        var (wt, warning) = await worktrees.TryCreateForRunAsync("u1", "run-1", main, default);
        Assert.Multiple(() =>
        {
            Assert.That(wt, Is.Null);
            Assert.That(warning, Is.Null);
            Assert.That(Directory.Exists(Path.Join(_root, "data", "worktrees")), Is.False);
        });
    }

    [Test]
    public async Task Worktree_SemGit_FallbackShared()
    {
        var worktrees = Worktrees(new() { ["Workspace:RunIsolation"] = "worktree" });
        // Sem repo vinculado: workdir = raiz do workspace (sem .git).
        var main = await _repos.ResolveWorkdirAsync("u1", default);
        var (wt, warning) = await worktrees.TryCreateForRunAsync("u1", "run-1", main, default);

        Assert.Multiple(() =>
        {
            Assert.That(wt, Is.Null);
            Assert.That(warning, Does.Contain("shared").Or.Contain("compartilhado"));
        });
    }

    [Test]
    public async Task Worktree_DuasRuns_SemColisao()
    {
        RequireGit();
        var (worktrees, main) = await SetupRepoAsync();
        var (wt1, _) = await worktrees.TryCreateForRunAsync("u1", "run-a", main, default);
        var (wt2, _) = await worktrees.TryCreateForRunAsync("u1", "run-b", main, default);
        Assert.That(wt1, Is.Not.EqualTo(wt2));

        // As duas runs editam o mesmo arquivo — sem colisão.
        File.WriteAllText(Path.Join(wt1!, "readme.md"), "conteúdo da run A\n");
        File.WriteAllText(Path.Join(wt2!, "readme.md"), "conteúdo da run B\n");
        File.WriteAllText(Path.Join(wt2!, "novo.txt"), "só na B\n");

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(Path.Join(wt1!, "readme.md")), Is.EqualTo("conteúdo da run A\n"));
            Assert.That(File.ReadAllText(Path.Join(wt2!, "readme.md")), Is.EqualTo("conteúdo da run B\n"));
            // Workdir principal intacto.
            Assert.That(File.ReadAllText(Path.Join(main, "readme.md")), Is.EqualTo("oi\n"));
            Assert.That(File.Exists(Path.Join(main, "novo.txt")), Is.False);
        });
    }

    [Test]
    public async Task Worktree_Merge_AplicaLimpo_RemoveWorktree()
    {
        RequireGit();
        var (worktrees, main) = await SetupRepoAsync();
        var (wt, _) = await worktrees.TryCreateForRunAsync("u1", "run-1", main, default);
        File.WriteAllText(Path.Join(wt!, "readme.md"), "atualizado pela run\n");
        File.WriteAllText(Path.Join(wt!, "novo.txt"), "arquivo novo\n");

        var result = await worktrees.MergeAsync(main, wt!, default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Merged, Is.True);
            Assert.That(result.Conflicts, Is.Empty);
            Assert.That(result.Applied, Does.Contain("readme.md"));
            // Untracked entra no merge (intent-to-add).
            Assert.That(result.Applied, Does.Contain("novo.txt"));
            Assert.That(File.ReadAllText(Path.Join(main, "readme.md")), Is.EqualTo("atualizado pela run\n"));
            Assert.That(File.ReadAllText(Path.Join(main, "novo.txt")), Is.EqualTo("arquivo novo\n"));
            // Merge limpo remove o worktree.
            Assert.That(Directory.Exists(wt), Is.False);
        });
    }

    [Test]
    public async Task Worktree_Merge_Conflito_ListaNuncaForca()
    {
        RequireGit();
        var (worktrees, main) = await SetupRepoAsync();
        var (wt, _) = await worktrees.TryCreateForRunAsync("u1", "run-1", main, default);
        File.WriteAllText(Path.Join(wt!, "readme.md"), "run mexeu\n");
        File.WriteAllText(Path.Join(wt!, "ok.txt"), "sem conflito\n");
        // Divergência no main depois do worktree.
        File.WriteAllText(Path.Join(main, "readme.md"), "main divergiu\n");

        var result = await worktrees.MergeAsync(main, wt!, default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Merged, Is.False);
            Assert.That(result.Conflicts, Has.Count.EqualTo(1));
            Assert.That(result.Conflicts[0], Does.Contain("readme.md"));
            Assert.That(result.Applied, Does.Contain("ok.txt"));
            // Main NUNCA é forçado — divergência preservada.
            Assert.That(File.ReadAllText(Path.Join(main, "readme.md")), Is.EqualTo("main divergiu\n"));
            Assert.That(File.ReadAllText(Path.Join(main, "ok.txt")), Is.EqualTo("sem conflito\n"));
            // Worktree fica para nova tentativa.
            Assert.That(Directory.Exists(wt), Is.True);
        });
    }

    [Test]
    public async Task Worktree_Merge_Inexistente_Erro()
    {
        RequireGit();
        var (worktrees, main) = await SetupRepoAsync();
        var result = await worktrees.MergeAsync(
            main, Path.Join(_root, "nao-existe"), default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Merged, Is.False);
            Assert.That(result.Error, Is.Not.Null);
        });
    }

    [Test]
    public async Task Worktree_Prune_OrfaosEExpirados()
    {
        RequireGit();
        var (worktrees, main) = await SetupRepoAsync();

        _db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        _db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });

        // Run ativa → worktree preservado.
        var ativa = "run-ativa";
        _db.ChatRuns.Add(new ChatRun
        {
            Id = ativa, ChatId = "c1", UserId = "u1", Model = "m",
            Status = ChatRunStatus.Running, RequestJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        // Run terminada recente → preservado (ainda mergeável).
        var recente = "run-recente";
        _db.ChatRuns.Add(new ChatRun
        {
            Id = recente, ChatId = "c1", UserId = "u1", Model = "m",
            Status = ChatRunStatus.Completed, RequestJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        // Run terminada há muito tempo → podado (TTL default 168h).
        var velha = "run-velha";
        _db.ChatRuns.Add(new ChatRun
        {
            Id = velha, ChatId = "c1", UserId = "u1", Model = "m",
            Status = ChatRunStatus.Interrupted, RequestJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (200L * 3600),
        });
        await _db.SaveChangesAsync();

        var wtAtiva = (await worktrees.TryCreateForRunAsync("u1", ativa, main, default)).Worktree;
        var wtRecente = (await worktrees.TryCreateForRunAsync("u1", recente, main, default)).Worktree;
        var wtVelha = (await worktrees.TryCreateForRunAsync("u1", velha, main, default)).Worktree;
        // Órfão puro: run inexistente no banco.
        var wtOrfao = Path.Join(worktrees.WorktreesRoot("u1"), "run-fantasma");
        Directory.CreateDirectory(wtOrfao);

        await worktrees.PruneOrphansAsync(default);

        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(wtAtiva), Is.True, "run ativa preservada");
            Assert.That(Directory.Exists(wtRecente), Is.True, "terminada recente preservada");
            Assert.That(Directory.Exists(wtVelha), Is.False, "terminada além do TTL podada");
            Assert.That(Directory.Exists(wtOrfao), Is.False, "órfão sem run podado");
        });
    }

    [Test]
    public async Task Worktree_Remove_Limpa()
    {
        RequireGit();
        var (worktrees, main) = await SetupRepoAsync();
        var (wt, _) = await worktrees.TryCreateForRunAsync("u1", "run-1", main, default);
        Assert.That(Directory.Exists(wt), Is.True);

        await worktrees.RemoveAsync(main, wt!, default);
        Assert.That(Directory.Exists(wt), Is.False);
    }

    // ---------------- apply_patch ----------------

    private BuiltinToolContext Ctx(string workdir) =>
        new("u1", "c1", "run-1", workdir, Path.Join(_root, "uploads"));

    private static JsonElement Args(string patch) =>
        JsonSerializer.SerializeToElement(new { patch });

    /// <summary>O payload estruturado da tool como JsonElement para asserções.</summary>
    private static JsonElement ResultOf(BuiltinToolResult result) =>
        JsonSerializer.SerializeToElement(result.Result);

    [Test]
    public async Task ApplyPatch_Add_CriaComDiff()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        var tool = new ApplyPatchBuiltinTool();
        var patch = """
            *** Begin Patch
            *** Add File: src/novo.cs
            +linha 1
            +linha 2
            *** End Patch
            """;

        var result = await tool.ExecuteAsync(Args(patch), Ctx(dir), default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Refused, Is.False);
            Assert.That(File.ReadAllText(Path.Join(dir, "src", "novo.cs")),
                Is.EqualTo("linha 1\nlinha 2"));
            var files = ResultOf(result).GetProperty("files");
            Assert.That(files.GetArrayLength(), Is.EqualTo(1));
            Assert.That(files[0].GetProperty("path").GetString(), Is.EqualTo("src/novo.cs"));
            Assert.That(files[0].GetProperty("action").GetString(), Is.EqualTo("add"));
            Assert.That(files[0].GetProperty("added").GetInt32(), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task ApplyPatch_Update_Hunk()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        File.WriteAllText(Path.Join(dir, "a.txt"), "um\ndois\ntrês\n");
        var tool = new ApplyPatchBuiltinTool();
        var patch = """
            *** Begin Patch
            *** Update File: a.txt
             um
            -dois
            +DOIS
             três
            *** End Patch
            """;

        var result = await tool.ExecuteAsync(Args(patch), Ctx(dir), default);

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(Path.Join(dir, "a.txt")), Is.EqualTo("um\nDOIS\ntrês\n"));
            var files = ResultOf(result).GetProperty("files");
            Assert.That(files[0].GetProperty("action").GetString(), Is.EqualTo("update"));
            Assert.That(files[0].GetProperty("added").GetInt32(), Is.EqualTo(1));
            Assert.That(files[0].GetProperty("removed").GetInt32(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ApplyPatch_Update_Move()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        File.WriteAllText(Path.Join(dir, "velho.txt"), "conteúdo\n");
        var tool = new ApplyPatchBuiltinTool();
        var patch = """
            *** Begin Patch
            *** Update File: velho.txt
            *** Move to: sub/novo.txt
             conteúdo
            +extra
            *** End Patch
            """;

        var result = await tool.ExecuteAsync(Args(patch), Ctx(dir), default);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Join(dir, "velho.txt")), Is.False);
            Assert.That(File.ReadAllText(Path.Join(dir, "sub", "novo.txt")),
                Is.EqualTo("conteúdo\nextra\n"));
            var files = ResultOf(result).GetProperty("files");
            Assert.That(files[0].GetProperty("path").GetString(), Is.EqualTo("sub/novo.txt"));
        });
    }

    [Test]
    public async Task ApplyPatch_Delete_Remove()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        File.WriteAllText(Path.Join(dir, "x.txt"), "vai embora\n");
        var tool = new ApplyPatchBuiltinTool();
        var patch = """
            *** Begin Patch
            *** Delete File: x.txt
            *** End Patch
            """;

        var result = await tool.ExecuteAsync(Args(patch), Ctx(dir), default);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Join(dir, "x.txt")), Is.False);
            var files = ResultOf(result).GetProperty("files");
            Assert.That(files[0].GetProperty("action").GetString(), Is.EqualTo("delete"));
            Assert.That(files[0].GetProperty("removed").GetInt32(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ApplyPatch_Jail_BloqueiaFora()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        var tool = new ApplyPatchBuiltinTool();
        foreach (var section in new[] { "Add File: ../fora.txt\n+x", "Delete File: ../fora.txt" })
        {
            var patch = $"*** Begin Patch\n*** {section}\n*** End Patch";
            var result = await tool.ExecuteAsync(Args(patch), Ctx(dir), default);
            Assert.That(result.Text, Does.Contain("../fora.txt"));
            Assert.That(result.Text.ToLowerInvariant(), Does.Contain("fora").Or.Contain("path"));
        }
        Assert.That(File.Exists(Path.Join(_root, "fora.txt")), Is.False);
    }

    [Test]
    public async Task ApplyPatch_Erro_NaoAplicaNada()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        var tool = new ApplyPatchBuiltinTool();
        // 1ª op ok, 2ª quebrada (arquivo inexistente) → nada escrito.
        var patch = """
            *** Begin Patch
            *** Add File: ok.txt
            +oi
            *** Update File: nao-existe.txt
            -x
            +y
            *** End Patch
            """;

        var result = await tool.ExecuteAsync(Args(patch), Ctx(dir), default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Does.Contain("não existe"));
            Assert.That(File.Exists(Path.Join(dir, "ok.txt")), Is.False,
                "patch inválido não aplica nada (fase 1 valida tudo antes)");
        });
    }

    [Test]
    public async Task ApplyPatch_Update_Ambiguo_Erro()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        File.WriteAllText(Path.Join(dir, "dup.txt"), "mesma\nlinha\nmeio\nmesma\nlinha\n");
        var tool = new ApplyPatchBuiltinTool();
        var patch = """
            *** Begin Patch
            *** Update File: dup.txt
             mesma
            -linha
            +LINHA
            *** End Patch
            """;

        var result = await tool.ExecuteAsync(Args(patch), Ctx(dir), default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Does.Contain("ambíguo").Or.Contain("ocorrências"));
            Assert.That(File.ReadAllText(Path.Join(dir, "dup.txt")),
                Is.EqualTo("mesma\nlinha\nmeio\nmesma\nlinha\n"));
        });
    }

    [Test]
    public async Task ApplyPatch_Parse_Invalido()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        var tool = new ApplyPatchBuiltinTool();
        var result = await tool.ExecuteAsync(Args("não é patch"), Ctx(dir), default);
        Assert.That(result.Text, Does.Contain("Begin Patch"));

        var semFim = "*** Begin Patch\n*** Delete File: x.txt\n";
        result = await tool.ExecuteAsync(Args(semFim), Ctx(dir), default);
        Assert.That(result.Text, Does.Contain("End Patch"));
    }

    [Test]
    public async Task ApplyPatch_Add_Existente_Erro()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        File.WriteAllText(Path.Join(dir, "ja.txt"), "existe\n");
        var tool = new ApplyPatchBuiltinTool();
        var patch = """
            *** Begin Patch
            *** Add File: ja.txt
            +novo
            *** End Patch
            """;

        var result = await tool.ExecuteAsync(Args(patch), Ctx(dir), default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Does.Contain("já existe"));
            Assert.That(File.ReadAllText(Path.Join(dir, "ja.txt")), Is.EqualTo("existe\n"));
        });
    }

    [Test]
    public async Task ApplyPatch_Multi_Arquivos()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        File.WriteAllText(Path.Join(dir, "b.txt"), "b1\nb2\n");
        var tool = new ApplyPatchBuiltinTool();
        var patch = """
            *** Begin Patch
            *** Add File: a.txt
            +novo a
            *** Update File: b.txt
             b1
            -b2
            +B2
            *** Delete File: c.txt
            *** End Patch
            """;

        // c.txt não existe → erro antes de qualquer escrita.
        var result = await tool.ExecuteAsync(Args(patch), Ctx(dir), default);
        Assert.That(result.Text, Does.Contain("c.txt"));

        File.WriteAllText(Path.Join(dir, "c.txt"), "c\n");
        result = await tool.ExecuteAsync(Args(patch), Ctx(dir), default);
        Assert.Multiple(() =>
        {
            Assert.That(ResultOf(result).GetProperty("filesChanged").GetInt32(), Is.EqualTo(3));
            Assert.That(File.ReadAllText(Path.Join(dir, "a.txt")), Is.EqualTo("novo a"));
            Assert.That(File.ReadAllText(Path.Join(dir, "b.txt")), Is.EqualTo("b1\nB2\n"));
            Assert.That(File.Exists(Path.Join(dir, "c.txt")), Is.False);
        });
    }

    // ---------------- Format hook ----------------

    [Test]
    public async Task FormatHook_Interpolacao_PathsJailed()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        File.WriteAllText(Path.Join(dir, "a.txt"), "x\n");
        var hook = FormatHook(new()
        {
            ["Format:Command"] = "printf '%s\\n' {files} > _hook.out",
        });

        var warning = await hook.RunForUserAsync("u1", dir, ["a.txt", "sub/b.txt"], default);

        Assert.Multiple(() =>
        {
            Assert.That(warning, Is.Null);
            // {files} interpolado com paths RELATIVOS jailed (não absolutos).
            Assert.That(File.ReadAllText(Path.Join(dir, "_hook.out")),
                Is.EqualTo("a.txt\nsub/b.txt\n"));
        });
    }

    [Test]
    public async Task FormatHook_SemComando_Null()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        var hook = FormatHook();
        var warning = await hook.RunForUserAsync("u1", dir, ["a.txt"], default);
        Assert.That(warning, Is.Null);
        Assert.That(File.Exists(Path.Join(dir, "a.txt")), Is.False);
    }

    [Test]
    public async Task FormatHook_Binding_SobrepoeGlobal()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        // Sem binding cai no Format:Command global.
        var hookSemBinding = FormatHook(new() { ["Format:Command"] = "echo global > _who.txt" });
        _ = await hookSemBinding.RunForUserAsync("u1", dir, ["a.txt"], default);
        Assert.That(File.ReadAllText(Path.Join(dir, "_who.txt")).Trim(),
            Does.StartWith("global"));

        // Com binding vinculado o FormatCommand dele prevalece sobre o global
        // (kv key espelha WorkspaceRepoService.BindingKey — privado lá).
        await _config.SetAsync("u:u1:workspace.repo",
            new OpenWebUI.Application.Contracts.WorkspaceRepoBinding(
                "a/b", "main", "repos/a__b", null, "echo binding > _who.txt"), default);
        var hook = FormatHook(new() { ["Format:Command"] = "echo global > _who.txt" });
        _ = await hook.RunForUserAsync("u1", dir, ["a.txt"], default);
        Assert.That(File.ReadAllText(Path.Join(dir, "_who.txt")).Trim(),
            Does.StartWith("binding"));
    }

    [Test]
    public async Task FormatHook_ChatBinding_SobrepoeGlobalEBindingUsuario()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        // Binding de usuário + global configurados — o binding POR CHAT
        // (SPEC-15) tem precedência quando a run tem chatId.
        await _config.SetAsync("u:u1:workspace.repo",
            new OpenWebUI.Application.Contracts.WorkspaceRepoBinding(
                "a/b", "main", "repos/a__b", null, "echo usuario > _who.txt"), default);
        await _config.SetAsync("chat:c1:workspace.repo",
            new OpenWebUI.Application.Contracts.WorkspaceRepoBinding(
                "a/b", "main", "worktrees/a__b/c1", null, "echo chat > _who.txt"), default);
        var hook = FormatHook(new() { ["Format:Command"] = "echo global > _who.txt" });

        _ = await hook.RunForUserAsync("u1", dir, ["a.txt"], default, chatId: "c1");
        Assert.That(File.ReadAllText(Path.Join(dir, "_who.txt")).Trim(),
            Does.StartWith("chat"));

        // Sem chatId cai no binding de usuário (comportamento legado).
        File.Delete(Path.Join(dir, "_who.txt"));
        _ = await hook.RunForUserAsync("u1", dir, ["a.txt"], default);
        Assert.That(File.ReadAllText(Path.Join(dir, "_who.txt")).Trim(),
            Does.StartWith("usuario"));
    }

    [Test]
    public async Task FormatHook_Falha_ViraWarning()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        // O hook anexa os arquivos como argumentos finais quando o comando não
        // tem {files}; o "#" comenta o append — "exit 3 'a.txt'" daria "too
        // many arguments" no bash (dash ignora o excedente).
        var hook = FormatHook(new() { ["Format:Command"] = "echo deu-ruim >&2; exit 3 # {files}" });
        var warning = await hook.RunForUserAsync("u1", dir, ["a.txt"], default);
        Assert.Multiple(() =>
        {
            Assert.That(warning, Is.Not.Null);
            Assert.That(warning, Does.Contain("exit 3"));
            Assert.That(warning, Does.Contain("deu-ruim"));
        });
    }

    [Test]
    public async Task FormatHook_Timeout_Warning()
    {
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        var hook = FormatHook(new()
        {
            ["Format:Command"] = "sleep 30 && echo {files}",
            ["Format:TimeoutSeconds"] = "1",
        });
        var warning = await hook.RunForUserAsync("u1", dir, ["a.txt"], default);
        Assert.Multiple(() =>
        {
            Assert.That(warning, Is.Not.Null);
            Assert.That(warning, Does.Contain("excedeu"));
        });
    }

    [Test]
    public async Task FormatHook_FileWrite_Integra()
    {
        // file_write roda o hook quando configurado — warning vai pro result.
        var dir = Directory.CreateDirectory(Path.Join(_root, "ws")).FullName;
        var hook = FormatHook(new() { ["Format:Command"] = "exit 1" });
        var tool = new FileWriteBuiltinTool(formatHook: hook);
        var args = JsonSerializer.SerializeToElement(new { path = "a.txt", content = "x\n" });
        var result = await tool.ExecuteAsync(args, Ctx(dir), default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Does.Contain("[format]"));
            Assert.That(ResultOf(result).GetProperty("formatWarning").GetString(),
                Does.Contain("exit 1"));
            // A escrita em si não falhou.
            Assert.That(File.Exists(Path.Join(dir, "a.txt")), Is.True);
        });
    }

    // ---------------- helpers ----------------

    private static void RequireGit()
    {
        try
        {
            Git(null, "--version");
        }
        catch (Win32Exception) { Assert.Ignore("git indisponível neste ambiente."); }
        catch (InvalidOperationException) { Assert.Ignore("git indisponível neste ambiente."); }
    }

    /// <summary>Cria um repo local (remote <c>file://</c>) com 1 commit.</summary>
    private static string CriarOrigem(string branch)
    {
        var origin = Path.Join(Path.GetTempPath(), $"owui-origin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(origin);
        Git(origin, "init", "-b", branch);
        File.WriteAllText(Path.Join(origin, "readme.md"), "oi\n");
        Git(origin, "-c", "user.email=t@t", "-c", "user.name=t", "add", ".");
        Git(origin, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "base");
        return origin;
    }

    private static string? Git(string? workdir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (workdir is not null)
        {
            psi.WorkingDirectory = workdir;
        }
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(psi);
        process!.WaitForExit(15000);
        return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
    }

    /// <summary>IHostEnvironment apontando para a raiz temporária do teste.</summary>
    private sealed class StubEnv(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
