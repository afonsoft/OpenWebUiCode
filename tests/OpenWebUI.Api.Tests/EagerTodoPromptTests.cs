using NUnit.Framework;
using Microsoft.Extensions.Configuration;
using OpenWebUI.Api.Completions;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261128-eager-todo (plano P2.21 few-shots + P2.23 eager todo):
/// o bloco de tracking do system prompt exige <c>todo_write</c> como
/// primeira tool em pedido multi-step e traz few-shots de disciplina.
/// </summary>
[TestFixture]
public sealed class EagerTodoPromptTests
{
    [Test]
    public void TrackingPrompt_ExigeTodoComoPrimeiraTool()
    {
        Assert.That(ChatPipeline.TrackingPrompt,
            Does.Contain("FIRST tool call must be builtin_todo_write"));
    }

    [Test]
    public void TrackingPrompt_TrazFewShotsDeDisciplina()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChatPipeline.TrackingPrompt, Does.Contain("in_progress"));
            Assert.That(ChatPipeline.TrackingPrompt, Does.Contain("completed"));
            Assert.That(ChatPipeline.TrackingPrompt, Does.Contain("Examples:"));
        });
    }

    /// <summary>SPECT-20261128-auto-parallel (plano P1.15): delegate_task
    /// entra nos grupos paralelos do loop (cap BuiltinTools:MaxParallel).</summary>
    [Test]
    public void DelegateTask_EhSeguraEmParalelo()
    {
        var registry = new BuiltinToolRegistry(
            [new DelegateTaskBuiltinTool(null!, null!)],
            new ConfigurationBuilder().Build());
        Assert.That(registry.IsSafeInParallel("builtin:delegate_task"),
            Is.True);
    }

    /// <summary>SPEC-20261128-prompt-modules (plano P2.20): os blocos
    /// tracking/autonomy vêm dos .md embutidos — o prompt composto deve
    /// conter as âncoras dos dois módulos.</summary>
    [Test]
    public void DefaultSystemPrompt_CarregaModulosMd()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChatPipeline.DefaultSystemPrompt,
                Does.Contain("FIRST tool call must be builtin_todo_write"),
                "tracking.md não carregado");
            Assert.That(ChatPipeline.DefaultSystemPrompt,
                Does.Contain("Classify every unit of work by risk"),
                "autonomy.md não carregado");
        });
    }
}
