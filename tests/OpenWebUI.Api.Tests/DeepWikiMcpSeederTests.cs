using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;
using System.Net;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Teste do <see cref="DeepWikiMcpSeeder"/>: o server global
/// <c>deepwiki</c> é provisionado no boot com <c>UserId=null</c> e
/// habilitado; o refresh de tools é best-effort (rede externa pode não
/// existir no ambiente de teste — a linha do server existe de qualquer
/// forma e o marker impede recriação após remoção admin).
/// </summary>
[TestFixture, IsolateEnvironment]
public class DeepWikiMcpSeederTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _dbPath = TestInfra.NewDbPath("openwebui-deepwiki-seed");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory.Dispose();
        TestInfra.DeleteDb(_dbPath);
    }

    [Test]
    public async Task Seed_CriaServerGlobalHabilitado()
    {
        _ = _factory.CreateClient();   // dispara o host + hosted services
        // Seeder: delay 4s + refresh best-effort.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        McpServer? server = null;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            server = await db.McpServers.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == DeepWikiMcpSeeder.ServerId);
            if (server is not null)
            {
                break;
            }
            await Task.Delay(500);
        }

        Assert.That(server, Is.Not.Null, "server deepwiki não provisionado");
        Assert.Multiple(() =>
        {
            Assert.That(server!.UserId, Is.Null, "deve ser global (org)");
            Assert.That(server.Enabled, Is.True, "default habilitado");
            Assert.That(server.Url, Is.EqualTo(DeepWikiMcpSeeder.ServerUrl));
            Assert.That(server.Transport, Is.EqualTo("http"));
        });
    }
}
