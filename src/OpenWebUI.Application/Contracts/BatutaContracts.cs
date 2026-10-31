namespace OpenWebUI.Application.Contracts;

/// <summary>
/// Contratos DTO para o Pipeline Batuta (Orquestração multi-agente / Arquiteto → Orquestrador → Workers).
/// # Covers RF-002
/// </summary>
public sealed record BatutaTaskItem(
    string Id,
    string Title,
    string Status, // pending, in_progress, completed, failed
    string? AssignedWorker,
    string? WorktreePath
);

public sealed record BatutaPipelineDto(
    string PipelineId,
    string Title,
    string Status,
    IReadOnlyList<BatutaTaskItem> Tasks,
    string MarkdownContent,
    long UpdatedAt
);

public sealed record BatutaDispatchRequest(
    string TaskId,
    string WorkerName
);
