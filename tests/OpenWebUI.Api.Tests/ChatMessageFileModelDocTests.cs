using OpenWebUI.Application.Contracts;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261128-chat-doc-attachments: classificação client-side
/// <see cref="ChatMessageFileModel.DocKind"/>/<see cref="ChatMessageFileModel.IsDoc"/>
/// — extensão e Content-Type decidem o card de documento; mídia e
/// binários seguem no thumb/chip genérico.
/// </summary>
[TestFixture]
public class ChatMessageFileModelDocTests
{
    private static ChatMessageFileModel File(string name, string? contentType) =>
        new("f1", name, contentType, 123, "file");

    [TestCase("README.md", null)]
    [TestCase("guia.markdown", "text/plain")]
    [TestCase("notas", "text/markdown")]
    public void DocKind_Markdown_PorExtensaoOuMime(string name, string? contentType)
    {
        var f = File(name, contentType);
        Assert.Multiple(() =>
        {
            Assert.That(f.DocKind, Is.EqualTo("markdown"));
            Assert.That(f.IsDoc, Is.True);
        });
    }

    [TestCase("app.log", null)]
    [TestCase("dados.csv", "text/csv")]
    [TestCase("config.yaml", null)]
    [TestCase("payload.json", "application/json")]
    [TestCase("Program.cs", "application/octet-stream")]
    [TestCase("script.py", null)]
    [TestCase("sem_extensao", "text/plain")]
    public void DocKind_Text_PorExtensaoOuMimeTexto(string name, string? contentType)
    {
        var f = File(name, contentType);
        Assert.Multiple(() =>
        {
            Assert.That(f.DocKind, Is.EqualTo("text"));
            Assert.That(f.IsDoc, Is.True);
        });
    }

    [TestCase("foto.png", "image/png")]
    [TestCase("video.mp4", "video/mp4")]
    [TestCase("app.zip", "application/zip")]
    [TestCase("bin", "application/octet-stream")]
    [TestCase("doc.pdf", "application/pdf")]
    public void DocKind_Null_ParaBinariosENaoTexto(string name, string? contentType)
    {
        var f = File(name, contentType);
        Assert.Multiple(() =>
        {
            Assert.That(f.DocKind, Is.Null);
            Assert.That(f.IsDoc, Is.False);
        });
    }

    [Test]
    public void DocKind_Null_QuandoTypeNaoEhFile()
    {
        // image/video resolvem pelo pipeline de mídia — nunca viram doc.
        var img = new ChatMessageFileModel("f2", "x.md", "image/png", 1, "image");
        var vid = new ChatMessageFileModel("f3", "x.txt", "video/mp4", 1, "video");
        Assert.Multiple(() =>
        {
            Assert.That(img.IsDoc, Is.False);
            Assert.That(vid.IsDoc, Is.False);
        });
    }
}
