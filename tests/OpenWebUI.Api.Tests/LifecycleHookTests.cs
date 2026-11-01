using System.Text.Json;
using NUnit.Framework;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261031-lifecycle-hooks: loader multi-fonte (RF-007), runner
/// command (RF-001), decisões (RF-003/RF-004) e parse do prompt hook
/// (RF-002).
/// </summary>
[TestFixture]
public class LifecycleHookTests
{
    private string _dir = string.Empty;
    private string _home = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"hk-{Guid.NewGuid():N}");
        _home = Path.Combine(Path.GetTempPath(), $"hkhome-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_home);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    private static JsonElement EmptyPayload() =>
        JsonSerializer.SerializeToElement(new { });

    private static HookDefinition Cmd(string cmd, string ev = "PreToolUse",
        string? matcher = null, int timeout = 60) =>
        new(ev, matcher, "command", cmd, timeout, "test");

    [Test]
    public void Loader_NativoJson_ParseHooksComMatcher()
    {
        var dir = Path.Combine(_dir, ".openwebui");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hooks.json"), """
            {"hooks": {"PreToolUse": [
              {"matcher": "shell_exec", "hooks": [
                {"type": "command", "command": "./guard.sh", "timeout": 10}]}]}}
            """);
        var hooks = HookConfigLoader.Load(_dir, home: _home);
        Assert.Multiple(() =>
        {
            Assert.That(hooks, Has.Count.EqualTo(1));
            Assert.That(hooks[0].Event, Is.EqualTo("PreToolUse"));
            Assert.That(hooks[0].Matcher, Is.EqualTo("shell_exec"));
            Assert.That(hooks[0].Type, Is.EqualTo("command"));
            Assert.That(hooks[0].TimeoutSeconds, Is.EqualTo(10));
        });
    }

    [Test]
    public void Loader_DevinHooksV1_Compat()
    {
        var dir = Path.Combine(_dir, ".devin");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hooks.v1.json"), """
            {"hooks": {"UserPromptSubmit": [
              {"hooks": [{"type": "prompt", "prompt": "check"}]}]}}
            """);
        var hooks = HookConfigLoader.Load(_dir, home: _home);
        Assert.Multiple(() =>
        {
            Assert.That(hooks, Has.Count.EqualTo(1));
            Assert.That(hooks[0].Type, Is.EqualTo("prompt"));
            Assert.That(hooks[0].Command, Is.EqualTo("check"));
        });
    }

    [Test]
    public void Loader_ClaudeSettingsKey_Compat()
    {
        var dir = Path.Combine(_dir, ".claude");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.json"), """
            {"model": "x", "hooks": {"Stop": [
              {"hooks": [{"type": "command", "command": "./stop.sh"}]}]}}
            """);
        var hooks = HookConfigLoader.Load(_dir, home: _home);
        Assert.That(hooks, Has.Count.EqualTo(1));
        Assert.That(hooks[0].Event, Is.EqualTo("Stop"));
    }

    [Test]
    public void Loader_RegexInvalido_DesabilitaHook()
    {
        var dir = Path.Combine(_dir, ".openwebui");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hooks.json"), """
            {"hooks": {"PreToolUse": [{"matcher": "[", "hooks":
              [{"type": "command", "command": "x"}]}]}}
            """);
        Assert.That(HookConfigLoader.Load(_dir, home: _home), Is.Empty);
    }

    [Test]
    public void Loader_RepoAntesDoUsuario()
    {
        var repoDir = Path.Combine(_dir, ".openwebui");
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "hooks.json"),
            """{"hooks": {"Stop": [{"hooks": [{"type": "command", "command": "repo"}]}]}}""");
        var userDir = Path.Combine(_home, ".config", "openwebui");
        Directory.CreateDirectory(userDir);
        File.WriteAllText(Path.Combine(userDir, "hooks.json"),
            """{"hooks": {"Stop": [{"hooks": [{"type": "command", "command": "user"}]}]}}""");
        var hooks = HookConfigLoader.Load(_dir, home: _home);
        Assert.Multiple(() =>
        {
            Assert.That(hooks, Has.Count.EqualTo(2));
            Assert.That(hooks[0].Command, Is.EqualTo("repo"));
            Assert.That(hooks[1].Command, Is.EqualTo("user"));
        });
    }

    [Test]
    public async Task Runner_ExitZero_JsonDecisao()
    {
        var runner = new CommandHookRunner();
        var outcome = await runner.RunAsync(
            Cmd("""echo '{"decision":"block","reason":"no rm"}'"""),
            EmptyPayload(), _dir, "s1", "p1", default);
        Assert.Multiple(() =>
        {
            Assert.That(outcome.Decision, Is.EqualTo(HookDecision.Block));
            Assert.That(outcome.Reason, Is.EqualTo("no rm"));
        });
    }

    [Test]
    public async Task Runner_ExitDois_BlockComStderr()
    {
        var runner = new CommandHookRunner();
        var outcome = await runner.RunAsync(
            Cmd("echo deny >&2; exit 2"), EmptyPayload(), _dir, null, null, default);
        Assert.Multiple(() =>
        {
            Assert.That(outcome.Decision, Is.EqualTo(HookDecision.Block));
            Assert.That(outcome.Reason, Is.EqualTo("deny"));
        });
    }

    [Test]
    public async Task Runner_JsonInvalido_NaoBloqueante()
    {
        var runner = new CommandHookRunner();
        var outcome = await runner.RunAsync(
            Cmd("echo lixo"), EmptyPayload(), _dir, null, null, default);
        Assert.That(outcome.Decision, Is.EqualTo(HookDecision.Approve));
    }

    [Test]
    public async Task Runner_UpdatedInputERContexto()
    {
        var runner = new CommandHookRunner();
        var outcome = await runner.RunAsync(Cmd(
            """echo '{"hookSpecificOutput":{"updatedInput":{"x":1},"additionalContext":"ctx"}}'"""),
            EmptyPayload(), _dir, null, null, default);
        Assert.Multiple(() =>
        {
            Assert.That(outcome.UpdatedInput, Does.Contain("\"x\":1"));
            Assert.That(outcome.AdditionalContexts, Is.EqualTo(new[] { "ctx" }));
        });
    }

    [Test]
    public void Payload_CamposDoProtocolo()
    {
        var run = new ChatRun { Id = "r1", ChatId = "c1", UserId = "u1" };
        var el = LifecycleHookService.BuildPayload("PreToolUse", "/ws", run,
            "shell_exec", new { tool_input = "{}" });
        Assert.Multiple(() =>
        {
            Assert.That(el.GetProperty("session_id").GetString(), Is.EqualTo("r1"));
            Assert.That(el.GetProperty("prompt_id").GetString(), Is.EqualTo("c1"));
            Assert.That(el.GetProperty("cwd").GetString(), Is.EqualTo("/ws"));
            Assert.That(el.GetProperty("hook_event_name").GetString(), Is.EqualTo("PreToolUse"));
            Assert.That(el.GetProperty("tool_name").GetString(), Is.EqualTo("shell_exec"));
            Assert.That(el.GetProperty("tool_input").GetString(), Is.EqualTo("{}"));
        });
    }

    [Test]
    public void Matcher_NuloOuRegex()
    {
        var sem = new HookDefinition("PreToolUse", null, "command", "x", 60, "t");
        var com = new HookDefinition("PreToolUse", "^shell_", "command", "x", 60, "t");
        Assert.Multiple(() =>
        {
            Assert.That(LifecycleHookService.Matches(sem, "file_read"), Is.True);
            Assert.That(LifecycleHookService.Matches(com, "shell_exec"), Is.True);
            Assert.That(LifecycleHookService.Matches(com, "file_read"), Is.False);
        });
    }

    [Test]
    public async Task Service_PrimeiroBlockVence()
    {
        var dir = Path.Combine(_dir, ".openwebui");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hooks.json"), """
            {"hooks": {"PreToolUse": [
              {"hooks": [{"type": "command", "command": "echo '{\"decision\":\"approve\"}'"}]},
              {"hooks": [{"type": "command", "command": "echo b>&2; exit 2"}]}]}}
            """);
        var svc = new LifecycleHookService(new CommandHookRunner(),
            prompts: null!, events: null);
        var run = new ChatRun { Id = "r", ChatId = "c", UserId = "u" };
        var outcome = await svc.RunAsync(
            "PreToolUse", _dir, run, "anything", null, default);
        Assert.Multiple(() =>
        {
            Assert.That(outcome.Decision, Is.EqualTo(HookDecision.Block));
            Assert.That(outcome.Reason, Is.EqualTo("b"));
        });
    }

    [Test]
    public void PromptParse_FormatVariantes()
    {
        var hook = new HookDefinition("Stop", null, "prompt", "r", 60, "t");
        Assert.Multiple(() =>
        {
            Assert.That(PromptHookEvaluator.Parse("approve", hook).Decision,
                Is.EqualTo(HookDecision.Approve));
            Assert.That(PromptHookEvaluator.Parse("block\nrazão", hook).Decision,
                Is.EqualTo(HookDecision.Block));
            Assert.That(PromptHookEvaluator.Parse("{\"pass\":false}", hook).Decision,
                Is.EqualTo(HookDecision.Block));
            Assert.That(PromptHookEvaluator.Parse("{\"pass\":true}", hook).Decision,
                Is.EqualTo(HookDecision.Approve));
            Assert.That(PromptHookEvaluator.Parse("confuso", hook).Decision,
                Is.EqualTo(HookDecision.Approve));
            Assert.That(PromptHookEvaluator.Parse("false", hook).Decision,
                Is.EqualTo(HookDecision.Block));
        });
    }
}
