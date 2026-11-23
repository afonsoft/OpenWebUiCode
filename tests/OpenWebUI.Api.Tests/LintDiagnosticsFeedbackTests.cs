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
    public void Parse_SomenteLinhasDeError_DedupECap()
    {
        var lines = new List<string> { "Build started", "warn: ignora" };
        for (var i = 0; i < 20; i++)
        {
            lines.Add($"F{i}.cs({i},1): error CS{i:D4}: erro {i}");
            lines.Add($"F{i}.cs({i},1): error CS{i:D4}: erro {i}"); // dup
        }
        var errors = ChatPipeline.ParseBuildErrors(string.Join('\n', lines));
        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Not.Null);
            Assert.That(errors!, Has.Count.EqualTo(15), "cap de 15");
            Assert.That(errors!.All(e => e.Contains(": error")), Is.True);
        });
    }

    [Test]
    public void Parse_SemErros_RetornaNull()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChatPipeline.ParseBuildErrors(
                "Build succeeded\n0 Error(s)"), Is.Null);
            Assert.That(ChatPipeline.ParseBuildErrors(
                "warn: algo estranho"), Is.Null);
        });
    }

    private static ChatPipeline.ToolLoopMutable NewState() => new()
    {
        Request = new ChatCompletionRequest("m", []),
        Messages = [],
        ToolMessages = [],
        StepUsages = [],
        ContinuedAnswer = new StringBuilder(),
    };

    private static BuiltinToolContext Ctx(string dir) =>
        new("u", null, null, dir, dir);

    [Test]
    public async Task Inject_ComErros_AdicionaNotaEPublica()
    {
        var dir = Directory.CreateTempSubdirectory("lint-inj").FullName;
        try
        {
            var state = NewState();
            state.EditsApplied = 1;
            var published = 0;
            var callbacks = new ChatPipeline.ToolLoopCallbacks(
                OnMessagesAddedAsync: (m, t, ct) =>
                {
                    published += m.Count;
                    return Task.CompletedTask;
                });
            await ChatPipeline.InjectLintDiagnosticsAsync(
                state, Ctx(dir), callbacks, null, CancellationToken.None,
                collect: _ => ["a.cs(1,1): error CS1: x"]);
            Assert.Multiple(() =>
            {
                Assert.That(state.EditsApplied, Is.Zero);
                Assert.That(state.Messages, Has.Count.EqualTo(1));
                Assert.That(state.Messages[0].Content,
                    Does.Contain("<lint_diagnostics>")
                        .And.Contain("a.cs(1,1): error CS1: x"));
                Assert.That(published, Is.EqualTo(1));
            });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Inject_BuildLimpo_NaoAdicionaNota()
    {
        var dir = Directory.CreateTempSubdirectory("lint-clean").FullName;
        try
        {
            var state = NewState();
            state.EditsApplied = 3;
            await ChatPipeline.InjectLintDiagnosticsAsync(
                state, Ctx(dir), null, null, CancellationToken.None,
                collect: _ => null);
            Assert.Multiple(() =>
            {
                Assert.That(state.EditsApplied, Is.Zero);
                Assert.That(state.Messages, Is.Empty);
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
            var state = NewState(); // EditsApplied = 0
            var collected = false;
            await ChatPipeline.InjectLintDiagnosticsAsync(
                state, Ctx(dir), null, null, CancellationToken.None,
                collect: _ => { collected = true; return ["e"]; });
            Assert.Multiple(() =>
            {
                Assert.That(collected, Is.False);
                Assert.That(state.Messages, Is.Empty);
            });
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
