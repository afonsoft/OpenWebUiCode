using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>Referência resolvida para uso (attach ou leitura via tools).</summary>
/// <param name="Entry">Entry de origem (alias/source/descrição).</param>
/// <param name="Root">Diretório físico raiz — jail das leituras.</param>
/// <param name="Warning">Aviso não-fatal (ex.: fetch falhou, cache stale).</param>
public sealed record ResolvedReference(
    ReferenceEntry Entry, string Root, string? Warning = null);

/// <summary>
/// SPEC-20261018-references: resolve <c>ref://alias/path</c> para um
/// diretório físico confinado. Origem <c>path</c> aponta para um dir local;
/// origem git faz clone shallow em <c>{DATA_ROOT}/references/{user}/{hash}</c>
/// e re-faz fetch+reset quando <c>LastFetchedAt</c> passa de 24h — falha de
/// fetch serve o cache velho com warning, nunca derruba a leitura.
/// Referências são sempre somente-leitura.
/// </summary>
public class ReferenceService(
    AppDbContext db,
    IWebHostEnvironment env,
    ILogger<ReferenceService> logger)
{
    /// <summary>Frescor máximo do clone antes de re-fetch (24h — RF-001).</summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(24);

    /// <summary>Cap de paths no listing anexado (RF-002).</summary>
    public const int MaxListingPaths = 2000;

    private static readonly Regex RefUri =
        new(@"^ref:(?://)?(?<alias>[a-z0-9-]+)(?:/(?<sub>.*))?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(100));

    /// <summary>Alias válido: <c>[a-z0-9-]+</c> (mesmo domínio das variants).</summary>
    public static bool IsValidAlias(string alias) =>
        Regex.IsMatch(alias, "^[a-z0-9-]+$",
            RegexOptions.None, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Separa <c>ref://alias/sub/path</c> (ou <c>ref:alias/sub</c>) em
    /// (<paramref name="alias"/>, <paramref name="sub"/>); false quando o
    /// caminho não é URI de referência.
    /// </summary>
    public static bool TrySplitRef(
        string? path, out string alias, out string? sub)
    {
        alias = string.Empty;
        sub = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        var m = RefUri.Match(path.Trim());
        if (!m.Success)
        {
            return false;
        }
        alias = m.Groups["alias"].Value.ToLowerInvariant();
        sub = m.Groups["sub"].Success && m.Groups["sub"].Value.Length > 0
            ? m.Groups["sub"].Value
            : null;
        return true;
    }

    /// <summary>Entradas do usuário (todas — o menu filtra <c>Hidden</c>).</summary>
    public async Task<List<ReferenceEntry>> ListAsync(string userId, CancellationToken ct) =>
        await db.ReferenceEntries.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.Alias)
            .ToListAsync(ct);

    /// <summary>
    /// Resolve o alias para a raiz física: dir local normalizado ou clone
    /// git atualizado conforme a janela de 24h. Null quando o alias não
    /// existe ou a origem é inacessível.
    /// </summary>
    public async Task<ResolvedReference?> ResolveAsync(
        string userId, string alias, CancellationToken ct)
    {
        var entry = await db.ReferenceEntries.AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.UserId == userId && r.Alias == alias, ct);
        if (entry is null)
        {
            return null;
        }

        if (!IsGitSource(entry.Source))
        {
            // Path local: validado no uso (SPEC — não precisa existir no boot).
            var root = Path.GetFullPath(entry.Source);
            return Directory.Exists(root)
                ? new ResolvedReference(entry, root)
                : null;
        }

        return await ResolveGitAsync(entry, ct);
    }

    /// <summary>True quando a origem é URL git (http(s)/ssh/file) e não dir local.</summary>
    public static bool IsGitSource(string source) =>
        source.Contains("://", StringComparison.Ordinal)
        || source.StartsWith("git@", StringComparison.Ordinal);

    /// <summary>
    /// Jail do sub-path dentro da raiz da referência — mesmo fail-closed
    /// do workspace (sem <c>..</c>, absolutos, <c>~</c>).
    /// </summary>
    public static string? ResolveSub(ResolvedReference reference, string? sub, out string error) =>
        WorkspaceFiles.ResolveInside(reference.Root, sub, out error);

    /// <summary>
    /// Listing relativo ordenado da referência (cap <see cref="MaxListingPaths"/>) —
    /// injetado no contexto quando a referência é anexada (RF-002).
    /// </summary>
    public static Task<List<string>> ListTreeAsync(
        ResolvedReference reference, CancellationToken ct)
    {
        var paths = new List<string>(Math.Min(MaxListingPaths, 256));
        var stack = new Stack<string>();
        stack.Push(reference.Root);
        while (stack.Count > 0 && paths.Count < MaxListingPaths)
        {
            ct.ThrowIfCancellationRequested();
            CollectDir(stack.Pop(), reference.Root, stack, paths);
        }
        paths.Sort(StringComparer.Ordinal);
        return Task.FromResult(paths);
    }

    /// <summary>Acumula arquivos do dir (empilha subdirs, exceto <c>.git</c>).</summary>
    private static void CollectDir(
        string dir, string root, Stack<string> stack, List<string> paths)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var e in entries.OrderBy(e => e, StringComparer.Ordinal))
        {
            if (Directory.Exists(e))
            {
                if (!e.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                {
                    stack.Push(e);
                }
                continue;
            }
            paths.Add(WorkspaceFiles.RelativeOf(root, e));
            if (paths.Count >= MaxListingPaths)
            {
                return;
            }
        }
    }

    /// <summary>Alvo de tool resolvido dentro da raiz de uma referência.</summary>
    /// <param name="Root">Raiz física (jail) — base dos relativos.</param>
    /// <param name="Full">Caminho físico completo dentro do jail.</param>
    /// <param name="Display">URI <c>ref://</c> para display nos resultados.</param>
    /// <param name="Warning">Aviso não-fatal propagado do resolve (ex.: cache stale).</param>
    public sealed record RefToolTarget(
        string Root, string Full, string Display, string? Warning);

    /// <summary>
    /// Resolve um alvo <c>ref://alias/sub</c> para tools de arquivo — o
    /// alias precisa estar anexado ao contexto da run (RF-003); alias não
    /// anexado devolve erro orientando o anexo via <c>@alias</c> no
    /// composer (acesso direto a referências não-anexadas dependeria da
    /// permissão <c>external_directory</c>, ainda não implementada no
    /// sistema de policies — ver SPEC, seção Desvios).
    /// </summary>
    public async Task<(RefToolTarget? Target, string? Error)> ResolveToolTargetAsync(
        string userId, string? path,
        IReadOnlyList<string>? attachedRefs, CancellationToken ct)
    {
        if (!TrySplitRef(path, out var alias, out var sub))
        {
            return (null, $"caminho '{path}' não é um ref:// válido.");
        }
        if (attachedRefs?.Any(a => string.Equals(
                a, alias, StringComparison.OrdinalIgnoreCase)) != true)
        {
            return (null, $"referência '{alias}' não está anexada — "
                + $"mencione @{alias} no composer para anexar (somente-leitura).");
        }
        var resolved = await ResolveAsync(userId, alias, ct);
        if (resolved is null)
        {
            return (null, $"referência '{alias}' indisponível.");
        }
        var full = ResolveSub(resolved, sub, out var err);
        if (full is null)
        {
            return (null, err);
        }
        return (new RefToolTarget(
            resolved.Root, full,
            $"ref://{alias}/{(sub ?? string.Empty)}", resolved.Warning), null);
    }

    /// <summary>Invalida o timestamp para forçar refresh no próximo uso (testes/admin).</summary>
    public async Task MarkStaleAsync(ReferenceEntry entry, CancellationToken ct)
    {
        var tracked = await db.ReferenceEntries.FirstAsync(r => r.Id == entry.Id, ct);
        tracked.LastFetchedAt = 0;
        await db.SaveChangesAsync(ct);
    }

    private async Task<ResolvedReference?> ResolveGitAsync(
        ReferenceEntry entry, CancellationToken ct)
    {
        var dir = Path.Join(
            DataPaths.Root(env.ContentRootPath), "references", entry.UserId,
            CacheKey(entry));
        var branch = string.IsNullOrWhiteSpace(entry.Branch) ? null : entry.Branch.Trim();
        string? warning = null;

        if (!Directory.Exists(Path.Join(dir, ".git")))
        {
            var (ok, output) = await RunGitAsync(
                null, CloneArgs(entry.Source, branch, dir), ct, timeoutSec: 120);
            if (!ok)
            {
                logger.LogWarning(
                    "reference {Alias}: clone git falhou: {Output}", entry.Alias, output);
                return null;
            }
            await TouchFetchedAsync(entry, ct);
        }
        else if (Stale(entry))
        {
            var fetch = await RunGitAsync(
                dir, FetchArgs(branch), ct, timeoutSec: 60);
            if (fetch.Ok)
            {
                var reset = await RunGitAsync(
                    dir, ["reset", "--hard", "FETCH_HEAD"], ct, timeoutSec: 30);
                if (reset.Ok)
                {
                    await TouchFetchedAsync(entry, ct);
                }
                else
                {
                    warning = $"git reset falhou ({Trim(reset.Output)}) — servindo cache antigo.";
                }
            }
            else
            {
                warning = $"git fetch falhou ({Trim(fetch.Output)}) — servindo cache antigo.";
            }
        }

        return new ResolvedReference(entry, dir, warning);
    }

    private static bool Stale(ReferenceEntry entry) =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() - entry.LastFetchedAt
            > (long)RefreshAfter.TotalSeconds;

    private static string CacheKey(ReferenceEntry entry)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{entry.Source}|{entry.Branch}")))
            .ToLowerInvariant();
        return $"{entry.Alias}-{hash[..12]}";
    }

    private static List<string> CloneArgs(string source, string? branch, string dir)
    {
        var args = new List<string> { "clone", "--depth", "1" };
        if (branch is not null)
        {
            args.AddRange(["--branch", branch]);
        }
        args.AddRange([source, dir]);
        return args;
    }

    private static List<string> FetchArgs(string? branch) =>
        branch is null
            ? ["fetch", "--depth", "1", "origin"]
            : ["fetch", "--depth", "1", "origin", branch];

    private async Task TouchFetchedAsync(ReferenceEntry entry, CancellationToken ct)
    {
        try
        {
            await db.ReferenceEntries
                .Where(r => r.Id == entry.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    r => r.LastFetchedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogDebug(ex, "reference {Id}: timestamp de fetch não persistiu.", entry.Id);
        }
    }

    private static string Trim(string output) =>
        output.Length <= 200 ? output : output[..200];

    /// <summary>Path absoluto do git resolvido uma vez (S4036): o
    /// processo nunca depende de resolução por PATH em runtime — PATH é
    /// mutável entre requests e o Sonar trata spawn por nome como risco
    /// de PATH-hijack.</summary>
    private static readonly Lazy<string> GitExePath = new(() => ResolveGitPath());

    /// <summary>Resolve o executável git para path absoluto: procura
    /// <c>git</c>/<c>git.exe</c> nas entradas de <paramref name="pathEnv"/>
    /// (PATH do ambiente quando omitido) e depois nos paths padrão de
    /// container. Lança <see cref="FileNotFoundException"/> legível.</summary>
    internal static string ResolveGitPath(string? pathEnv = null)
    {
        var exeName = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var pathValue = pathEnv ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        foreach (var fallback in (string[])["/usr/bin/git", "/usr/local/bin/git", "/bin/git"])
        {
            if (File.Exists(fallback))
            {
                return fallback;
            }
        }
        throw new FileNotFoundException(
            "git não encontrado no PATH nem nos paths padrão de container.");
    }

    private static async Task<(bool Ok, string Output)> RunGitAsync(
        string? workdir, List<string> args, CancellationToken ct, int timeoutSec)
    {
        string gitPath;
        try
        {
            gitPath = GitExePath.Value;
        }
        catch (FileNotFoundException ex)
        {
            return (false, ex.Message);
        }

        var psi = new ProcessStartInfo(gitPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (workdir is not null)
        {
            psi.WorkingDirectory = workdir;
        }
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, "git não encontrado no PATH.");
            }
            using var timeout = CancellationTokenSource
                .CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(timeout.Token);
            return (proc.ExitCode == 0,
                $"{await stdout}\n{await stderr}".Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, ex.Message);
        }
    }
}
