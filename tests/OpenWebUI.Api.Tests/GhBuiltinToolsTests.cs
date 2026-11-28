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
    }
}
