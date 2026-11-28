using NUnit.Framework;
using OpenWebUI.Api.Completions;

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
}
