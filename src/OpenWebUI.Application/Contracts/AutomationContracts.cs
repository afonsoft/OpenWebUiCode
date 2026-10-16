namespace OpenWebUI.Application.Contracts;

/// <summary>Automação exibida na lista do usuário.</summary>
/// <param name="ActionKind">prompt | skill | mcp | reminder | webhook (RF-001).</param>
/// <param name="ActionJson">Payload da ação (skill/mcp/reminder/webhook).</param>
/// <param name="PermissionMode">default | auto | bypass — futuro gate de tools.</param>
/// <param name="ChatId">Chat reutilizado nas execuções; nulo cria chat novo.</param>
public sealed record AutomationResponse(
    string Id,
    string Name,
    string Prompt,
    string ModelId,
    string ScheduleKind,
    int IntervalMinutes,
    string? TimeOfDay,
    int? Weekday,
    bool Enabled,
    long? NextRunAt,
    long CreatedAt,
    long UpdatedAt,
    string ActionKind = "prompt",
    string? ActionJson = null,
    string PermissionMode = "default",
    string? ChatId = null);

/// <summary>Criação/atualização de automação.</summary>
/// <param name="ScheduleKind">interval | daily | weekly | once.</param>
/// <param name="IntervalMinutes">Minutos entre execuções (kind=interval, mín. 1).</param>
/// <param name="TimeOfDay">"HH:mm" em UTC (kind=daily/weekly).</param>
/// <param name="Weekday">0-6 domingo..sábado em UTC (kind=weekly).</param>
/// <param name="RunAt">Execução única em epoch seconds UTC (kind=once).</param>
/// <param name="InMinutes">Execução única daqui a N minutos (kind=once, alternativa a RunAt).</param>
/// <param name="ActionKind">prompt | skill | mcp | reminder | webhook; default "prompt".</param>
/// <param name="ActionJson">Payload validado por ActionKind (skill/mcp/reminder/webhook).</param>
/// <param name="PermissionMode">default | auto | bypass.</param>
/// <param name="ChatId">Chat a reutilizar nas execuções.</param>
public sealed record AutomationUpsertRequest(
    string Name,
    string Prompt,
    string ModelId,
    string ScheduleKind,
    int? IntervalMinutes,
    string? TimeOfDay,
    int? Weekday,
    bool? Enabled,
    long? RunAt = null,
    int? InMinutes = null,
    string? ActionKind = null,
    string? ActionJson = null,
    string? PermissionMode = null,
    string? ChatId = null);

/// <summary>Execução registrada de uma automação.</summary>
/// <param name="ActionKind">Tipo de ação executada.</param>
/// <param name="Output">Saída capada (4 KiB) do caller (mcp/webhook/reminder).</param>
public sealed record AutomationRunResponse(
    string Id,
    string AutomationId,
    string AutomationName,
    string Status,
    string? Error,
    string? ChatId,
    long StartedAt,
    long FinishedAt,
    string ActionKind = "prompt",
    string? Output = null);

/// <summary>Webhook de automação vinculado a um chat (RF-021).</summary>
public sealed record AutomationHookResponse(
    string Id,
    string Name,
    string ChatId,
    bool Enabled,
    long CreatedAt,
    long? LastFiredAt,
    string Url);

/// <summary>Criação de webhook de automação; ChatId nulo cria um chat novo.</summary>
public sealed record AutomationHookCreateRequest(string Name, string? ChatId);

/// <summary>Resposta do disparo de webhook (run enfileirada).</summary>
public sealed record AutomationHookFireResponse(string RunId, string ChatId, string Status);

/// <summary>Configuração da integração n8n (admin).</summary>
public sealed record N8nConfigResponse(bool Configured, string? BaseUrl, bool HasApiKey);

/// <summary>Atualização da configuração n8n; campos nulos mantêm o valor atual.</summary>
public sealed record N8nConfigUpdateRequest(string? BaseUrl, string? ApiKey);

/// <summary>Workflow do n8n exposto pela API pública.</summary>
public sealed record N8nWorkflowResponse(string Id, string Name, bool Active);
