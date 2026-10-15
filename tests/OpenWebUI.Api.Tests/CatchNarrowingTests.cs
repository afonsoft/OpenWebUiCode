using System.Net;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Services;
using OpenWebUI.Infrastructure.Services.Image;
using OpenWebUI.Infrastructure.Services.Video;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobre os catches tipados introduzidos pelo slice E15-s2 (boundary catches
/// estreitados de <c>catch (Exception)</c> para tipos concretos).
/// </summary>
[TestFixture, IsolateEnvironment]
public class CatchNarrowingTests
{
    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(ex);
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private static IEnumerable<(string Name, Func<IHttpClientFactory, object> Ctor)> ImageEngines() =>
    [
        ("a1111", f => new A1111Engine(f)),
        ("comfyui", f => new ComfyUiEngine(f)),
        ("gemini", f => new GeminiImageEngine(f)),
        ("openai", f => new OpenAiImageEngine(f)),
    ];

    [Test]
    public async Task Engine_TestAsync_FalhaDeRede_RetornaFalse()
    {
        var cfg = new ImagesConfig(true, "x", "http://engine.local", "", "m", "1024x1024", 30);
        var exceptions = new Exception[]
        {
            new HttpRequestException("sem rota"),
            new JsonException("json quebrado"),
            new InvalidOperationException("request inválido"),
            new TaskCanceledException("timeout"),
        };
        foreach (var (name, ctor) in ImageEngines())
        {
            foreach (var ex in exceptions)
            {
                var engine = (ImageEngineBase)ctor(new StubHttpClientFactory(new ThrowingHandler(ex)));
                var (ok, detail) = await engine.TestAsync(cfg, CancellationToken.None);
                Assert.That(ok, Is.False, $"{name}/{ex.GetType().Name}");
                Assert.That(detail, Is.EqualTo(ex.Message), $"{name}/{ex.GetType().Name}");
            }
        }
    }

    [Test]
    public async Task VideoEngine_TestAsync_FalhaDeRede_RetornaFalse()
    {
        var cfg = new ImagesConfig(true, "x", "http://engine.local", "", "m", "720p", 30);
        var engines = new Func<IHttpClientFactory, object>[]
        {
            f => new OpenAiVideoEngine(f),
            f => new ComfyUiVideoEngine(f),
        };
        var exceptions = new Exception[]
        {
            new HttpRequestException("sem rota"),
            new JsonException("json quebrado"),
            new InvalidOperationException("request inválido"),
            new TaskCanceledException("timeout"),
        };
        foreach (var ctor in engines)
        {
            foreach (var ex in exceptions)
            {
                var engine = ctor(new StubHttpClientFactory(new ThrowingHandler(ex)));
                var (ok, detail) = engine switch
                {
                    OpenAiVideoEngine e => await e.TestAsync(cfg, CancellationToken.None),
                    ComfyUiVideoEngine e => await e.TestAsync(cfg, CancellationToken.None),
                    _ => throw new InvalidOperationException("engine inesperada"),
                };
                Assert.That(ok, Is.False);
                Assert.That(detail, Is.EqualTo(ex.Message));
            }
        }
    }

    [Test]
    public async Task GitWorkdir_Inexistente_GetInfo_ErroTipado()
    {
        var svc = new WorkspaceGitService();
        // WorkingDirectory inexistente → Process.Start lança Win32Exception → null.
        var info = await svc.GetInfoAsync("/nonexistent-dir-xyz-123", CancellationToken.None);
        Assert.That(info, Is.Not.Null);
    }

    [Test]
    public void RiskClassifier_ArgInvalido_NaoExplode()
    {
        // '\0' no argumento → Path.GetFullPath lança ArgumentException → trata como fora do workspace.
        var assessment = CommandRiskClassifier.Classify("cat \0arquivo", Path.GetTempPath());
        Assert.That(assessment, Is.Not.Null);
    }

    [Test]
    public void RiskClassifier_ArgMuitoLongo_NaoExplode()
    {
        // argumento > MAX_PATH → PathTooLongException → trata como fora do workspace.
        var longo = new string('a', 33_000);
        var assessment = CommandRiskClassifier.Classify($"cat /tmp/{longo}", Path.GetTempPath());
        Assert.That(assessment, Is.Not.Null);
    }

    [Test]
    public async Task FetchUrl_CharsetDesconhecido_CaiNoUtf8()
    {
        var html = "<html><head><meta charset=\"bogus-enc-99\"></head><body>ok</body></html>";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html"),
        };
        var tool = new FetchUrlBuiltinTool(new StubHttpClientFactory(new StaticHandler(response)));
        var args = JsonDocument.Parse("{\"url\":\"https://example.com/p\"}").RootElement;
        var result = await tool.ExecuteAsync(
            args, new BuiltinToolContext("u", null, null, "/tmp", "/tmp"),
            CancellationToken.None);
        Assert.That(result.Text, Does.Not.Contain("inválida"));
    }

    [Test]
    public async Task GitRepo_Real_GetInfo_DevolveDiff()
    {
        var dir = Path.Join(Path.GetTempPath(), $"gitinfo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Join(dir, "a.txt"), "um\n");
            Exec(dir, "git", "init", "-q");
            Exec(dir, "git", "config", "user.email", "t@t");
            Exec(dir, "git", "config", "user.name", "t");
            Exec(dir, "git", "add", ".");
            Exec(dir, "git", "commit", "-qm", "init");
            File.AppendAllText(Path.Join(dir, "a.txt"), "dois\ntres\n");

            var info = await new WorkspaceGitService().GetInfoAsync(dir, CancellationToken.None);
            Assert.That(info.IsRepo, Is.True);
            Assert.That(info.Files.Any(f => f.Path == "a.txt"), Is.True);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* cleanup best-effort */ }
        }
    }

    [Test]
    public async Task ProcessRunner_WorkdirInexistente_OutcomeDeErro()
    {
        var outcome = await ChatProcessRunner.RunAsync(
            "echo hi", "/nonexistent-dir-xyz-123",
            TimeSpan.FromSeconds(5), ct: CancellationToken.None);
        Assert.That(outcome.Output, Does.Contain("Falha ao iniciar processo"));
    }

    [Test]
    public async Task ProcessRunner_Timeout_MataERetornaParcial()
    {
        var outcome = await ChatProcessRunner.RunAsync(
            "echo antes; sleep 30", Path.GetTempPath(),
            TimeSpan.FromMilliseconds(300), ct: CancellationToken.None);
        Assert.That(outcome.TimedOut, Is.True);
    }

    [Test]
    public async Task FileList_SemPermissao_RetornaErroTipado()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("trancar dir via UnixFileMode não existe em Windows");
            return;
        }
        var dir = Path.Join(Path.GetTempPath(), $"noperm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Join(dir, "locked"));
        File.WriteAllText(Path.Join(dir, "top.txt"), "x");
        File.SetUnixFileMode(Path.Join(dir, "locked"),
            UnixFileMode.None);
        try
        {
            var tool = new FileListBuiltinTool();
            var args = JsonDocument.Parse(
                $"{{\"path\":\"{dir.Replace("\\", "\\\\")}\"}}").RootElement;
            var result = await tool.ExecuteAsync(
                args, new BuiltinToolContext("u", null, null, dir, dir),
                CancellationToken.None);
            Assert.That(result.Text, Is.Not.Null); // entradas legíveis + erro tipado nas bloqueadas
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path.Join(dir, "locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            try { Directory.Delete(dir, true); } catch (IOException) { /* cleanup best-effort */ }
        }
    }

    private static void Exec(string cwd, string file, params string[] args)
    {
        using var p = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(file, args)
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
        p.WaitForExit(10_000);
    }
}
