using NUnit.Framework;
using OpenWebUI.Api.Completions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.ChatTools;
using System.Text;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261124-devin-parity-batch2 RF-002 (Devin user-edits-injected):
/// WorkspaceEditWatcher filtra ruído e escritas de tools; o pipeline
/// injeta <c>&lt;user_edits&gt;</c> na fronteira do round.
/// </summary>
[TestFixture]
public sealed class UserEditsInjectionTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp() =>
        _dir = Directory.CreateTempSubdirectory("user-edits").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, recursive: true);

    // ---------------- WorkspaceEditWatcher ----------------

    [Test]
    public void Drain_PathExterno_ListaRelativo()
    {
        using var w = new WorkspaceEditWatcher(_dir);
        w.Track(Path.Join(_dir, "src", "App.cs"));
        w.Track(Path.Join(_dir, "src", "App.cs")); // dedup
        var drain = w.Drain();
        Assert.That(drain, Is.EqualTo(new[] { "src/App.cs" }));
        Assert.That(w.Drain(), Is.Empty, "segundo drain é vazio");
    }

    [Test]
    public void Drain_MarkWrite_SuprimeEscritaDaTool()
    {
        using var w = new WorkspaceEditWatcher(_dir);
        var toolFile = Path.Join(_dir, "feito-pela-tool.cs");
        w.MarkWrite(toolFile);
        w.Track(toolFile); // FSW viu a escrita — mas é nossa.
        w.Track(Path.Join(_dir, "editado-pelo-usuario.cs"));
        Assert.That(w.Drain(),
            Is.EqualTo(new[] { "editado-pelo-usuario.cs" }));
    }

    [Test]
    public void Track_Ruido_EhIgnorado()
    {
        using var w = new WorkspaceEditWatcher(_dir);
        w.Track(Path.Join(_dir, ".chat-jobs", "j1.log"));
        w.Track(Path.Join(_dir, ".git", "index"));
        w.Track(Path.Join(_dir, "bin", "Debug", "app.dll"));
        w.Track(Path.Join(_dir, "obj", "cache.o"));
        w.Track(Path.Join(_dir, "node_modules", "pkg", "i.js"));
        w.Track(Path.Join(_dir, "scratch.tmp"));
        w.Track(Path.Join(_dir, "swap.swp"));
        w.Track(Path.Join(_dir, "backup~"));
        w.Track(Path.Join(_dir, "real.cs"));
        Assert.That(w.Drain(), Is.EqualTo(new[] { "real.cs" }));
    }

    [Test]
    public void Drain_CapVinte_Trunca()
    {
        using var w = new WorkspaceEditWatcher(_dir);
        for (var i = 0; i < 30; i++)
        {
            w.Track(Path.Join(_dir, $"f{i:D2}.cs"));
        }
        Assert.That(w.Drain(), Has.Count.EqualTo(WorkspaceEditWatcher.DrainCap));
    }

    [Test]
    public void IsNoise_CasosClassicos()
    {
        Assert.Multiple(() =>
        {
            Assert.That(WorkspaceEditWatcher.IsNoise("/w/.git/config"), Is.True);
            Assert.That(WorkspaceEditWatcher.IsNoise("/w/bin/x.dll"), Is.True);
            Assert.That(WorkspaceEditWatcher.IsNoise("/w/x.tmp"), Is.True);
            Assert.That(WorkspaceEditWatcher.IsNoise("/w/src/App.cs"), Is.False);
        });
    }

    // ---------------- Extração de paths escritos pela tool ----------------

    [Test]
    public void ExtractWrittenPaths_PathArg()
    {
        var paths = ChatPipeline.ExtractWrittenPaths(
            """{"path":"src/A.cs"}""");
        Assert.That(paths, Is.EqualTo(new[] { "src/A.cs" }));
    }

    [Test]
    public void ExtractWrittenPaths_NotebookPath()
    {
        var paths = ChatPipeline.ExtractWrittenPaths(
            """{"notebook_path":"nb.ipynb","cell_number":0}""");
        Assert.That(paths, Is.EqualTo(new[] { "nb.ipynb" }));
    }

    [Test]
    public void ExtractWrittenPaths_PatchBody_ExtraiAlvos()
    {
        var patch = "*** Begin Patch\n"
            + "*** Add File: novo.cs\n+linha\n"
            + "*** Update File: velho.cs\n*** Move to: renomeado.cs\n@@ x\n"
            + "*** Delete File: morto.cs\n*** End Patch";
        var argsJson = System.Text.Json.JsonSerializer.Serialize(
            new { patch });
        var paths = ChatPipeline.ExtractWrittenPaths(argsJson);
        Assert.That(paths, Is.EqualTo(
            new[] { "novo.cs", "velho.cs", "renomeado.cs", "morto.cs" }));
    }

    [Test]
    public void ExtractPatchTargets_Direto()
    {
        var targets = ChatPipeline.ExtractPatchTargets(
            "*** Add File: a.cs\n*** Delete File: b.cs\n*** Move to: c.cs\nfoo").ToList();
        Assert.That(targets, Is.EqualTo(new[] { "a.cs", "b.cs", "c.cs" }));
        Assert.That(ChatPipeline.ExtractPatchTargets(null), Is.Empty);
        Assert.That(ChatPipeline.ExtractPatchTargets(""), Is.Empty);
    }

    [Test]
    public void ExtractWrittenPaths_ArgsQuebrados_NaoLanca()
    {
        Assert.That(ChatPipeline.ExtractWrittenPaths("{{{"), Is.Empty);
        Assert.That(ChatPipeline.ExtractWrittenPaths("{}"), Is.Empty);
    }

    // ---------------- ApplyUserEditsAsync ----------------

    private static ChatPipeline.ToolLoopMutable NewState() => new()
    {
        Request = new ChatCompletionRequest("m", []),
        Messages = [],
        ToolMessages = [],
        StepUsages = [],
        ContinuedAnswer = new StringBuilder(),
    };

    [Test]
    public async Task ApplyUserEdits_ComMudancas_InjetaNota()
    {
        using var w = new WorkspaceEditWatcher(_dir);
        w.Track(Path.Join(_dir, "tocado.cs"));
        var ctx = new BuiltinToolContext("u", null, null, _dir, _dir,
            EditWatcher: w);
        var state = NewState();
        var published = 0;
        var callbacks = new ChatPipeline.ToolLoopCallbacks(
            OnMessagesAddedAsync: (m, t, ct) =>
            {
                published += m.Count;
                return Task.CompletedTask;
            });
        await ChatPipeline.ApplyUserEditsAsync(
            state, ctx, callbacks, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Messages, Has.Count.EqualTo(1));
            Assert.That(state.Messages[0].Content,
                Does.Contain("<user_edits>").And.Contain("tocado.cs"));
            Assert.That(state.Messages[0].Role, Is.EqualTo("user"));
            Assert.That(published, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ApplyUserEdits_SemMudancas_NoOp()
    {
        using var w = new WorkspaceEditWatcher(_dir);
        var ctx = new BuiltinToolContext("u", null, null, _dir, _dir,
            EditWatcher: w);
        var state = NewState();
        await ChatPipeline.ApplyUserEditsAsync(
            state, ctx, null, CancellationToken.None);
        Assert.That(state.Messages, Is.Empty);
    }

    [Test]
    public async Task ApplyUserEdits_SemWatcher_NoOp()
    {
        var state = NewState();
        await ChatPipeline.ApplyUserEditsAsync(
            state, new BuiltinToolContext("u", null, null, _dir, _dir),
            null, CancellationToken.None);
        Assert.That(state.Messages, Is.Empty);
    }
}
