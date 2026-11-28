using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261128-wait-tool: <c>builtin wait</c> — aguarda N segundos
/// (cap 60) ou até um job de background sair de <c>running</c>.
/// </summary>
[TestFixture]
public sealed class WaitBuiltinToolTests
{
    private ServiceProvider _provider = null!;
    private string _dbPath = null!;

    private static readonly BuiltinToolContext Ctx =
        new("u1", "c1", "r1", "/tmp/ws", "/tmp/up");

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"owui-wait-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(
            o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddLogging();
        _provider = services.BuildServiceProvider();
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        db.Users.Add(new User { Id = "u1", Name = "U1", Email = "u1@t.local", Role = "user" });
        db.Chats.Add(new Chat { Id = "c1", UserId = "u1", Title = "t" });
        db.SaveChanges();
    }

    [TearDown]
    public void TearDown() => _provider.Dispose();

    private ChatJobService Jobs() =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<ILogger<ChatJobService>>());

    [Test]
    public async Task Wait_SemJob_AguardaSeconds()
    {
        var tool = new WaitBuiltinTool(Jobs());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await tool.ExecuteAsync(Args("""{"seconds":1}"""), Ctx, default);
        sw.Stop();
        Assert.Multiple(() =>
        {
            Assert.That(sw.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(0.9)));
            Assert.That(r.Text, Does.Contain("1s"));
        });
        Assert.That(tool.Tier, Is.EqualTo(ApprovalTier.Read));
    }

    [Test]
    public async Task Wait_JobInexistente_RetornaErro()
    {
        var tool = new WaitBuiltinTool(Jobs());
        var r = await tool.ExecuteAsync(
            Args("""{"seconds":2,"job_id":"job-404"}"""), Ctx, default);
        Assert.That(r.Text, Does.Contain("não encontrado"));
    }

    [Test]
    public async Task Wait_JobTerminado_RetornaStatus()
    {
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChatJobs.Add(new ChatJob
            {
                Id = "j1", UserId = "u1", ChatId = "c1",
                Command = "echo ok", Status = ChatJobStatus.Completed,
            });
            await db.SaveChangesAsync();
        }
        var tool = new WaitBuiltinTool(Jobs());
        var r = await tool.ExecuteAsync(
            Args("""{"seconds":5,"job_id":"j1"}"""), Ctx, default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("completed"));
            Assert.That(r.Result, Is.Not.Null);
        });
    }
}
