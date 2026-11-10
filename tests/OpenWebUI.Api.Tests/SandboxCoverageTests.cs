using System.Net.Sockets;
using System.Text;
using NUnit.Framework;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Sandbox;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261031-sandboxed-exec: allowlist de domínios (RF-003), proxy
/// filtrante CONNECT/GET (RF-003), montagem do psi bwrap (RF-001) e
/// fallback degradado do runner quando o bwrap falha/ausente (RF-006).
/// </summary>
[TestFixture, NonParallelizable] // muta PATH para o bwrap fake
public class SandboxCoverageTests
{
    // ---------- DomainAllowlist (RF-003) ----------

    /// <summary>localhost/127.0.0.1/::1 sempre passam; wildcard cobre
    /// subdomínio e o próprio apex; exato casa só exato; fora → negado.</summary>
    [Test]
    public void Allowlist_LocalhostWildcardEExato()
    {
        var allow = new[] { "*.example.com", "api.github.com" };
        Assert.Multiple(() =>
        {
            Assert.That(DomainAllowlist.IsAllowed("localhost", allow), Is.True);
            Assert.That(DomainAllowlist.IsAllowed("127.0.0.1", allow), Is.True);
            Assert.That(DomainAllowlist.IsAllowed("::1", allow), Is.True);
            Assert.That(DomainAllowlist.IsAllowed("x.localhost", allow), Is.True);
            Assert.That(DomainAllowlist.IsAllowed("sub.example.com", allow), Is.True);
            Assert.That(DomainAllowlist.IsAllowed("example.com", allow), Is.True);
            Assert.That(DomainAllowlist.IsAllowed("api.github.com", allow), Is.True);
            Assert.That(DomainAllowlist.IsAllowed("github.com", allow), Is.False);
            Assert.That(DomainAllowlist.IsAllowed("notexample.com", allow), Is.False);
            Assert.That(DomainAllowlist.IsAllowed("", allow), Is.False);
            Assert.That(DomainAllowlist.IsAllowed("  ", allow), Is.False);
        });
    }

    // ---------- SandboxProxyServer (RF-003) ----------

    /// <summary>GET absolute-URI para domínio fora da allowlist → 403 e o
    /// host entra em <see cref="SandboxProxyServer.DeniedHosts"/>.</summary>
    [Test]
    public async Task Proxy_Get_DominioNegado_403ERegistra()
    {
        await using var proxy = SandboxProxyServer.Start(["*.ok.com"]);
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", proxy.Port);
        var stream = client.GetStream();
        var req = Encoding.ASCII.GetBytes(
            "GET http://evil.com/path HTTP/1.1\r\nHost: evil.com\r\n\r\n");
        await stream.WriteAsync(req);

        var response = await ReadAllAsync(stream);

        Assert.Multiple(() =>
        {
            Assert.That(response, Does.Contain("403"));
            Assert.That(proxy.DeniedHosts(), Does.Contain("evil.com"));
        });
    }

    /// <summary>CONNECT para domínio negado → 403; método não-CONNECT para
    /// domínio permitido → 501 (só CONNECT é implementado).</summary>
    [Test]
    public async Task Proxy_ConnectNegado403_NaoConnect501()
    {
        await using var proxy = SandboxProxyServer.Start([]);
        using var denied = new TcpClient();
        await denied.ConnectAsync("127.0.0.1", proxy.Port);
        var deniedStream = denied.GetStream();
        await deniedStream.WriteAsync(Encoding.ASCII.GetBytes(
            "CONNECT blocked.com:443 HTTP/1.1\r\nHost: blocked.com:443\r\n\r\n"));
        var deniedResp = await ReadAllAsync(deniedStream);

        using var allowed = new TcpClient();
        await allowed.ConnectAsync("127.0.0.1", proxy.Port);
        var allowedStream = allowed.GetStream();
        await allowedStream.WriteAsync(Encoding.ASCII.GetBytes(
            "GET http://localhost/x HTTP/1.1\r\nHost: localhost\r\n\r\n"));
        var allowedResp = await ReadAllAsync(allowedStream);

        Assert.Multiple(() =>
        {
            Assert.That(deniedResp, Does.Contain("403"));
            Assert.That(allowedResp, Does.Contain("501"));
            Assert.That(proxy.DeniedHosts(), Does.Contain("blocked.com"));
        });
    }

    /// <summary>CONNECT localhost para um servidor local real →
    /// 200 Connection Established e o túnel repassa bytes nos dois
    /// sentidos.</summary>
    [Test]
    public async Task Proxy_ConnectLocalhost_TunelRepassaBytes()
    {
        var echo = new TcpListener(System.Net.IPAddress.Loopback, 0);
        echo.Start();
        var echoPort = ((System.Net.IPEndPoint)echo.LocalEndpoint).Port;
        var echoTask = Task.Run(async () =>
        {
            try
            {
                using var up = await echo.AcceptTcpClientAsync();
                var upStream = up.GetStream();
                var buf = new byte[64];
                var n = await upStream.ReadAsync(buf);
                // ecoa o payload de volta prefixado — prova os dois sentidos.
                await upStream.WriteAsync(
                    Encoding.ASCII.GetBytes("echo:").Concat(buf[..n]).ToArray());
            }
            catch (Exception ex) when (ex is SocketException
                or ObjectDisposedException or IOException)
            {
                // listener parado no finally sem conexão — ok.
            }
        });
        try
        {
            await using var proxy = SandboxProxyServer.Start([]);
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", proxy.Port);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"CONNECT localhost:{echoPort} HTTP/1.1\r\n\r\n"));

            // Lê o 200 do proxy (fecha \r\n\r\n).
            var header = new List<byte>();
            var b = new byte[1];
            while (header.Count < 4
                   || Encoding.ASCII.GetString(header.ToArray()[^4..]) != "\r\n\r\n")
            {
                if (await stream.ReadAsync(b) <= 0) break;
                header.Add(b[0]);
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("oi-tunel"));
            var buf = new byte[64];
            var n = await stream.ReadAsync(buf);
            var payload = Encoding.ASCII.GetString(buf[..n]);

            Assert.Multiple(() =>
            {
                Assert.That(Encoding.ASCII.GetString(header.ToArray()),
                    Does.Contain("200"));
                Assert.That(payload, Does.Contain("echo:oi-tunel"));
            });
        }
        finally
        {
            echo.Stop();
            await echoTask;
        }
    }

    /// <summary>CONNECT para porta sem serviço (domínio permitido) →
    /// 502 Bad Gateway (upstream inalcançável).</summary>
    [Test]
    public async Task Proxy_ConnectSemUpstream_502()
    {
        await using var proxy = SandboxProxyServer.Start([]);
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", proxy.Port);
        var stream = client.GetStream();
        // Porta efêmera sem listener — connect falha na hora.
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "CONNECT localhost:1 HTTP/1.1\r\n\r\n"));

        var response = await ReadAllAsync(stream);

        Assert.That(response, Does.Contain("502"));
    }

    /// <summary>Primeira linha mal formada → conexão fechada sem resposta
    /// e sem registro de host negado.</summary>
    [Test]
    public async Task Proxy_RequestMalFormado_FechaSemDeny()
    {
        await using var proxy = SandboxProxyServer.Start([]);
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", proxy.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GARBAGE\r\n\r\n"));

        var response = await ReadAllAsync(stream);

        Assert.Multiple(() =>
        {
            Assert.That(response, Is.Empty);
            Assert.That(proxy.DeniedHosts(), Is.Empty);
        });
    }

    private static async Task<string> ReadAllAsync(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var buf = new byte[1024];
        stream.ReadTimeout = 5000;
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buf)) > 0)
            {
                sb.Append(Encoding.ASCII.GetString(buf[..n]));
            }
        }
        catch (IOException)
        {
            // timeout/fim de stream — devolve o acumulado.
        }
        return sb.ToString();
    }

    // ---------- BwrapBuilder.Build (RF-001) ----------

    /// <summary>Sem rede habilitada o psi usa <c>--unshare-net</c>; com
    /// proxy injeta HTTP(S)_PROXY; deny-read de arquivo vira
    /// <c>/dev/null</c> bind e de diretório vira <c>--tmpfs</c>; env entra
    /// como <c>--setenv</c>.</summary>
    [Test]
    public void Build_VariantesDeRedeEDenyReads()
    {
        var ws = Path.Join(Path.GetTempPath(), $"bwrap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(Path.Join(ws, "secretdir"));
        File.WriteAllText(Path.Join(ws, "segredo.txt"), "x");
        try
        {
            var scope = new SandboxScope(
                [ws], ["segredo.txt", "secretdir"], ["*.ok.com"], [], false);
            var psi = BwrapBuilder.Build("/fake/bwrap", "echo oi", ws,
                scope, proxyPort: null,
                env: new Dictionary<string, string> { ["K"] = "v" });
            var args = psi.ArgumentList;
            var joined = string.Join(' ', args);

            Assert.Multiple(() =>
            {
                Assert.That(psi.FileName, Is.EqualTo("/fake/bwrap"));
                Assert.That(joined, Does.Contain("--unshare-net"));
                Assert.That(joined, Does.Contain("--die-with-parent"));
                Assert.That(joined, Does.Contain("--bind"));
                Assert.That(joined,
                    Does.Contain($"--ro-bind /dev/null {Path.Join(ws, "segredo.txt")}"));
                Assert.That(joined,
                    Does.Contain($"--tmpfs {Path.Join(ws, "secretdir")}"));
                Assert.That(joined, Does.Contain("--setenv K v"));
                Assert.That(args[args.Count - 3], Is.EqualTo("/bin/sh"));
                Assert.That(args[args.Count - 2], Is.EqualTo("-c"));
                Assert.That(args[args.Count - 1], Is.EqualTo("echo oi"));
            });

            var scopeNet = scope with { NetworkEnabled = true };
            var psiNet = BwrapBuilder.Build("/fake/bwrap", "x", ws,
                scopeNet, proxyPort: 9999, env: null);
            var joinedNet = string.Join(' ', psiNet.ArgumentList);
            Assert.Multiple(() =>
            {
                Assert.That(joinedNet, Does.Not.Contain("--unshare-net"));
                Assert.That(joinedNet,
                    Does.Contain("--setenv HTTP_PROXY http://127.0.0.1:9999"));
                Assert.That(joinedNet, Does.Contain("NO_PROXY"));
            });
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    // ---------- SandboxedProcessRunner (RF-006) ----------

    /// <summary>Sem bwrap no ambiente → modo degradado: comando roda
    /// direto, <c>UsedSandbox=false</c> e o footer avisa.</summary>
    [Test]
    public async Task RunAsync_SemBwrap_FallbackDegradado()
    {
        if (BwrapBuilder.FindBwrap() is not null)
        {
            Assert.Ignore("bwrap presente no ambiente — path real cobre o teste do fake.");
        }
        var outcome = await SandboxedProcessRunner.RunAsync(
            "echo oi-degradado", Path.GetTempPath(), "sess-x",
            new SandboxScope([], [], [], [], false),
            TimeSpan.FromSeconds(10));

        Assert.Multiple(() =>
        {
            Assert.That(outcome.UsedSandbox, Is.False);
            Assert.That(outcome.Outcome.Output, Does.Contain("oi-degradado"));
            Assert.That(outcome.FooterText(), Does.Contain("bwrap indisponível"));
        });
    }

    /// <summary>Com um bwrap fake no PATH (script que exec o comando
    /// após <c>--</c>), o runner monta o psi sandboxed e reporta
    /// <c>UsedSandbox=true</c> — cobre o caminho completo bwrap.</summary>
    [Test]
    public async Task RunAsync_BwrapFake_PathSandboxado()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("bwrap fake depende de shell script — só Linux.");
            return;
        }
        if (BwrapBuilder.FindBwrap() is not null)
        {
            Assert.Ignore("bwrap real instalado — o fake competiria com ele.");
            return;
        }
        var dir = Path.Join(Path.GetTempPath(), $"fakebin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var bwrap = Path.Join(dir, "bwrap");
        await File.WriteAllTextAsync(bwrap,
            "#!/bin/sh\nwhile [ \"$1\" != \"--\" ]; do shift; done\nshift\nexec \"$@\"\n");
        File.SetUnixFileMode(bwrap,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH",
                $"{dir}:{originalPath}");
            Assert.That(BwrapBuilder.FindBwrap(), Is.EqualTo(bwrap),
                "o fake precisa ser encontrado no PATH");

            var outcome = await SandboxedProcessRunner.RunAsync(
                "echo oi-sandbox", Path.GetTempPath(), "sess-y",
                new SandboxScope([], [], [], [], false),
                TimeSpan.FromSeconds(10));

            Assert.Multiple(() =>
            {
                Assert.That(outcome.UsedSandbox, Is.True);
                Assert.That(outcome.Outcome.ExitCode, Is.EqualTo(0));
                Assert.That(outcome.Outcome.Output, Does.Contain("oi-sandbox"));
                Assert.That(outcome.FooterText(), Is.Empty);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary><see cref="SandboxCapabilities.Probe"/> reflete o
    /// ambiente: sem bwrap → degraded; o footer agrega deny-list.</summary>
    [Test]
    public void CapabilitiesEFooterText_Variantes()
    {
        var caps = SandboxCapabilities.Probe();
        Assert.Multiple(() =>
        {
            Assert.That(caps.Mode, Is.EqualTo(
                caps.Bwrap ? "available" : "degraded"));
            Assert.That(caps.Seccomp, Is.EqualTo(caps.Bwrap));
            Assert.That(caps.Proxy, Is.True);
        });

        var outcome = new SandboxedOutcome(
            new ProcessOutcome(0, "", false, false),
            UsedSandbox: true,
            DeniedDomains: ["evil.com"]);
        Assert.That(outcome.FooterText(),
            Does.Contain("sandbox.network.denied").And.Contain("evil.com"));
    }
}
