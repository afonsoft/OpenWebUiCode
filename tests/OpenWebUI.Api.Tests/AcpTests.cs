using System.Text.Json;
using OpenWebUI.Acp;

namespace OpenWebUI.Api.Tests;

/// <summary>Regresión del dispatch ACP (SPEC-20261021-harness-seams) —
/// params de session/prompt extraídos del documento JSON-RPC.</summary>
[TestFixture]
public class AcpTests
{
    [Test]
    public void ExtractPromptParams_TodosOsCampos_RetornaOsTres()
    {
        var parms = JsonDocument.Parse(
            """{"sessionId":"chat-9","prompt":[{"type":"text","text":"hola"}],"model":"gpt-x"}""")
            .RootElement;
        var (sessionId, prompt, model) = AcpDispatch.ExtractPromptParams(parms);
        Assert.Multiple(() =>
        {
            Assert.That(sessionId, Is.EqualTo("chat-9"));
            Assert.That(prompt, Is.EqualTo("hola"));
            Assert.That(model, Is.EqualTo("gpt-x"));
        });
    }

    [Test]
    public void ExtractPromptParams_SemModel_UsaDefault()
    {
        var parms = JsonDocument.Parse(
            """{"sessionId":"c1","prompt":[{"type":"text","text":"oi"}]}""")
            .RootElement;
        var (_, _, model) = AcpDispatch.ExtractPromptParams(parms);
        Assert.That(model, Is.EqualTo("default"));
    }

    [Test]
    public void ExtractPromptParams_SemSession_DevuelveNull()
    {
        var parms = JsonDocument.Parse(
            """{"prompt":[{"type":"text","text":"oi"}]}""").RootElement;
        var (sessionId, _, _) = AcpDispatch.ExtractPromptParams(parms);
        Assert.That(sessionId, Is.Null);
    }
}
