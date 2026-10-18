using System.ComponentModel;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Gerencia os <see cref="ChatJob"/>s — comandos de background spawnados
/// pelo chat (SPEC-20261007-chat-agent-tools RF-004). Singleton: spawna o
/// processo desacoplado da run, captura stdout+stderr num arquivo por job
/// em <c>{workspace}/.chat-jobs/{id}.log</c> e vigia o término para marcar
/// status/exit code. Linhas persistidas são mascaradas contra segredos.
/// </summary>
public sealed class ChatJobService(IServiceScopeFactory scopeFactory, ILogger<ChatJobService> logger)
{
    private static readonly TimeSpan MaxRuntime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Registra e dispara um job: valida o comando, cria a linha
    /// <see cref="ChatJob"/> e spawna <c>/bin/sh -c</c> com output indo
    /// para o arquivo de log do job. Retorna a linha criada.
    /// </summary>
    /// <exception cref="InvalidOperationException">Comando negado pelo classifier.</exception>
    public async Task<ChatJob> StartAsync(
        string command, BuiltinToolContext context, CancellationToken ct)
    {
        var assessment = CommandRiskClassifier.Classify(command, context.WorkspacePath);
        if (!assessment.Allowed)
        {
            throw new InvalidOperationException($"Comando negado: {assessment.Reason}");
        }

        Directory.CreateDirectory(context.WorkspacePath);
        var jobsDir = Path.Join(context.WorkspacePath, ".chat-jobs");
        Directory.CreateDirectory(jobsDir);

        // Env resolvida (secrets user∪global ∪ managed GitHub —
        // SPEC-20261017-agent-secrets-env RF-005/006) via scope: o service
        // é singleton, os resolvedores são scoped.
        AgentEnv agentEnv = AgentEnv.Empty;
        await using (var envScope = scopeFactory.CreateAsyncScope())
        {
            var envService = envScope.ServiceProvider
                .GetService<AgentSecretsEnvService>();
            if (envService is not null)
            {
                agentEnv = await envService.BuildAsync(context.UserId, ct);
            }
        }

        var job = new ChatJob
        {
            ChatId = context.ChatId,
            UserId = context.UserId,
            RunId = context.RunId,
            Command = command,
            WorkspacePath = context.WorkspacePath,
            Status = ChatJobStatus.Running,
            StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        job.OutputPath = Path.Join(jobsDir, $"{job.Id}.log");

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = context.WorkspacePath,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        foreach (var (key, value) in agentEnv.Vars)
        {
            psi.Environment[key] = value;
        }

        Process? process = null;
        try
        {
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();
            job.Pid = process.Id;
        }
        catch (Win32Exception ex)
        {
            job.Status = ChatJobStatus.Failed;
            job.Error = $"Falha ao iniciar: {ex.Message}";
            job.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        catch (ObjectDisposedException ex)
        {
            job.Status = ChatJobStatus.Failed;
            job.Error = $"Falha ao iniciar: {ex.Message}";
            job.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        catch (InvalidOperationException ex)
        {
            job.Status = ChatJobStatus.Failed;
            job.Error = $"Falha ao iniciar: {ex.Message}";
            job.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        catch (PlatformNotSupportedException ex)
        {
            job.Status = ChatJobStatus.Failed;
            job.Error = $"Falha ao iniciar: {ex.Message}";
            job.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChatJobs.Add(job);
            await db.SaveChangesAsync(ct);
        }

        if (job.Status == ChatJobStatus.Running && process is not null)
        {
            _ = WatchAsync(job.Id, process, agentEnv.SecretsToScrub);
        }

        return job;
    }

    /// <summary>Lista os jobs do usuário (e do chat, quando informado).</summary>
    public async Task<List<ChatJob>> ListAsync(
        string userId, string? chatId, bool includeFinished, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var query = db.ChatJobs.AsNoTracking().Where(j => j.UserId == userId);
        if (!string.IsNullOrEmpty(chatId))
        {
            query = query.Where(j => j.ChatId == chatId);
        }

        if (!includeFinished)
        {
            query = query.Where(j => j.Status == ChatJobStatus.Running);
        }

        return await query.OrderByDescending(j => j.StartedAt).Take(50).ToListAsync(ct);
    }

    /// <summary>Busca um job do usuário pelo id (null se não for dele).</summary>
    public async Task<ChatJob?> GetAsync(string userId, string jobId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ChatJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct);
    }

    /// <summary>
    /// Lê o log do job (últimas <paramref name="tailChars"/> chars, segredos
    /// já mascarados na gravação). Null se o job não existe/não é do usuário.
    /// </summary>
    public async Task<(ChatJob Job, string Output)?> GetOutputAsync(
        string userId, string jobId, int tailChars, CancellationToken ct)
    {
        var job = await GetAsync(userId, jobId, ct);
        if (job is null)
        {
            return null;
        }

        var output = string.Empty;
        if (!string.IsNullOrEmpty(job.OutputPath) && File.Exists(job.OutputPath))
        {
            var all = await File.ReadAllTextAsync(job.OutputPath, ct);
            output = all.Length > tailChars
                ? "…" + all[^tailChars..]
                : all;
        }

        return (job, output);
    }

    /// <summary>
    /// Mata o processo do job (árvore inteira) e marca <c>killed</c>.
    /// False se o job não existe/não é do usuário/já terminou.
    /// </summary>
    public async Task<bool> KillAsync(string userId, string jobId, CancellationToken ct)
    {
        int? pid;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.ChatJobs
                .FirstOrDefaultAsync(j => j.Id == jobId && j.UserId == userId, ct);
            if (job is null || job.Status != ChatJobStatus.Running)
            {
                return false;
            }

            pid = job.Pid;
            job.Status = ChatJobStatus.Killed;
            job.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await db.SaveChangesAsync(ct);
        }

        if (pid is not null)
        {
            try
            {
                Process.GetProcessById(pid.Value).Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { /* Já morreu. */ }
            catch (InvalidOperationException) { /* Já morreu. */ }
            catch (Win32Exception) { /* Já morreu. */ }
            catch (NotSupportedException) { /* Já morreu. */ }
            catch (AggregateException) { /* Já morreu. */ }
        }

        return true;
    }

    /// <summary>
    /// Varredura no boot: jobs <c>running</c> de um processo que já não existe
    /// (restart do servidor) são marcados <c>killed</c>.
    /// </summary>
    public async Task SweepOrphansAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stale = await db.ChatJobs
            .Where(j => j.Status == ChatJobStatus.Running)
            .ToListAsync(ct);
        foreach (var job in stale)
        {
            var alive = job.Pid is not null && IsAlive(job.Pid.Value);
            if (!alive)
            {
                job.Status = ChatJobStatus.Killed;
                job.Error = "Servidor reiniciado — processo não encontrado.";
                job.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            return !Process.GetProcessById(pid).HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }

    /// <summary>
    /// Vigia o processo: copia stdout+stderr para o arquivo de log (linhas
    /// mascaradas), mata após <see cref="MaxRuntime"/> e grava o status final.
    /// </summary>
    private async Task WatchAsync(
        string jobId, Process process, IReadOnlyList<string> secretsToScrub)
    {
        var outputPath = string.Empty;
        try
        {
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                outputPath = (await db.ChatJobs.FindAsync(jobId))?.OutputPath ?? string.Empty;
            }

            await using var writer = new StreamWriter(outputPath, append: false);
            var pumpOut = PumpAsync(process.StandardOutput, writer, secretsToScrub);
            var pumpErr = PumpAsync(process.StandardError, writer, secretsToScrub);

            using var timeout = new CancellationTokenSource(MaxRuntime);
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { /* Já morreu. */ }
                catch (Win32Exception) { /* Já morreu. */ }
                catch (NotSupportedException) { /* Já morreu. */ }
            }

            await Task.WhenAll(pumpOut, pumpErr);
            var exitCode = process.HasExited ? process.ExitCode : -1;
            process.Dispose();

            await using var scope2 = scopeFactory.CreateAsyncScope();
            var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db2.ChatJobs.FindAsync(jobId);
            if (job is not null && job.Status == ChatJobStatus.Running)
            {
                job.Status = timedOut
                    ? ChatJobStatus.Failed
                    : exitCode == 0 ? ChatJobStatus.Completed : ChatJobStatus.Failed;
                job.ExitCode = timedOut ? -1 : exitCode;
                if (timedOut)
                {
                    job.Error = $"Timeout de {MaxRuntime.TotalMinutes}min — processo morto.";
                }

                job.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await db2.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Watcher do job {JobId} falhou", jobId);
        }
    }

    private static async Task PumpAsync(
        StreamReader reader, StreamWriter writer, IReadOnlyList<string> secretsToScrub)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            await writer.WriteLineAsync(SecretScrubber.Scrub(line, secretsToScrub));
            await writer.FlushAsync();
        }
    }
}
