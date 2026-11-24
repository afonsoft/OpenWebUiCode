using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.SystemPrompt;
using OpenWebUI.Api.DevPipeline;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261124-autonomous-delivery: guard de branch protegida no
/// shell_exec (RF-001), auto-branch + gh PR no dev_pipeline (RF-002) e
/// diretivas do prompt de entrega (RF-003/AC-6).
/// </summary>
[TestFixture]
[NonParallelizable]
public class AutonomousDeliveryTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-autodel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------- RF-001: ProtectedBranchGuard (puro) ----------

    private static readonly ISet<string> Protected =
        ProtectedBranchGuard.ParseSet(null);

    [Test]
    public void ParseSet_Null_Cai_No_Default()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Protected.Contains("main"), Is.True);
            Assert.That(Protected.Contains("master"), Is.True);
            Assert.That(Protected.Contains("MAIN"), Is.True); // case-insensitive
        });
    }

    [Test]
    public void ParseSet_Csv_Customizado_Substitui_Default()
    {
        var set = ProtectedBranchGuard.ParseSet("trunk, release ");
        Assert.Multiple(() =>
        {
            Assert.That(set.Contains("trunk"), Is.True);
            Assert.That(set.Contains("main"), Is.False); // AC-5
        });
    }

    [Test]
    public void Check_Commit_Na_Main_Recusa()
    {
        var violation = ProtectedBranchGuard.Check(
            "git commit -m x", "main", Protected);
        Assert.That(violation, Does.Contain("checkout -b feature/"));
    }

    [Test]
    public void Check_Commit_Em_Feature_Libera() =>
        Assert.That(ProtectedBranchGuard.Check(
            "git commit -m x", "feature/x", Protected), Is.Null);

    [Test]
    public void Check_Push_Origin_Main_Recusa_Mesmo_Em_Feature()
    {
        // AC-2: refspec explícito protegido bloqueia de qualquer branch.
        Assert.Multiple(() =>
        {
            Assert.That(ProtectedBranchGuard.Check(
                "git push origin main", "feature/x", Protected),
                Is.Not.Null);
            Assert.That(ProtectedBranchGuard.Check(
                "git push origin HEAD:main", "feature/x", Protected),
                Is.Not.Null);
            Assert.That(ProtectedBranchGuard.Check(
                "git push origin :main", "feature/x", Protected),
                Is.Not.Null);
        });
    }

    [Test]
    public void Check_Push_Em_Feature_Libera() =>
        Assert.That(ProtectedBranchGuard.Check(
            "git push -u origin feature/x", "feature/x", Protected), Is.Null);

    [Test]
    public void Check_Push_Bare_Na_Main_Recusa() =>
        Assert.That(ProtectedBranchGuard.Check(
            "git push", "main", Protected), Is.Not.Null);

    [Test]
    public void Check_Preparacao_Na_Main_Libera()
    {
        // AC-3: checkout -b é a correção, não pode ser bloqueado.
        Assert.Multiple(() =>
        {
            Assert.That(ProtectedBranchGuard.Check(
                "git checkout -b feature/x", "main", Protected), Is.Null);
            Assert.That(ProtectedBranchGuard.Check(
                "git status", "main", Protected), Is.Null);
            Assert.That(ProtectedBranchGuard.Check(
                "git add -A", "main", Protected), Is.Null);
        });
    }

    [Test]
    public void Check_Comando_Encadeado_Pega_Segmento_Git()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProtectedBranchGuard.Check(
                "cd src && git commit -m x", "main", Protected),
                Is.Not.Null);
            Assert.That(ProtectedBranchGuard.Check(
                "FOO=1 git merge feature/x", "master", Protected),
                Is.Not.Null);
        });
    }

    [Test]
    public void Check_Merge_Rebase_Na_Main_Recusa()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProtectedBranchGuard.Check(
                "git merge feature/x", "main", Protected), Is.Not.Null);
            Assert.That(ProtectedBranchGuard.Check(
                "git rebase main", "main", Protected), Is.Not.Null);
        });
    }

    // ---------- RF-001: guard no shell_exec (git real) ----------

    [Test]
    public async Task ShellGuard_Commit_Na_Main_Recusado()
    {
        RequireGit();
        var dir = NewRepo("main");
        var refusal = await ShellExecBuiltinTool.GitProtectedBranchCheckAsync(
            "git commit -m x", dir, null, CancellationToken.None);
        Assert.That(refusal, Is.Not.Null);
        Assert.That(refusal!.Text, Does.Contain("protegida"));
    }

    [Test]
    public async Task ShellGuard_Commit_Em_Feature_Liberado()
    {
        RequireGit();
        var dir = NewRepo("main");
        Git(dir, "checkout", "-b", "feature/x");
        var refusal = await ShellExecBuiltinTool.GitProtectedBranchCheckAsync(
            "git commit -m x", dir, null, CancellationToken.None);
        Assert.That(refusal, Is.Null);
    }

    [Test]
    public async Task ShellGuard_Fora_De_Repo_Libera()
    {
        var refusal = await ShellExecBuiltinTool.GitProtectedBranchCheckAsync(
            "git commit -m x", _root, null, CancellationToken.None);
        Assert.That(refusal, Is.Null);
    }

    [Test]
    public async Task ShellGuard_Kv_Customizado_Protege_Trunk()
    {
        RequireGit();
        var dir = NewRepo("trunk");
        var refusal = await ShellExecBuiltinTool.GitProtectedBranchCheckAsync(
            "git commit -m x", dir, "trunk", CancellationToken.None);
        Assert.That(refusal, Is.Not.Null);
    }

    // ---------- RF-002: EnsureWorkBranchAsync / TryCreatePrAsync ----------

    [Test]
    public async Task EnsureWorkBranch_Na_Main_Cria_Feature()
    {
        RequireGit();
        var dir = NewRepo("main");
        var created = await WorktreeService.EnsureWorkBranchAsync(
            dir, "minha-tarefa", Protected, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(created, Does.StartWith("feature/agent-"));
            Assert.That(created, Does.Contain("minha-tarefa"));
            Assert.That(CurrentBranch(dir), Is.EqualTo(created));
        });
    }

    [Test]
    public async Task EnsureWorkBranch_Em_Feature_Nao_Mexe()
    {
        RequireGit();
        var dir = NewRepo("main");
        Git(dir, "checkout", "-b", "feature/existente");
        var created = await WorktreeService.EnsureWorkBranchAsync(
            dir, "x", Protected, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(created, Is.Null);
            Assert.That(CurrentBranch(dir), Is.EqualTo("feature/existente"));
        });
    }

    [Test]
    public async Task TryCreatePr_Remoto_Nao_Github_Pula()
    {
        RequireGit();
        var dir = NewRepo("main");
        Git(dir, "remote", "add", "origin", "https://gitlab.com/x/y.git");
        var url = await WorktreeService.TryCreatePrAsync(
            dir, new Dictionary<string, string> { ["GH_TOKEN"] = "t" },
            CancellationToken.None,
            runner: (_, _) => Task.FromResult((0, "https://x", "")));
        Assert.That(url, Is.Null);
    }

    [Test]
    public async Task TryCreatePr_Sem_GhToken_Pula()
    {
        RequireGit();
        var dir = NewRepo("main");
        Git(dir, "remote", "add", "origin", "https://github.com/x/y.git");
        var url = await WorktreeService.TryCreatePrAsync(
            dir, new Dictionary<string, string>(), CancellationToken.None);
        Assert.That(url, Is.Null);
    }

    [Test]
    public async Task TryCreatePr_Gh_Ok_Devolve_Url()
    {
        RequireGit();
        var dir = NewRepo("main");
        Git(dir, "remote", "add", "origin", "https://github.com/x/y.git");
        var url = await WorktreeService.TryCreatePrAsync(
            dir, new Dictionary<string, string> { ["GH_TOKEN"] = "t" },
            CancellationToken.None,
            runner: (_, _) => Task.FromResult(
                (0, "https://github.com/x/y/pull/7\n", "")));
        Assert.That(url, Is.EqualTo("https://github.com/x/y/pull/7"));
    }

    [Test]
    public async Task TryCreatePr_Gh_Falha_Devolve_Null()
    {
        RequireGit();
        var dir = NewRepo("main");
        Git(dir, "remote", "add", "origin", "https://github.com/x/y.git");
        var url = await WorktreeService.TryCreatePrAsync(
            dir, new Dictionary<string, string> { ["GH_TOKEN"] = "t" },
            CancellationToken.None,
            runner: (_, _) => Task.FromResult((1, "", "already exists")));
        Assert.That(url, Is.Null);
    }

    [Test]
    public async Task TryCreatePr_Sem_Runner_Real_Path_Nao_Explode()
    {
        // Cobre o caminho real (psi + processo): sem gh válido/sem token
        // real o retorno é null — nunca exceção (PR não é fatal).
        RequireGit();
        var dir = NewRepo("main");
        Git(dir, "remote", "add", "origin", "https://github.com/x/y.git");
        var url = await WorktreeService.TryCreatePrAsync(
            dir, new Dictionary<string, string> { ["GH_TOKEN"] = "t" },
            CancellationToken.None);
        Assert.That(url, Is.Null);
    }

    // ---------- RF-002: Slugify ----------

    [Test]
    public void Slugify_Normaliza_Para_Kebab()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DevPipelineService.Slugify("feat(login): add OAuth"),
                Is.EqualTo("feat-login-add-oauth"));
            Assert.That(DevPipelineService.Slugify("  corrigir autenticação!!"),
                Is.EqualTo("corrigir-autentica-o"));
            Assert.That(DevPipelineService.Slugify(""),
                Is.EqualTo("task"));
            Assert.That(DevPipelineService.Slugify(
                new string('x', 60)).Length, Is.LessThanOrEqualTo(40));
        });
    }

    // ---------- AC-6: prompts carregam o ciclo de entrega ----------

    private static PromptContext Ctx(
        string? workdir = null, string? mode = null) => new()
    {
        Request = new ChatCompletionRequest(
            "m", [new ChatCompletionMessage("user", "oi")], Stream: true),
        User = new User { Id = "u1", Name = "U", Email = "u@c.local" },
        LastUserText = "oi",
        Model = "m",
        AgentMode = mode,
        Ct = CancellationToken.None,
        WorkdirFactory = _ => Task.FromResult(workdir ?? string.Empty),
    };

    [Test]
    public async Task Build_Prompt_Ordena_Branch_Push_Pr()
    {
        var sections = await new Completions.Contributors.AgentModeContributor()
            .ContributeAsync(Ctx(mode: "build"));
        Assert.That(sections, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(sections[0], Does.Contain("git checkout -b feature/"));
            Assert.That(sections[0], Does.Contain("git push -u origin HEAD"));
            Assert.That(sections[0], Does.Contain("gh pr create --fill"));
        });
    }

    [Test]
    public async Task Sdd_Prompt_Ordena_Branch_E_Pr()
    {
        var workdir = Path.Join(_root, "ws-sdd");
        Directory.CreateDirectory(Path.Join(workdir, ".specs"));
        File.WriteAllText(
            Path.Join(workdir, ".specs", "SPEC-x.md"), "x");
        var sections = await new Completions.Contributors.SddWorkflowContributor(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>()).Build())
            .ContributeAsync(Ctx(workdir));
        Assert.That(sections, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(sections[0], Does.Contain("feature/{slug}"));
            Assert.That(sections[0], Does.Contain("gh pr create --fill"));
        });
    }

    // ---------------- helpers ----------------

    private string NewRepo(string branch)
    {
        var dir = Path.Join(_root, $"repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-b", branch);
        Git(dir, "config", "user.email", "t@t");
        Git(dir, "config", "user.name", "t");
        File.WriteAllText(Path.Join(dir, "f.txt"), "x");
        Git(dir, "add", "-A");
        Git(dir, "commit", "-m", "init");
        return dir;
    }

    private static string CurrentBranch(string dir)
    {
        var psi = new ProcessStartInfo("git", "rev-parse --abbrev-ref HEAD")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15000);
        return output.Trim();
    }

    private static void Git(string workdir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var process = Process.Start(psi);
        process!.WaitForExit(15000);
        Assert.That(process.ExitCode, Is.EqualTo(0),
            $"git {string.Join(' ', args)} falhou: {process.StandardError.ReadToEnd()}");
    }

    private static void RequireGit()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            p!.WaitForExit(5000);
            if (p.ExitCode != 0)
            {
                Assert.Ignore("git indisponível neste ambiente.");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Ignore("git indisponível neste ambiente.");
        }
        catch (InvalidOperationException)
        {
            Assert.Ignore("git indisponível neste ambiente.");
        }
    }
}
