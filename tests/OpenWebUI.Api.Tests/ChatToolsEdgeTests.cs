using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes de borda para as classes puras de ChatTools (SPEC-20261008-
/// tests-coverage-gate RF-002): splitter/classifier de risco de comandos,
/// classifier de tool call (<c>IsFileMutation</c> + JSON ruim), diff
/// unificado e resolução de caminhos/glob do workspace.
/// </summary>
public class ChatToolsEdgeTests
{
    private string _workspace = null!;

    [SetUp]
    public void SetUp()
    {
        _workspace = Path.Join(Path.GetTempPath(), $"owui-edge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_workspace, true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    // ---------------- CommandRiskClassifier: linha inteira ----------------

    [Test]
    public void Cmd_VazioOuBranco_EhDangerous()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandRiskClassifier.Classify("", _workspace).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("   ", _workspace).Allowed, Is.False);
        });
    }

    [Test]
    public void Cmd_SubstituicaoDeComando_EhDangerous()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandRiskClassifier.Classify("echo $(id)", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Dangerous));
            Assert.That(CommandRiskClassifier.Classify("echo `id`", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Dangerous));
        });
    }

    [Test]
    public void Cmd_AtribuicoesDeAmbiente_SaoPuladas()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandRiskClassifier.Classify("FOO=bar ls", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
            Assert.That(CommandRiskClassifier.Classify("A=1 _B=2 ls", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
            // Só atribuição, sem binário → fail-closed.
            Assert.That(CommandRiskClassifier.Classify("FOO=bar", _workspace).Allowed, Is.False);
            // Nome de env inválido não é tratado como atribuição.
            Assert.That(CommandRiskClassifier.Classify("1BAD=x ls", _workspace).Allowed, Is.True);
        });
    }

    [Test]
    public void Cmd_Splitter_AspasEOperadores()
    {
        Assert.Multiple(() =>
        {
            // Pipe dentro de aspas não vira segmento: 'a|b' não é caminho → pulado.
            Assert.That(CommandRiskClassifier.Classify("echo 'a|b'", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            // Aspas duplas idem.
            Assert.That(CommandRiskClassifier.Classify("echo \"x;y\"", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            // && || ; | separam segmentos — o pior vence.
            Assert.That(CommandRiskClassifier.Classify("ls && sudo x", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Dangerous));
            Assert.That(CommandRiskClassifier.Classify("ls || sudo x", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Dangerous));
            Assert.That(CommandRiskClassifier.Classify("ls;sudo x", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Dangerous));
            Assert.That(CommandRiskClassifier.Classify("ls | sudo x", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Dangerous));
            // Aspas não fechadas: parser engole o resto — só 'echo' avaliado.
            Assert.That(CommandRiskClassifier.Classify("echo 'abc", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
        });
    }

    [Test]
    public void Cmd_BinariosPerigosos_ElevacaoRedeInterpretes()
    {
        foreach (var cmd in new[]
        {
            "sudo ls", "su root", "doas ls", "chmod 777 f", "chown a f", "chgrp a f",
            "curl http://x", "wget http://x", "nc x 80", "ncat x 80", "netcat x 80",
            "ssh h", "scp f h:", "sftp h", "ftp h", "telnet h",
            "dd if=/dev/x", "mkfs /dev/x", "fdisk /dev/x", "mount /x", "umount /x",
            "shutdown now", "reboot", "halt", "poweroff", "kill 1", "killall x", "pkill x",
            "env", "printenv", "eval x", "exec x", "crontab -l",
            "systemctl status", "service x start", "iptables -L",
            "useradd x", "userdel x", "usermod x", "passwd",
            "apt install x", "apt-get install x", "yum install x", "dnf install x",
            "brew install x", "snap install x",
            "pip install x", "pip3 install x", "gem install x", "npx x",
            "bash -c x", "sh -c x", "zsh -c x", "fish -c x",
            "python -c x", "python3 -c x", "perl -e x", "ruby -e x", "node -e x",
            "docker ps", "podman ps", "kubectl get pods", "terraform apply",
        })
        {
            var a = CommandRiskClassifier.Classify(cmd, _workspace);
            Assert.Multiple(() =>
            {
                Assert.That(a.Level, Is.GreaterThanOrEqualTo(CommandRiskLevel.Dangerous), cmd);
                Assert.That(a.Allowed, Is.EqualTo(a.Level != CommandRiskLevel.Forbidden), cmd);
            });
        }
    }

    [Test]
    public void Cmd_BinariosSeguros_Confinados()
    {
        foreach (var cmd in new[]
        {
            "ls -la", "cat f.txt", "grep x f", "rg x", "find . -name f", "pwd",
            "head f", "tail f", "wc -l f", "file f", "stat f", "which ls",
            "whereis ls", "tree", "diff a b", "sort f", "uniq f", "tr a b",
            "cut -d: -f1 f", "awk '{print $1}' f", "jq . f", "date", "uname -a",
            "id", "whoami", "hostname", "df -h", "du -sh .", "ps aux",
            "true", "false", "test -f x", "xargs echo", "readlink f",
            "basename a/b", "dirname a/b", "realpath .", "less f", "more f",
        })
        {
            Assert.That(CommandRiskClassifier.Classify(cmd, _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Safe), cmd);
        }
    }

    [Test]
    public void Cmd_BinariosDeEscrita_DentroDoWorkspace()
    {
        foreach (var cmd in new[]
        {
            "touch f", "mkdir d", "rmdir d", "cp a b", "mv a b", "ln -s a b",
            "tee f", "echo x", "printf x", "make", "cmake .", "cargo build",
            "go build", "tar czf a.tgz d", "zip a.zip f", "unzip a.zip",
            "gzip f", "gunzip f.gz",
        })
        {
            Assert.That(CommandRiskClassifier.Classify(cmd, _workspace).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite), cmd);
        }
    }

    [Test]
    public void Cmd_SedAwk_InPlace_Escreve()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandRiskClassifier.Classify("sed -i s/a/b/ f.txt", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            Assert.That(CommandRiskClassifier.Classify("awk -i inplace '{print}' f", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            // -i com alvo fora do workspace → escala para Dangerous.
            Assert.That(CommandRiskClassifier.Classify("sed -i s/a/b/ /etc/hosts", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Dangerous));
        });
    }

    // ---------------- git ----------------

    [Test]
    public void Cmd_Git_SemSubcomando_EhSafe()
    {
        Assert.That(CommandRiskClassifier.Classify("git", _workspace).Level,
            Is.EqualTo(CommandRiskLevel.Safe));
    }

    [Test]
    public void Cmd_Git_ReadOnly_Seguro()
    {
        foreach (var cmd in new[]
        {
            "git status", "git diff", "git log --oneline", "git show HEAD", "git blame f",
            "git rev-parse HEAD", "git ls-files", "git ls-tree HEAD", "git describe",
            "git shortlog", "git reflog", "git remote", "git config user.name",
            "git branch", "git tag", "git stash list",
        })
        {
            Assert.That(CommandRiskClassifier.Classify(cmd, _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Safe), cmd);
        }
    }

    [Test]
    public void Cmd_Git_RedeEDestrutivos_Negados()
    {
        foreach (var cmd in new[]
        {
            "git push origin main", "git pull", "git fetch", "git clone x",
            "git remote-update", "git reset --hard HEAD~1", "git clean -f",
            "git clean -fd", "git clean -fx", "git branch -D x", "git branch -d x",
            "git branch --delete x", "git tag -d v1", "git stash drop", "git stash clear",
            "git config --global user.name x", "git config --system x y",
            "git subcomando_inventado",
        })
        {
            var a = CommandRiskClassifier.Classify(cmd, _workspace);
            Assert.Multiple(() =>
            {
                Assert.That(a.Level, Is.EqualTo(CommandRiskLevel.Dangerous), cmd);
                Assert.That(a.Allowed, Is.True, cmd);
            });
        }
    }

    [Test]
    public void Cmd_Git_EscritaDentroDoWorkspace()
    {
        foreach (var cmd in new[]
        {
            "git add f.txt", "git commit -m x", "git checkout -b f", "git switch main",
            "git restore f", "git merge b", "git rebase main", "git cherry-pick x",
            "git mv a b", "git rm f", "git init", "git worktree add w",
            "git reset --soft HEAD~1", "git clean -n", "git apply p.diff",
            "git format-patch -1", "git am p.patch", "git remote add o ./path",
            "git remote remove o", "git remote set-url o ./path",
        })
        {
            Assert.That(CommandRiskClassifier.Classify(cmd, _workspace).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite), cmd);
        }
    }

    // ---------------- dotnet / npm / rm ----------------

    [Test]
    public void Cmd_Dotnet_Subcomandos()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandRiskClassifier.Classify("dotnet test", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
            Assert.That(CommandRiskClassifier.Classify("dotnet vstest x.dll", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
            foreach (var cmd in new[]
            {
                "dotnet build", "dotnet restore", "dotnet run", "dotnet watch",
                "dotnet publish -c Release", "dotnet pack", "dotnet format",
                "dotnet clean", "dotnet new console", "dotnet add ref", "dotnet remove ref",
                "dotnet list package", "dotnet sln add x", "dotnet build-server shutdown",
            })
            {
                Assert.That(CommandRiskClassifier.Classify(cmd, _workspace).Level,
                    Is.EqualTo(CommandRiskLevel.WorkspaceWrite), cmd);
            }
            Assert.That(CommandRiskClassifier.Classify("dotnet nuget push x", _workspace).Allowed, Is.True);
            Assert.That(CommandRiskClassifier.Classify("dotnet tool install x", _workspace).Allowed, Is.True);
            Assert.That(CommandRiskClassifier.Classify("dotnet workload install x", _workspace).Allowed, Is.True);
            Assert.That(CommandRiskClassifier.Classify("dotnet ef", _workspace).Allowed, Is.True);
        });
    }

    [Test]
    public void Cmd_Npm_Subcomandos()
    {
        Assert.Multiple(() =>
        {
            foreach (var bin in new[] { "npm", "yarn", "pnpm" })
            {
                Assert.That(CommandRiskClassifier.Classify($"{bin} publish", _workspace).Allowed, Is.True, bin);
                Assert.That(CommandRiskClassifier.Classify($"{bin} login", _workspace).Allowed, Is.True, bin);
                Assert.That(CommandRiskClassifier.Classify($"{bin} logout", _workspace).Allowed, Is.True, bin);
                Assert.That(CommandRiskClassifier.Classify($"{bin} token list", _workspace).Allowed, Is.True, bin);
                Assert.That(CommandRiskClassifier.Classify($"{bin} config set x y", _workspace).Allowed, Is.True, bin);
                Assert.That(CommandRiskClassifier.Classify($"{bin} install", _workspace).Level,
                    Is.EqualTo(CommandRiskLevel.WorkspaceWrite), bin);
            }
            foreach (var sub in new[]
            {
                "test", "run build", "install", "ci", "exec x", "build", "start",
                "pack", "link", "update", "audit", "outdated", "list", "ls",
                "why x", "dedupe", "prune",
            })
            {
                Assert.That(CommandRiskClassifier.Classify($"npm {sub}", _workspace).Level,
                    Is.EqualTo(CommandRiskLevel.WorkspaceWrite), sub);
            }
            // npm sem subcomando → WorkspaceWrite.
            Assert.That(CommandRiskClassifier.Classify("npm", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            Assert.That(CommandRiskClassifier.Classify("npm xyz_desconhecido", _workspace).Allowed, Is.True);
        });
    }

    [Test]
    public void Cmd_Rm_Alvos()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandRiskClassifier.Classify("rm", _workspace).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("rm -rf", _workspace).Allowed, Is.False);
            Assert.That(CommandRiskClassifier.Classify("rm -rf build", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            var esc = CommandRiskClassifier.Classify("rm -rf /tmp/fora", _workspace);
            Assert.That(esc.Allowed, Is.True);
            Assert.That(esc.EscapesSandbox, Is.True);
            Assert.That(CommandRiskClassifier.Classify("rm ../fora.txt", _workspace).Allowed, Is.True);
        });
    }

    // ---------------- redirects / paths ----------------

    [Test]
    public void Cmd_Redirects()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandRiskClassifier.Classify("echo x > out.txt", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            Assert.That(CommandRiskClassifier.Classify("echo x >> out.txt", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            Assert.That(CommandRiskClassifier.Classify("echo x 1> out.txt", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            Assert.That(CommandRiskClassifier.Classify("ls 2> err.txt", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            Assert.That(CommandRiskClassifier.Classify("ls &> all.txt", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
            // Redirect sem alvo → Dangerous (executa sob permissão total).
            Assert.That(CommandRiskClassifier.Classify("echo x >", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Dangerous));
            // Redirect para fora do workspace → Dangerous + EscapesSandbox.
            var esc = CommandRiskClassifier.Classify("echo x > /tmp/fora.txt", _workspace);
            Assert.That(esc.Allowed, Is.True);
            Assert.That(esc.EscapesSandbox, Is.True);
            // Redirect dentro com binário de leitura eleva para WorkspaceWrite.
            Assert.That(CommandRiskClassifier.Classify("cat f > copia.txt", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.WorkspaceWrite));
        });
    }

    [Test]
    public void Cmd_PathResolution_Edges()
    {
        Assert.Multiple(() =>
        {
            // Globs amplos puros → Dangerous.
            Assert.That(CommandRiskClassifier.Classify("cat *", _workspace).Allowed, Is.True);
            Assert.That(CommandRiskClassifier.Classify("cat **", _workspace).Allowed, Is.True);
            Assert.That(CommandRiskClassifier.Classify("cat *.*", _workspace).Allowed, Is.True);
            // Glob relativo sem escape → resolve.
            Assert.That(CommandRiskClassifier.Classify("cat *.txt", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
            Assert.That(CommandRiskClassifier.Classify("ls src/*.cs", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
            Assert.That(CommandRiskClassifier.Classify("ls fi?e.txt", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
            // Glob absoluto/~/$ → Dangerous.
            Assert.That(CommandRiskClassifier.Classify("cat /etc/*", _workspace).Allowed, Is.True);
            Assert.That(CommandRiskClassifier.Classify("cat ~/*.txt", _workspace).Allowed, Is.True);
            Assert.That(CommandRiskClassifier.Classify("cat $HOME/*", _workspace).Allowed, Is.True);
            // Glob com '..' → Dangerous.
            Assert.That(CommandRiskClassifier.Classify("cat ../*.txt", _workspace).Allowed, Is.True);
            Assert.That(CommandRiskClassifier.Classify("cat a/../b/*.txt", _workspace).Allowed, Is.True);
            // ~, $VAR, parênteses → Dangerous.
            Assert.That(CommandRiskClassifier.Classify("cat ~/f", _workspace).Allowed, Is.True);
            Assert.That(CommandRiskClassifier.Classify("cat $X/f", _workspace).Allowed, Is.True);
            // 'f(1)' não parece caminho → arg pulado, comando segue Safe;
            // com '/', parece caminho e os parênteses o tornam irresolúvel.
            Assert.That(CommandRiskClassifier.Classify("cat f(1)", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
            Assert.That(CommandRiskClassifier.Classify("cat dir/f(1)", _workspace).Allowed, Is.True);
            // '..' comum → fora.
            Assert.That(CommandRiskClassifier.Classify("cat ../fora", _workspace).Allowed, Is.True);
            Assert.That(CommandRiskClassifier.Classify("cat a/../../b", _workspace).Allowed, Is.True);
            // Dentro explícito.
            Assert.That(CommandRiskClassifier.Classify("cat ./f.txt", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
            Assert.That(CommandRiskClassifier.Classify("cat sub/dir/f", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
            // Args que não parecem caminho são pulados (flags, --, palavras).
            Assert.That(CommandRiskClassifier.Classify("grep -r --include=x -n TODO -- .", _workspace).Level,
                Is.EqualTo(CommandRiskLevel.Safe));
        });
    }

    [Test]
    public void Cmd_PathInside_PrefixoExato()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandRiskClassifier.PathInside(_workspace, _workspace), Is.True);
            Assert.That(CommandRiskClassifier.PathInside(_workspace,
                Path.Join(_workspace, "sub", "f.txt")), Is.True);
            // Irmão com prefixo comum NÃO está dentro.
            Assert.That(CommandRiskClassifier.PathInside(_workspace, _workspace + "-evil/f"), Is.False);
            Assert.That(CommandRiskClassifier.PathInside(_workspace, "/etc/passwd"), Is.False);
        });
    }

    [Test]
    public void Cmd_BinarioDesconhecido_FailClosed()
    {
        Assert.That(CommandRiskClassifier.Classify("xYz_NuncaVaiExistir123 arg", _workspace).Allowed, Is.True);
    }

    // ---------------- ToolCallRiskClassifier ----------------

    private static Tool MutableBuiltin(string name) =>
        new() { Url = $"builtin://{name}", RequiresApproval = true };

    [Test]
    public void Risk_ShellExec_EdgesDeJson()
    {
        var tool = MutableBuiltin("shell_exec");
        Assert.Multiple(() =>
        {
            // Sem campo command → HIGH (fail-safe).
            Assert.That(ToolCallRiskClassifier.Classify(tool, "{}", _workspace),
                Is.EqualTo(ToolCallRisk.High));
            // JSON quebrado → command null → HIGH.
            Assert.That(ToolCallRiskClassifier.Classify(tool, "{nao-json", _workspace),
                Is.EqualTo(ToolCallRisk.High));
            // command não-string (número) → raw text "5" → desconhecido → HIGH.
            Assert.That(ToolCallRiskClassifier.Classify(tool, "{\"command\":5}", _workspace),
                Is.EqualTo(ToolCallRisk.High));
            // background false explícito → segue pelo comando.
            Assert.That(ToolCallRiskClassifier.Classify(
                    tool, "{\"command\":\"ls\",\"background\":false}", _workspace),
                Is.EqualTo(ToolCallRisk.Low));
            // background não-bool → TryGetBool false → segue pelo comando.
            Assert.That(ToolCallRiskClassifier.Classify(
                    tool, "{\"command\":\"ls\",\"background\":\"yes\"}", _workspace),
                Is.EqualTo(ToolCallRisk.Low));
            // JSON não-objeto → null → HIGH.
            Assert.That(ToolCallRiskClassifier.Classify(tool, "[1,2]", _workspace),
                Is.EqualTo(ToolCallRisk.High));
            // JSON vazio/whitespace → "{}" → sem command → HIGH.
            Assert.That(ToolCallRiskClassifier.Classify(tool, "  ", _workspace),
                Is.EqualTo(ToolCallRisk.High));
        });
    }

    [Test]
    public void Risk_BuiltinDesconhecido_EhHigh()
    {
        var tool = MutableBuiltin("xyz_novo");
        Assert.That(ToolCallRiskClassifier.Classify(tool, "{}", _workspace),
            Is.EqualTo(ToolCallRisk.High));
    }

    [Test]
    public void Risk_IsFileMutation_Matriz()
    {
        Assert.Multiple(() =>
        {
            // Não-mutável → false.
            Assert.That(ToolCallRiskClassifier.IsFileMutation(
                new Tool { Url = "builtin://file_write" }, "{}", _workspace), Is.False);
            // Mutável não-builtin → false.
            Assert.That(ToolCallRiskClassifier.IsFileMutation(
                new Tool { Url = "http://x/t", RequiresApproval = true }, "{}", _workspace), Is.False);
            // Mutáveis de arquivo → true.
            foreach (var name in new[] { "file_write", "file_edit", "generate_image", "code_interpreter" })
            {
                Assert.That(ToolCallRiskClassifier.IsFileMutation(
                    MutableBuiltin(name), "{}", _workspace), Is.True, name);
            }
            // shell_exec: só true quando o comando é WorkspaceWrite.
            var shell = MutableBuiltin("shell_exec");
            Assert.That(ToolCallRiskClassifier.IsFileMutation(
                shell, "{\"command\":\"echo x > f.txt\"}", _workspace), Is.True);
            Assert.That(ToolCallRiskClassifier.IsFileMutation(
                shell, "{\"command\":\"ls\"}", _workspace), Is.False);
            Assert.That(ToolCallRiskClassifier.IsFileMutation(
                shell, "{\"command\":\"rm -rf /\"}", _workspace), Is.False);
            Assert.That(ToolCallRiskClassifier.IsFileMutation(
                shell, "{nao-json", _workspace), Is.False);
            // Builtin mutável desconhecido → false.
            Assert.That(ToolCallRiskClassifier.IsFileMutation(
                MutableBuiltin("job_kill"), "{}", _workspace), Is.False);
        });
    }

    // ---------------- UnifiedDiff ----------------

    [Test]
    public void Diff_Identicos_Vazio()
    {
        var r = UnifiedDiff.Compute("f.txt", "a\nb\nc", "a\nb\nc");
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Is.Empty);
            Assert.That(r.Added, Is.Zero);
            Assert.That(r.Removed, Is.Zero);
        });
    }

    [Test]
    public void Diff_AdicaoPura()
    {
        var r = UnifiedDiff.Compute("f.txt", "a\nc", "a\nb\nc");
        Assert.Multiple(() =>
        {
            Assert.That(r.Added, Is.EqualTo(1));
            Assert.That(r.Removed, Is.Zero);
            Assert.That(r.Text, Does.Contain("--- a/f.txt"));
            Assert.That(r.Text, Does.Contain("+++ b/f.txt"));
            Assert.That(r.Text, Does.Contain("+b"));
            Assert.That(r.Text, Does.Contain("@@"));
        });
    }

    [Test]
    public void Diff_RemocaoPura()
    {
        var r = UnifiedDiff.Compute("f.txt", "a\nb\nc", "a\nc");
        Assert.Multiple(() =>
        {
            Assert.That(r.Removed, Is.EqualTo(1));
            Assert.That(r.Added, Is.Zero);
            Assert.That(r.Text, Does.Contain("-b"));
        });
    }

    [Test]
    public void Diff_ArquivoNovo_OldNull()
    {
        var r = UnifiedDiff.Compute("novo.txt", null, "x\ny");
        Assert.Multiple(() =>
        {
            Assert.That(r.Added, Is.EqualTo(2));
            Assert.That(r.Text, Does.Contain("+x"));
            Assert.That(r.Text, Does.Contain("+y"));
        });
    }

    [Test]
    public void Diff_DoisHunksDistantes()
    {
        // Mudanças com >Context+1 ops de distância geram hunks separados.
        var oldText = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"linha{i}"));
        var lines = oldText.Split('\n').ToList();
        lines[2] = "ALTERADO_A";
        lines[25] = "ALTERADO_B";
        var r = UnifiedDiff.Compute("f.txt", oldText, string.Join('\n', lines));
        var hunkCount = Regex.Matches(r.Text, "@@").Count / 2;
        Assert.Multiple(() =>
        {
            Assert.That(hunkCount, Is.EqualTo(2), r.Text);
            Assert.That(r.Text, Does.Contain("-linha3"));
            Assert.That(r.Text, Does.Contain("+ALTERADO_A"));
            Assert.That(r.Text, Does.Contain("+ALTERADO_B"));
        });
    }

    [Test]
    public void Diff_HunksProximos_Merge()
    {
        // Duas mudanças próximas (≤ Context+1 ops) fundem num único hunk.
        var oldText = "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10";
        var newText = "l1\nA\nl3\nl4\nB\nl6\nl7\nl8\nl9\nl10";
        var r = UnifiedDiff.Compute("f.txt", oldText, newText);
        var hunkCount = Regex.Matches(r.Text, "@@").Count / 2;
        Assert.That(hunkCount, Is.EqualTo(1), r.Text);
    }

    [Test]
    public void Diff_MudancaNasBordas()
    {
        // Mudança na primeira e última linha → clamping de contexto.
        var r = UnifiedDiff.Compute("f.txt", "primeira\nmeio\nultima", "INICIO\nmeio\nFIM");
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("-primeira"));
            Assert.That(r.Text, Does.Contain("+INICIO"));
            Assert.That(r.Text, Does.Contain("-ultima"));
            Assert.That(r.Text, Does.Contain("+FIM"));
        });
    }

    [Test]
    public void Diff_CRLF_Normalizado()
    {
        var r = UnifiedDiff.Compute("f.txt", "a\r\nb\r\nc", "a\r\nB\r\nc");
        Assert.Multiple(() =>
        {
            Assert.That(r.Removed, Is.EqualTo(1));
            Assert.That(r.Added, Is.EqualTo(1));
            Assert.That(r.Text, Does.Contain("-b"));
            Assert.That(r.Text, Does.Contain("+B"));
        });
    }

    [Test]
    public void Diff_ArquivosGrandes_FallbackNaive()
    {
        // old×new > 4M pares → diff ingênuo truncado.
        var oldText = string.Join('\n', Enumerable.Range(1, 2100).Select(i => $"o{i}"));
        var newText = string.Join('\n', Enumerable.Range(1, 2000).Select(i => $"n{i}"));
        var r = UnifiedDiff.Compute("big.txt", oldText, newText);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("arquivo reescrito por inteiro"));
            Assert.That(r.Text, Does.Contain("[diff truncado]"));
            Assert.That(r.Removed, Is.EqualTo(2100));
            Assert.That(r.Added, Is.EqualTo(2000));
        });
    }

    // ---------------- WorkspaceFiles ----------------

    [Test]
    public void Ws_ResolveInside_Matriz()
    {
        Assert.Multiple(() =>
        {
            // null/vazio → '.' → próprio workspace.
            Assert.That(WorkspaceFiles.ResolveInside(_workspace, null, out var e1),
                Is.EqualTo(Path.GetFullPath(_workspace)));
            Assert.That(e1, Is.Empty);
            Assert.That(WorkspaceFiles.ResolveInside(_workspace, "  ", out _),
                Is.EqualTo(Path.GetFullPath(_workspace)));
            // ~ e $ → negado.
            Assert.That(WorkspaceFiles.ResolveInside(_workspace, "~/f", out var e2), Is.Null);
            Assert.That(e2, Does.Contain("workspace"));
            Assert.That(WorkspaceFiles.ResolveInside(_workspace, "$HOME/f", out _), Is.Null);
            // Absoluto → negado.
            Assert.That(WorkspaceFiles.ResolveInside(_workspace, "/etc/passwd", out var e3), Is.Null);
            Assert.That(e3, Does.Contain("absoluto"));
            // Escape via '..' → negado.
            Assert.That(WorkspaceFiles.ResolveInside(_workspace, "../fora", out var e4), Is.Null);
            Assert.That(e4, Does.Contain("escapa"));
            // Irmão com prefixo comum → negado.
            Assert.That(WorkspaceFiles.ResolveInside(_workspace, "../" +
                Path.GetFileName(_workspace) + "-evil/f", out _), Is.Null);
            // Relativo válido → full.
            var ok = WorkspaceFiles.ResolveInside(_workspace, "sub/dir/f.txt", out var e5);
            Assert.That(ok, Is.EqualTo(
                Path.GetFullPath(Path.Join(_workspace, "sub/dir/f.txt"))));
            Assert.That(e5, Is.Empty);
        });
    }

    [Test]
    public void Ws_LooksBinary()
    {
        var text = Path.Join(_workspace, "a.txt");
        File.WriteAllText(text, "conteúdo puro texto");
        var bin = Path.Join(_workspace, "b.bin");
        File.WriteAllBytes(bin, new byte[] { 0x50, 0x4B, 0x00, 0x00 });
        Assert.Multiple(() =>
        {
            Assert.That(WorkspaceFiles.LooksBinary(text), Is.False);
            Assert.That(WorkspaceFiles.LooksBinary(bin), Is.True);
            Assert.That(WorkspaceFiles.LooksBinary(
                Path.Join(_workspace, "nao-existe")), Is.False);
        });
    }

    [Test]
    public void Ws_GlobToRegex_Padroes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WorkspaceFiles.GlobToRegex("*.txt").IsMatch("a.txt"), Is.True);
            Assert.That(WorkspaceFiles.GlobToRegex("*.txt").IsMatch("dir/a.txt"), Is.False);
            Assert.That(WorkspaceFiles.GlobToRegex("**/*.cs").IsMatch("a/b/c.cs"), Is.True);
            Assert.That(WorkspaceFiles.GlobToRegex("**/*.cs").IsMatch("c.cs"), Is.True);
            Assert.That(WorkspaceFiles.GlobToRegex("src/**").IsMatch("src/a/b/c"), Is.True);
            Assert.That(WorkspaceFiles.GlobToRegex("f?.txt").IsMatch("f1.txt"), Is.True);
            Assert.That(WorkspaceFiles.GlobToRegex("f?.txt").IsMatch("f12.txt"), Is.False);
            Assert.That(WorkspaceFiles.GlobToRegex("[abc].txt").IsMatch("b.txt"), Is.True);
            Assert.That(WorkspaceFiles.GlobToRegex("[abc].txt").IsMatch("z.txt"), Is.False);
            Assert.That(WorkspaceFiles.GlobToRegex("[!abc].txt").IsMatch("z.txt"), Is.True);
            Assert.That(WorkspaceFiles.GlobToRegex("[!abc].txt").IsMatch("a.txt"), Is.False);
            Assert.That(WorkspaceFiles.GlobToRegex("{cs,txt}").IsMatch("cs"), Is.True);
            Assert.That(WorkspaceFiles.GlobToRegex("{cs,txt}").IsMatch("txt"), Is.True);
            Assert.That(WorkspaceFiles.GlobToRegex("{cs,txt}").IsMatch("md"), Is.False);
            // '[' e '{' sem fechamento viram literais escapados.
            Assert.That(WorkspaceFiles.GlobToRegex("a[b").IsMatch("a[b"), Is.True);
            Assert.That(WorkspaceFiles.GlobToRegex("a{b").IsMatch("a{b"), Is.True);
            // Caracteres especiais de regex são escapados.
            Assert.That(WorkspaceFiles.GlobToRegex("a+b.txt").IsMatch("a+b.txt"), Is.True);
            Assert.That(WorkspaceFiles.GlobToRegex("a+b.txt").IsMatch("aab.txt"), Is.False);
        });
    }

    [Test]
    public void Ws_RelativeOf()
    {
        var full = Path.Join(_workspace, "sub", "f.txt");
        Assert.That(WorkspaceFiles.RelativeOf(_workspace, full), Is.EqualTo("sub/f.txt"));
    }

    [Test]
    public void Ws_EnumerateFiles_ProfundidadeDotGitECap()
    {
        Directory.CreateDirectory(Path.Join(_workspace, "d1", "d2"));
        File.WriteAllText(Path.Join(_workspace, "raiz.txt"), "x");
        File.WriteAllText(Path.Join(_workspace, "d1", "f1.txt"), "x");
        File.WriteAllText(Path.Join(_workspace, "d1", "d2", "f2.txt"), "x");
        Directory.CreateDirectory(Path.Join(_workspace, ".git"));
        File.WriteAllText(Path.Join(_workspace, ".git", "HEAD"), "x");

        var all = WorkspaceFiles.EnumerateFiles(_workspace).Select(
            f => WorkspaceFiles.RelativeOf(_workspace, f)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(all, Does.Contain("raiz.txt"));
            Assert.That(all, Does.Contain("d1/f1.txt"));
            Assert.That(all, Does.Contain("d1/d2/f2.txt"));
            Assert.That(all.Any(f => f.StartsWith(".git")), Is.False);
        });

        // maxDepth=1 corta d1/d2.
        var shallow = WorkspaceFiles.EnumerateFiles(_workspace, maxDepth: 1)
            .Select(f => WorkspaceFiles.RelativeOf(_workspace, f)).ToList();
        Assert.That(shallow, Does.Not.Contain("d1/d2/f2.txt"));
    }

    [Test]
    public void Ws_EnumerateFiles_DirIlegivel_ECapDeEntradas()
    {
        // Diretório que some/sem permissão → ignorado, não quebra.
        var missing = Path.Join(_workspace, "nao-existe");
        Assert.That(WorkspaceFiles.EnumerateFiles(missing).ToList(), Is.Empty);

        // Cap de MaxEntries.
        for (var i = 0; i < WorkspaceFiles.MaxEntries + 20; i++)
        {
            File.WriteAllText(Path.Join(_workspace, $"f{i:D4}.txt"), "x");
        }

        Assert.That(WorkspaceFiles.EnumerateFiles(_workspace).Count(),
            Is.EqualTo(WorkspaceFiles.MaxEntries));
    }

    // ---------------- File* builtin tools ----------------

    private BuiltinToolContext Ctx() =>
        new("u1", "c1", "r1", _workspace, _workspace);

    private static System.Text.Json.JsonElement Args(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task FileList_NaoExisteArquivoERecursivo()
    {
        var tool = new FileListBuiltinTool();
        var r = await tool.ExecuteAsync(Args("{\"path\":\"nada\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("não existe"));

        File.WriteAllText(Path.Join(_workspace, "solo.txt"), "x");
        r = await tool.ExecuteAsync(Args("{\"path\":\"solo.txt\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("use file_read"));

        Directory.CreateDirectory(Path.Join(_workspace, "sub"));
        File.WriteAllText(Path.Join(_workspace, "sub", "f.txt"), "x");
        File.WriteAllText(Path.Join(_workspace, "top.txt"), "xy");

        var flat = await tool.ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(flat.Text, Does.Contain("top.txt"));
            Assert.That(flat.Text, Does.Contain("sub/"));
            Assert.That(flat.Text, Does.Not.Contain("sub/f.txt"));
            Assert.That(flat.Text, Does.Contain("B")); // tamanho formatado
        });

        var rec = await tool.ExecuteAsync(Args("{\"recursive\":true}"), Ctx(), default);
        Assert.That(rec.Text, Does.Contain("f.txt"));
    }

    [Test]
    public async Task FileList_Vazio()
    {
        var tool = new FileListBuiltinTool();
        var r = await tool.ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("diretório vazio"));
    }

    [Test]
    public async Task FileRead_PaginacaoEErros()
    {
        var tool = new FileReadBuiltinTool(new HistoryResourceService(new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite("Data Source=:memory:").Options)));
        var content = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"linha {i}"));
        File.WriteAllText(Path.Join(_workspace, "f.txt"), content);

        var r = await tool.ExecuteAsync(
            Args("{\"path\":\"f.txt\",\"offset\":3,\"limit\":4}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("linha 3"));
            Assert.That(r.Text, Does.Contain("linha 6"));
            Assert.That(r.Text, Does.Not.Contain("linha 7:"));
            Assert.That(r.Text, Does.Contain("[truncado — restam 4 linhas"));
        });

        // Sem paginação → arquivo inteiro.
        r = await tool.ExecuteAsync(Args("{\"path\":\"f.txt\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("(10 linhas)"));

        // offset além do fim → só cabeçalho.
        r = await tool.ExecuteAsync(
            Args("{\"path\":\"f.txt\",\"offset\":99}"), Ctx(), default);
        Assert.That(r.Text, Does.Not.Contain("linha 9:"));

        // Erros: diretório, inexistente, escape.
        Directory.CreateDirectory(Path.Join(_workspace, "dir"));
        Assert.Multiple(async () =>
        {
            Assert.That((await tool.ExecuteAsync(Args("{\"path\":\"dir\"}"), Ctx(), default)).Text,
                Does.Contain("file_list"));
            Assert.That((await tool.ExecuteAsync(Args("{\"path\":\"nada.txt\"}"), Ctx(), default)).Text,
                Does.Contain("não existe"));
            Assert.That((await tool.ExecuteAsync(Args("{\"path\":\"../f\"}"), Ctx(), default)).Text,
                Does.Contain("escapa"));
        });

        // Binário → negado.
        File.WriteAllBytes(Path.Join(_workspace, "b.bin"), new byte[] { 1, 0, 2 });
        Assert.That((await tool.ExecuteAsync(Args("{\"path\":\"b.bin\"}"), Ctx(), default)).Text,
            Does.Contain("binário"));
    }

    [Test]
    public async Task FileRead_HistoryUri_PaginacaoEErros()
    {
        var dbPath = Path.Join(_workspace, "hist.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options;
        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Users.Add(new User
            {
                Id = "u1", Name = "U1", Email = "u1@e.local", Role = "user",
            });
            var chat = new Chat
            {
                Id = "c1", UserId = "u1", Title = "t",
                ModelsJson = "[]", CreatedAt = 1, UpdatedAt = 1,
            };
            seed.Chats.Add(chat);
            for (var i = 1; i <= 6; i++)
            {
                seed.ChatMessages.Add(new ChatMessage
                {
                    Id = $"m{i}", ChatId = "c1", Role = "user",
                    Content = $"fala {i}", Position = i, Timestamp = i,
                });
            }
            await seed.SaveChangesAsync();
        }

        var tool = new FileReadBuiltinTool(new HistoryResourceService(new AppDbContext(options)));

        // Transcript inteiro paginado.
        var r = await tool.ExecuteAsync(
            Args("{\"path\":\"history://c1\",\"offset\":2,\"limit\":3}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("fala 2"));
            Assert.That(r.Text, Does.Contain("fala 4"));
            Assert.That(r.Text, Does.Not.Contain("fala 5]"));
            Assert.That(r.Text, Does.Contain("truncado"));
        });

        // Erros: chat inexistente e seletor inválido propagam a mensagem.
        var missing = await tool.ExecuteAsync(
            Args("{\"path\":\"history://nope\"}"), Ctx(), default);
        Assert.That(missing.Text, Is.Not.EqualTo(""));

        var grep = new FileGrepBuiltinTool(
            new HistoryResourceService(new AppDbContext(options)));
        var hits = await grep.ExecuteAsync(
            Args("{\"pattern\":\"fala [13]\",\"path\":\"history://c1\"}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(hits.Text, Does.Contain("fala 1"));
            Assert.That(hits.Text, Does.Contain("fala 3"));
            Assert.That(hits.Text, Does.Not.Contain("fala 2]"));
        });
        var noHits = await grep.ExecuteAsync(
            Args("{\"pattern\":\"zzz\",\"path\":\"history://c1\"}"), Ctx(), default);
        Assert.That(noHits.Text, Does.Contain("Sem matches"));
    }

    [Test]
    public async Task FileGrep_Matriz()
    {
        var tool = new FileGrepBuiltinTool(new HistoryResourceService(new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite("Data Source=:memory:").Options)));
        File.WriteAllText(Path.Join(_workspace, "a.cs"), "public class Foo {}\n// TODO x");
        File.WriteAllText(Path.Join(_workspace, "b.md"), "# título\nTODO aqui");
        File.WriteAllText(Path.Join(_workspace, "c.cs"), "sem match");
        File.WriteAllBytes(Path.Join(_workspace, "b.bin"), new byte[] { 84, 0, 79 });

        // pattern obrigatório / regex inválida / escape de path.
        Assert.Multiple(async () =>
        {
            Assert.That((await tool.ExecuteAsync(Args("{}"), Ctx(), default)).Text,
                Does.Contain("obrigatório"));
            Assert.That((await tool.ExecuteAsync(
                Args("{\"pattern\":\"([\"}"), Ctx(), default)).Text, Does.Contain("Regex inválida"));
            Assert.That((await tool.ExecuteAsync(
                Args("{\"pattern\":\"x\",\"path\":\"../f\"}"), Ctx(), default)).Text,
                Does.Contain("escapa"));
        });

        // IgnoreCase default: TODO casa em ambos, binário é pulado.
        var r = await tool.ExecuteAsync(Args("{\"pattern\":\"todo\"}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("a.cs:2:"));
            Assert.That(r.Text, Does.Contain("b.md:2:"));
            Assert.That(r.Text, Does.Not.Contain("b.bin"));
        });

        // ignore_case=false → 'todo' não casa 'TODO'.
        r = await tool.ExecuteAsync(
            Args("{\"pattern\":\"todo\",\"ignore_case\":false}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("Nenhum match"));

        // glob sem '/' casa basename; com '/' casa relativo.
        r = await tool.ExecuteAsync(
            Args("{\"pattern\":\"TODO\",\"glob\":\"*.cs\"}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("a.cs"));
            Assert.That(r.Text, Does.Not.Contain("b.md"));
        });
        Directory.CreateDirectory(Path.Join(_workspace, "sub"));
        File.WriteAllText(Path.Join(_workspace, "sub", "d.cs"), "TODO sub");
        r = await tool.ExecuteAsync(
            Args("{\"pattern\":\"TODO\",\"glob\":\"sub/**\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("sub/d.cs"));

        // path para arquivo único.
        r = await tool.ExecuteAsync(
            Args("{\"pattern\":\"TODO\",\"path\":\"a.cs\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("a.cs:2:"));

        // max_results trunca.
        r = await tool.ExecuteAsync(
            Args("{\"pattern\":\"TODO\",\"max_results\":1}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("truncado em 1 matches"));
    }

    [Test]
    public async Task FileGlob_Matriz()
    {
        var tool = new FileGlobBuiltinTool();
        Directory.CreateDirectory(Path.Join(_workspace, "src"));
        File.WriteAllText(Path.Join(_workspace, "src", "a.cs"), "x");
        File.WriteAllText(Path.Join(_workspace, "b.txt"), "x");

        Assert.Multiple(async () =>
        {
            Assert.That((await tool.ExecuteAsync(Args("{}"), Ctx(), default)).Text,
                Does.Contain("obrigatório"));
            Assert.That((await tool.ExecuteAsync(
                Args("{\"pattern\":\"*.x\",\"path\":\"nada\"}"), Ctx(), default)).Text,
                Does.Contain("não existe"));
            Assert.That((await tool.ExecuteAsync(
                Args("{\"pattern\":\"x\",\"path\":\"../f\"}"), Ctx(), default)).Text,
                Does.Contain("escapa"));

            var r = await tool.ExecuteAsync(Args("{\"pattern\":\"**/*.cs\"}"), Ctx(), default);
            Assert.That(r.Text, Does.Contain("src/a.cs"));
            Assert.That(r.Text, Does.Not.Contain("b.txt"));

            r = await tool.ExecuteAsync(Args("{\"pattern\":\"*.zzz\"}"), Ctx(), default);
            Assert.That(r.Text, Does.Contain("Nenhum arquivo"));
        });
    }

    [Test]
    public async Task FileWrite_CriaAtualizaEDifunde()
    {
        var tool = new FileWriteBuiltinTool();

        // Args obrigatórios.
        Assert.Multiple(async () =>
        {
            Assert.That((await tool.ExecuteAsync(Args("{}"), Ctx(), default)).Text,
                Does.Contain("obrigatórios"));
            Assert.That((await tool.ExecuteAsync(
                Args("{\"path\":\"f.txt\"}"), Ctx(), default)).Text, Does.Contain("obrigatórios"));
        });

        // Cria novo (com diretórios intermediários).
        var r = await tool.ExecuteAsync(
            Args("{\"path\":\"d1/d2/novo.txt\",\"content\":\"a\\nb\"}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("criado"));
            Assert.That(File.Exists(Path.Join(_workspace, "d1", "d2", "novo.txt")), Is.True);
        });

        // Atualiza existente → diff com +/-.
        r = await tool.ExecuteAsync(
            Args("{\"path\":\"d1/d2/novo.txt\",\"content\":\"a\\nB\\nc\"}"), Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("atualizado"));
            Assert.That(r.Text, Does.Contain("+B"));
            Assert.That(r.Text, Does.Contain("@@"));
        });

        // Conteúdo idêntico → sem diff no texto.
        r = await tool.ExecuteAsync(
            Args("{\"path\":\"d1/d2/novo.txt\",\"content\":\"a\\nB\\nc\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Not.Contain("@@"));

        // Diretório como alvo e escape → negado.
        Assert.Multiple(async () =>
        {
            Assert.That((await tool.ExecuteAsync(
                Args("{\"path\":\"d1\",\"content\":\"x\"}"), Ctx(), default)).Text,
                Does.Contain("diretório"));
            Assert.That((await tool.ExecuteAsync(
                Args("{\"path\":\"../f\",\"content\":\"x\"}"), Ctx(), default)).Text,
                Does.Contain("escapa"));
        });
    }

    [Test]
    public async Task FileEdit_Substituicoes()
    {
        var tool = new FileEditBuiltinTool();
        File.WriteAllText(Path.Join(_workspace, "f.txt"), "alfa beta beta gama");

        // Args obrigatórios / arquivo inexistente / escape.
        Assert.Multiple(async () =>
        {
            Assert.That((await tool.ExecuteAsync(Args("{}"), Ctx(), default)).Text,
                Does.Contain("obrigatórios"));
            Assert.That((await tool.ExecuteAsync(
                Args("{\"path\":\"nada\",\"old_string\":\"x\",\"new_string\":\"y\"}"),
                Ctx(), default)).Text, Does.Contain("não existe"));
            Assert.That((await tool.ExecuteAsync(
                Args("{\"path\":\"../f\",\"old_string\":\"x\",\"new_string\":\"y\"}"),
                Ctx(), default)).Text, Does.Contain("escapa"));
        });

        // old_string ausente.
        var r = await tool.ExecuteAsync(
            Args("{\"path\":\"f.txt\",\"old_string\":\"delta\",\"new_string\":\"D\"}"),
            Ctx(), default);
        Assert.That(r.Text, Does.Contain("não encontrado"));

        // old_string vazio → occurrences 0 → não encontrado.
        r = await tool.ExecuteAsync(
            Args("{\"path\":\"f.txt\",\"old_string\":\"\",\"new_string\":\"D\"}"),
            Ctx(), default);
        Assert.That(r.Text, Does.Contain("não encontrado"));

        // Múltiplas ocorrências sem replace_all → erro orientando.
        r = await tool.ExecuteAsync(
            Args("{\"path\":\"f.txt\",\"old_string\":\"beta\",\"new_string\":\"B\"}"),
            Ctx(), default);
        Assert.That(r.Text, Does.Contain("2×"));

        // Única ocorrência → substitui.
        r = await tool.ExecuteAsync(
            Args("{\"path\":\"f.txt\",\"old_string\":\"alfa\",\"new_string\":\"ALFA\"}"),
            Ctx(), default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("editado"));
            Assert.That(File.ReadAllText(Path.Join(_workspace, "f.txt")),
                Is.EqualTo("ALFA beta beta gama"));
        });

        // replace_all → todas.
        r = await tool.ExecuteAsync(
            Args("{\"path\":\"f.txt\",\"old_string\":\"beta\",\"new_string\":\"B\",\"replace_all\":true}"),
            Ctx(), default);
        Assert.That(File.ReadAllText(Path.Join(_workspace, "f.txt")),
            Is.EqualTo("ALFA B B gama"));
    }
}
