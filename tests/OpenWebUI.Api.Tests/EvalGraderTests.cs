using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes dos graders de avaliação (SPEC-20261023-agent-evals RF-003/004):
/// exact/contains/regex determinísticos, rubric com juiz injetado e
/// trajectory sobre eventos persistidos da run.
/// </summary>
[TestFixture, IsolateEnvironment]
public class EvalGraderTests
{
    private string _dbPath = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(),
            $"openwebui-evals-{Guid.NewGuid():N}.db");
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private AppDbContext CreateContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);

    private EvalGraderService Grader(AppDbContext db, string? judgeResponse = null,
        string? judgeError = null) => new(db,
        (_, _, _) => judgeError is not null
            ? Task.FromException<string>(new InvalidOperationException(judgeError))
            : Task.FromResult(judgeResponse ?? """{"score":0.9,"rationale":"ok"}"""),
        judgeModel: "judge-model");

    private static EvalCase Case(string grader, string? expected = null,
        string? rubric = null, string? expect = null,
        string? forbid = null, int? maxSteps = null) => new()
    {
        DatasetId = "ds1",
        Prompt = "p",
        Expected = expected,
        Grader = grader,
        Rubric = rubric,
        ExpectToolsJson = expect,
        ForbidToolsJson = forbid,
        MaxSteps = maxSteps,
    };

    private ChatRun Run(string status = ChatRunStatus.Completed) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Status = status,
    };

    private async Task<ChatRun> SeedRunAsync(AppDbContext db,
        string status = ChatRunStatus.Completed)
    {
        db.Users.Add(new User
        {
            Id = "u1", Name = "u", Email = "u@e.local",
            PasswordHash = "x", Role = "user",
        });
        db.Chats.Add(new Chat
        {
            Id = "chat-1", UserId = "u1", CreatedAt = 1, UpdatedAt = 1,
        });
        var run = Run(status);
        run.ChatId = "chat-1";
        run.UserId = "u1";
        db.ChatRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    [Test]
    public async Task Exact_ToleratesCaseAndTrailingPunctuation()
    {
        await using var db = CreateContext();
        var grade = await Grader(db).GradeAsync(
            Case("exact", expected: "42"), Run(), "42.",
            "u1", CancellationToken.None);
        Assert.That(grade.Pass, Is.True);
    }

    [Test]
    public async Task Exact_MismatchFails()
    {
        await using var db = CreateContext();
        var grade = await Grader(db).GradeAsync(
            Case("exact", expected: "paris"), Run(), "london",
            "u1", CancellationToken.None);
        Assert.That(grade.Pass, Is.False);
    }

    [Test]
    public async Task Contains_IsCaseInsensitive()
    {
        await using var db = CreateContext();
        var grade = await Grader(db).GradeAsync(
            Case("contains", expected: "Paris"), Run(), "the capital is PARIS, France",
            "u1", CancellationToken.None);
        Assert.That(grade.Pass, Is.True);
    }

    [Test]
    public async Task Regex_MatchesAndFails()
    {
        await using var db = CreateContext();
        var ok = await Grader(db).GradeAsync(
            Case("regex", expected: @"\b\d{4}\b"), Run(), "year 2026 was fine",
            "u1", CancellationToken.None);
        var bad = await Grader(db).GradeAsync(
            Case("regex", expected: @"\b\d{4}\b"), Run(), "no digits word",
            "u1", CancellationToken.None);
        Assert.That(ok.Pass, Is.True);
        Assert.That(bad.Pass, Is.False);
    }

    [Test]
    public async Task Regex_InvalidPattern_ReturnsError()
    {
        await using var db = CreateContext();
        var grade = await Grader(db).GradeAsync(
            Case("regex", expected: "([unclosed"), Run(), "x",
            "u1", CancellationToken.None);
        Assert.That(grade.Pass, Is.False);
        Assert.That(grade.Score, Is.Null);
    }

    [Test]
    public async Task Rubric_ParsesJudgeJson()
    {
        await using var db = CreateContext();
        var grade = await Grader(db, """{"score":0.8,"rationale":"good"}""")
            .GradeAsync(Case("rubric", rubric: "be accurate"), Run(),
                "answer", "u1", CancellationToken.None);
        Assert.That(grade.Pass, Is.True);
        Assert.That(grade.Score, Is.EqualTo(0.8));
    }

    [Test]
    public async Task Rubric_LowScoreFails()
    {
        await using var db = CreateContext();
        var grade = await Grader(db, """{"score":0.2,"rationale":"bad"}""")
            .GradeAsync(Case("rubric", rubric: "be accurate"), Run(),
                "answer", "u1", CancellationToken.None);
        Assert.That(grade.Pass, Is.False);
    }

    [Test]
    public async Task Rubric_InvalidJudgeOutput_IsGracefulFail()
    {
        await using var db = CreateContext();
        var grade = await Grader(db, "not json at all")
            .GradeAsync(Case("rubric", rubric: "r"), Run(),
                "answer", "u1", CancellationToken.None);
        Assert.That(grade.Pass, Is.False);
        Assert.That(grade.Score, Is.Null);
    }

    [Test]
    public async Task Rubric_JudgeThrows_IsGracefulFail()
    {
        await using var db = CreateContext();
        var grade = await Grader(db, judgeError: "provider down")
            .GradeAsync(Case("rubric", rubric: "r"), Run(),
                "answer", "u1", CancellationToken.None);
        Assert.That(grade.Pass, Is.False);
        Assert.That(grade.Score, Is.Null);
    }

    [Test]
    public async Task Trajectory_ExpectToolsAndMaxSteps()
    {
        await using var db = CreateContext();
        var run = await SeedRunAsync(db);
        db.ChatRunEvents.AddRange(
            Event(run.Id, 1, RunEventWriter.Types.StepFinished, "{}"),
            Event(run.Id, 2, RunEventWriter.Types.ToolCall,
                """{"id":"c1","name":"fs_write"}"""),
            Event(run.Id, 3, RunEventWriter.Types.StepFinished, "{}"));
        await db.SaveChangesAsync();

        var grade = await Grader(db).GradeAsync(
            Case("trajectory", expect: """["fs_write"]""", maxSteps: 5),
            run, null, "u1", CancellationToken.None);
        Assert.That(grade.Pass, Is.True);
    }

    [Test]
    public async Task Trajectory_ForbidToolFails()
    {
        await using var db = CreateContext();
        var run = await SeedRunAsync(db);
        db.ChatRunEvents.Add(Event(run.Id, 1, RunEventWriter.Types.ToolCall,
            """{"id":"c1","name":"shell_exec"}"""));
        await db.SaveChangesAsync();

        var grade = await Grader(db).GradeAsync(
            Case("trajectory", forbid: """["shell_exec"]"""),
            run, null, "u1", CancellationToken.None);
        Assert.That(grade.Pass, Is.False);
    }

    [Test]
    public async Task Trajectory_FailedRunFails()
    {
        await using var db = CreateContext();
        var run = await SeedRunAsync(db, ChatRunStatus.Failed);

        var grade = await Grader(db).GradeAsync(
            Case("trajectory"), run, null, "u1", CancellationToken.None);
        Assert.That(grade.Pass, Is.False);
    }

    [Test]
    public async Task Trajectory_ExceedsMaxStepsFails()
    {
        await using var db = CreateContext();
        var run = await SeedRunAsync(db);
        for (var i = 1; i <= 6; i++)
        {
            db.ChatRunEvents.Add(
                Event(run.Id, i, RunEventWriter.Types.StepFinished, "{}"));
        }
        await db.SaveChangesAsync();

        var grade = await Grader(db).GradeAsync(
            Case("trajectory", maxSteps: 5),
            run, null, "u1", CancellationToken.None);
        Assert.That(grade.Pass, Is.False);
    }

    private static ChatRunEvent Event(string runId, long seq, string type,
        string payload) => new()
    {
        RunId = runId,
        Seq = seq,
        Type = type,
        PayloadJson = payload,
        CreatedAt = 1,
    };
}
