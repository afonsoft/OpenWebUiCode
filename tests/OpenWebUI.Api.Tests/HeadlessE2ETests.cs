using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261021-harness-seams RF-C1: <see cref="OpenWebUI.Api.Headless.HeadlessRunner"/>
/// de ponta a ponta contra a DI real — usuário existe, chat/run criados, o
/// executor falha sem provider de modelo e o exit code reflete o status.
/// </summary>
public class HeadlessE2ETests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-headless-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default",
            $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
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

    [Test]
    public async Task Headless_Run_Completa_Cria_Chat_Run_E_Sai_Com_Status()
    {
        var email = $"headless-{Guid.NewGuid():N}@local";
        var res = await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name = "H", email, password = "senha123" });
        Assert.That(res.IsSuccessStatusCode, Is.True);

        var code = await OpenWebUI.Api.Headless.HeadlessRunner.RunAsync(
            _factory.Services,
            ["--headless", "--user-email", email,
             "--prompt", "diga oi", "--model", "llama3"],
            CancellationToken.None);

        // Sem provider configurado a run falha → exit 1 (mas todo o caminho
        // de criação/enqueue/eventos rodou — é o que este teste cobre).
        Assert.That(code, Is.EqualTo(1));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ChatRuns.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt).FirstAsync();
        Assert.Multiple(() =>
        {
            Assert.That(run.Status, Is.Not.Null.And.Not.Empty);
            Assert.That(
                db.Chats.AsNoTracking().Count(c => c.Id == run.ChatId),
                Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Headless_ChatId_Inexistente_Exit2()
    {
        var email = $"headless-{Guid.NewGuid():N}@local";
        await _client.PostAsJsonAsync("/api/v1/auths/signup",
            new { name = "H2", email, password = "senha123" });

        var code = await OpenWebUI.Api.Headless.HeadlessRunner.RunAsync(
            _factory.Services,
            ["--headless", "--user-email", email,
             "--prompt", "oi", "--chat-id", "chat-que-nao-existe"],
            CancellationToken.None);
        Assert.That(code, Is.EqualTo(2));
    }
}
