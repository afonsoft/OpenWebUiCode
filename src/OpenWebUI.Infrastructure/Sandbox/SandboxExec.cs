using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.Sandbox;

/// <summary>
/// Ponto de entrada das tools para execução possivelmente confinada
/// (SPEC-20261031-sandboxed-exec): se <c>Sandbox:Enabled</c> e a tool não
/// estiver em <see cref="SandboxScope.ExcludedTools"/>, roda via
/// <see cref="SandboxedProcessRunner"/>; caso contrário delega ao
/// <see cref="ChatProcessRunner"/> direto.
/// </summary>
public static class SandboxExec
{
    /// <summary>Sandbox habilitada por configuração?</summary>
    public static Task<bool> EnabledAsync(
        ConfigService config, CancellationToken ct) =>
        config.GetAsync("Sandbox:Enabled", false, ct);

    /// <summary>
    /// Roda o comando — sandbox quando ativo e não-excluído; sempre retorna
    /// o outcome e, quando em sandbox, domínios negados pelo proxy.
    /// </summary>
    public static async Task<SandboxedOutcome> RunAsync(
        string toolName,
        string command,
        BuiltinToolContext context,
        ConfigService config,
        TimeSpan timeout,
        ChatProcessRunner.RunOptions? options = null,
        CancellationToken ct = default)
    {
        if (!await EnabledAsync(config, ct))
        {
            var plain = await ChatProcessRunner.RunAsync(
                command, context.WorkspacePath, timeout, options, ct);
            return new SandboxedOutcome(plain, false, []);
        }

        var sessionKey = context.RunId ?? context.ChatId ?? context.UserId;
        var scope = await SandboxScope.LoadAsync(config, sessionKey, ct);
        if (scope.IsExcluded(toolName))
        {
            var plain = await ChatProcessRunner.RunAsync(
                command, context.WorkspacePath, timeout, options, ct);
            return new SandboxedOutcome(plain, false,
                [$"tool '{toolName}' excluída do sandbox (sandbox.excluded)"]);
        }

        return await SandboxedProcessRunner.RunAsync(
            command, context.WorkspacePath, sessionKey, scope,
            timeout, options, ct);
    }
}
