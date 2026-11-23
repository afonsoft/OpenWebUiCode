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

    [Test]
    public void Collect_ProjetoQuebrado_RetornaLinhasDeErro()
    {
        var dir = Directory.CreateTempSubdirectory("lint-broken").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Broken.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(dir, "Bad.cs"),
                "public class Bad { public void M( { } }");
            var errors = ChatPipeline.CollectBuildDiagnostics(dir);
            Assert.Multiple(() =>
            {
                Assert.That(errors, Is.Not.Null.And.Not.Empty);
                Assert.That(errors!.All(e => e.Contains(": error")), Is.True);
            });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void Collect_ProjetoLimpo_RetornaNull()
    {
        var dir = Directory.CreateTempSubdirectory("lint-ok").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Ok.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(dir, "Ok.cs"),
                "public class Ok { }");
            Assert.That(ChatPipeline.CollectBuildDiagnostics(dir), Is.Null);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
