using System.ComponentModel;
using System.Diagnostics;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Arquivo alterado no workdir git (path + numstat + status).</summary>
public sealed record GitChangedFile(string Path, int Added, int Removed, string Status);

/// <summary>Snapshot do workdir git para a git bar/aba Changes do chat.</summary>
public sealed record GitWorkspaceInfo(
    bool IsRepo,
    string? Branch,
    int Added,
    int Removed,
    IReadOnlyList<GitChangedFile> Files,
    string? Diff,
    bool DiffTruncated);

/// <summary>
/// Inspeção git do workspace do chat (SPEC-20261007-chat-agent-parity
/// RF-018): quando <c>data/workspaces/{userId}/.git</c> existe, expõe
/// branch, numstat agregado e o unified diff de <c>HEAD</c> (staged +
/// unstaged + untracked listados). Invoca o binário <c>git</c> — zero
/// deps novas; sem git ou sem repo retorna <c>IsRepo=false</c>.
/// </summary>
public sealed class WorkspaceGitService
{
    /// <summary>Cap do diff unificado devolvido (linhas grandes demais são cortadas).</summary>
    private const int MaxDiffChars = 96_000;

    /// <summary>Timeout por invocação do git.</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Limite para contar linhas de arquivo untracked (acima disso vira binário).</summary>
    private const long MaxUntrackedCountBytes = 1_000_000;

    /// <summary>
    /// Snapshot do repo no <paramref name="workdir"/>: branch corrente,
    /// numstat de <c>git diff HEAD</c> mais untracked (<c>??</c>) como
    /// adições, e o unified diff truncado em ~96KB.
    /// </summary>
    public async Task<GitWorkspaceInfo> GetInfoAsync(string workdir, CancellationToken ct)
    {
        // `.git` pode ser ARQUIVO (worktree ligado → aponta pro gitdir real).
        if (!Directory.Exists(Path.Join(workdir, ".git"))
            && !File.Exists(Path.Join(workdir, ".git")))
        {
            return Empty;
        }
        if (!await IsRepoRootAsync(workdir, ct))
        {
            return Empty;
        }

        var branch = await RunGitAsync(workdir, ct, "rev-parse", "--abbrev-ref", "HEAD");
        if (branch is null)
        {
            // Sem git no PATH ou repo corrompido → trata como "não-repo".
            return Empty;
        }

        var (files, added, removed) = await CollectChangedFilesAsync(workdir, ct);

        var diff = await RunGitAsync(workdir, ct, "diff", "HEAD", "--no-renames");
        var truncated = false;
        if (diff is not null && diff.Length > MaxDiffChars)
        {
            diff = diff[..MaxDiffChars] + "\n… [diff truncado]";
            truncated = true;
        }

        return new GitWorkspaceInfo(
            true, branch.Trim(), added, removed, files, diff, truncated);
    }

    /// <summary>
    /// Arquivos alterados vs <c>HEAD</c> (numstat) mais untracked (<c>??</c> do
    /// porcelain, que não aparece no diff) com linhas contadas como adições.
    /// </summary>
    private static async Task<(List<GitChangedFile> Files, int Added, int Removed)>
        CollectChangedFilesAsync(string workdir, CancellationToken ct)
    {
        var files = new List<GitChangedFile>();
        var numstat = await RunGitAsync(
            workdir, ct, "diff", "HEAD", "--numstat", "--no-renames") ?? string.Empty;
        var added = 0;
        var removed = 0;
        foreach (var parts in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(line => line.Split('\t'))
                     .Where(p => p.Length >= 3))
        {

            var a = int.TryParse(parts[0], out var av) ? av : 0; // "-" = binário
            var r = int.TryParse(parts[1], out var rv) ? rv : 0;
            added += a;
            removed += r;
            files.Add(new GitChangedFile(parts[2].Trim(), a, r, "M"));
        }

        // Untracked (?? no porcelain) não aparece no diff HEAD — conta
        // linhas do arquivo quando pequeno, senão só lista como novo.
        var status = await RunGitAsync(
            workdir, ct, "status", "--porcelain=v1", "--untracked-files=all") ?? string.Empty;
        foreach (var line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = ParseUntrackedPath(line);
            if (path is null)
            {
                continue;
            }

            var a = CountLines(Path.Join(workdir, path));
            added += a;
            files.Add(new GitChangedFile(path, a, 0, "A"));
        }

        return (files, added, removed);
    }

    /// <summary>Path de uma linha <c>?? </c> do porcelain (desfaz core.quotePath); null fora do formato.</summary>
    private static string? ParseUntrackedPath(string line)
    {
        if (!line.StartsWith("?? ", StringComparison.Ordinal))
        {
            return null;
        }

        var path = line[3..].Trim();
        // git cita paths com caracteres especiais (core.quotePath).
        if (path.Length > 1 && path.StartsWith('"') && path.EndsWith('"'))
        {
            path = path[1..^1].Replace("\\\"", "\"");
        }
        return path.Length == 0 ? null : path;
    }

    private static GitWorkspaceInfo Empty => new(false, null, 0, 0, [], null, false);

    private static int CountLines(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || info.Length > MaxUntrackedCountBytes)
            {
                return 0;
            }

            var count = 0;
            using var reader = new StreamReader(path);
            while (reader.ReadLine() is not null)
            {
                count++;
                if (count > 50_000)
                {
                    break;
                }
            }
            return count;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Roda <c>git -C workdir args…</c> e devolve stdout; null quando o
    /// processo falha, excede o timeout ou o binário não existe.
    /// </summary>
    private static async Task<string?> RunGitAsync(
        string workdir, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            await process.WaitForExitAsync(ct).WaitAsync(GitTimeout, ct);
            return process.ExitCode == 0
                ? await process.StandardOutput.ReadToEndAsync(ct)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw; // cancelamento do request propaga, não vira "sem git"
        }
        catch (Win32Exception) { return null; }
        catch (ObjectDisposedException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (IOException) { return null; }
        catch (TimeoutException) { return null; }
    }

    /// <summary>
    /// Commit do painel SCM (SPEC-20261011-ide-search-scm): <c>git add -A</c>
    /// + <c>git commit</c> com identidade <c>-c</c> (não depender de config do
    /// repo). Retorna o hash novo; null no erro/sem git.
    /// </summary>
    public async Task<string?> CommitAsync(string workdir, string message, CancellationToken ct)
    {
        if (!await IsRepoRootAsync(workdir, ct))
        {
            return null; // workdir não é raiz de repo — não commitar no pai
        }
        var add = await RunGitAsync(workdir, ct, "add", "-A");
        if (add is null)
        {
            return null;
        }
        var commit = await RunGitAsync(workdir, ct,
            "-c", "user.name=OpenWebUI IDE",
            "-c", "user.email=ide@openwebui.local",
            "commit", "--no-verify", "-m", message);
        if (commit is null)
        {
            return null; // nada a commitar ou falha do git
        }
        var head = await RunGitAsync(workdir, ct, "rev-parse", "HEAD");
        return head?.Trim();
    }

    /// <summary>True só quando <paramref name="workdir"/> é a raiz do repo git —
    /// impede que <c>git</c> escale pra um repo pai (workdir sob o clone do app)
    /// e leia/escreva no repositório errado (SPEC-20261011-ide-search-scm).</summary>
    private static async Task<bool> IsRepoRootAsync(string workdir, CancellationToken ct)
    {
        var top = await RunGitAsync(workdir, ct, "rev-parse", "--show-toplevel");
        if (top is null)
        {
            return false;
        }
        return string.Equals(
            Path.GetFullPath(top.Trim().Replace('/', Path.DirectorySeparatorChar))
                .TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(workdir).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.Ordinal);
    }
}
