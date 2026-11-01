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
    private static readonly string[] RoSystemPaths =
        ["/usr", "/lib", "/lib64", "/bin", "/sbin", "/etc/resolv.conf",
         "/etc/ssl", "/etc/ca-certificates", "/etc/hosts", "/etc/nsswitch.conf"];

    private static readonly string[] BwrapCandidates =
        ["/usr/bin/bwrap", "/usr/local/bin/bwrap", "/bin/bwrap",
         "/snap/bin/bwrap"];

    /// <summary>Path do binário bwrap, ou <c>null</c> se ausente.</summary>
    public static string? FindBwrap()
    {
        foreach (var c in BwrapCandidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Join(dir, "bwrap");
            if (File.Exists(p))
            {
                return p;
            }
        }
        return null;
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

        // Sistema essencial read-only.
        foreach (var p in RoSystemPaths)
        {
            if (File.Exists(p) || Directory.Exists(p))
            {
                args.Add("--ro-bind"); args.Add(p); args.Add(p);
            }
        }

        // Workspace rw + escopos Write extras.
        args.Add("--bind"); args.Add(workspacePath); args.Add(workspacePath);
        foreach (var w in scope.WriteScopes)
        {
            if (Directory.Exists(w))
            {
                args.Add("--bind"); args.Add(w); args.Add(w);
            }
        }

        // Deny wins: arquivos → /dev/null por cima; diretórios → tmpfs.
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
                args.Add("--ro-bind"); args.Add("/dev/null"); args.Add(full);
            }
        }

        // HOME falso mínimo.
        var home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            args.Add("--tmpfs"); args.Add(home);
        }
        args.Add("--setenv"); args.Add("HOME"); args.Add("/tmp");

        // Rede.
        var netAllowed = scope.NetworkEnabled && proxyPort is not null;
        if (netAllowed)
        {
            var proxy = $"http://127.0.0.1:{proxyPort}";
            args.Add("--setenv"); args.Add("HTTP_PROXY"); args.Add(proxy);
            args.Add("--setenv"); args.Add("HTTPS_PROXY"); args.Add(proxy);
            args.Add("--setenv"); args.Add("ALL_PROXY"); args.Add(proxy);
            args.Add("--setenv"); args.Add("http_proxy"); args.Add(proxy);
            args.Add("--setenv"); args.Add("https_proxy"); args.Add(proxy);
            args.Add("--setenv"); args.Add("all_proxy"); args.Add(proxy);
            args.Add("--setenv"); args.Add("NO_PROXY");
            args.Add("localhost,127.0.0.1,::1");
        }
        else
        {
            args.Add("--unshare-net");
        }

        // Env resolvida (secrets mascarados na saída pelo runner).
        if (env is not null)
        {
            foreach (var (k, v) in env)
            {
                args.Add("--setenv"); args.Add(k); args.Add(v);
            }
        }

        args.Add("--");
        args.Add("/bin/sh");
        args.Add("-c");
        args.Add(command);
        return psi;
    }
}
