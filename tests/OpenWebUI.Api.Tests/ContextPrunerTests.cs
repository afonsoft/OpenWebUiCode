using System.Text.Json;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261014-context-maintenance-v2 RF-001: poda mecânica da janela —
/// tool results supersedes, "ok" sem payload e grandes viram marcadores;
/// user/assistant nunca são tocados e a lista original não é mutada.
/// </summary>
[TestFixture]
public class ContextPrunerTests
{
    private static ChatCompletionMessage Tool(string content, string? callId = null) =>
        new("tool", content, ToolCallId: callId);

    private static ChatCompletionMessage WriteCall(string id, string path) =>
        new("assistant", string.Empty, ToolCallsJson: JsonSerializer.Serialize(new[]
        {
            new
            {
                id,
                function = new
                {
                    name = "file_write",
                    arguments = JsonSerializer.Serialize(new { path }),
                },
            },
        }));

    [Test]
    public void Superseded_WriteResult_ViraMarcador()
    {
        var history = new List<ChatCompletionMessage>
        {
            new("user", "escreve a.txt"),
            WriteCall("c1", "a.txt"),
            Tool("arquivo gravado", "c1"),
            WriteCall("c2", "a.txt"),
            Tool("arquivo gravado de novo", "c2"),
            new("assistant", "pronto"),
        };

        var pruned = ContextPruner.Prune(history, "chat1");

        Assert.Multiple(() =>
        {
            Assert.That(pruned[2].Content, Does.Contain("elidido").And.Contain("a.txt"));
            Assert.That(pruned[4].Content, Is.EqualTo("arquivo gravado de novo"));
            // user/assistant intactos; lista original não mutada
            Assert.That(pruned[0].Content, Is.EqualTo("escreve a.txt"));
            Assert.That(history[2].Content, Is.EqualTo("arquivo gravado"));
            Assert.That(pruned.Count, Is.EqualTo(history.Count));
        });
    }

    [Test]
    public void OkSemPayload_ViraMarcadorCurto()
    {
        var history = new List<ChatCompletionMessage>
        {
            new("assistant", "", ToolCallsJson: "[{\"id\":\"c1\",\"function\":{\"name\":\"shell_exec\",\"arguments\":\"{}\"}}]"),
            Tool("ok"),
            Tool("{\"ok\":true}"),
        };

        var pruned = ContextPruner.Prune(history, "chat1");

        Assert.Multiple(() =>
        {
            Assert.That(pruned[1].Content, Is.EqualTo(ContextPruner.OkMarker));
            Assert.That(pruned[2].Content, Is.EqualTo(ContextPruner.OkMarker));
        });
    }

    [Test]
    public void ResultadoGrande_ViraMarcadorComPointer()
    {
        var big = new string('x', ContextPruner.ElideBytes + 100);
        var history = new List<ChatCompletionMessage>
        {
            new("assistant", "", ToolCallsJson: "[{\"id\":\"call-9\",\"function\":{\"name\":\"file_read\",\"arguments\":\"{}\"}}]"),
            Tool(big, "call-9"),
        };

        var pruned = ContextPruner.Prune(history, "chat1");

        Assert.That(pruned[1].Content, Does.Contain("elidido")
            .And.Contain("history://chat1/call:call-9")
            .And.Contain($"{ContextPruner.ElideBytes + 100} bytes"));
    }

    [Test]
    public void FallbackPosicional_PareiaSemToolCallId()
    {
        // Cliente não envia ToolCallId → pareamento pela ordem dos calls.
        var history = new List<ChatCompletionMessage>
        {
            WriteCall("c1", "b.txt"),
            Tool("v1 escrito"),
            WriteCall("c2", "b.txt"),
            Tool("v2 escrito"),
        };

        var pruned = ContextPruner.Prune(history, "c");

        Assert.That(pruned[1].Content, Does.Contain("elidido"));
        Assert.That(pruned[3].Content, Is.EqualTo("v2 escrito"));
    }

    [Test]
    public void PequenoEUltimaEscrita_NaoSaoPodados()
    {
        var history = new List<ChatCompletionMessage>
        {
            new("user", "lê x"),
            Tool("conteúdo útil e detalhado do arquivo x"),
            WriteCall("c1", "y.txt"),
            Tool("y.txt escrito com sucesso"),
        };

        var pruned = ContextPruner.Prune(history, "c");

        Assert.That(pruned.Select(m => m.Content),
            Is.EqualTo(history.Select(m => m.Content)));
    }

    [Test]
    public void ArgumentsDoubleEncoded_NaoDerrubaRun()
    {
        // Regressão run.failed: modelos (Gemini/OmniRoute) emitem arguments
        // "double-encoded" — uma string JSON contendo o objeto. O root
        // String fazia TryGetProperty lançar InvalidOperationException,
        // que escapava do catch (JsonException) e derrubava a run inteira.
        var doubleEncoded = JsonSerializer.Serialize(
            JsonSerializer.Serialize(new { path = "x.txt" }));
        var history = new List<ChatCompletionMessage>
        {
            new("assistant", "", ToolCallsJson: JsonSerializer.Serialize(new[]
            {
                new { id = "c1", function = new { name = "file_write", arguments = doubleEncoded } },
            })),
            new("tool", "gravado", "c1"),
            WriteCall("c2", "x.txt"),
            new("tool", "reescrito", "c2"),
        };

        var pruned = ContextPruner.Prune(history, "c");

        // Args desembrulhados → path recuperado → result superseded elidido.
        Assert.That(pruned[1].Content, Does.Contain("elidido").And.Contain("x.txt"));
    }

    [Test]
    public void ArgumentsComoObjeto_NaoDerrubaRun()
    {
        // Alguns providers serializam arguments como objeto JSON (não
        // string): GetString() lançava InvalidOperationException ("requires
        // an element of type 'String'...'Object'") e derrubava a run.
        var history = new List<ChatCompletionMessage>
        {
            new("assistant", "", ToolCallsJson: """[{"id":"c1","function":{"name":"file_write","arguments":{"path":"x.txt"}}}]"""),
            new("tool", "gravado", "c1"),
            WriteCall("c2", "x.txt"),
            new("tool", "reescrito", "c2"),
        };

        var pruned = ContextPruner.Prune(history, "c");

        Assert.That(pruned[1].Content, Does.Contain("elidido").And.Contain("x.txt"));
    }

    [Test]
    public void ArgumentsTextoSimples_NaoDerrubaRun()
    {
        // arguments não-JSON (texto puro) — malformado, ignora o alvo.
        var history = new List<ChatCompletionMessage>
        {
            new("assistant", "", ToolCallsJson: """[{"id":"c1","function":{"name":"file_write","arguments":"escreve x"}}]"""),
            new("tool", "gravado", "c1"),
        };

        var pruned = ContextPruner.Prune(history, "c");

        Assert.That(pruned[1].Content, Is.EqualTo("gravado"));
    }

    [Test]
    public void Structured_ParseJsonOuDegradaProsa()
    {
        var json = """{"summary":"fez X","files":["a.cs"],"decisions":["usar DI"],"pending":["testes"]}""";
        var ok = LlmContextSummarizer.Parse(json);
        var prose = LlmContextSummarizer.Parse("resposta em prosa sem json");

        Assert.Multiple(() =>
        {
            Assert.That(ok.Text, Is.EqualTo("fez X"));
            Assert.That(ok.Files, Is.EqualTo(new[] { "a.cs" }));
            Assert.That(ok.Decisions, Is.EqualTo(new[] { "usar DI" }));
            Assert.That(ok.Pending, Is.EqualTo(new[] { "testes" }));
            Assert.That(prose.Text, Is.EqualTo("resposta em prosa sem json"));
            Assert.That(prose.Files, Is.Empty);
        });
    }
}
