using NUnit.Framework;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// CommandExpansion (SPEC-20261016-commands-v2 RF-002/RF-006): semântica de
/// args do opencode — <c>$ARGUMENTS</c>, <c>$1..$9</c> com rest-capture no
/// último placeholder, aspas agrupando, fallback de anexo e blocos
/// <c>!`shell`</c> opt-in.
/// </summary>
[TestFixture]
public class CommandExpansionTests
{
    [Test]
    public void Interpolate_Arguments_LinhaInteira()
    {
        var prompt = CommandExpansion.Interpolate(
            "Review $ARGUMENTS.", "src/auth.ts strict");
        Assert.That(prompt, Is.EqualTo("Review src/auth.ts strict."));
    }

    [Test]
    public void Interpolate_Posicionais_UltimoAbsorveResto()
    {
        // AC: /compare a "b c" d → "Compare a with b c d."
        var prompt = CommandExpansion.Interpolate(
            "Compare $1 with $2.", "a \"b c\" d");
        Assert.That(prompt, Is.EqualTo("Compare a with b c d."));
    }

    [Test]
    public void Interpolate_Posicionais_FaltandoViramVazio()
    {
        var prompt = CommandExpansion.Interpolate(
            "[$1][$2][$3]", "um dois");
        Assert.That(prompt, Is.EqualTo("[um][dois][]"));
    }

    [Test]
    public void Interpolate_ZeroEDezSaoLiterais()
    {
        // $0 e $10 são literais; $1 absorve o resto por ser o único
        // placeholder numérico do template.
        var prompt = CommandExpansion.Interpolate(
            "$0 $10 $1", "a b");
        Assert.That(prompt, Is.EqualTo("$0 $10 a b"));
    }

    [Test]
    public void Interpolate_SemPlaceholder_AnexaArgs()
    {
        // Edge do SPEC: "/cmd x" com template fixo → args após linha em branco.
        var prompt = CommandExpansion.Interpolate("Fix the bug.", "urgent");
        Assert.That(prompt, Is.EqualTo("Fix the bug.\n\nurgent"));
    }

    [Test]
    public void Interpolate_AspasSimplesTambemAgrupam()
    {
        var prompt = CommandExpansion.Interpolate("$1|$2", "'a b' c");
        Assert.That(prompt, Is.EqualTo("a b|c"));
    }

    [Test]
    public async Task Expand_ShellOff_ViraLiteral()
    {
        var prompt = await CommandExpansion.ExpandAsync(
            "Diff: !`git diff --stat`", string.Empty,
            allowShell: false, workdir: null, shellRunner: null);
        Assert.That(
            prompt, Is.EqualTo(
                "Diff: " + CommandExpansion.ShellDisabledLiteral));
    }

    [Test]
    public async Task Expand_ShellOn_SaidaEmbutidaComTag()
    {
        var prompt = await CommandExpansion.ExpandAsync(
            "Resumo: !`git diff --stat` fim", string.Empty,
            allowShell: true, workdir: "/tmp",
            shellRunner: (cmd, dir, _) => Task.FromResult(" 2 files changed"));
        Assert.That(
            prompt, Is.EqualTo(
                "Resumo: <shell-output cmd=\"git diff --stat\">"
                + " 2 files changed</shell-output> fim"));
    }

    [Test]
    public async Task Expand_ShellOn_PassaWorkdir()
    {
        string? seenDir = null;
        string? seenCmd = null;
        await CommandExpansion.ExpandAsync(
            "!`pwd`", string.Empty,
            allowShell: true, workdir: "/work/dir",
            shellRunner: (cmd, dir, _) =>
            {
                seenCmd = cmd;
                seenDir = dir;
                return Task.FromResult("x");
            });
        Assert.Multiple(() =>
        {
            Assert.That(seenCmd, Is.EqualTo("pwd"));
            Assert.That(seenDir, Is.EqualTo("/work/dir"));
        });
    }
}
