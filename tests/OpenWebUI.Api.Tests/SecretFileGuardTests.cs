using NUnit.Framework;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261123-secret-file-guard: arquivos de credenciais são
/// bloqueados nas tools de arquivo e no staging do git.
/// </summary>
[TestFixture]
public sealed class SecretFileGuardTests
{
    private string _workdir = null!;

    [SetUp]
    public void SetUp()
    {
        _workdir = Directory.CreateTempSubdirectory("secret-guard").FullName;
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_workdir, recursive: true); } catch { /* best-effort */ }
    }

    // ---------- IsSecretPath ----------

    [Test]
    public void ArquivosComuns_NaoSaoSecretos()
    {
        foreach (var path in new[] { "src/Program.cs", "README.md", ".gitignore",
            "appsettings.json", "package.json", "Dockerfile", "dir/sub/file.txt" })
        {
            Assert.That(SecretPathGuard.IsSecretPath(path), Is.False, path);
        }
    }

    [Test]
    public void EnvEChaves_SaoSecretos()
    {
        foreach (var path in new[] { ".env", ".env.local", ".env.production",
            "deploy/.env", "certs/server.pem", "keys/private.key",
            "store/keys.p12", "app.pfx", "vault.keystore" })
        {
            Assert.That(SecretPathGuard.IsSecretPath(path), Is.True, path);
        }
    }

    [Test]
    public void SshECloudDirs_SaoSecretosEmQualquerNivel()
    {
        foreach (var path in new[] { ".ssh/id_rsa", "sub/.ssh/config",
            ".aws/credentials", ".gnupg/secring.gpg", ".docker/config.json" })
        {
            Assert.That(SecretPathGuard.IsSecretPath(path), Is.True, path);
        }
    }

    [Test]
    public void WebuiDb_ESecretosNomeados_SaoSecretos()
    {
        foreach (var path in new[] { "webui.db", ".netrc", "id_ed25519",
            "service-account.json", "secrets.json", ".npmrc" })
        {
            Assert.That(SecretPathGuard.IsSecretPath(path), Is.True, path);
        }
    }

    [Test]
    public void CaseInsensitive()
    {
        Assert.That(SecretPathGuard.IsSecretPath("Dir/.ENV"), Is.True);
    }

    // ---------- ResolveInsideOrExternal ----------

    [Test]
    public void Resolve_BloqueiaEnvNoWorkspace()
    {
        File.WriteAllText(Path.Combine(_workdir, ".env"), "SECRET=x");
        var full = WorkspaceFiles.ResolveInsideOrExternal(
            _workdir, ".env", null, null, out var error);
        Assert.Multiple(() =>
        {
            Assert.That(full, Is.Null);
            Assert.That(error, Does.Contain("credenciais"));
        });
    }

    [Test]
    public void Resolve_BloqueiaArquivoEmDirSecreto()
    {
        Directory.CreateDirectory(Path.Combine(_workdir, ".ssh"));
        File.WriteAllText(Path.Combine(_workdir, ".ssh", "id_rsa"), "KEY");
        var full = WorkspaceFiles.ResolveInsideOrExternal(
            _workdir, ".ssh/id_rsa", null, null, out var error);
        Assert.Multiple(() =>
        {
            Assert.That(full, Is.Null);
            Assert.That(error, Does.Contain("credenciais"));
        });
    }

    [Test]
    public void Resolve_PermiteArquivoComum()
    {
        File.WriteAllText(Path.Combine(_workdir, "a.txt"), "ok");
        var full = WorkspaceFiles.ResolveInsideOrExternal(
            _workdir, "a.txt", null, null, out var error);
        Assert.Multiple(() =>
        {
            Assert.That(full, Is.Not.Null);
            Assert.That(error, Is.Empty);
        });
    }

    // ---------- Git staging check ----------

    private static void Git(string workdir, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(10_000);
    }

    [Test]
    public async Task GitCommit_ComEnvStaged_Bloqueado()
    {
        Git(_workdir, "init");
        File.WriteAllText(Path.Combine(_workdir, ".env"), "SECRET=x");
        Git(_workdir, "add .env");
        var result = await ShellExecBuiltinTool.GitSecretStagingCheckAsync(
            "git commit -m x", _workdir, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Text, Does.Contain("staged"));
        });
    }

    [Test]
    public async Task GitAdd_EnvLiteral_Bloqueado()
    {
        Git(_workdir, "init");
        var result = await ShellExecBuiltinTool.GitSecretStagingCheckAsync(
            "git add .env", _workdir, CancellationToken.None);
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task GitStatus_NaoVerifica()
    {
        var result = await ShellExecBuiltinTool.GitSecretStagingCheckAsync(
            "git status", _workdir, CancellationToken.None);
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GitAdd_ArquivoComum_Liberado()
    {
        Git(_workdir, "init");
        var result = await ShellExecBuiltinTool.GitSecretStagingCheckAsync(
            "git add README.md", _workdir, CancellationToken.None);
        Assert.That(result, Is.Null);
    }

    // ---------- Detector de credencial literal ----------

    [Test]
    public void ContainsSecret_DetectaLiteralNosArgs()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SecretScrubber.ContainsSecret(
                """{"command":"export KEY=sk-ant-api03-abcdefghijklmnopqrstuvwxyz"}"""), Is.True);
            Assert.That(SecretScrubber.ContainsSecret(
                """{"path":"src/app.cs"}"""), Is.False);
        });
    }
}
