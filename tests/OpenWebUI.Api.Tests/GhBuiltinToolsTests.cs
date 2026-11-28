using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;

namespace OpenWebUI.Api.Tests;

/// <summary>SPEC-20261128-gh-tools (plano P1.17): metadados do
/// <c>builtin:gh_pulls</c>.</summary>
public class GhBuiltinToolsTests
{
    [Test]
    public void GhPulls_MetadadosEsperados()
    {
        var tool = new GhPullsBuiltinTool(null!, null!);
        Assert.Multiple(() =>
        {
            Assert.That(tool.Name, Is.EqualTo("gh_pulls"));
            Assert.That(tool.Tier, Is.EqualTo(ApprovalTier.Read));
            Assert.That(tool.Description, Does.Contain("pull request").IgnoreCase);
        });
    }

    [Test]
    public void GhView_MetadadosEsperados()
    {
        var tool = new GhViewBuiltinTool(null!, null!);
        Assert.Multiple(() =>
        {
            Assert.That(tool.Name, Is.EqualTo("gh_view"));
            Assert.That(tool.Tier, Is.EqualTo(ApprovalTier.Read));
            Assert.That(tool.ParametersJson, Does.Contain("number"));
        });
    }


    [Test]
    public void GhDiff_MetadadosEsperados()
    {
        var tool = new GhDiffBuiltinTool(null!, null!);
        Assert.Multiple(() =>
        {
            Assert.That(tool.Name, Is.EqualTo("gh_diff"));
            Assert.That(tool.Tier, Is.EqualTo(ApprovalTier.Read));
            Assert.That(tool.ParametersJson, Does.Contain("number"));
        });
    }

    [Test]
    public void GhComment_MetadadosWriteForaDoPlan()
    {
        var tool = new GhCommentBuiltinTool(null!, null!);
        Assert.Multiple(() =>
        {
            Assert.That(tool.Name, Is.EqualTo("gh_comment"));
            Assert.That(tool.Tier, Is.EqualTo(ApprovalTier.Write));
            Assert.That(((IBuiltinChatTool)tool).RequiresApproval, Is.True);
            Assert.That(tool.ParametersJson, Does.Contain("body"));
        });
    }

    [Test]
    public void GhPulls_EhPermitidoNoPlanMode()
    {
        var field = typeof(PermissionRuleset)
            .GetField("PlanAllowed",
                System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Static);
        Assert.That(field, Is.Not.Null);
        var set = field!.GetValue(null)
            as System.Collections.Generic.HashSet<string>;
        Assert.That(set, Is.Not.Null.And.Contain("gh_pulls"));
        Assert.That(set, Does.Contain("gh_view"));
        Assert.That(set, Does.Contain("gh_diff"));
        Assert.That(set, Does.Not.Contain("gh_comment"));
    }
}
