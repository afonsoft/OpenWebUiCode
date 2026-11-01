using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261018-references: split de ref://, resolve local/git, refresh
/// 24h com cache stale, jail de sub-path, gate de anexo nas tools e CRUD.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ReferenceServiceTests
{
    private string _tmp = null!;
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _tmp = Path.Join(Path.GetTempPath(), $"refs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
        _dbPath = Path.Join(_tmp, "refs.db");
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tmp, true); }
        catch (IOException) { /* git pode segurar handles em Windows */ }
    }

    private AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private ReferenceService NewService(AppDbContext db)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tmp);
        Environment.SetEnvironmentVariable("DATA_ROOT", Path.Join(_tmp, "data"));
        return new ReferenceService(db, env,
            NullLogger<ReferenceService>.Instance);
    }

    // S4036 (SPEC-20261031-sonar-vuln-code): RunGitAsync resolve o path
    // absoluto do git na criação do processo — sem depender do PATH em
    // runtime.
    [Test]
    public void ResolveGitPath_EncontraExeNoPath_RetornaAbsoluto()
    {
        var dir = Path.Join(_tmp, "bin");
        Directory.CreateDirectory(dir);
        var expected = Path.Join(dir, OperatingSystem.IsWindows() ? "git.exe" : "git");
        File.WriteAllText(expected, "");

        var resolved = ReferenceService.ResolveGitPath(dir);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.EqualTo(expected));
            Assert.That(Path.IsPathRooted(resolved), Is.True);
        });
    }

    [Test]
    public void ResolveGitPath_PathSemGit_FallbackAbsolutoOuErroLegivel()
    {
        // PATH sem git: cai nos fallbacks de container (/usr/bin/git etc.)
        // ou lança erro legível — nunca depende de resolução por PATH.
        try
        {
            var resolved = ReferenceService.ResolveGitPath(Path.Join(_tmp, "vazio"));
            Assert.Multiple(() =>
            {
                Assert.That(Path.IsPathRooted(resolved), Is.True);
                Assert.That(File.Exists(resolved), Is.True);
            });
        }
        catch (FileNotFoundException ex)
        {
            Assert.That(ex.Message, Does.Contain("git"));
        }
    }

    private async Task<ReferenceEntry> SeedRefAsync(
        AppDbContext db, string userId, string alias, string source,
        bool hidden = false)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entry = new ReferenceEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId, Alias = alias, Source = source,
            Hidden = hidden, LastFetchedAt = now,
            CreatedAt = now, UpdatedAt = now,
        };
        db.ReferenceEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry;
    }

    private static string NewGitRepo()
    {
        var dir = Path.Join(Path.GetTempPath(), $"refs-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Run("git", dir, "init", "-q");
        Run("git", dir, "config", "user.email", "t@t");
        Run("git", dir, "config", "user.name", "t");
        File.WriteAllText(Path.Join(dir, "a.txt"), "v1");
        Run("git", dir, "add", "-A");
        Run("git", dir, "commit", "-qm", "init");
        return dir;
    }

    private static void Run(string cmd, string workdir, params string[] args)
    {
        var psi = new ProcessStartInfo(cmd)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.WorkingDirectory = workdir;
        using var p = Process.Start(psi)!;
        p.WaitForExit(20_000);
        Assert.That(p.ExitCode, Is.EqualTo(0),
            $"{cmd} {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
    }

    // ---------- TrySplitRef ----------

    [Test]
    public void Split_ParsesRefUriComSubpath()
    {
        Assert.That(ReferenceService.TrySplitRef(
            "ref://docs/src/a.cs", out var alias, out var sub), Is.True);
        Assert.That(alias, Is.EqualTo("docs"));
        Assert.That(sub, Is.EqualTo("src/a.cs"));
        Assert.That(ReferenceService.TrySplitRef("ref:docs", out var a2, out var s2), Is.True);
        Assert.That(a2, Is.EqualTo("docs"));
        Assert.That(s2, Is.Null);
    }

    [Test]
    public void Split_RejeitaCaminhosComuns()
    {
        Assert.That(ReferenceService.TrySplitRef("src/a.cs", out _, out _), Is.False);
        Assert.That(ReferenceService.TrySplitRef("../x", out _, out _), Is.False);
        Assert.That(ReferenceService.TrySplitRef(null, out _, out _), Is.False);
        // Maiúsculas normalizam para o alias lowercase (case-insensitive).
        Assert.That(ReferenceService.TrySplitRef(
            "ref://UPPER/x", out var upper, out _), Is.True);
        Assert.That(upper, Is.EqualTo("upper"));
    }

    // ---------- Resolve local ----------

    [Test]
    public async Task Resolve_DirLocalExistente_RetornaRaiz()
    {
        await using var db = NewDb();
        var refs = NewService(db);
        await SeedRefAsync(db, "u1", "docs", _tmp);

        var resolved = await refs.ResolveAsync("u1", "docs", default);
        Assert.That(resolved, Is.Not.Null);
        Assert.That(resolved!.Root, Is.EqualTo(Path.GetFullPath(_tmp)));
    }

    [Test]
    public async Task Resolve_DirLocalInexistente_RetornaNull()
    {
        await using var db = NewDb();
        var refs = NewService(db);
        await SeedRefAsync(db, "u1", "docs", Path.Join(_tmp, "nope"));
        Assert.That(await refs.ResolveAsync("u1", "docs", default), Is.Null);
        Assert.That(await refs.ResolveAsync("u1", "desconhecido", default), Is.Null);
    }

    [Test]
    public async Task Resolve_RefOculta_AindaResolve()
    {
        await using var db = NewDb();
        var refs = NewService(db);
        await SeedRefAsync(db, "u1", "segredo", _tmp, hidden: true);
        Assert.That(await refs.ResolveAsync("u1", "segredo", default), Is.Not.Null);
    }

    // ---------- Git clone / cache / refresh ----------

    [Test]
    public async Task Resolve_GitClonaShallowEReusaCache()
    {
        var repo = NewGitRepo();
        await using var db = NewDb();
        var refs = NewService(db);
        var entry = await SeedRefAsync(
            db, "u1", "lib", $"file://{repo}");

        var first = await refs.ResolveAsync("u1", "lib", default);
        Assert.That(first, Is.Not.Null);
        Assert.That(File.Exists(Path.Join(first!.Root, "a.txt")), Is.True);
        Assert.That(Directory.Exists(Path.Join(first.Root, ".git")), Is.True);

        // Segundo resolve dentro da janela de 24h não re-fetcha: marca
        // um arquivo só no cache e ele continua lá.
        File.WriteAllText(Path.Join(first.Root, "mark.txt"), "x");
        var second = await refs.ResolveAsync("u1", "lib", default);
        Assert.That(File.Exists(Path.Join(second!.Root, "mark.txt")), Is.True);

        _ = entry; // entry usado acima; refresh testado no próximo caso.
    }

    [Test]
    public async Task Resolve_GitStale_FazFetchEReset()
    {
        var repo = NewGitRepo();
        await using var db = NewDb();
        var refs = NewService(db);
        var entry = await SeedRefAsync(db, "u1", "lib", $"file://{repo}");

        var first = await refs.ResolveAsync("u1", "lib", default);
        Assert.That(first, Is.Not.Null);

        // Novo commit na origem + stale (24h) → fetch+reset traz o arquivo.
        File.WriteAllText(Path.Join(repo, "b.txt"), "v2");
        Run("git", repo, "add", "-A");
        Run("git", repo, "commit", "-qm", "b");
        await refs.MarkStaleAsync(entry, default);

        var second = await refs.ResolveAsync("u1", "lib", default);
        Assert.That(second, Is.Not.Null);
        Assert.That(second!.Warning, Is.Null);
        Assert.That(File.Exists(Path.Join(second.Root, "b.txt")), Is.True);
    }

    [Test]
    public async Task Resolve_GitFetchFalha_ServeCacheComWarning()
    {
        var repo = NewGitRepo();
        await using var db = NewDb();
        var refs = NewService(db);
        var entry = await SeedRefAsync(db, "u1", "lib", $"file://{repo}");

        var first = await refs.ResolveAsync("u1", "lib", default);
        Assert.That(first, Is.Not.Null);

        // Origem some → stale → fetch falha → cache com warning (RF-001).
        Directory.Delete(repo, true);
        await refs.MarkStaleAsync(entry, default);

        var second = await refs.ResolveAsync("u1", "lib", default);
        Assert.That(second, Is.Not.Null);
        Assert.That(second!.Warning, Does.Contain("cache antigo"));
        Assert.That(File.Exists(Path.Join(second.Root, "a.txt")), Is.True);
    }

    [Test]
    public async Task Resolve_GitCloneFalha_RetornaNull()
    {
        await using var db = NewDb();
        var refs = NewService(db);
        // Fonte git inexistente, nunca clonada → resolve retorna null.
        var entry = await SeedRefAsync(
            db, "u1", "ghost", "file:///nonexistent/repo-xyz");
        entry.LastFetchedAt = 0;
        await db.SaveChangesAsync();

        var resolved = await refs.ResolveAsync("u1", "ghost", default);
        Assert.That(resolved, Is.Null);
    }

    // ---------- Jail / listing / tools ----------

    [Test]
    public async Task ResolveToolTarget_PathNaoRef_Recusa()
    {
        await using var db = NewDb();
        var refs = NewService(db);

        var (target, error) = await refs.ResolveToolTargetAsync(
            "u1", "/etc/passwd", ["docs"], default);
        Assert.That(target, Is.Null);
        Assert.That(error, Does.Contain("não é um ref:// válido"));
    }

    [Test]
    public async Task ResolveToolTarget_RefIndisponivel_Recusa()
    {
        await using var db = NewDb();
        var refs = NewService(db);
        // Alias anexado mas com fonte que não resolve → "indisponível".
        await SeedRefAsync(db, "u1", "gone", Path.Join(_tmp, "inexistente"));

        var (target, error) = await refs.ResolveToolTargetAsync(
            "u1", "ref://gone/a.txt", ["gone"], default);
        Assert.That(target, Is.Null);
        Assert.That(error, Does.Contain("indisponível"));
    }

    [Test]
    public async Task ResolveToolTarget_RecusaTraversal()
    {
        await using var db = NewDb();
        var refs = NewService(db);
        await SeedRefAsync(db, "u1", "docs", _tmp);
        File.WriteAllText(Path.Join(_tmp, "a.txt"), "x");

        var (target, error) = await refs.ResolveToolTargetAsync(
            "u1", "ref://docs/../../etc/passwd", ["docs"], default);
        Assert.That(target, Is.Null);
        Assert.That(error, Is.Not.Null);
    }

    [Test]
    public async Task ResolveToolTarget_ExigeAnexo()
    {
        await using var db = NewDb();
        var refs = NewService(db);
        await SeedRefAsync(db, "u1", "docs", _tmp);

        // Sem anexo → erro orientando o @alias.
        var (t1, e1) = await refs.ResolveToolTargetAsync(
            "u1", "ref://docs/a.txt", null, default);
        Assert.That(t1, Is.Null);
        Assert.That(e1, Does.Contain("não está anexada"));

        // Anexado → target dentro do jail.
        var (t2, e2) = await refs.ResolveToolTargetAsync(
            "u1", "ref://docs/a.txt", ["docs"], default);
        Assert.That(e2, Is.Null);
        Assert.That(t2!.Display, Does.StartWith("ref://docs/"));
    }

    [Test]
    public async Task ListTree_OrdenadoESemGitDir()
    {
        await using var db = NewDb();
        var refs = NewService(db);
        var dir = Path.Join(_tmp, "lib");
        Directory.CreateDirectory(Path.Join(dir, "sub"));
        Directory.CreateDirectory(Path.Join(dir, ".git"));
        File.WriteAllText(Path.Join(dir, "b.txt"), "b");
        File.WriteAllText(Path.Join(dir, "sub", "a.txt"), "a");
        File.WriteAllText(Path.Join(dir, ".git", "cfg"), "x");
        await SeedRefAsync(db, "u1", "lib", dir);

        var resolved = await refs.ResolveAsync("u1", "lib", default);
        var tree = await ReferenceService.ListTreeAsync(resolved!, default);
        Assert.That(tree, Is.EqualTo(new[] { "b.txt", "sub/a.txt" })
            .Or.EqualTo(new[] { "b.txt", Path.Join("sub", "a.txt") }));
    }

    [Test]
    public async Task FileRead_RefAnexada_LeArquivo()
    {
        await using var db = NewDb();
        var refs = NewService(db);
        var dir = Path.Join(_tmp, "lib");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "a.txt"), "hello ref");
        await SeedRefAsync(db, "u1", "lib", dir);

        var tool = new FileReadBuiltinTool(
            new HistoryResourceService(db), NewToolOutputs(), refs);
        var ctx = new BuiltinToolContext(
            "u1", null, null, _tmp, _tmp, AttachedRefs: ["lib"]);
        var args = System.Text.Json.JsonDocument
            .Parse("""{"path":"ref://lib/a.txt"}""").RootElement;
        var result = await tool.ExecuteAsync(args, ctx, default);
        Assert.That(result.Text, Does.Contain("hello ref"));
    }

    [Test]
    public async Task FileRead_RefNaoAnexada_Recusa()
    {
        await using var db = NewDb();
        var refs = NewService(db);
        await SeedRefAsync(db, "u1", "lib", _tmp);
        var tool = new FileReadBuiltinTool(
            new HistoryResourceService(db), NewToolOutputs(), refs);
        var ctx = new BuiltinToolContext("u1", null, null, _tmp, _tmp);
        var args = System.Text.Json.JsonDocument
            .Parse("""{"path":"ref://lib/a.txt"}""").RootElement;
        var result = await tool.ExecuteAsync(args, ctx, default);
        Assert.That(result.Text, Does.Contain("não está anexada"));
    }

    [Test]
    public async Task FileWrite_RefUri_RecusaSomenteLeitura()
    {
        var tool = new FileWriteBuiltinTool();
        var ctx = new BuiltinToolContext("u1", null, null, _tmp, _tmp);
        var args = System.Text.Json.JsonDocument
            .Parse("""{"path":"ref://lib/a.txt","content":"x"}""").RootElement;
        var result = await tool.ExecuteAsync(args, ctx, default);
        Assert.That(result.Text, Does.Contain("somente-leitura"));
    }

    private ToolOutputService NewToolOutputs() =>
        new(new ConfigService(NewDb(), TestCache.Create()),
            new StubEnv(_tmp), NullLogger<ToolOutputService>.Instance);

    private sealed class StubEnv(string root)
        : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider
            ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
