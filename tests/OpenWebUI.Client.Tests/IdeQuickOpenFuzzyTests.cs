using NUnit.Framework;
using OpenWebUI.Client.Utils;

namespace OpenWebUI.Client.Tests;

/// <summary>SPEC-20261011-ide-editor-core — fuzzy do Quick Open (subsequência).</summary>
[TestFixture]
public class IdeQuickOpenFuzzyTests
{
    [Test]
    public void Score_SemQuery_RetornaZero()
    {
        Assert.That(IdeQuickOpenFuzzy.Score("", "src/Api.cs"), Is.EqualTo(0));
    }

    [Test]
    public void Score_MatchSimples_RetornaScore()
    {
        Assert.That(IdeQuickOpenFuzzy.Score("api", "src/Api.cs"), Is.Not.Null);
    }

    [Test]
    public void Score_NaoSubsequencia_RetornaNull()
    {
        Assert.That(IdeQuickOpenFuzzy.Score("zzz", "src/Api.cs"), Is.Null);
    }

    [Test]
    public void Match_BasenameTemBonus_SobreDir()
    {
        var files = new[] { "src/controllers/UserController.cs", "deep/nested/dir/xu.css" };
        var hits = IdeQuickOpenFuzzy.Match("uc", files);
        Assert.That(hits[0], Does.Contain("UserController"));
    }

    [Test]
    public void Match_Vazio_DevolvePrimeiros()
    {
        var files = new[] { "a.cs", "b.cs", "c.cs" };
        Assert.That(IdeQuickOpenFuzzy.Match("", files, 2), Has.Count.EqualTo(2));
    }
}
