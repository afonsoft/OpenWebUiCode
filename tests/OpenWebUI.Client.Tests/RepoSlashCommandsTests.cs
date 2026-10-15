using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Client.Services;

namespace OpenWebUI.Client.Tests;

/// <summary>
/// Merge e interpolação dos slash commands do composer
/// (SPEC-20261009-repo-skills-slash-commands RF-003): prompts do usuário +
/// skills/commands do repo com badge de fonte, e templates
/// <c>$ARGUMENTS</c>/<c>$1..$N</c> interpolados pelos args digitados.
/// </summary>
[TestFixture]
public class RepoSlashCommandsTests
{
    private static PromptResponse Prompt(string cmd, string title = "t") =>
        new("id-" + cmd, cmd, title, "corpo-" + cmd, 0, 0);

    private static RepoSkillItemResponse Skill(string name, string desc = "d") =>
        new(name, desc, "skill", "skills/" + name + "/SKILL.md");

    private static RepoCommandItemResponse Cmd(string name, string desc = "d") =>
        new(name, desc, null, null, false, "command", ".opencode/command/" + name + ".md");

    [Test]
    public void Merge_MesclaFontes_ComBadge()
    {
        var items = RepoSlashCommands.Merge(
            [Prompt("fix")], [Skill("review")], [Cmd("deploy")], "");
        Assert.That(items.Select(i => i.Source),
            Is.EqualTo(new[] { "prompt", "skill", "command" }));
        Assert.That(items.Select(i => i.Command),
            Is.EqualTo(new[] { "fix", "review", "deploy" }));
    }

    [Test]
    public void Merge_FiltraPorPrefixo()
    {
        var items = RepoSlashCommands.Merge(
            [Prompt("fix"), Prompt("help")], [Skill("helpful")], [Cmd("deploy")], "he");
        Assert.That(items.Select(i => i.Command),
            Is.EqualTo(new[] { "help", "helpful" }));
    }

    [Test]
    public void Merge_RespeitaTake()
    {
        var prompts = Enumerable.Range(0, 20).Select(i => Prompt($"p{i}")).ToList();
        var items = RepoSlashCommands.Merge(prompts, [], [], "p", take: 5);
        Assert.That(items, Has.Count.EqualTo(5));
    }

    /// <summary>SPEC-20261015-plan-spec-sdd RF-003: builtins entram na
    /// frente do merge e casam por prefixo como as demais fontes.</summary>
    [Test]
    public void Merge_BuiltinPlan_PrimeiroEFiltraPorPrefixo()
    {
        var items = RepoSlashCommands.Merge(
            [Prompt("plugin")], [], [], "pl",
            builtins: RepoSlashCommands.Builtins);
        Assert.Multiple(() =>
        {
            Assert.That(items[0].Command, Is.EqualTo("plan"));
            Assert.That(items[0].Source, Is.EqualTo("builtin"));
            Assert.That(items[0].Title, Is.EqualTo("chat.cmd_plan"),
                "título do builtin é a chave i18n resolvida no render");
            Assert.That(items.Select(i => i.Command),
                Is.EqualTo(new[] { "plan", "plugin" }));
        });

        var foraDoPrefixo = RepoSlashCommands.Merge(
            [], [], [], "fix", builtins: RepoSlashCommands.Builtins);
        Assert.That(foraDoPrefixo, Is.Empty);
    }

    [Test]
    public void Interpolate_Arguments()
    {
        Assert.That(
            RepoSlashCommands.Interpolate("rode $ARGUMENTS agora", "os testes"),
            Is.EqualTo("rode os testes agora"));
    }

    [Test]
    public void Interpolate_Posicionais()
    {
        Assert.That(
            RepoSlashCommands.Interpolate("de $1 para $2", "dev main"),
            Is.EqualTo("de dev para main"));
    }

    [Test]
    public void Interpolate_PosicionalFaltante_Vazio()
    {
        Assert.That(
            RepoSlashCommands.Interpolate("[$1][$3]", "a"),
            Is.EqualTo("[a][]"));
    }

    [Test]
    public void ArgsAfter_PegaTextoAposNome()
    {
        Assert.That(RepoSlashCommands.ArgsAfter("/deploy staging"), Is.EqualTo("staging"));
        Assert.That(RepoSlashCommands.ArgsAfter("/deploy"), Is.EqualTo(""));
    }
}
