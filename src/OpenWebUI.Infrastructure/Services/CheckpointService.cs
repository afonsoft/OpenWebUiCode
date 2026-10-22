using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Checkpoint imutável do workdir (commit na ref oculta do snapshot repo).</summary>
/// <param name="Hash">Commit sha do checkpoint.</param>
/// <param name="Files">Arquivos cobertos pelo delta deste turno (vs. checkpoint anterior).</param>
/// <param name="Turn">Turno da run (0 = pré-run).</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
public sealed record CheckpointInfo(
    string Hash, IReadOnlyList<string> Files, int Turn, long CreatedAt);

/// <summary>Resumo de um checkpoint persistido (listagem para a UI).</summary>
public sealed record CheckpointSummary(string Hash, int Turn, long CreatedAt);

/// <summary>Resultado de um revert: arquivos restaurados e conflitos.</summary>
public sealed record RevertResult(
    IReadOnlyList<string> Reverted, IReadOnlyList<string> Conflicts);

/// <summary>
/// Snapshots por turno do workdir + revert granular
/// (SPEC-20261009-checkpoints-revert, E16 S6 — padrão opencode
/// <c>src/snapshot/index.ts</c>). Em vez de mexer no repo do usuário,
/// mantém um <b>git dir separado</b> em <c>data/checkpoints/repo/{hash}</c>:
/// <c>--git-dir</c> aponta pra ele e <c>--work-tree</c> pro workdir real —
/// index, objects e refs próprios; <c>git log</c>/<c>git status</c>/
/// <c>git stash</c> do usuário ficam intactos (RF-005). O object store do
/// repo original entra via <c>objects/info/alternates</c>, então blobs
/// já hashados não são recalculados/copiados.
///
/// Sem repo git, cai no backend de manifesto: <c>{path→sha256}</c> +
/// cópias dos arquivos em <c>data/checkpoints/files/{hash}/{seq}/</c>.
///
/// Revert = restaura o conteúdo gravado no checkpoint sobre os arquivos
/// que divergem dele hoje (o "patch" do opencode). Um arquivo só é
/// sobrescrito quando o conteúdo atual bate com o checkpoint mais novo
/// (a diferença veio de turns snapshotados); drift fora da trilha vira
/// conflito — <paramref name="force"/> explícito ignora.
/// </summary>
public sealed class CheckpointService(
    IHostEnvironment env,
    IConfiguration configuration,
    ILogger<CheckpointService> logger)
{
    /// <summary>Ref oculta dentro do git dir do snapshot (nunca no repo do usuário).</summary>
    private const string Ref = "refs/openwebui/checkpoints";

    /// <summary>Timeout por invocação do git.</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Cap do diff devolvido no preview do revert.</summary>
    private const int MaxDiffChars = 64_000;

    /// <summary>Tamanho máximo de arquivo copiado no fallback sem git.</summary>
    private const long MaxFallbackFileBytes = 1_000_000;

    /// <summary>Intervalo mínimo entre prunes oportunistas por workdir.</summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(6);

    /// <summary>Ignorados no backend de manifesto (eco do CollapsedDirs da file API).</summary>
    private static readonly HashSet<string> FallbackSkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules",
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastPrune = new();

    private bool Enabled => configuration.GetValue("Checkpoints:Enabled", true);
    private int MaxAgeDays => configuration.GetValue("Checkpoints:MaxAgeDays", 7);
    private long MaxBytes => configuration.GetValue("Checkpoints:MaxBytes", 2L * 1024 * 1024);

    /// <summary>Janela para <c>gc --prune</c>; ≤0 dias poda tudo agora.</summary>
    private string PruneWindow => MaxAgeDays <= 0 ? "now" : $"{MaxAgeDays}.days";

    private string DataRoot => Path.Join(DataPaths.Root(env.ContentRootPath), "checkpoints");

    /// <summary>Lock por workdir (padrão opencode <c>locks: Map&lt;string, Semaphore&gt;</c>).</summary>
    public SemaphoreSlim LockFor(string workdir) =>
        _locks.GetOrAdd(Path.GetFullPath(workdir), _ => new SemaphoreSlim(1, 1));

    // ==================== API pública ====================

    /// <summary>
    /// Cria um checkpoint do workdir: commit em <see cref="Ref"/> cujo tree é
    /// o estado completo do workdir (pai = checkpoint anterior). Devolve null
    /// quando desabilitado, quando a tree não mudou desde o último checkpoint
    /// ou quando o snapshot falha (log — nunca derruba a run).
    /// </summary>
    public async Task<CheckpointInfo?> SnapshotAsync(
        string workdir, string runId, int turn, CancellationToken ct)
    {
        if (!Enabled || !Directory.Exists(workdir))
        {
            return null;
        }

        var gate = LockFor(workdir);
        await gate.WaitAsync(ct);
        try
        {
            var info = IsGitWorkdir(workdir)
                ? await GitSnapshotAsync(workdir, runId, turn, ct)
                : await FallbackSnapshotAsync(workdir, turn, ct);
            await PruneIfDueAsync(workdir, ct);
            return info;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            logger.LogWarning(ex, "checkpoint snapshot falhou workdir={Workdir} turn={Turn}", workdir, turn);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// "Patch" do opencode: arquivos que divergem entre o checkpoint
    /// <paramref name="hash"/> e o workdir atual — o conjunto que um revert
    /// desse checkpoint tocaria.
    /// </summary>
    public async Task<IReadOnlyList<string>> PatchAsync(
        string workdir, string hash, CancellationToken ct)
    {
        var gate = LockFor(workdir);
        await gate.WaitAsync(ct);
        try
        {
            if (!IsGitWorkdir(workdir))
            {
                return await FallbackPatchAsync(workdir, hash, ct);
            }
            var gitdir = GitDirFor(workdir);
            await EnsureGitRepoAsync(workdir, gitdir, ct);
            await GitAddAsync(workdir, gitdir, ct);
            var diff = await RunGitAsync(workdir, gitdir, ct,
                "diff", "--cached", "--no-ext-diff", "--name-only", hash, "--", ".");
            return SplitLines(diff);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Unified diff workdir-atual vs. checkpoint (preview do modal de revert).</summary>
    public async Task<string?> DiffAsync(string workdir, string hash, CancellationToken ct)
    {
        var gate = LockFor(workdir);
        await gate.WaitAsync(ct);
        try
        {
            if (!IsGitWorkdir(workdir))
            {
                return null; // fallback não tem diff unificado — a lista de paths basta
            }
            var gitdir = GitDirFor(workdir);
            var diff = await RunGitAsync(workdir, gitdir, ct,
                "-c", "core.quotepath=false", "diff", "--no-ext-diff", hash, "--", ".");
            if (diff is null)
            {
                return null;
            }
            return diff.Length > MaxDiffChars
                ? diff[..MaxDiffChars] + "\n… [diff truncado]"
                : diff;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reverte o workdir para o conteúdo gravado no checkpoint
    /// <paramref name="hash"/>, restrito aos arquivos que divergem dele.
    /// Arquivo cujo conteúdo atual difere do checkpoint mais novo (tip da
    /// <see cref="Ref"/>) virou drift fora da trilha → conflito, não
    /// sobrescrito; <paramref name="force"/> ignora a guarda.
    /// </summary>
    public async Task<RevertResult> RevertAsync(
        string workdir, string hash, bool force, CancellationToken ct)
    {
        var gate = LockFor(workdir);
        await gate.WaitAsync(ct);
        try
        {
            return IsGitWorkdir(workdir)
                ? await GitRevertAsync(workdir, hash, force, ct)
                : await FallbackRevertAsync(workdir, hash, force, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Checkpoints da <see cref="Ref"/> (mais novo primeiro).</summary>
    public async Task<IReadOnlyList<CheckpointSummary>> ListAsync(
        string workdir, int take, CancellationToken ct)
    {
        var gate = LockFor(workdir);
        await gate.WaitAsync(ct);
        try
        {
            if (!IsGitWorkdir(workdir))
            {
                return FallbackList(workdir, take);
            }
            var gitdir = GitDirFor(workdir);
            if (!Directory.Exists(gitdir))
            {
                return [];
            }
            var log = await RunGitAsync(workdir, gitdir, ct,
                "log", $"--max-count={take}", "--format=%H %ct %s", Ref, "--");
            return SplitLines(log)
                .Select(line => line.Split(' ', 3))
                .Where(parts => parts.Length >= 2 && long.TryParse(parts[1], out _))
                .Select(parts => new CheckpointSummary(
                    parts[0],
                    parts.Length > 2 && parts[2].StartsWith("turn ", StringComparison.Ordinal)
                        && int.TryParse(parts[2][5..].Split(' ', 2)[0], out var t) ? t : 0,
                    long.Parse(parts[1])))
                .ToList();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Limpeza de checkpoints: git → <c>gc --prune={MaxAgeDays}.days</c>
    /// (commits antigos na ref ficam unreachable e caem) e, se o gitdir
    /// ainda exceder <c>MaxBytes</c>, <c>gc --prune=now</c>. Manifesto →
    /// apaga snapshots além do TTL e os mais antigos até caber em MaxBytes.
    /// </summary>
    public async Task PruneAsync(string workdir, CancellationToken ct)
    {
        var gate = LockFor(workdir);
        await gate.WaitAsync(ct);
        try
        {
            if (!IsGitWorkdir(workdir))
            {
                PruneFallback(workdir);
                return;
            }
            var gitdir = GitDirFor(workdir);
            if (!Directory.Exists(gitdir))
            {
                return;
            }
            await RunGitAsync(workdir, gitdir, ct, "gc", $"--prune={PruneWindow}");
            if (DirSize(gitdir) > MaxBytes)
            {
                await RunGitAsync(workdir, gitdir, ct, "gc", "--prune=now");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task PruneIfDueAsync(string workdir, CancellationToken ct)
    {
        var key = Path.GetFullPath(workdir);
        var now = DateTime.UtcNow;
        if (_lastPrune.TryGetValue(key, out var last) && now - last < PruneInterval)
        {
            return;
        }
        _lastPrune[key] = now;
        try
        {
            if (!IsGitWorkdir(workdir))
            {
                PruneFallback(workdir);
                return;
            }
            var gitdir = GitDirFor(workdir);
            if (Directory.Exists(gitdir))
            {
                await RunGitAsync(workdir, gitdir, ct, "gc", $"--prune={PruneWindow}");
                if (DirSize(gitdir) > MaxBytes)
                {
                    await RunGitAsync(workdir, gitdir, ct, "gc", "--prune=now");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
        {
            logger.LogWarning(ex, "checkpoint prune falhou workdir={Workdir}", workdir);
        }
    }

    // ==================== Backend git ====================

    private static bool IsGitWorkdir(string workdir) =>
        // Em linked worktrees `.git` é um arquivo apontando pro gitdir real.
        Directory.Exists(Path.Join(workdir, ".git"))
        || File.Exists(Path.Join(workdir, ".git"));

    private string GitDirFor(string workdir) =>
        Path.Join(DataRoot, "repo", HashPath(workdir));

    /// <summary>
    /// Remove TODO o estado de checkpoint do workdir — gitdir de snapshots
    /// em <c>checkpoints/repo/{hash}</c> e manifests em
    /// <c>checkpoints/files/{hash}</c>. Chamado quando o workspace dono
    /// some (delete de chat / release de worktree) — libera o disco que
    /// sobreviveria ao workdir removido. No-op quando nada existe.
    /// </summary>
    public void DeleteForWorkdir(string workdir)
    {
        TryDeleteDir(GitDirFor(workdir));
        TryDeleteDir(FallbackRootFor(workdir));
    }

    private static string HashPath(string path) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..16].ToLowerInvariant();

    /// <summary>
    /// Inicializa o git dir do snapshot (primeira vez): repo próprio com
    /// index separado, object store do repo original via alternates e uma
    /// cópia do index original como seed (reuso de hashes — opencode).
    /// </summary>
    private async Task EnsureGitRepoAsync(string workdir, string gitdir, CancellationToken ct)
    {
        if (Directory.Exists(Path.Join(gitdir, "objects")))
        {
            return;
        }

        Directory.CreateDirectory(gitdir);
        var init = await RunRawGitAsync(workdir, ct,
            ("GIT_DIR", gitdir), ("GIT_WORK_TREE", workdir),
            "init");
        if (init is null)
        {
            throw new InvalidOperationException("git init do snapshot falhou");
        }
        foreach (var (key, value) in new[]
        {
            ("core.autocrlf", "false"), ("core.longpaths", "true"),
            ("core.symlinks", "true"), ("core.fsmonitor", "false"),
            ("feature.manyFiles", "true"), ("index.version", "4"),
            ("index.threads", "true"), ("core.untrackedCache", "true"),
        })
        {
            await RunGitAsync(workdir, gitdir, ct, "config", key, value);
        }

        // Objects do repo original ficam visíveis via alternates — blobs do
        // HEAD não precisam ser re-hashados nem copiados (opencode seed()).
        var common = await RunGitAsync(workdir, null, ct,
            "rev-parse", "--path-format=absolute", "--git-common-dir");
        var commonDir = common?.Trim();
        if (!string.IsNullOrEmpty(commonDir) && Directory.Exists(commonDir))
        {
            var alternates = new List<string>();
            var objects = Path.Join(commonDir, "objects");
            if (Directory.Exists(objects))
            {
                alternates.Add(objects);
                var chain = Path.Join(objects, "info", "alternates");
                if (File.Exists(chain))
                {
                    alternates.AddRange((await File.ReadAllLinesAsync(chain, ct))
                        .Select(l => l.Trim()).Where(l => l.Length > 0 && Directory.Exists(l)));
                }
            }
            if (alternates.Count > 0)
            {
                Directory.CreateDirectory(Path.Join(gitdir, "objects", "info"));
                await File.WriteAllLinesAsync(
                    Path.Join(gitdir, "objects", "info", "alternates"), alternates, ct);
            }

            // Index do repo original como seed: entradas já hashadas são
            // reutilizadas — o primeiro `add` fica limitado ao delta.
            var sourceIndex = Path.Join(commonDir, "index");
            if (File.Exists(sourceIndex) && !File.Exists(Path.Join(gitdir, "index")))
            {
                File.Copy(sourceIndex, Path.Join(gitdir, "index"), overwrite: false);
            }
        }
    }

    /// <summary>
    /// Stage no index do snapshot: arquivos modificados
    /// (<c>diff-files</c>) + untracked (<c>ls-files --others</c>) —
    /// <c>--exclude-standard</c> honra .gitignore. <c>add --all</c> também
    /// registra deleções. Nunca toca o index do repo do usuário.
    /// </summary>
    private async Task GitAddAsync(string workdir, string gitdir, CancellationToken ct)
    {
        var diff = await RunGitAsync(workdir, gitdir, ct,
            "-c", "core.quotepath=false",
            "diff-files", "--name-only", "-z", "--", ".") ?? string.Empty;
        var untracked = await RunGitAsync(workdir, gitdir, ct,
            "-c", "core.quotepath=false",
            "ls-files", "--full-name", "--others", "--exclude-standard", "-z", "--", ".")
            ?? string.Empty;

        var files = diff.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Concat(untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            return;
        }

        var spec = new StringBuilder();
        foreach (var f in files)
        {
            spec.Append(":(top,literal)").Append(f).Append('\0');
        }
        await RunGitStdinAsync(workdir, gitdir, ct, spec.ToString(),
            "add", "--all", "--sparse", "--pathspec-from-file=-", "--pathspec-file-nul");
    }

    private async Task<CheckpointInfo?> GitSnapshotAsync(
        string workdir, string runId, int turn, CancellationToken ct)
    {
        var gitdir = GitDirFor(workdir);
        await EnsureGitRepoAsync(workdir, gitdir, ct);
        await GitAddAsync(workdir, gitdir, ct);

        var tree = (await RunGitAsync(workdir, gitdir, ct, "write-tree"))?.Trim();
        if (string.IsNullOrEmpty(tree))
        {
            return null;
        }

        var tip = (await RunGitAsync(workdir, gitdir, ct,
            "rev-parse", "--verify", Ref))?.Trim();
        string? parentTree = null;
        if (!string.IsNullOrEmpty(tip))
        {
            parentTree = (await RunGitAsync(workdir, gitdir, ct,
                "rev-parse", $"{tip}^{{tree}}"))?.Trim();
            if (parentTree == tree)
            {
                return null; // tree idêntica à do último checkpoint — dedupe
            }
        }

        var args = new List<string>
        {
            "-c", "user.email=openwebui@local", "-c", "user.name=openwebui",
            "commit-tree", tree,
        };
        if (!string.IsNullOrEmpty(tip))
        {
            args.Add("-p");
            args.Add(tip);
        }
        args.Add("-m");
        args.Add($"turn {turn}\nrun {runId}");
        var commit = (await RunGitAsync(workdir, gitdir, ct, args.ToArray()))?.Trim();
        if (string.IsNullOrEmpty(commit))
        {
            return null;
        }
        await RunGitAsync(workdir, gitdir, ct, "update-ref", Ref, commit);

        // Arquivos deste turno = delta vs. checkpoint anterior; no primeiro,
        // vs. o HEAD do repo (sujeira pré-existente) ou a tree vazia.
        var baseTree = parentTree
            ?? (await RunGitAsync(workdir, null, ct, "rev-parse", "HEAD^{tree}"))?.Trim()
            ?? "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        var files = SplitLines(await RunGitAsync(workdir, gitdir, ct,
            "diff", "--name-only", baseTree, tree, "--", "."));

        return new CheckpointInfo(
            commit, files, turn, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private async Task<RevertResult> GitRevertAsync(
        string workdir, string hash, bool force, CancellationToken ct)
    {
        var gitdir = GitDirFor(workdir);
        if (!Directory.Exists(gitdir)
            || (await RunGitAsync(workdir, gitdir, ct, "cat-file", "-t", hash))?.Trim() != "commit")
        {
            return new RevertResult([], []);
        }

        await EnsureGitRepoAsync(workdir, gitdir, ct);
        await GitAddAsync(workdir, gitdir, ct);
        var covered = SplitLines(await RunGitAsync(workdir, gitdir, ct,
            "diff", "--cached", "--no-ext-diff", "--name-only", hash, "--", "."));

        var tipTree = (await RunGitAsync(workdir, gitdir, ct,
            "rev-parse", $"{Ref}^{{tree}}"))?.Trim();

        var reverted = new List<string>();
        var conflicts = new List<string>();
        foreach (var rel in covered)
        {
            var current = await WorkdirBlobAsync(workdir, gitdir, rel, ct);
            var tip = tipTree is null
                ? null
                : await TreeBlobAsync(workdir, gitdir, tipTree, rel, ct);
            if (!force && current != tip)
            {
                // Conteúdo atual não é o do último checkpoint — drift fora
                // da trilha: reporta conflito em vez de sobrescrever.
                conflicts.Add(rel);
                continue;
            }

            var inSnapshot = await TreeBlobAsync(workdir, gitdir, hash, rel, ct) is not null
                || await ExistsInTreeAsync(workdir, gitdir, hash, rel, ct);
            if (inSnapshot)
            {
                var ok = await RunGitAsync(workdir, gitdir, ct,
                    "checkout", hash, "--", rel);
                if (ok is null)
                {
                    conflicts.Add(rel);
                    continue;
                }
            }
            else
            {
                // Não existia no checkpoint (criado depois) → remove.
                TryDelete(Path.Join(workdir, rel));
            }
            reverted.Add(rel);
        }
        return new RevertResult(reverted, conflicts);
    }

    /// <summary>Blob sha do arquivo no workdir (hash sem escrever objeto); null se ausente.</summary>
    private static async Task<string?> WorkdirBlobAsync(
        string workdir, string gitdir, string rel, CancellationToken ct)
    {
        var full = Path.Join(workdir, rel);
        if (!File.Exists(full))
        {
            return null;
        }
        var hash = await RunGitAsync(workdir, gitdir, ct,
            "hash-object", "--", rel);
        return hash?.Trim();
    }

    /// <summary>Blob sha de <paramref name="rel"/> dentro da tree; null se ausente.</summary>
    private static async Task<string?> TreeBlobAsync(
        string workdir, string gitdir, string treeish, string rel, CancellationToken ct)
    {
        var entry = (await RunGitAsync(workdir, gitdir, ct,
            "ls-tree", treeish, "--", rel))?.Trim();
        var tab = entry?.IndexOf('\t') ?? -1;
        if (tab < 0)
        {
            return null;
        }
        var fields = entry![..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields is [_, "blob", var sha, ..] ? sha : null;
    }

    private static async Task<bool> ExistsInTreeAsync(
        string workdir, string gitdir, string treeish, string rel, CancellationToken ct) =>
        (await RunGitAsync(workdir, gitdir, ct, "ls-tree", treeish, "--", rel))?
            .Trim().Length > 0;

    // ==================== Backend manifesto (sem git) ====================

    private string FallbackRootFor(string workdir) =>
        Path.Join(DataRoot, "files", HashPath(workdir));

    private sealed record ManifestEntry(string Sha256, string? Stored);
    private sealed record Manifest(
        int Turn, long CreatedAt, string Base, Dictionary<string, ManifestEntry> Files);

    private async Task<CheckpointInfo?> FallbackSnapshotAsync(
        string workdir, int turn, CancellationToken ct)
    {
        var root = FallbackRootFor(workdir);
        var seq = NextSeq(root);
        var id = $"m{seq:D6}";
        var dir = Path.Join(root, id);
        Directory.CreateDirectory(Path.Join(dir, "files"));

        var prev = ReadManifest(root, LatestId(root));
        var files = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        var changed = new List<string>();
        foreach (var (rel, full) in WalkFiles(workdir))
        {
            var sha = await HashFileAsync(full, ct);
            string? stored = null;
            var storeRel = Path.Join("files", rel);
            if (prev?.Files.TryGetValue(rel, out var pe) == true
                && pe.Sha256 == sha && pe.Stored is not null
                && File.Exists(Path.Join(root, pe.Stored)))
            {
                stored = pe.Stored; // dedupe: Stored já é root-relative (m{seq}/files/rel)
            }
            else if (new FileInfo(full).Length <= MaxFallbackFileBytes)
            {
                var dest = Path.Join(dir, storeRel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(full, dest, overwrite: true);
                stored = Path.Join(id, storeRel);
            }
            // else: acima do limite — manifestado sem payload (conflito no revert)
            files[rel] = new ManifestEntry(sha, stored);
            if (prev?.Files.TryGetValue(rel, out var pp) != true || pp!.Sha256 != sha)
            {
                changed.Add(rel);
            }
        }
        if (prev is not null)
        {
            // deletado desde o último snapshot
            changed.AddRange(prev.Files.Keys.Where(rel => !files.ContainsKey(rel)));
        }

        var manifest = new Manifest(turn, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), id, files);
        await File.WriteAllTextAsync(Path.Join(dir, "manifest.json"),
            JsonSerializer.Serialize(manifest), ct);
        await File.WriteAllTextAsync(Path.Join(root, "latest"), id, ct);

        return new CheckpointInfo(id, changed.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            turn, manifest.CreatedAt);
    }

    private Task<IReadOnlyList<string>> FallbackPatchAsync(
        string workdir, string hash, CancellationToken ct)
    {
        var root = FallbackRootFor(workdir);
        var manifest = ReadManifest(root, hash);
        if (manifest is null)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
        var diff = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rel, full) in WalkFiles(workdir))
        {
            seen.Add(rel);
            if (!manifest.Files.TryGetValue(rel, out var entry)
                || entry.Sha256 != HashFile(full))
            {
                diff.Add(rel);
            }
        }
        diff.AddRange(manifest.Files.Keys.Where(k => !seen.Contains(k)));
        return Task.FromResult<IReadOnlyList<string>>(diff);
    }

    private async Task<RevertResult> FallbackRevertAsync(
        string workdir, string hash, bool force, CancellationToken ct)
    {
        var root = FallbackRootFor(workdir);
        var manifest = ReadManifest(root, hash);
        var tip = ReadManifest(root, LatestId(root));
        if (manifest is null)
        {
            return new RevertResult([], []);
        }

        var covered = await FallbackPatchAsync(workdir, hash, ct);
        var reverted = new List<string>();
        var conflicts = new List<string>();
        foreach (var rel in covered)
        {
            var full = Path.Join(workdir, rel);
            var current = File.Exists(full) ? HashFile(full) : null;
            var tipHash = tip?.Files.TryGetValue(rel, out var te) == true ? te.Sha256 : null;
            if (!force && current != tipHash)
            {
                conflicts.Add(rel);
                continue;
            }

            if (manifest.Files.TryGetValue(rel, out var entry) && entry.Stored is not null)
            {
                var source = Path.Join(root, entry.Stored);
                if (!File.Exists(source))
                {
                    conflicts.Add(rel);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.Copy(source, full, overwrite: true);
            }
            else
            {
                TryDelete(full);
            }
            reverted.Add(rel);
        }
        return new RevertResult(reverted, conflicts);
    }

    private IReadOnlyList<CheckpointSummary> FallbackList(string workdir, int take)
    {
        var root = FallbackRootFor(workdir);
        if (!Directory.Exists(root))
        {
            return [];
        }
        return Directory.EnumerateDirectories(root)
            .Select(d => (id: Path.GetFileName(d), m: ReadManifest(root, Path.GetFileName(d))))
            .Where(x => x.m is not null)
            .OrderByDescending(x => x.m!.CreatedAt)
            .ThenByDescending(x => SeqOf(x.id))
            .Take(take)
            .Select(x => new CheckpointSummary(x.id!, x.m!.Turn, x.m.CreatedAt))
            .ToList();
    }

    private void PruneFallback(string workdir)
    {
        var root = FallbackRootFor(workdir);
        if (!Directory.Exists(root))
        {
            return;
        }
        var cutoff = DateTimeOffset.UtcNow.AddDays(-MaxAgeDays).ToUnixTimeSeconds();
        var dirs = Directory.EnumerateDirectories(root)
            .Select(d => (dir: d, m: ReadManifest(root, Path.GetFileName(d))))
            .Where(x => x.m is not null)
            .OrderBy(x => x.m!.CreatedAt)
            .ThenBy(x => SeqOf(Path.GetFileName(x.dir)))
            .ToList();
        var remaining = dirs.Where(x => x.m!.CreatedAt >= cutoff).ToList();
        foreach (var stale in dirs.Where(x => x.m!.CreatedAt < cutoff))
        {
            TryDeleteDir(stale.dir);
        }
        // Ainda estourou o teto agregado → remove os mais antigos (mantém latest).
        var latest = LatestId(root);
        while (remaining.Count > 1 && DirSize(root) > MaxBytes)
        {
            var oldest = remaining.First();
            if (Path.GetFileName(oldest.dir) == latest)
            {
                break;
            }
            TryDeleteDir(oldest.dir);
            remaining.RemoveAt(0);
        }
    }

    private static Manifest? ReadManifest(string root, string? id)
    {
        if (id is null)
        {
            return null;
        }
        var file = Path.Join(root, id, "manifest.json");
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(file))
                : null;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private static string? LatestId(string root)
    {
        var marker = Path.Join(root, "latest");
        try
        {
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>Seq numérico do id <c>m{seq}</c>; 0 quando o nome não casa o formato.</summary>
    private static int SeqOf(string? id) =>
        id is { Length: > 1 } && id[0] == 'm' && int.TryParse(id[1..], out var n) ? n : 0;

    private static int NextSeq(string root)
    {
        if (!Directory.Exists(root))
        {
            return 1;
        }
        var max = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => name is { Length: > 1 } && name[0] == 'm')
            .Select(name => int.TryParse(name![1..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        return max + 1;
    }

    /// <summary>Walk do workdir para o manifesto (rel, full) — pula dirs gerados.</summary>
    private static IEnumerable<(string Rel, string Full)> WalkFiles(string workdir)
    {
        var stack = new Stack<string>();
        stack.Push(workdir);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                dirs = Directory.EnumerateDirectories(dir);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var f in files)
            {
                yield return (Path.GetRelativePath(workdir, f)
                    .Replace(Path.DirectorySeparatorChar, '/'), f);
            }
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (!FallbackSkipDirs.Contains(name)
                    && new DirectoryInfo(d).LinkTarget is null)
                {
                    stack.Push(d);
                }
            }
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // ==================== helpers ====================

    private void TryDelete(string full)
    {
        try
        {
            File.Delete(full);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "checkpoint revert: não removeu {Path}", full);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "checkpoint revert: não removeu {Path}", full);
        }
    }

    private void TryDeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "checkpoint prune: não removeu {Dir}", dir);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "checkpoint prune: não removeu {Dir}", dir);
        }
    }

    private static long DirSize(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static List<string> SplitLines(string? text) => text is null
        ? []
        : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>Roda git com <c>--git-dir</c>/<c>--work-tree</c> do snapshot.</summary>
    private static async Task<string?> RunGitAsync(
        string workdir, string? gitdir, CancellationToken ct, params string[] args) =>
        await RunGitStdinAsync(workdir, gitdir, ct, null, args);

    /// <summary>Versão com stdin (pathspec NUL do <c>add</c>).</summary>
    private static async Task<string?> RunGitStdinAsync(
        string workdir, string? gitdir, CancellationToken ct,
        string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExecutableResolver.Resolve("git"),
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (gitdir is not null)
        {
            psi.ArgumentList.Add("--git-dir");
            psi.ArgumentList.Add(gitdir);
            psi.ArgumentList.Add("--work-tree");
            psi.ArgumentList.Add(workdir);
        }
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false");
        // Snapshots no volume DATA_ROOT podem ter outro dono —
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
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).WaitAsync(GitTimeout, ct);
            return process.ExitCode == 0 ? await stdout : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception) { return null; }
        catch (ObjectDisposedException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (IOException) { return null; }
        catch (TimeoutException) { return null; }
    }

    /// <summary>git puro com variáveis de ambiente (init do snapshot).</summary>
    private static async Task<string?> RunRawGitAsync(
        string workdir, CancellationToken ct,
        (string Key, string Value) env1, (string Key, string Value) env2,
        params string[] args)
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
        psi.Environment[env1.Key] = env1.Value;
        psi.Environment[env2.Key] = env2.Value;
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
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).WaitAsync(GitTimeout, ct);
            return process.ExitCode == 0 ? await stdout : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception) { return null; }
        catch (ObjectDisposedException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (IOException) { return null; }
        catch (TimeoutException) { return null; }
    }
}
