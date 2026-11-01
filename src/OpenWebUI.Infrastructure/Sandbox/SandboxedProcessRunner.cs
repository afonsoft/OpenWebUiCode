using System.Diagnostics;
using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Infrastructure.Sandbox;

/// <summary>Capacidades detectadas do ambiente de sandbox (RF-006).</summary>
/// <param name="Bwrap">bubblewrap presente.</param>
/// <param name="Seccomp">seccomp suportado pelo bwrap detectado.</param>
/// <param name="Proxy">proxy de rede interno disponível.</param>
/// <param name="Mode"><c>available</c> ou <c>degraded</c>.</param>
public sealed record SandboxCapabilities(
    bool Bwrap, bool Seccomp, bool Proxy, string Mode)
{
    /// <summary>Sonda o ambiente uma vez por processo.</summary>
    public static SandboxCapabilities Probe()
    {
        var bwrap = BwrapBuilder.IsAvailable;
        return new SandboxCapabilities(
            Bwrap: bwrap,
            Seccomp: bwrap, // bwrap fornece o filtro básico
            Proxy: true,    // proxy interno sempre sobe em processo
            Mode: bwrap ? "available" : "degraded");
    }
}

/// <summary>
/// Runner que envelopa comandos no bubblewrap quando
/// <c>Sandbox:Enabled</c> (SPEC-20261031-sandboxed-exec): monta o psi via
/// <see cref="BwrapBuilder"/>, sobe o <see cref="SandboxProxyServer"/> para
/// rede allowlisted e delega ao <see cref="ChatProcessRunner"/>. Sem bwrap
/// → fallback sem sandbox com aviso (nunca aborta a execução).
/// </summary>
public sealed record SandboxedOutcome(
    ProcessOutcome Outcome,
    bool UsedSandbox,
    IReadOnlyList<string> DeniedDomains)
{
    /// <summary>Texto de aviso agregável ao resultado da tool.</summary>
    public string FooterText()
    {
        var parts = new List<string>();
        if (!UsedSandbox)
        {
            parts.Add("[sandbox] bwrap indisponível — execução sem confinamento.");
        }
        if (DeniedDomains.Count > 0)
        {
            parts.Add("[sandbox] domínios bloqueados: "
                + string.Join(", ", DeniedDomains)
                + " (evento sandbox.network.denied).");
        }
        return string.Join("\n", parts);
    }
}

public static class SandboxedProcessRunner
{
    private static readonly Lazy<SandboxCapabilities> Caps =
        new(SandboxCapabilities.Probe);

    /// <summary>Capacidades cacheadas do ambiente.</summary>
    public static SandboxCapabilities Capabilities => Caps.Value;

    /// <summary>
    /// Executa <paramref name="command"/> confinado quando bwrap presente;
    /// senão roda direto (modo degradado). <paramref name="sessionKey"/>
    /// identifica a sessão para concessões de domínio via request_scope.
    /// </summary>
    public static async Task<SandboxedOutcome> RunAsync(
        string command,
        string workspacePath,
        string sessionKey,
        SandboxScope scope,
        TimeSpan timeout,
        ChatProcessRunner.RunOptions? options = null,
        CancellationToken ct = default)
    {
        var bwrap = BwrapBuilder.FindBwrap();
        if (bwrap is null)
        {
            var outcome = await ChatProcessRunner.RunAsync(
                command, workspacePath, timeout, options, ct);
            return new SandboxedOutcome(outcome, false, []);
        }

        SandboxProxyServer? proxy = null;
        try
        {
            int? proxyPort = null;
            if (scope.NetworkEnabled)
            {
                proxy = SandboxProxyServer.Start(scope.AllowedDomains);
                proxyPort = proxy.Port;
            }

            var psi = BwrapBuilder.Build(
                bwrap, command, workspacePath, scope, proxyPort,
                options?.Env);
            var outcome = await ChatProcessRunner.RunAsync(
                psi, timeout, options, ct);
            // Falha de spawn do bwrap (exit 1 imediato + erro de args) →
            // fallback sem sandbox com aviso.
            if (outcome is { ExitCode: -1 })
            {
                var fallback = await ChatProcessRunner.RunAsync(
                    command, workspacePath, timeout, options, ct);
                return new SandboxedOutcome(fallback, false, []);
            }
            return new SandboxedOutcome(
                outcome, true, proxy?.DeniedHosts.Keys.ToArray() ?? []);
        }
        finally
        {
            if (proxy is not null)
            {
                await proxy.DisposeAsync();
            }
        }
    }
}
