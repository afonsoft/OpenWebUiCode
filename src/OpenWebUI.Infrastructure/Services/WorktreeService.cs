using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Resultado de um merge worktree → workdir principal.</summary>
/// <param name="Merged">True quando todos os arquivos aplicaram e o worktree foi removido.</param>
/// <param name="Applied">Paths relativos aplicados no workdir principal.</param>
/// <param name="Conflicts">Arquivos que falharam no <c>git apply --check</c> (nunca forçados).</param>
/// <param name="Error">Erro inesperado (quando há, Merged=false).</param>
public sealed record WorktreeMergeResult(
    bool Merged,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Conflicts,
    string? Error = null);

/// <summary>
/// Worktrees git por run (SPEC-20261009-worktree-format-hooks, E16 S9):
/// quando <c>Workspace:RunIsolation=worktree</c>, cada run com tools ganha
/// um checkout isolado em <c>data/worktrees/{userId}/{runId}</c> —
/// <c>git worktree add --detach</c> no HEAD do workdir principal
/// (detached: a branch aberta no checkout principal não pode ser
/// re-check-out; o diff é sempre comparado com HEAD).
/// O worktree sobrevive ao fim da run: a aba Changes lê dele e o botão
/// "Merge into workspace" aplica o diff. A remoção acontece no merge
/// bem-sucedido ou no prune de órfãos no boot
/// (<see cref="PruneOrphansAsync"/>, TTL <c>Workspace:WorktreeTtlHours</c>,
/// default 168h). Sem git ou sem repo no workdir → fallback
/// <c>shared</c> com aviso (o executor emite evento status).
/// </summary>
public sealed class WorktreeService(
    IHostEnvironment env,
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    ILogger<WorktreeService> logger)
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(1);
    private const int MaxErrorChars = 240;

    /// <summary>True quando o isolamento por run está ligado (default: <c>shared</c>).</summary>
    public bool IsolationEnabled => string.Equals(
        configuration["Workspace:RunIsolation"], "worktree", StringComparison.OrdinalIgnoreCase);

    /// <summary>Horas até um worktree órfão/encerrado ser removido (default 7 dias).</summary>
    private int OrphanTtlHours => configuration.GetValue("Workspace:WorktreeTtlHours", 168);

    /// <summary>Raiz dos worktrees do usuário.</summary>
    public string WorktreesRoot(string userId) =>
        Path.Join(DataPaths.Root(env.ContentRootPath), "worktrees", Sanitize(userId));

    /// <summary>Caminho determinístico do worktree da run.</summary>
    public string PathFor(string userId, string runId) =>
        Path.Join(WorktreesRoot(userId), Sanitize(runId));

    /// <summary>Worktree existente da run (null quando não há).</summary>
    public string? ResolveIsolated(string userId, string runId)
    {
        var dir = PathFor(userId, runId);
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>
    /// Cria o worktree da run em <paramref name="mainWorkdir"/> quando o
    /// isolamento está ligado. Devolve o caminho do worktree ou null com
    /// <c>Warning</c> descrevendo o fallback <c>shared</c>.
    /// </summary>
    public async Task<(string? Worktree, string? Warning)> TryCreateForRunAsync(
        string userId, string runId, string mainWorkdir, CancellationToken ct,
        bool force = false)
    {
        if (!IsolationEnabled && !force)
        {
            return (null, null);
        }

        if (await RunGitAsync(mainWorkdir, ct, "rev-parse", "--git-dir") is null)
        {
            return (null, "worktree off — workdir sem repo git; run usa o workdir compartilhado");
        }

        var dir = PathFor(userId, runId);
        if (Directory.Exists(dir))
        {
            // Sobra de uma tentativa anterior com o mesmo id — remove antes.
            await RemoveAsync(mainWorkdir, dir, ct);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        var error = await RunGitLoggedAsync(
            mainWorkdir, ct, "worktree", "add", "--detach", dir, "HEAD");
        if (error is not null)
        {
            logger.LogWarning(
                "git worktree add falhou ({Error}) — run {RunId} segue em shared", error, runId);
            return (null, $"worktree off ({error}) — run usa o workdir compartilhado");
        }

        return (dir, null);
    }

    /// <summary>
    /// Remove o worktree: <c>git worktree remove --force</c> + <c>prune</c>;
    /// se o repo principal não responder, deleta o diretório direto.
    /// </summary>
    public async Task RemoveAsync(string mainWorkdir, string worktreePath, CancellationToken ct)
    {
        var error = await RunGitLoggedAsync(
            mainWorkdir, ct, "worktree", "remove", "--force", worktreePath);
        if (error is not null && Directory.Exists(worktreePath))
        {
            TryDelete(worktreePath);
        }

        _ = await RunGitAsync(mainWorkdir, ct, "worktree", "prune");
    }

    /// <summary>
    /// Aplica o diff do worktree no workdir principal (RF-002). Cada
    /// arquivo é validado com <c>git apply --check</c> implícito: os que
    /// falham viram conflitos listados e nunca são forçados. Merge
    /// completo remove o worktree; com conflito ele permanece para nova
    /// tentativa depois de resolvido à mão.
    /// </summary>
    public async Task<WorktreeMergeResult> MergeAsync(
        string mainWorkdir, string worktreePath, CancellationToken ct)
    {
        if (!Directory.Exists(worktreePath))
        {
            return new WorktreeMergeResult(false, [], [], "Worktree não existe.");
        }

        // Intent-to-add faz `git diff HEAD` cobrir também arquivos untracked;
        // o reset limpa o índice depois.
        _ = await RunGitAsync(worktreePath, ct, "add", "--intent-to-add", "--all");
        var patch = await RunGitAsync(
            worktreePath, ct, "diff", "HEAD", "--binary", "--no-renames");
        _ = await RunGitAsync(worktreePath, ct, "reset");

        if (string.IsNullOrWhiteSpace(patch))
        {
            await RemoveAsync(mainWorkdir, worktreePath, ct);
            return new WorktreeMergeResult(true, [], []);
        }

        var applied = new List<string>();
        var conflicts = new List<string>();
        foreach (var (file, filePatch) in SplitPerFile(patch))
        {
            // Sem --check separado: git apply é atômico por patch, então um
            // arquivo que falha sai intacto — exit≠0 já vira conflito.
            var (code, _, err) = await RunGitInputAsync(
                mainWorkdir, filePatch, ct, "apply", "--whitespace=nowarn");
            if (code == 0)
            {
                applied.Add(file);
            }
            else
            {
                conflicts.Add($"{file}: {FirstLine(err)}");
            }
        }

        if (conflicts.Count > 0)
        {
            return new WorktreeMergeResult(false, applied, conflicts);
        }

        await RemoveAsync(mainWorkdir, worktreePath, ct);
        return new WorktreeMergeResult(true, applied, conflicts);
    }

    /// <summary>
    /// Limpeza no boot (RF-005): remove worktrees cuja run não existe mais
    /// ou já terminou há mais de <c>Workspace:WorktreeTtlHours</c>.
    /// </summary>
    public async Task PruneOrphansAsync(CancellationToken ct)
    {
        try
        {
            var root = Path.Join(DataPaths.Root(env.ContentRootPath), "worktrees");
            if (!Directory.Exists(root))
            {
                return;
            }

            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)OrphanTtlHours * 3600;
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repos = scope.ServiceProvider.GetRequiredService<WorkspaceRepoService>();

            foreach (var userDir in Directory.EnumerateDirectories(root))
            {
                var userId = Path.GetFileName(userDir);
                string? mainWorkdir = null;
                foreach (var wtDir in Directory.EnumerateDirectories(userDir))
                {
                    var runId = Path.GetFileName(wtDir);
                    var run = await db.ChatRuns.AsNoTracking()
                        .Where(r => r.Id == runId)
                        .Select(r => new { r.CompletedAt })
                        .FirstOrDefaultAsync(ct);

                    // Órfão = run inexistente ou terminada além do TTL.
                    var orphan = run is null
                        || (run.CompletedAt is not null && run.CompletedAt < cutoff);
                    if (!orphan)
                    {
                        continue;
                    }

                    mainWorkdir ??= await repos.ResolveWorkdirAsync(userId, ct);
                    await RemoveAsync(mainWorkdir, wtDir, ct);
                    logger.LogInformation("Worktree órfão removido: {Dir}", wtDir);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Prune de worktrees órfãos falhou.");
        }
    }

    /// <summary>Divide um unified diff multi-arquivo em patches por arquivo.</summary>
    private static IEnumerable<(string File, string Patch)> SplitPerFile(string patch)
    {
        var current = new StringBuilder();
        string? file = null;
        foreach (var rawLine in patch.Split('\n'))
        {
            var line = rawLine;
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) && file is not null)
            {
                yield return (file, current.ToString());
                current.Clear();
                file = null;
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                // "diff --git a/x b/y" → path do lado b/.
                var marker = line.IndexOf(" b/", StringComparison.Ordinal);
                file = marker > 0 ? line[(marker + 3)..].TrimEnd('\r') : line[11..].TrimEnd('\r');
            }

            current.Append(line).Append('\n');
        }

        if (file is not null && current.Length > 0)
        {
            yield return (file, current.ToString());
        }
    }

    private void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Falha ao remover diretório de worktree {Dir}", dir);
        }
    }

    /// <summary>
    /// <c>git status --porcelain</c> do worktree (SPEC-20261010-worktree-review):
    /// não-vazio = há mudanças (tracked ou untracked) a revisar.
    /// </summary>
    public async Task<string?> StatusPorcelainAsync(string worktreePath, CancellationToken ct) =>
        await RunGitAsync(worktreePath, ct, "status", "--porcelain");

    /// <summary>
    /// <c>git diff HEAD</c> do worktree — <paramref name="stat"/>=true devolve
    /// só <c>--stat</c>; senão o patch completo (binário, sem renames). O
    /// intent-to-add faz o diff cobrir também arquivos untracked; o índice é
    /// restaurado com <c>reset</c> no fim.
    /// </summary>
    public async Task<string?> DiffAsync(
        string worktreePath, CancellationToken ct, bool stat = false)
    {
        _ = await RunGitAsync(worktreePath, ct, "add", "--intent-to-add", "--all");
        var output = stat
            ? await RunGitAsync(worktreePath, ct, "diff", "HEAD", "--stat")
            : await RunGitAsync(worktreePath, ct, "diff", "HEAD", "--binary", "--no-renames");
        _ = await RunGitAsync(worktreePath, ct, "reset");
        return output;
    }

    private async Task<string?> RunGitAsync(string workdir, CancellationToken ct, params string[] args)
    {
        var (code, stdout, _) = await RunGitInputAsync(workdir, null, ct, args);
        return code == 0 ? stdout : null;
    }

    /// <summary>git -C workdir args; devolve a 1ª linha do erro ou null em sucesso.</summary>
    private async Task<string?> RunGitLoggedAsync(
        string workdir, CancellationToken ct, params string[] args)
    {
        var (code, _, stderr) = await RunGitInputAsync(workdir, null, ct, args);
        return code == 0 ? null : FirstLine(stderr);
    }

    /// <summary>git -C workdir args com patch opcional no stdin.</summary>
    private static async Task<(int Code, string Stdout, string Stderr)> RunGitInputAsync(
        string workdir, string? stdin, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExecutableResolver.Resolve("git"),
            WorkingDirectory = workdir,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Checkouts/worktrees no volume DATA_ROOT podem ter outro dono —
        // evita "detected dubious ownership" sem mexer em config global.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.directory=*");
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (-1, "", "git não iniciou");
            }

            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(GitTimeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return (-1, "", ex.Message);
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0, "");
        return line.Length > MaxErrorChars ? line[..MaxErrorChars] : line;
    }

    private static string Sanitize(string id)
    {
        var chars = id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return new string(chars.ToArray());
    }
}
