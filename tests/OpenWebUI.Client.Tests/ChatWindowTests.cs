using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Client.Services;

namespace OpenWebUI.Client.Tests;

/// <summary>SPEC-20261019-chat-window-perf RF-002/003 — janela deslizante do chat.</summary>
[TestFixture]
public class ChatWindowTests
{
    private static ChatMessageModel Msg(string id, string role, string content = "x") =>
        new(id, role, content, null, 0);

    [Test]
    public void TrimToTail_JanelaMenor_NaoCorta()
    {
        var msgs = new List<ChatMessageModel> { Msg("a", "user"), Msg("b", "assistant") };
        Assert.That(ChatWindow.TrimToTail(msgs, 60), Is.EqualTo(0));
        Assert.That(msgs, Has.Count.EqualTo(2));
    }

    [Test]
    public void TrimToTail_CortaDoTopoMantendoCauda()
    {
        var msgs = Enumerable.Range(1, 120)
            .Select(i => Msg($"m{i}", i % 2 == 1 ? "user" : "assistant"))
            .ToList();
        Assert.That(ChatWindow.TrimToTail(msgs, 30), Is.EqualTo(90));
        Assert.That(msgs, Has.Count.EqualTo(30));
        Assert.That(msgs[0].Id, Is.EqualTo("m91"));
        Assert.That(msgs[^1].Id, Is.EqualTo("m120"));
    }

    [Test]
    public void TrimToTail_NaoAbreNoMeioDeGrupoTool()
    {
        var msgs = new List<ChatMessageModel>
        {
            Msg("m1", "user"), Msg("m2", "assistant"),
            Msg("m3", "tool"), Msg("m4", "tool"), Msg("m5", "tool"),
            Msg("m6", "assistant"), Msg("m7", "user"), Msg("m8", "assistant"),
        };
        // tail=5 abriria em m4 (meio do grupo tool) — estende até m3.
        ChatWindow.TrimToTail(msgs, 5);
        Assert.That(msgs.Select(m => m.Id),
            Is.EqualTo(new[] { "m3", "m4", "m5", "m6", "m7", "m8" }));
    }

    [Test]
    public void TrimToCharCap_AbaixoDoTeto_NaoCorta()
    {
        var msgs = new List<ChatMessageModel> { Msg("a", "user", "12345") };
        Assert.That(ChatWindow.TrimToCharCap(msgs, 100), Is.EqualTo(0));
    }

    [Test]
    public void TrimToCharCap_CortaTopoAteCaber()
    {
        var msgs = new List<ChatMessageModel>
        {
            Msg("a", "user", new string('x', 60)),
            Msg("b", "assistant", new string('x', 60)),
            Msg("c", "user", new string('x', 30)),
        };
        // cap=100: 'c'(30) + 'b'(60) = 90 cabe; 'a' estoura.
        Assert.That(ChatWindow.TrimToCharCap(msgs, 100), Is.EqualTo(1));
        Assert.That(msgs.Select(m => m.Id), Is.EqualTo(new[] { "b", "c" }));
    }

    [Test]
    public void TrimToCharCap_PrimeiroRestanteNuncaEhTool()
    {
        var msgs = new List<ChatMessageModel>
        {
            Msg("a", "user", new string('x', 50)),
            Msg("t1", "tool", new string('x', 50)),
            Msg("t2", "tool", new string('x', 50)),
            Msg("b", "assistant", new string('x', 10)),
        };
        // cap=60: 'b'(10)+'t2'(50)=60; corte em 't2' avança até fora do grupo.
        ChatWindow.TrimToCharCap(msgs, 60);
        Assert.That(msgs[^1].Id, Is.EqualTo("b"));
        Assert.That(msgs[0].Role, Is.Not.EqualTo("tool"));
    }
}
