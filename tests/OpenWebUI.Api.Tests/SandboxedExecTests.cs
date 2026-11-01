using System.Net;
using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using OpenWebUI.Infrastructure.Sandbox;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261031-sandboxed-exec: allowlist de domínios (RF-003), escopo
/// deny/excluded (RF-002/RF-005), montagem dos args do bwrap (RF-001) e
/// proxy negando domínio (sandbox.network.denied).
/// </summary>
[TestFixture]
public class SandboxedExecTests
{
    private static SandboxScope Scope(
        string[]? write = null, string[]? deny = null,
        string[]? allow = null, string[]? excluded = null,
        bool network = true) => new(
            write ?? [], deny ?? [], allow ?? [], excluded ?? [], network);

    [Test]
    public void Allowlist_WildcardExactoELocalhost()
    {
        var allow = new[] { "api.github.com", "*.example.com" };
        Assert.Multiple(() =>
        {
            Assert.That(DomainAllowlist.IsAllowed(
                "api.github.com", allow), Is.True);
            Assert.That(DomainAllowlist.IsAllowed(
                "x.example.com", allow), Is.True);
            Assert.That(DomainAllowlist.IsAllowed(
                "example.com", allow), Is.True);
            Assert.That(DomainAllowlist.IsAllowed(
                "localhost", allow), Is.True);
            Assert.That(DomainAllowlist.IsAllowed(
                "127.0.0.1", []), Is.True);
            Assert.That(DomainAllowlist.IsAllowed(
                "evil.com", allow), Is.False);
            Assert.That(DomainAllowlist.IsAllowed(
                "notexample.com", allow), Is.False);
            Assert.That(DomainAllowlist.IsAllowed(
                "", allow), Is.False);
        });
    }

    [Test]
    public void Scope_ExcludedTools_E_GrantDeDominio()
    {
        var scope = Scope(excluded: ["shell_exec", "browser_act"]);
        Assert.Multiple(() =>
        {
            Assert.That(scope.IsExcluded("shell_exec"), Is.True);
            Assert.That(scope.IsExcluded("SHELL_EXEC"), Is.True);
            Assert.That(scope.IsExcluded("file_write"), Is.False);
        });

        var key = $"sess-{Guid.NewGuid():N}";
        SandboxScope.GrantDomain(key, "npmjs.com");
        Assert.That(SandboxScope.GrantedDomains(key),
            Does.Contain("npmjs.com"));
        SandboxScope.ClearSession(key);
        Assert.That(SandboxScope.GrantedDomains(key), Is.Empty);
    }

    [Test]
    public void BwrapBuilder_ArgsConfinamFilesystemERede()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"ws-{Guid.NewGuid():N}");
        var secretDir = Path.Combine(ws, ".ssh");
        Directory.CreateDirectory(secretDir);
        File.WriteAllText(Path.Combine(ws, ".env"), "X=1");
        try
        {
            var psi = BwrapBuilder.Build(
                "/usr/bin/bwrap", "echo hi", ws,
                Scope(deny: [".env", ".ssh"]), proxyPort: 9999, env: null);
            var args = psi.ArgumentList.ToArray();
            var joined = string.Join(" ", args);
            Assert.Multiple(() =>
            {
                Assert.That(joined, Does.Contain("--die-with-parent"));
                Assert.That(joined, Does.Contain($"--bind {ws} {ws}"));
                // .env (arquivo) → /dev/null por cima
                Assert.That(joined, Does.Contain(
                    $"--ro-bind /dev/null {Path.Combine(ws, ".env")}"));
                // .ssh (dir) → tmpfs
                Assert.That(joined, Does.Contain(
                    $"--tmpfs {secretDir}"));
                Assert.That(joined, Does.Contain(
                    "HTTPS_PROXY http://127.0.0.1:9999"));
            });
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Test]
    public void BwrapBuilder_SemRedeOuProxy_UnshareNet()
    {
        var ws = Path.GetTempPath();
        var psiNoNet = BwrapBuilder.Build(
            "/usr/bin/bwrap", "echo hi", ws,
            Scope(network: false), proxyPort: null, env: null);
        var psiNoProxy = BwrapBuilder.Build(
            "/usr/bin/bwrap", "echo hi", ws,
            Scope(network: true), proxyPort: null, env: null);
        Assert.Multiple(() =>
        {
            Assert.That(string.Join(" ", psiNoNet.ArgumentList),
                Does.Contain("--unshare-net"));
            Assert.That(string.Join(" ", psiNoProxy.ArgumentList),
                Does.Contain("--unshare-net"));
        });
    }

    [Test]
    public async Task Proxy_Connect_DominioNegado_403()
    {
        await using var proxy = SandboxProxyServer.Start(["api.ok.com"]);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var stream = client.GetStream();
        var req = Encoding.ASCII.GetBytes(
            "CONNECT evil.com:443 HTTP/1.1\r\nHost: evil.com\r\n\r\n");
        await stream.WriteAsync(req);
        var buf = new byte[512];
        var n = await stream.ReadAsync(buf);
        var resp = Encoding.ASCII.GetString(buf, 0, n);
        Assert.Multiple(() =>
        {
            Assert.That(resp, Does.StartWith("HTTP/1.1 403"));
            Assert.That(proxy.DeniedHosts.Keys, Does.Contain("evil.com"));
        });
    }

    [Test]
    public void Capabilities_ModoCoerente()
    {
        var caps = SandboxedProcessRunner.Capabilities;
        Assert.Multiple(() =>
        {
            Assert.That(caps.Mode,
                Is.EqualTo(caps.Bwrap ? "available" : "degraded"));
            Assert.That(caps.Proxy, Is.True);
        });
    }
}
