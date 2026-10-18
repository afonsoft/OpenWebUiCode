using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Net.Sockets;
using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Sobe e gerencia servidores de terminal <c>type: "local"</c> como processos
/// filhos do host — o "spawn de Jupyter local" do upstream. O comando vem de
/// <c>Jupyter:LocalCommand</c> (env <c>JUPYTER_LOCAL_COMMAND</c>, default
/// <c>jupyter lab --no-browser --port={port} --ServerApp.token={token}
/// --ServerApp.root_dir={workdir}</c>) com placeholders <c>{port}</c>,
/// <c>{token}</c> e <c>{workdir}</c>. Readiness = qualquer resposta HTTP em
/// <c>/api/status</c> até 30s; processo morto é respawnado na próxima
/// requisição e todos são encerrados no shutdown.
/// </summary>
public sealed class LocalTerminalSpawner : IDisposable
{
    /// <summary>Template default: Jupyter Lab sem browser, token e root_dir isolados.</summary>
    public const string DefaultCommand =
        "jupyter lab --no-browser --port={port} --ServerApp.token={token} --ServerApp.root_dir={workdir}";

    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly IServiceProvider? _services;
    private readonly ConcurrentDictionary<string, LocalServer> _servers = new();
    private readonly SemaphoreSlim _spawnLock = new(1, 1);

    private sealed record LocalServer(TerminalServerConfig Resolved, Process Process);

    public LocalTerminalSpawner(
        IConfiguration configuration, IHttpClientFactory httpClientFactory,
        IServiceProvider? services = null, IHostApplicationLifetime? lifetime = null)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _services = services;
        _lifetime = lifetime;
        lifetime?.ApplicationStopping.Register(Dispose);
    }

    /// <summary>Template de comando configurado (Jupyter:LocalCommand).</summary>
    private string CommandTemplate =>
        _configuration["Jupyter:LocalCommand"]
        ?? _configuration["JUPYTER_LOCAL_COMMAND"]
        ?? DefaultCommand;

    /// <summary>Timeout de readiness (Jupyter:SpawnTimeoutSeconds, default 30s).</summary>
    private TimeSpan SpawnTimeout => TimeSpan.FromSeconds(
        int.TryParse(_configuration["Jupyter:SpawnTimeoutSeconds"], out var s) ? s : 30);

    /// <summary>
    /// Garante que o servidor local está rodando e devolve a config resolvida
    /// (URL real + token) para o proxy. Respawna se o processo morreu.
    /// </summary>
    public async Task<TerminalServerConfig> EnsureStartedAsync(
        TerminalServerConfig server, CancellationToken ct = default)
    {
        if (_servers.TryGetValue(server.Id, out var running) && !running.Process.HasExited)
        {
            return running.Resolved;
        }

        await _spawnLock.WaitAsync(ct);
        try
        {
            if (_servers.TryGetValue(server.Id, out running) && !running.Process.HasExited)
            {
                return running.Resolved;
            }

            _servers.TryRemove(server.Id, out _);
            var resolved = await SpawnAsync(server, ct);
            return resolved;
        }
        finally
        {
            _spawnLock.Release();
        }
    }

    /// <summary>Encerra o processo filho de um servidor (se existir).</summary>
    public void Stop(string serverId)
    {
        if (_servers.TryRemove(serverId, out var server))
        {
            KillQuietly(server.Process);
        }
    }

    private async Task<TerminalServerConfig> SpawnAsync(TerminalServerConfig server, CancellationToken ct)
    {
        var port = FreePort();
        var token = string.IsNullOrEmpty(server.Key) ? Guid.NewGuid().ToString("N") : server.Key;
        var workDir = Path.Join(Path.GetTempPath(), "openwebui-terminals", server.Id);
        Directory.CreateDirectory(workDir);

        var command = CommandTemplate
            .Replace("{port}", port.ToString(), StringComparison.Ordinal)
            .Replace("{token}", token, StringComparison.Ordinal)
            .Replace("{workdir}", workDir, StringComparison.Ordinal);

        var process = new Process
        {
            StartInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(ExecutableResolver.Resolve("cmd"), $"/c {command}")
                : new ProcessStartInfo("/bin/sh", $"-c \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\""),
            EnableRaisingEvents = true,
        };
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.WorkingDirectory = workDir;

        // Só a camada GLOBAL de secrets entra aqui (SPEC-20261017 RF-005):
        // o servidor local é compartilhado entre usuários — secret por
        // usuário num processo compartilhado vazaria cross-user.
        if (_services is not null)
        {
            try
            {
                await using var scope = _services.CreateAsyncScope();
                var agentEnv = await scope.ServiceProvider
                    .GetRequiredService<AgentSecretsEnvService>()
                    .BuildGlobalAsync(ct);
                foreach (var (key, value) in agentEnv.Vars)
                {
                    process.StartInfo.Environment[key] = value;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Env de secrets é best-effort — não derruba o spawn do terminal.
            }
        }
        // Descarta a saída para não bloquear o filho quando o buffer encher.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };

        var url = $"http://127.0.0.1:{port}";
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Falha ao spawnar terminal local '{server.Id}': {ex.Message}. " +
                "Instale o Jupyter ou configure Jupyter:LocalCommand.", ex);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var resolved = server with { Url = url, AuthType = "token", Key = token };
        await WaitReadyAsync(url, process, server.Id, ct);
        _servers[server.Id] = new LocalServer(resolved, process);
        return resolved;
    }

    /// <summary>Poll em /api/status até qualquer resposta HTTP (até 404 prova que subiu).</summary>
    // Poll de readiness do processo spawnado — depende de timing/socket real.
    [ExcludeFromCodeCoverage]
    private async Task WaitReadyAsync(string url, Process process, string serverId, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(SpawnTimeout);
        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(2);

        while (!timeoutCts.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Terminal local '{serverId}' morreu no spawn (exit {process.ExitCode}).");
            }
            try
            {
                using var response = await http.GetAsync($"{url}/api/status", timeoutCts.Token);
                return; // qualquer status HTTP = processo ouvindo
            }
            catch (HttpRequestException)
            {
                // Probe falhou — segue para o próximo candidato.
            }
            catch (TaskCanceledException) when (!timeoutCts.IsCancellationRequested)
            {
                // Probe falhou — segue para o próximo candidato.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                KillQuietly(process);
                throw;
            }
            await Task.Delay(250, CancellationToken.None);
        }

        KillQuietly(process);
        throw new InvalidOperationException(
            $"Terminal local '{serverId}' não respondeu em {SpawnTimeout.TotalSeconds}s após o spawn.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Processo/PTY já morto — best-effort no teardown.
        }
        process.Dispose();
    }

    /// <summary>Encerra todos os processos filhos spawnados.</summary>
    public void Dispose()
    {
        foreach (var id in _servers.Keys)
        {
            Stop(id);
        }
        _spawnLock.Dispose();
    }
}
