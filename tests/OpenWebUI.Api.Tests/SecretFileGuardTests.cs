using NUnit.Framework;
using OpenWebUI.Infrastructure.ChatTools;

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
