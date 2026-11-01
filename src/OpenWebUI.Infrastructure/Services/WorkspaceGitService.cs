using System.ComponentModel;
using System.Diagnostics;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Arquivo alterado no workdir git (path + numstat + status).</summary>
/// <param name="OldPath">Path anterior quando <see cref="Status"/> é <c>R</c> (rename).</param>
public sealed record GitChangedFile(
    string Path, int Added, int Removed, string Status, string? OldPath = null);

/// <summary>Snapshot do workdir git para a git bar/aba Changes do chat.</summary>
/// <param name="BaseRef">Ref de origem usada como base do diff (ex.: <c>origin/main</c>); null quando o fallback é <c>HEAD</c>.</param>
/// <param name="BaseSha">Sha (ou <c>HEAD</c>) usado como base efetiva do diff.</param>
public sealed record GitWorkspaceInfo(
    bool IsRepo,
    string? Branch,
    int Added,
    int Removed,
    IReadOnlyList<GitChangedFile> Files,
    string? Diff,
    bool DiffTruncated,
    string? BaseRef = null,
    string? BaseSha = null);

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

    /// <summary>Sha da árvore vazia do git — base de diff quando HEAD ainda não nasceu.</summary>
    private const string EmptyTreeSha = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private const string RevParseCommand = "rev-parse";

    /// <summary>
    /// Snapshot do repo no <paramref name="workdir"/>: branch corrente,
    /// numstat e unified diff (truncado em ~96KB) contra a <b>base da run</b>.
    /// A base é o merge-base com <paramref name="baseRef"/> (tipicamente
    /// <c>origin/{branch do binding}</c>) — cobre commits do agente, staged,
    /// unstaged e untracked (via intent-to-add, restaurado no finally).
    /// Sem remote/base → <c>HEAD</c> (comportamento anterior).
    /// </summary>
    /// <param name="baseRef">Ref preferida como base (ex.: <c>origin/main</c>);
    /// quando ausente tenta <c>origin/{branch corrente}</c>, depois HEAD.</param>
    public static async Task<GitWorkspaceInfo> GetInfoAsync(
        string workdir, CancellationToken ct, string? baseRef = null)
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

        var branch = await RunGitAsync(workdir, ct, RevParseCommand, "--abbrev-ref", "HEAD");
        if (branch is null)
        {
            // Sem git no PATH ou repo corrompido → trata como "não-repo".
            return Empty;
        }

        var hasHead = await RunGitAsync(
            workdir, ct, RevParseCommand, "--verify", "--quiet", "HEAD") is not null;
        var (displayRef, baseSha) = hasHead
            ? await ResolveBaseAsync(workdir, branch.Trim(), baseRef, ct)
            : (null, EmptyTreeSha);

        // Index sujo antes do intent-to-add é restaurado no finally —
        // diff {base} só enxerga untracked depois do add --intent-to-add.
        var stagedBefore = await StagedPathsAsync(workdir, ct);
        try
        {
            _ = await RunGitAsync(workdir, ct, "add", "--intent-to-add", "--all");

            var (files, added, removed) = await CollectChangedFilesAsync(workdir, baseSha, ct);

            var diff = await RunGitAsync(workdir, ct, "diff", baseSha, "-M");
            var truncated = false;
            if (diff is not null && diff.Length > MaxDiffChars)
            {
                diff = diff[..MaxDiffChars] + "\n… [diff truncado]";
                truncated = true;
            }

            return new GitWorkspaceInfo(
                true, branch.Trim(), added, removed, files, diff, truncated,
                displayRef, baseSha);
        }
        finally
        {
            // Cleanup roda com token próprio: mesmo se o request foi
            // cancelado no meio, o index não pode ficar sujo de i-t-a.
            var reset = await RunGitAsync(
                workdir, CancellationToken.None, "reset", "--quiet");
            if (reset is null && !hasHead)
            {
                _ = await RunGitAsync(
                    workdir, CancellationToken.None, "rm", "-r", "-q", "--cached", ".");
            }
            if (stagedBefore.Count > 0)
            {
                var readd = new List<string> { "add", "--" };
                readd.AddRange(stagedBefore);
                _ = await RunGitAsync(workdir, CancellationToken.None, [.. readd]);
            }
        }
    }

    /// <summary>
    /// Resolve a base do diff: merge-base de HEAD com a ref preferida
    /// (<paramref name="preferredRef"/>), depois <c>origin/{branch}</c>;
    /// históricos disjuntos ou repo sem remote caem em <c>HEAD</c>.
    /// </summary>
    private static async Task<(string? Ref, string Sha)> ResolveBaseAsync(
        string workdir, string branch, string? preferredRef, CancellationToken ct)
    {
        foreach (var candidate in new[] { preferredRef, $"origin/{branch}" })
        {
            if (string.IsNullOrEmpty(candidate))
            {
                continue;
            }
            var exists = await RunGitAsync(
                workdir, ct, RevParseCommand, "--verify", "--quiet", candidate);
            if (exists is null)
            {
                continue; // ref remota inexistente neste clone
            }
            var mergeBase = await RunGitAsync(
                workdir, ct, "merge-base", "HEAD", candidate);
            if (mergeBase is { } mb && mb.Trim().Length > 0)
            {
                return (candidate, mb.Trim());
            }
        }
        return (null, "HEAD");
    }

    /// <summary>Paths staged no index (restaurados após o intent-to-add).</summary>
    private static async Task<List<string>> StagedPathsAsync(
        string workdir, CancellationToken ct)
    {
        var staged = await RunGitAsync(
            workdir, ct, "diff", "--cached", "--name-only") ?? string.Empty;
        return staged.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(UnquoteGitPath)
            .Where(p => p.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Arquivos alterados vs <paramref name="baseSha"/>: <c>--name-status -M</c>
    /// dá o status real (M/A/D/R) e <c>--numstat</c> os contadores. Rodar sob
    /// intent-to-add para untracked aparecerem como <c>A</c> com contagem real.
    /// </summary>
    private static async Task<(List<GitChangedFile> Files, int Added, int Removed)>
        CollectChangedFilesAsync(string workdir, string baseSha, CancellationToken ct)
    {
        var stats = new Dictionary<string, (int A, int R)>(StringComparer.Ordinal);
        var added = 0;
        var removed = 0;
        var numstat = await RunGitAsync(
            workdir, ct, "diff", baseSha, "--numstat", "-M") ?? string.Empty;
        foreach (var parts in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(line => line.Split('\t'))
                     .Where(p => p.Length >= 3))
        {
            var a = int.TryParse(parts[0], out var av) ? av : 0; // "-" = binário
            var r = int.TryParse(parts[1], out var rv) ? rv : 0;
            added += a;
            removed += r;
            stats[NormalizeNumstatPath(parts[2].Trim())] = (a, r);
        }

        var files = new List<GitChangedFile>();
        var nameStatus = await RunGitAsync(
            workdir, ct, "diff", baseSha, "--name-status", "-M") ?? string.Empty;
        foreach (var parts in nameStatus.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(line => line.Split('\t'))
                     .Where(p => p.Length >= 2))
        {
            var letter = parts[0][0];
            string? oldPath = null;
            var path = UnquoteGitPath(parts[1]);
            if ((letter == 'R' || letter == 'C') && parts.Length >= 3)
            {
                oldPath = path;
                path = UnquoteGitPath(parts[2]);
            }
            var (a, r) = stats.TryGetValue(path, out var sr) ? sr : (0, 0);
            files.Add(new GitChangedFile(path, a, r, letter.ToString(), oldPath));
        }

        return (files, added, removed);
    }

    /// <summary>
    /// Normaliza path de linha <c>--numstat</c>: renames vêm como
    /// <c>src/{old =&gt; new}/f.cs</c> — devolve sempre o nome novo.
    /// </summary>
    private static string NormalizeNumstatPath(string path)
    {
        var arrow = path.IndexOf(" => ", StringComparison.Ordinal);
        if (arrow < 0)
        {
            return UnquoteGitPath(path);
        }
        var braceOpen = path.LastIndexOf('{', arrow);
        var braceClose = path.IndexOf('}', arrow);
        if (braceOpen >= 0 && braceClose > arrow)
        {
            return path[..braceOpen] + path[(arrow + 4)..braceClose] + path[(braceClose + 1)..];
        }
        return UnquoteGitPath(path[(arrow + 4)..]);
    }

    /// <summary>Desfaz a citação de <c>core.quotePath</c> em paths do porcelain/diff.</summary>
    private static string UnquoteGitPath(string path)
    {
        path = path.Trim();
        if (path.Length > 1 && path.StartsWith('"') && path.EndsWith('"'))
        {
            path = path[1..^1].Replace("\\\"", "\"");
        }
        return path;
    }

    private static GitWorkspaceInfo Empty => new(false, null, 0, 0, [], null, false);

    /// <summary>
    /// Roda <c>git -C workdir args…</c> e devolve stdout; null quando o
    /// processo falha, excede o timeout ou o binário não existe.
    /// </summary>
    private static async Task<string?> RunGitAsync(
        string workdir, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExecutableResolver.Resolve("git"),
            WorkingDirectory = workdir,
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
        catch (Win32Exception) { return null; } // falha de spawn/exec do git → null por contrato
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
    public static async Task<string?> CommitAsync(string workdir, string message, CancellationToken ct)
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
        var head = await RunGitAsync(workdir, ct, RevParseCommand, "HEAD");
        return head?.Trim();
    }

    /// <summary><c>git show HEAD:rel</c> — conteúdo do arquivo no HEAD
    /// (diff da IDE). null sem repo/sem commit/arquivo não trackeado.</summary>
    public static async Task<string?> ShowHeadAsync(string workdir, string relPath, CancellationToken ct)
    {
        if (!await IsRepoRootAsync(workdir, ct))
        {
            return null;
        }
        return await RunGitAsync(workdir, ct, "show", $"HEAD:{relPath.Replace('\\', '/')}");
    }

    /// <summary>True só quando <paramref name="workdir"/> é a raiz do repo git —
    /// impede que <c>git</c> escale pra um repo pai (workdir sob o clone do app)
    /// e leia/escreva no repositório errado (SPEC-20261011-ide-search-scm).</summary>
    private static async Task<bool> IsRepoRootAsync(string workdir, CancellationToken ct)
    {
        var top = await RunGitAsync(workdir, ct, RevParseCommand, "--show-toplevel");
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
