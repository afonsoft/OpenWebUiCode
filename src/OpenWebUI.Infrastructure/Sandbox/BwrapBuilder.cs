using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenWebUI.Infrastructure.Sandbox;

/// <summary>
/// Monta o <see cref="ProcessStartInfo"/> do bubblewrap
/// (SPEC-20261031-sandboxed-exec RF-001): workspace bind-rw, sistema
/// essencial ro, HOME falso, deny-reads ocultos, rede via
/// <c>--unshare-net</c> ou proxy controlado, <c>--die-with-parent</c>.
/// </summary>
public static class BwrapBuilder
{
    private const string Setenv = "--setenv";
    private const string Robind = "--ro-bind";

    private static readonly string[] RoSystemPaths =
        ["/usr", "/lib", "/lib64", "/bin", "/sbin", "/etc/resolv.conf",
         "/etc/ssl", "/etc/ca-certificates", "/etc/hosts", "/etc/nsswitch.conf"];

    private static readonly string[] BwrapCandidates =
        ["/usr/bin/bwrap", "/usr/local/bin/bwrap", "/bin/bwrap",
         "/snap/bin/bwrap"];

    /// <summary>Path do binário bwrap, ou <c>null</c> se ausente.</summary>
    public static string? FindBwrap()
    {
        var hit = BwrapCandidates.FirstOrDefault(File.Exists);
        if (hit is not null)
        {
            return hit;
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Join(dir, "bwrap"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>bwrap presente + Linux = sandbox disponível.</summary>
    public static bool IsAvailable =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        && FindBwrap() is not null;

    /// <summary>
    /// Monta o psi de <c>bwrap … -- /bin/sh -c &lt;command&gt;</c>.
    /// <paramref name="proxyPort"/>, quando definido, injeta
    /// <c>HTTP(S)_PROXY</c>/<c>ALL_PROXY</c> apontando para o proxy interno;
    /// sem proxy e sem rede → <c>--unshare-net</c>.
    /// </summary>
    public static ProcessStartInfo Build(
        string bwrapPath, string command, string workspacePath,
        SandboxScope scope, int? proxyPort,
        IReadOnlyDictionary<string, string>? env)
    {
        var psi = new ProcessStartInfo
        {
            FileName = bwrapPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = workspacePath,
        };
        var args = psi.ArgumentList;

        args.Add("--die-with-parent");
        args.Add("--new-session");

        AddFilesystemBinds(args, workspacePath, scope);
        AddFakeHome(args);
        AddNetwork(args, scope, proxyPort);
        AddEnv(args, env);

        args.Add("--");
        args.Add("/bin/sh");
        args.Add("-c");
        args.Add(command);
        return psi;
    }

    /// <summary>Binds de filesystem: sistema ro, workspace+escopos rw,
    /// deny-reads ocultos (deny sempre vence).</summary>
    private static void AddFilesystemBinds(
        IList<string> args, string workspacePath, SandboxScope scope)
    {
        foreach (var p in RoSystemPaths.Where(p =>
            File.Exists(p) || Directory.Exists(p)))
        {
            args.Add(Robind); args.Add(p); args.Add(p);
        }

        args.Add("--bind"); args.Add(workspacePath); args.Add(workspacePath);
        foreach (var w in scope.WriteScopes.Where(Directory.Exists))
        {
            args.Add("--bind"); args.Add(w); args.Add(w);
        }

        foreach (var deny in scope.DenyReads)
        {
            var full = Path.IsPathRooted(deny)
                ? deny
                : Path.Join(workspacePath, deny);
            if (Directory.Exists(full))
            {
                args.Add("--tmpfs"); args.Add(full);
            }
            else
            {
                args.Add(Robind); args.Add("/dev/null"); args.Add(full);
            }
        }
    }

    /// <summary>HOME falso: tmpfs privada sobre o HOME real (não pública,
    /// diferente de <c>/tmp</c>).</summary>
    private static void AddFakeHome(IList<string> args)
    {
        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            args.Add("--tmpfs"); args.Add(home);
            args.Add(Setenv); args.Add("HOME"); args.Add(home);
        }
    }

    /// <summary>Rede: proxy allowlisted via envs ou <c>--unshare-net</c>.</summary>
    private static void AddNetwork(
        IList<string> args, SandboxScope scope, int? proxyPort)
    {
        if (!scope.NetworkEnabled || proxyPort is null)
        {
            args.Add("--unshare-net");
            return;
        }
        var proxy = $"http://127.0.0.1:{proxyPort}";
        foreach (var key in
            new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY",
                    "http_proxy", "https_proxy", "all_proxy" })
        {
            args.Add(Setenv); args.Add(key); args.Add(proxy);
        }
        args.Add(Setenv); args.Add("NO_PROXY");
        args.Add("localhost,127.0.0.1,::1");
    }

    /// <summary>Env resolvida (secrets mascarados na saída pelo runner).</summary>
    private static void AddEnv(
        IList<string> args, IReadOnlyDictionary<string, string>? env)
    {
        if (env is null)
        {
            return;
        }
        foreach (var (k, v) in env)
        {
            args.Add(Setenv); args.Add(k); args.Add(v);
        }
    }
}
