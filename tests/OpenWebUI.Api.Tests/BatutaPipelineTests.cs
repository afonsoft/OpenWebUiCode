using NUnit.Framework;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes unitários para o gerenciamento de pipelines Batuta.
/// # Covers RF-002
/// </summary>
[TestFixture]
public class BatutaPipelineTests
{
    private string _tempDir = null!;
    private BatutaPipelineService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Join(Path.GetTempPath(), $"batuta-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _service = new BatutaPipelineService();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Test]
    public void CreateAndGetPipeline_ShouldPersistMarkdownAndTasks()
    {
        var pipelineId = "pipe-001";
        var dto = _service.CreatePipeline(_tempDir, pipelineId, "Refactor Core", ["Implement Feature A", "Write Tests"]);

        Assert.That(dto.PipelineId, Is.EqualTo(pipelineId));
        Assert.That(dto.Tasks.Count, Is.EqualTo(2));
        Assert.That(File.Exists(Path.Join(_tempDir, ".batuta", pipelineId, "pipeline.md")), Is.True);

        var retrieved = _service.GetPipeline(_tempDir, pipelineId);
        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved!.Title, Is.EqualTo("Refactor Core"));
    }

    [Test]
    public void UpdateTaskStatus_ShouldUpdateMarkdownAndCache()
    {
        var pipelineId = "pipe-002";
        _service.CreatePipeline(_tempDir, pipelineId, "Feature X", ["Task 1"]);

        var updated = _service.UpdateTaskStatus(_tempDir, pipelineId, "task-1", "completed", worker: "worker-claude", worktree: "/wt/path");
        var task1 = updated.Tasks.First(t => t.Id == "task-1");

        Assert.That(task1.Status, Is.EqualTo("completed"));
        Assert.That(task1.AssignedWorker, Is.EqualTo("worker-claude"));
        Assert.That(task1.WorktreePath, Is.EqualTo("/wt/path"));

        var content = File.ReadAllText(Path.Join(_tempDir, ".batuta", pipelineId, "pipeline.md"));
        Assert.That(content, Does.Contain("[x] task-1: Task 1"));
    }
}
