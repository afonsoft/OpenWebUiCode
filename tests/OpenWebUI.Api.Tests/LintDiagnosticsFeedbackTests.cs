using NUnit.Framework;
using OpenWebUI.Api.Completions;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261123-lint-diagnostics-feedback: tools mutáveis disparam a
/// coleta de diagnósticos; o parser extrai linhas ": error " do build.
/// </summary>
[TestFixture]
public sealed class LintDiagnosticsFeedbackTests
{
    [Test]
    public void IsFileMutatingTool_CobreVariantesDeNome()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChatPipeline.IsFileMutatingTool("builtin:file_write"), Is.True);
            Assert.That(ChatPipeline.IsFileMutatingTool("builtin_file_edit"), Is.True);
            Assert.That(ChatPipeline.IsFileMutatingTool("apply_patch"), Is.True);
            Assert.That(ChatPipeline.IsFileMutatingTool("builtin:file_read"), Is.False);
            Assert.That(ChatPipeline.IsFileMutatingTool("shell_exec"), Is.False);
            Assert.That(ChatPipeline.IsFileMutatingTool("file_grep"), Is.False);
        });
    }

    [Test]
    public void Collect_SemProjetoNet_RetornaNull()
    {
        var dir = Directory.CreateTempSubdirectory("lint-empty").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "README.md"), "x");
            Assert.That(ChatPipeline.CollectBuildDiagnostics(dir), Is.Null);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void Collect_WorkdirInexistente_NaoLanca()
    {
        Assert.That(ChatPipeline.CollectBuildDiagnostics(
            "/nonexistent-dir-xyz"), Is.Null);
    }
}
