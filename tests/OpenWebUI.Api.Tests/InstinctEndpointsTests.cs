using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Contrato de <c>/api/v1/instincts</c>
/// (SPEC-20261023-instincts-learning RF-004/RF-005): lista com
/// pendingCount, PATCH approve/reject/delete e evolve → draft SKILL.md.
/// </summary>
[TestFixture, IsolateEnvironment]
public class InstinctEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _user = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-instincts-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default",
            $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _user = await SignUpAsync("User", "u@instinct.local");
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
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    private AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private async Task<Instinct> SeedAsync(
        string status = "active", string domain = "dotnet",
        string? trigger = null, double conf = 0.8)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var i = new Instinct
        {
            UserId = _user.User.Id,
            Scope = "user",
            Domain = domain,
            Trigger = trigger ?? $"t-{Guid.NewGuid():N}",
            Body = "do x",
            Confidence = conf,
            Status = status,
            HitCount = 1,
            SourceRefs = "[]",
            CreatedAt = now,
            UpdatedAt = now,
            LastSeenAt = now,
        };
        await using var db = CreateDb();
        db.Instincts.Add(i);
        await db.SaveChangesAsync();
        return i;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage r)
    {
        var doc = JsonDocument.Parse(
            await r.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Test]
    public async Task List_Devolve_Itens_Do_Usuario()
    {
        var seeded = await SeedAsync(trigger: "list-me");
        UseToken(_user.Token);

        var response = await _client.GetAsync(
            "/api/v1/instincts?status=active");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await ReadJson(response);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.That(items.Any(
            i => i.GetProperty("id").GetString() == seeded.Id), Is.True);
        Assert.That(items.All(
            i => i.GetProperty("status").GetString() == "active"), Is.True);
    }

    [Test]
    public async Task List_PendingCount_E_Status_Invalido()
    {
        await SeedAsync(status: "pending", conf: 0.5);
        UseToken(_user.Token);

        var response = await _client.GetAsync("/api/v1/instincts");
        var body = await ReadJson(response);
        Assert.That(body.GetProperty("pendingCount").GetInt32(),
            Is.GreaterThanOrEqualTo(1));

        var bad = await _client.GetAsync(
            "/api/v1/instincts?status=bogus");
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var badScope = await _client.GetAsync(
            "/api/v1/instincts?scope=team");
        Assert.That(badScope.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Patch_Approve_Promove_E_Reforça_Conf()
    {
        var seeded = await SeedAsync(status: "pending", conf: 0.5);
        UseToken(_user.Token);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/instincts/{seeded.Id}", new { action = "approve" });
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await ReadJson(response);
        Assert.That(body.GetProperty("item")
            .GetProperty("status").GetString(), Is.EqualTo("active"));
        Assert.That(body.GetProperty("item")
            .GetProperty("confidence").GetDouble(),
            Is.GreaterThanOrEqualTo(0.71));
    }

    [Test]
    public async Task Patch_Reject_Delete_E_Erros()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        UseToken(_user.Token);

        var reject = await _client.PatchAsJsonAsync(
            $"/api/v1/instincts/{a.Id}", new { action = "reject" });
        Assert.That(reject.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var del = await _client.PatchAsJsonAsync(
            $"/api/v1/instincts/{b.Id}", new { action = "delete" });
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var db = CreateDb();
        Assert.That(await db.Instincts.FindAsync(b.Id), Is.Null);

        var notFound = await _client.PatchAsJsonAsync(
            "/api/v1/instincts/nao-existe", new { action = "approve" });
        Assert.That(notFound.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var badAction = await _client.PatchAsJsonAsync(
            $"/api/v1/instincts/{a.Id}", new { action = "explode" });
        Assert.That(badAction.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Evolve_409_Sem_Massa_E_200_Com_3()
    {
        var domain = $"d{Guid.NewGuid():N}";
        UseToken(_user.Token);

        var empty = await _client.PostAsJsonAsync(
            "/api/v1/instincts/evolve", new { domain });
        Assert.That(empty.StatusCode, Is.EqualTo((HttpStatusCode)409));

        await SeedAsync(domain: domain);
        await SeedAsync(domain: domain);
        await SeedAsync(domain: domain);
        var ok = await _client.PostAsJsonAsync(
            "/api/v1/instincts/evolve", new { domain });
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await ok.Content.ReadAsStringAsync());
        var body = await ReadJson(ok);
        var draft = body.GetProperty("draftSkillMd").GetString()!;
        Assert.That(draft, Does.Contain($"name: learned-{domain}"));
        Assert.That(draft, Does.Contain("not policy"));
        Assert.That(body.GetProperty("sources").GetArrayLength(),
            Is.EqualTo(3));
    }
}
