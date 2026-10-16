namespace OpenWebUI.Application.Contracts;

/// <summary>Resultado de um ciclo de manutenção (RF-004/RF-005).</summary>
public sealed record MaintenanceCycleResponse(
    long StartedAt,
    long FinishedAt,
    int ChatsPurged,
    int MemoriesSynthesized,
    int OpsPurged,
    bool Vacuumed,
    int ChatDays,
    bool DryRun);

/// <summary>Status da manutenção de sessões: último ciclo + config + próxima execução.</summary>
public sealed record MaintenanceStatusResponse(
    MaintenanceCycleResponse? LastRun,
    int ChatDays,
    long? NextRunAt,
    long DbSizeBytes);

/// <summary>Disparo de ciclo sob demanda (admin).</summary>
public sealed record MaintenanceRunRequest(bool DryRun = false);
