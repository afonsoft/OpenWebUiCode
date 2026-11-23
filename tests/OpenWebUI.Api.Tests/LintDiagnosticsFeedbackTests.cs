using NUnit.Framework;
using OpenWebUI.Api.Completions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.ChatTools;
using System.Text;

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

    private static ChatPipeline.ToolLoopMutable NewState() => new()
    {
        Request = new ChatCompletionRequest("m", []),
        Messages = [],
        ToolMessages = [],
        StepUsages = [],
        ContinuedAnswer = new StringBuilder(),
    };

    [Test]
    public async Task Inject_ComErrosDeBuild_AdicionaNota()
    {
        var dir = Directory.CreateTempSubdirectory("lint-inj").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "B.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(dir, "B.cs"), "class B { void M( {}");
            var state = NewState();
            state.EditsApplied = 1;
            var ctx = new BuiltinToolContext("u", null, null, dir, dir);
            var published = 0;
            var callbacks = new ChatPipeline.ToolLoopCallbacks(
                OnMessagesAddedAsync: (m, t, ct) =>
                {
                    published += m.Count;
                    return Task.CompletedTask;
                });
            await ChatPipeline.InjectLintDiagnosticsAsync(
                state, ctx, callbacks, null, CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(state.EditsApplied, Is.Zero);
                Assert.That(state.Messages, Has.Count.EqualTo(1));
                Assert.That(state.Messages[0].Content,
                    Does.Contain("<lint_diagnostics>"));
                Assert.That(published, Is.EqualTo(1));
            });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Inject_SemEdicoes_NaoColeta()
    {
        var dir = Directory.CreateTempSubdirectory("lint-noop").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "B.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(dir, "B.cs"), "class B { void M( {}");
            var state = NewState(); // EditsApplied = 0
            var ctx = new BuiltinToolContext("u", null, null, dir, dir);
            await ChatPipeline.InjectLintDiagnosticsAsync(
                state, ctx, null, null, CancellationToken.None);
            Assert.That(state.Messages, Is.Empty);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Inject_SemContexto_NaoColeta()
    {
        var state = NewState();
        state.EditsApplied = 2;
        await ChatPipeline.InjectLintDiagnosticsAsync(
            state, null, null, null, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.EditsApplied, Is.Zero);
            Assert.That(state.Messages, Is.Empty);
        });
    }
}
