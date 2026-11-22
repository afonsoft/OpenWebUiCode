using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261124-compaction-diet: escada de redução do transcript do
/// summarizer — clip tool outputs → clip tool inputs → clip user →
/// omitir trocas antigas; mídia vira marcador; [pos] absoluto e a
/// última troca nunca omitida.
/// </summary>
[TestFixture]
public class ContextCompactionDietTests
{
    private static string Big(char c, int n) => new(c, n);

    // Abaixo do budget, saída idêntica ao render antigo.
    [Test]
    public void Diet_AbaixoDoBudget_Identico()
    {
        var head = new List<ChatCompletionMessage>
        {
            new("user", "oi"),
            new("assistant", "tudo bem"),
        };
        var t = ContextCompactionService.ApplyTranscriptDiet(
            head, 3, 100_000);
        Assert.That(t, Is.EqualTo("[3] user: oi\n\n[4] assistant: tudo bem"));
    }

    // Degrau 1: tool output gigante clipa com marcador e preserva as
    // pontas; user e assistant pequenos seguem intactos.
    [Test]
    public void Diet_ClipToolOutputs_PreservaHeadTail()
    {
        var tool = new string('T', 10_000);
        var head = new List<ChatCompletionMessage>
        {
            new("user", "rode o teste"),
            new("assistant", "ok", ToolCallsJson: "{}"),
            new("tool", tool),
            new("assistant", "falhou em 2 testes"),
        };
        // Budget: cabe após clipar o tool output (~4k+overhead), mas não
        // o transcript bruto (~10k).
        var t = ContextCompactionService.ApplyTranscriptDiet(
            head, 0, 8_000);
        Assert.Multiple(() =>
        {
            Assert.That(t, Does.Contain("[elided"));
            Assert.That(t, Does.Contain(tool[..2000]));
            Assert.That(t, Does.Contain(tool[^2000..]));
            Assert.That(t, Does.Contain("rode o teste"));
            Assert.That(t, Does.Contain("falhou em 2 testes"));
        });
    }

    // Escada inteira: budget apertado passa por todos os degraus — o
    // resultado preserva só a última troca, com [pos] absoluto.
    [Test]
    public void Diet_EscadaProgressiva_OmiteAteUltimaTroca()
    {
        var head = new List<ChatCompletionMessage>
        {
            new("user", Big('A', 6_000)),                      // [0]
            new("assistant", "velha", ToolCallsJson: "{}"),    // [1]
            new("tool", Big('B', 6_000)),                      // [2]
            new("user", Big('C', 6_000)),                      // [3]
            new("assistant", "resposta final curta"),          // [4]
        };
        var t = ContextCompactionService.ApplyTranscriptDiet(
            head, 0, 5_000);
        Assert.Multiple(() =>
        {
            Assert.That(t, Does.Not.Contain("[0] user"));
            Assert.That(t, Does.Contain("[3] user"));
            Assert.That(t, Does.Contain("[4] assistant"));
            Assert.That(t.Length, Is.LessThanOrEqualTo(5_000 + 100),
                "a última troca pode exceder o budget, mas só ela");
        });
    }

    // Última troca nunca é omitida: head de 1 troca gigante sai clipada,
    // não removida — mesmo com budget irreal.
    [Test]
    public void Diet_NuncaOmiteUltimaTroca()
    {
        var head = new List<ChatCompletionMessage>
        {
            new("user", Big('X', 12_000)),
            new("assistant", Big('Y', 8_000)),
        };
        var t = ContextCompactionService.ApplyTranscriptDiet(
            head, 7, 1_000);
        Assert.Multiple(() =>
        {
            Assert.That(t, Does.Contain("[7] user"));
            Assert.That(t, Does.Contain("[8] assistant"));
            Assert.That(t, Does.Contain("[elided"));
        });
    }

    // Mídia inline (data-URL) vira marcador — nunca binário pro
    // summarizer, mesmo abaixo do budget.
    [Test]
    public void Diet_Midia_ViraMarcador()
    {
        var b64 = Convert.ToBase64String(new byte[3_000]);
        var head = new List<ChatCompletionMessage>
        {
            new("user", $"veja: data:image/png;base64,{b64} fim"),
        };
        var t = ContextCompactionService.ApplyTranscriptDiet(
            head, 0, 100_000);
        Assert.Multiple(() =>
        {
            Assert.That(t, Does.Contain("[image/png omitted]"));
            Assert.That(t, Does.Not.Contain("base64,"));
        });
    }

    // Após omissões, os marcadores [pos] seguem absolutos — âncoras dos
    // breadcrumbs continuam apontando para o índice real do histórico.
    [Test]
    public void Diet_PosicoesAbsolutasAposOmissao()
    {
        var head = new List<ChatCompletionMessage>
        {
            new("user", Big('A', 6_000)),     // [10]
            new("assistant", Big('B', 6_000)),
            new("user", Big('C', 6_000)),     // [12]
            new("assistant", "fim"),
        };
        var t = ContextCompactionService.ApplyTranscriptDiet(
            head, 10, 2_000);
        Assert.Multiple(() =>
        {
            Assert.That(t, Does.Not.Contain("[10] user"));
            Assert.That(t, Does.Contain("[12] user"));
            Assert.That(t, Does.Contain("[13] assistant"));
        });
    }
}
