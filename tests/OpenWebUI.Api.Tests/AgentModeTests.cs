using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenWebUI.Api.Runs;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura do SPEC-20261009-agent-modes-plan-build: matriz do
/// <see cref="PermissionRuleset"/> (plan×build × allow/ask/deny), padrões
/// de argumento da allowlist "sempre nesta sessão" (RF-005), escrita do
/// plano pelo <c>builtin:plan_exit</c> e o fluxo e2e — plan não anuncia
/// tools de escrita, pergunta "Executar este plano?" e a aprovação
/// promove o chat a build (evento <c>mode</c> + coluna persistida).
/// O mock local (HttpListener) faz de Ollama.
/// </summary>
[TestFixture, IsolateEnvironment]
public class AgentModeTests
{
    // ---- PermissionRuleset.Evaluate — matriz plan × build (RF-002) ----

    [TestCase("plan", "file_read", true)]
    [TestCase("plan", "file_list", true)]
    [TestCase("plan", "file_grep", true)]
    [TestCase("plan", "file_glob", true)]
    [TestCase("plan", "web_search", true)]
    [TestCase("plan", "fetch_url", true)]
    [TestCase("plan", "ask_user", true)]
    [TestCase("plan", "todo_write", true)]
    [TestCase("plan", "plan_exit", true)]
    [TestCase("plan", "browser_screenshot", true)]
    [TestCase("plan", "job_list", true)]
    [TestCase("plan", "job_output", true)]
    [TestCase("plan", "n8n_list_workflows", true)]
    public void Evaluate_Plan_ReadonlyTools_Allow(string mode, string name, bool isBuiltin) =>
        Assert.That(PermissionRuleset.Evaluate(mode, name, isBuiltin),
            Is.EqualTo(PermissionDecision.Allow));

    [TestCase("n8n_trigger")]
    [TestCase("job_kill")]
    [TestCase("delegate_task")]
    [TestCase("generate_image")]
    [TestCase("generate_video")]
    [TestCase("worktree_merge")]
    public void Evaluate_Plan_WriteTools_Deny(string name) =>
        Assert.That(PermissionRuleset.Evaluate("plan", name),
            Is.EqualTo(PermissionDecision.Deny));

    // SPEC-20261030-plan-exec-analysis: execução para análise detalhada —
    // anunciada no plan, mas a decisão fica com o gate do preset (Ask).
    [TestCase("shell_exec")]
    [TestCase("code_interpreter")]
    public void Evaluate_Plan_ExecTools_SeguePreset(string name) =>
        Assert.That(PermissionRuleset.Evaluate("plan", name),
            Is.EqualTo(PermissionDecision.Ask));

    // Leitura estrutural do projeto — diffs, SPECs e LSP rodam livres.
    [TestCase("worktree_diff")]
    [TestCase("specs")]
    [TestCase("lsp_diagnostics")]
    [TestCase("lsp_symbols")]
    [TestCase("lsp_workspace_symbols")]
    [TestCase("lsp_definition")]
    [TestCase("lsp_references")]
    [TestCase("lsp_hover")]
    public void Evaluate_Plan_LeituraEstrutural_Allow(string name) =>
        Assert.That(PermissionRuleset.Evaluate("plan", name),
            Is.EqualTo(PermissionDecision.Allow));

    // Escrita em modo plan só em artefatos de planejamento: *.md e as
    // pastas de SPECs/planos — decidido pelo path dos args.
    [Test]
    public void Evaluate_Plan_EscritaSpecs_Allow()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PermissionRuleset.Evaluate("plan", "file_write",
                true, """{"path":".specs/SPEC-20261030-x.md"}"""),
                Is.EqualTo(PermissionDecision.Allow));
            Assert.That(PermissionRuleset.Evaluate("plan", "file_write",
                true, """{"path":".openwebui/plans/plano.json"}"""),
                Is.EqualTo(PermissionDecision.Allow));
            Assert.That(PermissionRuleset.Evaluate("plan", "file_edit",
                true, """{"path":"docs/specs/SPEC-antiga.md"}"""),
                Is.EqualTo(PermissionDecision.Allow));
            Assert.That(PermissionRuleset.Evaluate("plan", "file_write",
                true, """{"path":"docs/notas.md"}"""),
                Is.EqualTo(PermissionDecision.Allow));
        });
    }

    [Test]
    public void Evaluate_Plan_EscritaForaDeSpecs_Deny()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PermissionRuleset.Evaluate("plan", "file_write",
                true, """{"path":"src/App.cs"}"""),
                Is.EqualTo(PermissionDecision.Deny));
            Assert.That(PermissionRuleset.Evaluate("plan", "file_edit",
                true, """{"path":"../fora.md"}"""),
                Is.EqualTo(PermissionDecision.Deny));
            Assert.That(PermissionRuleset.Evaluate("plan", "file_write",
                true, """{"path":"/abs/notas.md"}"""),
                Is.EqualTo(PermissionDecision.Deny));
        });
    }

    [Test]
    public void Evaluate_Plan_EscritaSemPath_SeguePreset() =>
        // Args ausentes/ilegíveis — a prévia da aprovação decide.
        Assert.That(PermissionRuleset.Evaluate("plan", "file_write", true, null),
            Is.EqualTo(PermissionDecision.Ask));

    [Test]
    public void Evaluate_Plan_ApplyPatch_SpecsVsCodigo()
    {
        var patchSpecs = """{"patch":"*** Begin Patch\n*** Update File: docs/specs/SPEC.md\n@@\n-old\n+new\n*** End Patch"}""";
        var patchCodigo = """{"patch":"*** Begin Patch\n*** Update File: src/A.cs\n@@\n-old\n+new\n*** End Patch"}""";
        Assert.Multiple(() =>
        {
            Assert.That(PermissionRuleset.Evaluate("plan", "apply_patch", true, patchSpecs),
                Is.EqualTo(PermissionDecision.Allow));
            Assert.That(PermissionRuleset.Evaluate("plan", "apply_patch", true, patchCodigo),
                Is.EqualTo(PermissionDecision.Deny));
        });
    }

    [Test]
    public void Evaluate_Plan_ToolNaoBuiltin_Deny() =>
        // HTTP/MCP/python abrem rede/código arbitrário — fora do plan.
        Assert.Multiple(() =>
        {
            Assert.That(PermissionRuleset.Evaluate("plan", "eco", isBuiltin: false),
                Is.EqualTo(PermissionDecision.Deny));
            Assert.That(PermissionRuleset.Evaluate("plan", "mcp_tool", isBuiltin: false),
                Is.EqualTo(PermissionDecision.Deny));
        });

    [Test]
    public void Evaluate_Build_NaoRestringe_Ask()
    {
        foreach (var name in new[] { "file_write", "shell_exec", "file_read", "plan_exit" })
        {
            Assert.That(PermissionRuleset.Evaluate("build", name),
                Is.EqualTo(PermissionDecision.Ask), name);
        }
    }

    [Test]
    public void Evaluate_FormasDoNome_Normaliza()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PermissionRuleset.Evaluate("plan", "builtin:file_write"),
                Is.EqualTo(PermissionDecision.Ask));
            Assert.That(PermissionRuleset.Evaluate("plan", "builtin_file_write"),
                Is.EqualTo(PermissionDecision.Ask));
            Assert.That(PermissionRuleset.Evaluate("PLAN", "file_read"),
                Is.EqualTo(PermissionDecision.Allow));
            Assert.That(PermissionRuleset.Evaluate(null, "file_write"),
                Is.EqualTo(PermissionDecision.Ask));
        });
    }

    // ---- PermissionRuleset — modo chat: com scratch garantido
    // (SPEC-20261027), workspace tools seguem o preset como as demais ----

    [TestCase("file_read")]
    [TestCase("file_list")]
    [TestCase("file_grep")]
    [TestCase("file_glob")]
    [TestCase("file_write")]
    [TestCase("file_edit")]
    [TestCase("apply_patch")]
    [TestCase("shell_exec")]
    [TestCase("code_interpreter")]
    [TestCase("job_list")]
    [TestCase("job_output")]
    [TestCase("job_kill")]
    [TestCase("worktree_diff")]
    [TestCase("worktree_merge")]
    [TestCase("plan_exit")]
    [TestCase("delegate_task")]
    [TestCase("lsp_diagnostics")]
    [TestCase("lsp_symbols")]
    [TestCase("lsp_workspace_symbols")]
    [TestCase("lsp_definition")]
    [TestCase("lsp_references")]
    [TestCase("lsp_hover")]
    public void Evaluate_Chat_WorkspaceTools_SeguePreset(string name) =>
        // Antes de SPEC-20261027: Deny (sem repo não havia onde trabalhar).
        Assert.That(PermissionRuleset.Evaluate("chat", name),
            Is.EqualTo(PermissionDecision.Ask));

    [TestCase("web_search")]
    [TestCase("fetch_url")]
    [TestCase("ask_user")]
    [TestCase("todo_write")]
    [TestCase("generate_image")]
    // skill saiu de WorkspaceTools (SPEC-20261016-global-agents-scope
    // RF-007): funciona no modo chat lendo ~/.agents sem repo vinculado.
    [TestCase("skill")]
    public void Evaluate_Chat_NaoWorkspace_SeguePreset(string name) =>
        // chat nega só workspace — o resto segue o gate do preset (Ask).
        Assert.That(PermissionRuleset.Evaluate("chat", name),
            Is.EqualTo(PermissionDecision.Ask));

    [Test]
    public void Evaluate_Chat_ToolNaoBuiltin_SeguePreset() =>
        Assert.That(PermissionRuleset.Evaluate("chat", "mcp_tool", isBuiltin: false),
            Is.EqualTo(PermissionDecision.Ask));

    [Test]
    public void Normalize_Chat_Reconhecido_LegadoViraBuild()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PermissionRuleset.Normalize("chat"), Is.EqualTo("chat"));
            Assert.That(PermissionRuleset.Normalize("CHAT"), Is.EqualTo("chat"));
            Assert.That(PermissionRuleset.Normalize("plan"), Is.EqualTo("plan"));
            Assert.That(PermissionRuleset.Normalize("legado"), Is.EqualTo("build"));
            Assert.That(PermissionRuleset.Normalize(null), Is.EqualTo("build"));
        });
    }

    // ---- ArgPattern — assinatura da allowlist por classe de ação (RF-005) ----

    [Test]
    public void ArgPattern_ShellExec_PrimeiroToken()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PermissionRuleset.ArgPattern(
                "builtin:shell_exec", "{\"command\":\"git status\"}"), Is.EqualTo("exec:git"));
            Assert.That(PermissionRuleset.ArgPattern(
                "shell_exec", "{\"command\":\"  npm install --save x\"}"), Is.EqualTo("exec:npm"));
        });
    }

    [Test]
    public void ArgPattern_FileWrite_Diretorio()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PermissionRuleset.ArgPattern(
                "builtin:file_write", "{\"path\":\"src/a/b.txt\"}"), Is.EqualTo("write:src/a/**"));
            Assert.That(PermissionRuleset.ArgPattern(
                "file_edit", "{\"path\":\"raiz.txt\"}"), Is.EqualTo("write:./**"));
        });
    }

    [Test]
    public void ArgPattern_FetchUrl_Host() =>
        Assert.That(PermissionRuleset.ArgPattern(
            "builtin:fetch_url", "{\"url\":\"https://api.exemplo.com/x/y?z=1\"}"),
            Is.EqualTo("fetch:https://api.exemplo.com/**"));

    [Test]
    public void ArgPattern_OutrasTools_Curinga() =>
        Assert.That(PermissionRuleset.ArgPattern("code_interpreter", "{}"), Is.EqualTo("*"));

    // ---- ChatRunApprovals — allowlist por padrão (RF-005) ----

    [Test]
    public async Task Remember_PorPadrao_ReaprovaMesmaClasse()
    {
        var approvals = new ChatRunApprovals();
        var wait = approvals.WaitAsync("run-1", "chat-1", "call-1",
            "builtin:shell_exec", CancellationToken.None,
            argPattern: "exec:git");

        Assert.That(approvals.Resolve("run-1", "chat-1", "call-1",
            approved: true, remember: true), Is.True);
        Assert.That((await wait).Approved, Is.True);

        Assert.Multiple(() =>
        {
            // Mesma classe (git *) — reaprova sem perguntar de novo.
            Assert.That(approvals.IsRemembered("chat-1", "builtin:shell_exec", "exec:git"),
                Is.True);
            // Outra classe — segue perguntando.
            Assert.That(approvals.IsRemembered("chat-1", "builtin:shell_exec", "exec:rm"),
                Is.False);
            Assert.That(approvals.IsRemembered("chat-2", "builtin:shell_exec", "exec:git"),
                Is.False);
        });
    }

    [Test]
    public async Task Remember_Glob_DiretorioCobreArquivos()
    {
        var approvals = new ChatRunApprovals();
        var wait = approvals.WaitAsync("run-1", "chat-1", "call-2",
            "builtin:file_write", CancellationToken.None,
            argPattern: "write:src/a/**");
        approvals.Resolve("run-1", "chat-1", "call-2", approved: true, remember: true);
        await wait;

        Assert.Multiple(() =>
        {
            Assert.That(approvals.IsRemembered("chat-1", "builtin:file_write",
                "write:src/a/**"), Is.True);
            Assert.That(approvals.IsRemembered("chat-1", "builtin:file_write",
                "write:src/b/**"), Is.False);
        });
    }

    // ---- plan_exit — gravação do plano (RF-003) ----

    [Test]
    public async Task PlanExit_GravaPlanoNoWorkdir()
    {
        var workdir = Path.Join(Path.GetTempPath(), $"plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workdir);
        try
        {
            var tool = new PlanExitBuiltinTool();
            // SPEC-20261015-plan-spec-sdd RF-002: plan_exit só aceita o
            // esqueleto SDD — sem heading próprio cai no wrapper de título.
            using var args = JsonDocument.Parse(
                """{"plan":"## 0. Metadata\nStatus: Draft\n## 1. User Story\nx\n## 2. Scope\nx\n## 4. Requirements\nRF-001 1. Ler X\n## 6. Acceptance Criteria\nx\n## 7. Task Plan\nx","title":"Plano de teste"}""");
            var outcome = await tool.ExecuteAsync(args.RootElement,
                new BuiltinToolContext("u1", "c1", "run-abc", workdir, workdir),
                CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(outcome.Refused, Is.False);
                Assert.That(outcome.Text, Does.Contain(".openwebui/plans/run-abc.md"));
            });
            var plan = File.ReadAllText(
                Path.Join(workdir, ".openwebui", "plans", "run-abc.md"));
            Assert.Multiple(() =>
            {
                Assert.That(plan, Does.Contain("# Plano de teste"));
                Assert.That(plan, Does.Contain("1. Ler X"));
                Assert.That(plan, Does.Contain("run-abc"));
            });
        }
        finally
        {
            Directory.Delete(workdir, recursive: true);
        }
    }

    [Test]
    public async Task PlanExit_SemPlan_Recusa()
    {
        var tool = new PlanExitBuiltinTool();
        using var args = JsonDocument.Parse("{}");
        var outcome = await tool.ExecuteAsync(args.RootElement,
            new BuiltinToolContext("u1", "c1", "r1", Path.GetTempPath(), Path.GetTempPath()),
            CancellationToken.None);
        Assert.That(outcome.Refused, Is.True);
    }
}
