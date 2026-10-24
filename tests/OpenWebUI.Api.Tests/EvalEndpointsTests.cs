using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Contrato dos endpoints de avaliação (SPEC-20261023-agent-evals):
/// datasets/casos/CRUD, import JSONL por linha, criação de EvalRun com
/// validação de k, baselines e compare com diff por caso.
/// </summary>
[TestFixture, IsolateEnvironment]
public class EvalEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private AuthResponse _admin = null!;
    private AuthResponse _user = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-evals-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default",
            $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("Providers__MaxAttempts", "1");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        _admin = await SignUpAsync("Admin", "admin@evals.local");
        UseToken(_admin.Token);
        var updated = await _client.PostAsJsonAsync(
            "/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
        _user = await SignUpAsync("User", "user@evals.local");
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("Providers__MaxAttempts", null);
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

    private async Task<string> SeedDatasetAsync(int cases = 2)
    {
        await using var db = CreateDb();
        var ds = new EvalDataset
        {
            UserId = _user.User.Id, Name = "ds", CreatedAt = 1,
        };
        db.EvalDatasets.Add(ds);
        for (var i = 0; i < cases; i++)
        {
            db.EvalCases.Add(new EvalCase
            {
                DatasetId = ds.Id, Prompt = $"p{i}",
                Expected = $"e{i}", CreatedAt = i,
            });
        }
        await db.SaveChangesAsync();
        return ds.Id;
    }

    [Test]
    public async Task DatasetCrud_Works()
    {
        UseToken(_user.Token);
        var created = await _client.PostAsJsonAsync("/api/v1/evals/datasets",
            new { name = "smoke", description = "d", tags = new[] { "a" } });
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var doc = System.Text.Json.JsonDocument
            .Parse(await created.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetString()!;

        var list = await _client.GetAsync("/api/v1/evals/datasets");
        Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await list.Content.ReadAsStringAsync(),
            Does.Contain(id));

        var del = await _client.DeleteAsync($"/api/v1/evals/datasets/{id}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    [Test]
    public async Task CasesImport_JsonlPerLineErrors()
    {
        var dsId = await SeedDatasetAsync(0);
        UseToken(_user.Token);
        var jsonl = string.Join('\n',
            """{"prompt":"q1","expected":"a1","grader":"exact"}""",
            """{"prompt":"q2","grader":"bogus"}""",
            "{not json");
        var res = await _client.PostAsync(
            $"/api/v1/evals/datasets/{dsId}/cases:import",
            new StringContent(jsonl, Encoding.UTF8, "application/jsonl"));
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await res.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("\"imported\":1"));
        Assert.That(body, Does.Contain("bogus"));

        await using var db = CreateDb();
        Assert.That(await db.EvalCases.CountAsync(c => c.DatasetId == dsId),
            Is.EqualTo(1));
    }

    [Test]
    public async Task CreateRun_ValidatesDatasetAndK()
    {
        UseToken(_user.Token);
        var missing = await _client.PostAsJsonAsync("/api/v1/evals/runs",
            new { datasetId = "nope", modelId = "m" });
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var dsId = await SeedDatasetAsync(2);
        var bigK = await _client.PostAsJsonAsync("/api/v1/evals/runs",
            new { datasetId = dsId, modelId = "m", k = 99 });
        Assert.That(bigK.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var ok = await _client.PostAsJsonAsync("/api/v1/evals/runs",
            new { datasetId = dsId, modelId = "m", k = 2 });
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await ok.Content.ReadAsStringAsync());
        var body = await ok.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("\"totalJobs\":4"));
        Assert.That(body, Does.Contain("\"status\":\"pending\""));
    }

    [Test]
    public async Task RunView_AggregatesPassAtK()
    {
        var dsId = await SeedDatasetAsync(2);
        await using (var db = CreateDb())
        {
            var cases = await db.EvalCases
                .Where(c => c.DatasetId == dsId).ToListAsync();
            var run = new EvalRun
            {
                DatasetId = dsId, UserId = _user.User.Id, ModelId = "m",
                K = 2, Status = "done", TotalJobs = 4, DoneJobs = 4,
            };
            db.EvalRuns.Add(run);
            // caso[0]: 1 pass em 2 reps (pass@k=1); caso[1]: 0 pass.
            db.EvalResults.AddRange(
                Result(run.Id, cases[0].Id, 0, true),
                Result(run.Id, cases[0].Id, 1, false),
                Result(run.Id, cases[1].Id, 0, false),
                Result(run.Id, cases[1].Id, 1, false));
            await db.SaveChangesAsync();
            RunId = run.Id;
        }
        UseToken(_user.Token);
        var res = await _client.GetAsync($"/api/v1/evals/runs/{RunId}");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await res.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("\"passAtK\":0.5"));
        Assert.That(body, Does.Contain("\"costUsd\""));
    }

    private string RunId = "";

    [Test]
    public async Task BaselineAndCompare_ClassifiesCases()
    {
        var dsId = await SeedDatasetAsync(2);
        string baseRunId, curRunId;
        await using (var db = CreateDb())
        {
            var cases = await db.EvalCases
                .Where(c => c.DatasetId == dsId).OrderBy(c => c.Id)
                .ToListAsync();
            var baseRun = new EvalRun
            {
                DatasetId = dsId, UserId = _user.User.Id, ModelId = "m",
                Status = "done",
            };
            var curRun = new EvalRun
            {
                DatasetId = dsId, UserId = _user.User.Id, ModelId = "m2",
                Status = "done",
            };
            db.EvalRuns.AddRange(baseRun, curRun);
            // caso[0]: fail→pass (improved); caso[1]: pass→fail (regressed).
            db.EvalResults.AddRange(
                Result(baseRun.Id, cases[0].Id, 0, false),
                Result(baseRun.Id, cases[1].Id, 0, true),
                Result(curRun.Id, cases[0].Id, 0, true),
                Result(curRun.Id, cases[1].Id, 0, false));
            await db.SaveChangesAsync();
            baseRunId = baseRun.Id;
            curRunId = curRun.Id;
        }
        UseToken(_user.Token);
        var bl = await _client.PostAsJsonAsync("/api/v1/evals/baselines",
            new { name = "v1", evalRunId = baseRunId });
        Assert.That(bl.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var cmp = await _client.GetAsync(
            $"/api/v1/evals/runs/{curRunId}/compare?baseline=v1");
        Assert.That(cmp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await cmp.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("improved"));
        Assert.That(body, Does.Contain("regressed"));

        var md = await _client.GetAsync(
            $"/api/v1/evals/runs/{curRunId}/compare?baseline=v1&format=md");
        Assert.That(md.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await md.Content.ReadAsStringAsync(),
            Does.Contain("| case | status |"));
    }

    [Test]
    public async Task Compare_WithoutBaseline_404()
    {
        var dsId = await SeedDatasetAsync(1);
        await using var db = CreateDb();
        var run = new EvalRun
        {
            DatasetId = dsId, UserId = _user.User.Id, ModelId = "m",
            Status = "done",
        };
        db.EvalRuns.Add(run);
        await db.SaveChangesAsync();

        UseToken(_user.Token);
        var res = await _client.GetAsync(
            $"/api/v1/evals/runs/{run.Id}/compare?baseline=missing");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task EvalEndpoints_RequireAuth()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var res = await _client.GetAsync("/api/v1/evals/datasets");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        UseToken(_user.Token);
    }

    [Test]
    public async Task EphemeralChat_HiddenFromSidebar()
    {
        await using var db = CreateDb();
        db.Chats.Add(new Chat
        {
            UserId = _user.User.Id, Title = "eval:x:y:0",
            IsEphemeral = true, CreatedAt = 1, UpdatedAt = 1,
        });
        await db.SaveChangesAsync();

        UseToken(_user.Token);
        var res = await _client.GetAsync("/api/v1/chats");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await res.Content.ReadAsStringAsync(),
            Does.Not.Contain("eval:x:y:0"));
    }

    private static EvalResult Result(string runId, string caseId, int rep,
        bool pass) => new()
    {
        EvalRunId = runId, CaseId = caseId, Rep = rep, Pass = pass,
        Score = pass ? 1 : 0, CreatedAt = rep,
    };
    [Test]
    public async Task CaseCrud_AndGetDataset_Work()
    {
        var dsId = await SeedDatasetAsync(0);
        UseToken(_user.Token);

        var bad = await _client.PostAsJsonAsync(
            $"/api/v1/evals/datasets/{dsId}/cases",
            new { prompt = "", grader = "exact" });
        Assert.That(bad.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var badGrader = await _client.PostAsJsonAsync(
            $"/api/v1/evals/datasets/{dsId}/cases",
            new { prompt = "q", grader = "nope" });
        Assert.That(badGrader.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var ok = await _client.PostAsJsonAsync(
            $"/api/v1/evals/datasets/{dsId}/cases",
            new { prompt = "q", expected = "a", grader = "exact",
                  expectTools = new[] { "fs_read" }, maxSteps = 3 });
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var doc = JsonDocument.Parse(
            await ok.Content.ReadAsStringAsync());
        var caseId = doc.RootElement.GetProperty("id").GetString()!;

        var get = await _client.GetAsync($"/api/v1/evals/datasets/{dsId}");
        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await get.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("fs_read"));

        var del = await _client.DeleteAsync(
            $"/api/v1/evals/datasets/{dsId}/cases/{caseId}");
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        var delAgain = await _client.DeleteAsync(
            $"/api/v1/evals/datasets/{dsId}/cases/{caseId}");
        Assert.That(delAgain.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var missingDs = await _client.GetAsync("/api/v1/evals/datasets/nope");
        Assert.That(missingDs.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Baseline_Upsert_List_Delete()
    {
        var dsId = await SeedDatasetAsync(1);
        string runId;
        await using (var db = CreateDb())
        {
            var run = new EvalRun
            {
                DatasetId = dsId, UserId = _user.User.Id, ModelId = "m",
                Status = "done",
            };
            db.EvalRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }
        UseToken(_user.Token);
        var b1 = await _client.PostAsJsonAsync("/api/v1/evals/baselines",
            new { name = "upsert-me", evalRunId = runId });
        Assert.That(b1.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        // Mesmo nome → aponta para o novo run (upsert).
        var b2 = await _client.PostAsJsonAsync("/api/v1/evals/baselines",
            new { name = "upsert-me", evalRunId = runId });
        Assert.That(b2.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = await _client.GetAsync("/api/v1/evals/baselines");
        Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await list.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("upsert-me"));

        await using (var db = CreateDb())
        {
            var row = await db.EvalBaselines.FirstAsync(
                b => b.Name == "upsert-me" && b.UserId == _user.User.Id);
            var del = await _client.DeleteAsync(
                $"/api/v1/evals/baselines/{row.Id}");
            Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            var delAgain = await _client.DeleteAsync(
                $"/api/v1/evals/baselines/{row.Id}");
            Assert.That(delAgain.StatusCode,
                Is.EqualTo(HttpStatusCode.NotFound));
        }
    }

    [Test]
    public async Task ListRuns_AndResults_Work()
    {
        var dsId = await SeedDatasetAsync(1);
        string runId;
        await using (var db = CreateDb())
        {
            var cases = await db.EvalCases
                .Where(c => c.DatasetId == dsId).ToListAsync();
            var run = new EvalRun
            {
                DatasetId = dsId, UserId = _user.User.Id, ModelId = "m",
                Status = "done", K = 1, TotalJobs = 1, DoneJobs = 1,
            };
            db.EvalRuns.Add(run);
            db.EvalResults.Add(Result(run.Id, cases[0].Id, 0, true));
            await db.SaveChangesAsync();
            runId = run.Id;
        }
        UseToken(_user.Token);
        var runs = await _client.GetAsync("/api/v1/evals/runs");
        Assert.That(runs.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await runs.Content.ReadAsStringAsync(),
            Does.Contain(runId));

        var results = await _client.GetAsync(
            $"/api/v1/evals/runs/{runId}/results");
        Assert.That(results.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await results.Content.ReadAsStringAsync(),
            Does.Contain("\"pass\":true"));

        var missing = await _client.GetAsync("/api/v1/evals/runs/nope");
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var missingRes = await _client.GetAsync(
            "/api/v1/evals/runs/nope/results");
        Assert.That(missingRes.StatusCode,
            Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task CreateRun_EmptyDataset_400()
    {
        var dsId = await SeedDatasetAsync(0);
        UseToken(_user.Token);
        var res = await _client.PostAsJsonAsync("/api/v1/evals/runs",
            new { datasetId = dsId, modelId = "m" });
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }
}
