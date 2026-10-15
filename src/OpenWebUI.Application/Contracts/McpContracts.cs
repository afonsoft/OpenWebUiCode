namespace OpenWebUI.Application.Contracts;

/// <summary>Servidor MCP em respostas de API (headers mascarados).</summary>
public sealed record McpServerResponse(
    string Id,
    string Name,
    string Transport,
    string? Command,
    List<string> Args,
    List<string> EnvNames,
    string? Url,
    Dictionary<string, string> Headers,
    bool Enabled,
    string? LastError,
    long CreatedAt);

/// <summary>Criação/atualização de servidor MCP.</summary>
/// <param name="Transport"><c>stdio</c> (command+args+env) ou <c>http</c> (url+headers).</param>
/// <param name="EnvNames">Nomes de env vars passadas ao processo stdio — só nomes, valores nunca são salvos.</param>
/// <param name="Headers">Headers HTTP para transport http; valor "********" preserva o gravado.</param>
public sealed record McpServerUpsertRequest(
    string Name,
    string Transport,
    string? Command = null,
    List<string>? Args = null,
    List<string>? EnvNames = null,
    string? Url = null,
    Dictionary<string, string>? Headers = null,
    bool Enabled = true);

/// <summary>
/// Entrada do catálogo efetivo de MCP do usuário
/// (SPEC-20261014-config-layers RF-002/RF-006): <c>Source</c> =
/// <c>global</c> (admin) ou <c>user</c> (próprio); <c>EnabledForMe</c>
/// desce quando o usuário desligou um servidor global só para si.
/// </summary>
public sealed record UserMcpServerEntry(
    McpServerResponse Server,
    string Source,
    bool EnabledForMe);

/// <summary>Liga/desliga um servidor MCP no escopo do usuário.</summary>
public sealed record McpServerEnabledRequest(bool Enabled);

/// <summary>Tool virtual descoberta num servidor MCP (materializada como Tool com UserId "mcp").</summary>
public sealed record McpToolResponse(
    string Id,
    string Name,
    string FunctionName,
    string? Description,
    bool Enabled);
