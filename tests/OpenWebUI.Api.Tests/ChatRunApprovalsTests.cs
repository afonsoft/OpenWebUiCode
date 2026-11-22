using NUnit.Framework;
using OpenWebUI.Api.Runs;
using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261124-reject-siblings (portado do opencode dev): o reject do
/// dono nega também as demais aprovações de tool pendentes do mesmo
/// chat — pendências de <c>question_asked</c> (ask_user, plan_exit) e
/// de outros chats não são tocadas.
/// </summary>
[TestFixture]
public class ChatRunApprovalsTests
{
    private static RunApprovalAskedEvent Approval(string callId) =>
        new(callId, "shell", "exec", "{}");

    private static RunQuestionAskedEvent Question(string callId) =>
        new(callId, "Pergunta?", ["a", "b"], false);

    private static async Task<ChatRunApprovals.ApprovalResult> WaitForPendingAsync(
        ChatRunApprovals approvals, string runId, string chatId,
        string callId, string tool, object? payload)
    {
        var wait = approvals.WaitAsync(runId, chatId, callId, tool,
            CancellationToken.None, askedPayload: payload);
        await Task.Delay(50); // registra a pendência antes de resolver
        return await wait;
    }

    [Test]
    public async Task Resolve_Reject_NegaIrmasDaMesmaChat()
    {
        var approvals = new ChatRunApprovals();
        var t1 = WaitForPendingAsync(approvals, "r1", "c1", "k1", "shell", Approval("k1"));
        var t2 = WaitForPendingAsync(approvals, "r1", "c1", "k2", "write", Approval("k2"));
        var t3 = WaitForPendingAsync(approvals, "r1", "c1", "k3", "exec", Approval("k3"));

        Assert.That(approvals.Resolve("r1", "c1", "k1", approved: false, remember: false), Is.True);

        var (r1, r2, r3) = (await t1, await t2, await t3);
        Assert.Multiple(() =>
        {
            Assert.That(r1.Approved, Is.False);
            Assert.That(r2.Approved, Is.False, "irmã 1 devia ser negada");
            Assert.That(r3.Approved, Is.False, "irmã 2 devia ser negada");
        });
        Assert.That(approvals.PendingChatIds(), Does.Not.Contain("c1"));
    }

    [Test]
    public async Task Resolve_Reject_PerguntaAskedSobrevive()
    {
        var approvals = new ChatRunApprovals();
        var tool = WaitForPendingAsync(approvals, "r1", "c1", "k1", "shell", Approval("k1"));
        var ask = approvals.WaitAsync("r1", "c1", "q1", "ask_user",
            CancellationToken.None, askedPayload: Question("q1"));
        await Task.Delay(50);

        Assert.That(approvals.Resolve("r1", "c1", "k1", approved: false, remember: false), Is.True);
        Assert.That((await tool).Approved, Is.False);
        Assert.That(approvals.IsPending("r1", "q1"), Is.True,
            "a pergunta aberta não pode morrer junto com a tool rejeitada");

        Assert.That(approvals.Resolve("r1", "c1", "q1", approved: true, remember: false), Is.True);
        Assert.That((await ask).Approved, Is.True);
    }

    [Test]
    public async Task Resolve_Reject_NaoTocaOutroChat()
    {
        var approvals = new ChatRunApprovals();
        var t1 = WaitForPendingAsync(approvals, "r1", "c1", "k1", "shell", Approval("k1"));
        var other = approvals.WaitAsync("r2", "c2", "k9", "shell",
            CancellationToken.None, askedPayload: Approval("k9"));
        await Task.Delay(50);

        Assert.That(approvals.Resolve("r1", "c1", "k1", approved: false, remember: false), Is.True);
        Assert.That((await t1).Approved, Is.False);
        Assert.That(approvals.IsPending("r2", "k9"), Is.True,
            "pendência de outro chat não pode ser negada");

        approvals.Cancel("r2");
        Assert.That((await other).Approved, Is.False);
    }

    [Test]
    public async Task Resolve_Approve_NaoCascata()
    {
        var approvals = new ChatRunApprovals();
        var t1 = WaitForPendingAsync(approvals, "r1", "c1", "k1", "shell", Approval("k1"));
        var t2 = approvals.WaitAsync("r1", "c1", "k2", "write",
            CancellationToken.None, askedPayload: Approval("k2"));
        await Task.Delay(50);

        Assert.That(approvals.Resolve("r1", "c1", "k1", approved: true, remember: false), Is.True);
        Assert.That((await t1).Approved, Is.True);
        Assert.That(approvals.IsPending("r1", "k2"), Is.True,
            "approve resolve só a alvo — irmã continua pendente");

        approvals.Cancel("r1");
        await t2;
    }
}
