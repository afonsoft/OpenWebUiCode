using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Infra compartilhada dos testes de snapshot de contexto
/// (SPEC-20261020-context-snapshots): banco SQLite temporário por teste,
/// fábricas de serviço/tool context e fakes (summarizer, host env).
/// </summary>
public abstract class SnapshotTestBase
{
    /// <summary>Raiz temporária do teste (workspaces, env).</summary>
    protected string Dir { get; private set; } = null!;
    private string _dbPath = null!;

    [SetUp]
    public void BaseSetUp()
    {
        Dir = Path.Join(Path.GetTempPath(), $"owui-snap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Dir);
        _dbPath = Path.Join(Dir, "t.db");
    }

    [TearDown]
    public void BaseTearDown()
    {
        try { if (Directory.Exists(Dir)) { Directory.Delete(Dir, true); } }
        catch { /* ignore */ }
    }

    /// <summary>DbContext SQLite temporário com schema criado.</summary>
    protected AppDbContext NewDb()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>Service de snapshots com WorkspaceRepoService real sobre a tmp dir.</summary>
    protected ContextSnapshotService Snapshots(AppDbContext db, ConfigService config) =>
        new(db, config, new WorkspaceRepoService(config, new TestEnv(Dir)));

    /// <summary>Resolver de papéis sem cadeias configuradas (cai no fallback).</summary>
    protected static ModelRoleResolver Roles(AppDbContext db, ConfigService config) =>
        new(db, config, _ => Task.FromResult(new List<ModelInfo>()),
            NullLogger<ModelRoleResolver>.Instance);

    /// <summary>Contexto de tool padrão: usuário u1, run r1, modelo m1.</summary>
    protected static BuiltinToolContext Ctx(string? chatId = "c1") =>
        new("u1", chatId, "r1", "/tmp", "/tmp", Model: "m1");

    /// <summary>Args JSON da tool → JsonElement.</summary>
    protected static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    /// <summary>Text do resultado é o JSON do payload (contrato das tools).</summary>
    protected static JsonElement ResultJson(BuiltinToolResult r) =>
        JsonDocument.Parse(r.Text).RootElement;

    internal sealed class FakeSummarizer(string? result) : IContextSummarizer
    {
        public Task<ContextSummary?> SummarizeAsync(
            string model, string transcript, string? userId, CancellationToken ct) =>
            Task.FromResult(result is null ? null : new ContextSummary(result, [], [], []));
    }

    internal sealed class ThrowingSummarizer : IContextSummarizer
    {
        public Task<ContextSummary?> SummarizeAsync(
            string model, string transcript, string? userId, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    /// <summary>IHostEnvironment mínimo apontando para a raiz temporária.</summary>
    protected sealed class TestEnv(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
