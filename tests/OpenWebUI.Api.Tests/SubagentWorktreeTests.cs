using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261010-subagent-worktree: runs filhas (force) criam worktree mesmo sem
/// o flag global; runs raiz seguem o flag; sem repo git → shared com warning.
/// </summary>
[TestFixture]
public class SubagentWorktreeTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Join(Path.GetTempPath(), $"owui-wt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { if (Directory.Exists(_dir)) { Directory.Delete(_dir, true); } }
        catch { /* worktree metadata pode segurar — ignore */ }
    }

    private WorktreeService NewService() =>
        new(new StubEnv(_dir),
            new ConfigurationBuilder().Build(),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WorktreeService>.Instance);

    private string NewGitRepo()
    {
        var repo = Path.Join(_dir, $"repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        Run(repo, "init");
        Run(repo, "config", "user.email", "t@t.local");
        Run(repo, "config", "user.name", "t");
        File.WriteAllText(Path.Combine(repo, "f.txt"), "x");
        Run(repo, "add", ".");
        Run(repo, "commit", "-m", "init");
        return repo;
    }

    private static void Run(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = cwd,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(30_000);
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {args[0]} falhou: {p.StandardError.ReadToEnd()}");
        }
    }

    [Test]
    public async Task SemFlag_SemForce_Shared()
    {
        var (wt, warn) = await NewService().TryCreateForRunAsync(
            "u1", "r1", _dir, CancellationToken.None);
        Assert.That(wt, Is.Null);
        Assert.That(warn, Is.Null);
    }

    [Test]
    public async Task Force_CriaWorktreeMesmoSemFlag()
    {
        var repo = NewGitRepo();
        var svc = NewService();
        var (wt, warn) = await svc.TryCreateForRunAsync(
            "u1", "run-filha", repo, CancellationToken.None, force: true);
        Assert.That(warn, Is.Null);
        Assert.That(wt, Is.Not.Null);
        Assert.That(Directory.Exists(wt), Is.True);
        Assert.That(wt, Is.Not.EqualTo(repo));
        Assert.That(File.Exists(Path.Combine(wt!, "f.txt")), Is.True);
        await svc.RemoveAsync(repo, wt!, CancellationToken.None);
    }

    [Test]
    public async Task Force_SemRepoGit_SharedComWarning()
    {
        var (wt, warn) = await NewService().TryCreateForRunAsync(
            "u1", "r1", _dir, CancellationToken.None, force: true);
        Assert.That(wt, Is.Null);
        Assert.That(warn, Does.Contain("sem repo git"));
    }

    private sealed class StubEnv(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
