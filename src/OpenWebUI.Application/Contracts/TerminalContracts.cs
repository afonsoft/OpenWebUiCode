namespace OpenWebUI.Application.Contracts;

/// <summary>Servidor de terminal/Jupyter configurado pelo admin (persistido em config).</summary>
/// <param name="Id">Identificador gerado.</param>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Url">URL base do servidor (ex.: http://jupyter:8888). Vazia em type local — resolvida no spawn.</param>
/// <param name="AuthType">none | token | password.</param>
/// <param name="Key">Token/senha — nunca serializado para o cliente.</param>
/// <param name="Type">jupyter | pty | local (spawn de Jupyter como processo filho).</param>
public sealed record TerminalServerConfig(
    string Id, string Name, string Url, string AuthType, string Key, string Type);

/// <summary>Criação/atualização de um terminal server (admin).</summary>
public sealed record TerminalServerRequest(
    string Name, string? Url, string AuthType = "token", string? Key = null, string Type = "jupyter");

/// <summary>Terminal server exposto ao cliente (sem a key).</summary>
public sealed record TerminalServerResponse(
    string Id, string Name, string Url, string AuthType, string Type, bool HasKey);

/// <summary>Resumo de uma sessão de terminal PTY do usuário.</summary>
/// <param name="Id">Identificador da sessão.</param>
/// <param name="IsRunning">Se o processo ainda está vivo.</param>
/// <param name="CreatedAtUtc">Criação.</param>
/// <param name="LastActivityUtc">Último input.</param>
/// <param name="ExitCode">Código de saída quando encerrada.</param>
/// <param name="ChatId">Chat dono da sessão, quando bindada
/// (SPEC-20261022-ade-surfaces).</param>
/// <param name="RunId">Run dona da sessão, quando bindada.</param>
/// <param name="Workdir">cwd resolvido do PTY.</param>
public sealed record TerminalSessionInfoResponse(
    string Id, bool IsRunning, DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastActivityUtc, int? ExitCode,
    string? ChatId = null, string? RunId = null, string? Workdir = null);
