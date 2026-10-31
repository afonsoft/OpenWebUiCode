using System.Collections.Concurrent;
using System.Text;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Serviço de gerenciamento de pipelines Batuta (Orquestração Arquiteto → Orquestrador → Workers).
/// Lê e escreve em .batuta/{pipelineId}/pipeline.md.
/// # Covers RF-002
/// </summary>
public sealed class BatutaPipelineService
{
    private readonly ConcurrentDictionary<string, BatutaPipelineDto> _cache = new();

    public BatutaPipelineDto CreatePipeline(string workspacePath, string pipelineId, string title, IEnumerable<string> taskTitles)
    {
        var batutaDir = Path.Join(workspacePath, ".batuta", pipelineId);
        Directory.CreateDirectory(batutaDir);

        var tasks = taskTitles.Select((t, i) => new BatutaTaskItem($"task-{i + 1}", t, "pending", null, null)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"# Pipeline Batuta: {title}");
        sb.AppendLine($"Id: {pipelineId}");
        sb.AppendLine($"Status: Draft");
        sb.AppendLine();
        sb.AppendLine("## Tasks");
        foreach (var task in tasks)
        {
            sb.AppendLine($"- [{ (task.Status == "completed" ? "x" : " ") }] {task.Id}: {task.Title} (Status: {task.Status})");
        }

        var mdPath = Path.Join(batutaDir, "pipeline.md");
        File.WriteAllText(mdPath, sb.ToString());

        var dto = new BatutaPipelineDto(pipelineId, title, "Draft", tasks, sb.ToString(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _cache[pipelineId] = dto;
        return dto;
    }

    public BatutaPipelineDto? GetPipeline(string workspacePath, string pipelineId)
    {
        if (_cache.TryGetValue(pipelineId, out var cached))
            return cached;

        var mdPath = Path.Join(workspacePath, ".batuta", pipelineId, "pipeline.md");
        if (!File.Exists(mdPath))
            return null;

        var content = File.ReadAllText(mdPath);
        var dto = new BatutaPipelineDto(pipelineId, pipelineId, "Active", [], content, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _cache[pipelineId] = dto;
        return dto;
    }

    public BatutaPipelineDto UpdateTaskStatus(string workspacePath, string pipelineId, string taskId, string status, string? worker = null, string? worktree = null)
    {
        var pipeline = GetPipeline(workspacePath, pipelineId) ?? throw new InvalidOperationException($"Pipeline {pipelineId} not found.");
        var updatedTasks = pipeline.Tasks.Select(t => t.Id == taskId ? t with { Status = status, AssignedWorker = worker ?? t.AssignedWorker, WorktreePath = worktree ?? t.WorktreePath } : t).ToList();

        var dto = pipeline with { Tasks = updatedTasks, UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        _cache[pipelineId] = dto;

        var batutaDir = Path.Join(workspacePath, ".batuta", pipelineId);
        Directory.CreateDirectory(batutaDir);
        var sb = new StringBuilder();
        sb.AppendLine($"# Pipeline Batuta: {dto.Title}");
        sb.AppendLine($"Id: {pipelineId}");
        sb.AppendLine($"Status: {dto.Status}");
        sb.AppendLine();
        sb.AppendLine("## Tasks");
        foreach (var task in dto.Tasks)
        {
            var check = task.Status == "completed" ? "x" : " ";
            sb.AppendLine($"- [{check}] {task.Id}: {task.Title} (Status: {task.Status}, Worker: {task.AssignedWorker ?? "none"}, Worktree: {task.WorktreePath ?? "none"})");
        }

        File.WriteAllText(Path.Join(batutaDir, "pipeline.md"), sb.ToString());
        return dto;
    }
}
