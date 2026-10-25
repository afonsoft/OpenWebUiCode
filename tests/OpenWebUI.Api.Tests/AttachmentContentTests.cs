using System.Text;
using NUnit.Framework;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261018-attachments-v2 RF-001: decode, sniff de binário, caps
/// por arquivo/mensagem e forma do bloco &lt;file&gt;.
/// </summary>
[TestFixture]
public class AttachmentContentTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"att-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private FileEntry Seed(string name, byte[] bytes, string mime = "text/plain")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return new FileEntry
        {
            Id = name, UserId = "u", Filename = name, ContentType = mime,
            StoragePath = path, Size = bytes.LongLength,
        };
    }

    [Test]
    public async Task TextoUtf8_DecodificaComoTexto()
    {
        var f = Seed("a.txt", Encoding.UTF8.GetBytes("olá mundo — ç"));
        var text = await AttachmentContentService.TryReadTextAsync(f);
        Assert.That(text, Is.EqualTo("olá mundo — ç"));
    }

    [Test]
    public async Task Latin1_CaiNoFallbackQuandoUtf8Invalido()
    {
        // "café" em latin1: 0xE9 inválido como UTF-8 standalone.
        var f = Seed("a.txt", new byte[] { 0x63, 0x61, 0x66, 0xE9 });
        var text = await AttachmentContentService.TryReadTextAsync(f);
        Assert.That(text, Is.EqualTo("café"));
    }

    [Test]
    public async Task Binario_ComNul_RetornaNull()
    {
        var bytes = new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02, 0x03 };
        var f = Seed("a.exe", bytes, "application/octet-stream");
        // .exe não é candidato: IsTextCandidate já filtra — force via extensão de texto com NUL dentro.
        var f2 = Seed("b.txt", bytes);
        Assert.That(await AttachmentContentService.TryReadTextAsync(f2), Is.Null);
    }

    [Test]
    public void Candidatos_ExtensaoEMime()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AttachmentContentService.IsTextCandidate(
                new FileEntry { Filename = "x.cs", ContentType = "application/octet-stream" }), Is.True);
            Assert.That(AttachmentContentService.IsTextCandidate(
                new FileEntry { Filename = "x.bin", ContentType = "application/json" }), Is.True);
            Assert.That(AttachmentContentService.IsTextCandidate(
                new FileEntry { Filename = "x.bin", ContentType = "text/whatever" }), Is.True);
            Assert.That(AttachmentContentService.IsTextCandidate(
                new FileEntry { Filename = "x.exe", ContentType = "application/octet-stream" }), Is.False);
            Assert.That(AttachmentContentService.IsTextCandidate(
                new FileEntry { Filename = "x", ContentType = null, ExtractedText = "y" }), Is.True);
        });
    }

    [Test]
    public void WrapFile_TruncaAcimaDe256KB()
    {
        var big = new string('x', AttachmentContentService.MaxFileChars + 100);
        var block = AttachmentContentService.WrapFile("big.txt", big);
        Assert.Multiple(() =>
        {
            Assert.That(block, Does.StartWith("<file name=\"big.txt\">\n"));
            Assert.That(block, Does.EndWith("</file>"));
            Assert.That(block, Does.Contain("[…truncated]"));
            Assert.That(block.Length, Is.LessThan(big.Length));
        });
    }

    [Test]
    public void WrapFile_EscapaAspasNoNome()
    {
        var block = AttachmentContentService.WrapFile("a\"b.txt", "x");
        Assert.That(block, Does.Contain("<file name=\"a'b.txt\">"));
    }

    [Test]
    public async Task InjectTextFiles_InjetaBlocoNoFimDaMensagem()
    {
        var f = Seed("a.txt", Encoding.UTF8.GetBytes("conteúdo"));
        var msgs = new List<ChatCompletionMessage>
        {
            new("user", "pergunta"),
        };
        var entries = new Dictionary<string, FileEntry> { ["a.txt"] = f };
        var out_ = await AttachmentContentService.InjectTextFilesAsync(
            msgs, entries, ["a.txt"],
            (fe, ct) => AttachmentContentService.TryReadTextAsync(fe, ct));
        Assert.Multiple(() =>
        {
            Assert.That(out_[0].Content, Does.StartWith("pergunta"));
            Assert.That(out_[0].Content, Does.Contain("<file name=\"a.txt\">\nconteúdo\n</file>"));
        });
    }

    [Test]
    public async Task InjectTextFiles_CapDe1MBPorMensagem()
    {
        // 5 arquivos de 256KB estouram 1MB agregado → último truncado.
        var entries = new Dictionary<string, FileEntry>();
        var ids = new List<string>();
        var big = new string('y', AttachmentContentService.MaxFileChars);
        for (var i = 0; i < 5; i++)
        {
            var f = new FileEntry
            {
                Id = $"f{i}", Filename = $"f{i}.txt", ContentType = "text/plain",
                ExtractedText = big,
            };
            entries[f.Id] = f;
            ids.Add(f.Id);
        }
        var msgs = new List<ChatCompletionMessage> { new("user", "q") };
        var out_ = await AttachmentContentService.InjectTextFilesAsync(
            msgs, entries, ids,
            (fe, ct) => AttachmentContentService.TryReadTextAsync(fe, ct));
        var content = out_[0].Content!;
        var blockCount = content.Split("<file name=").Length - 1;
        Assert.Multiple(() =>
        {
            Assert.That(blockCount, Is.LessThanOrEqualTo(5));
            Assert.That(content.Length,
                Is.LessThanOrEqualTo(AttachmentContentService.MaxMessageChars + 64));
        });
    }

    [Test]
    public async Task InjectTextFiles_BinarioViraMarcador()
    {
        var f = new FileEntry
        {
            Id = "e1", Filename = "run.exe", ContentType = "application/octet-stream",
            StoragePath = "/nonexistent",
        };
        var msgs = new List<ChatCompletionMessage> { new("user", "vê isso") };
        var out_ = await AttachmentContentService.InjectTextFilesAsync(
            msgs, new Dictionary<string, FileEntry> { ["e1"] = f }, ["e1"],
            (fe, ct) => AttachmentContentService.TryReadTextAsync(fe, ct));
        Assert.That(out_[0].Content,
            Does.Contain("<file name=\"run.exe\">[binary file — content omitted]</file>"));
    }

    [Test]
    public async Task InjectTextFiles_PulaImagens()
    {
        var f = new FileEntry
        {
            Id = "i1", Filename = "foto.png", ContentType = "image/png",
            StoragePath = "/nonexistent",
        };
        var msgs = new List<ChatCompletionMessage> { new("user", "olha") };
        var out_ = await AttachmentContentService.InjectTextFilesAsync(
            msgs, new Dictionary<string, FileEntry> { ["i1"] = f }, ["i1"],
            (fe, ct) => AttachmentContentService.TryReadTextAsync(fe, ct));
        Assert.That(out_[0].Content, Is.EqualTo("olha"));
    }

    [Test]
    public async Task InjectTextFiles_FalhaDeLeitura_OmiteArquivo()
    {
        var f = Seed("io.txt", Encoding.UTF8.GetBytes("x"));
        var msgs = new List<ChatCompletionMessage> { new("user", "q") };
        var out_ = await AttachmentContentService.InjectTextFilesAsync(
            msgs, new Dictionary<string, FileEntry> { ["io.txt"] = f }, ["io.txt"],
            (fe, ct) => throw new IOException("disk gone"));
        Assert.That(out_[0].Content, Is.EqualTo("q"));
    }

    [Test]
    public async Task ExtractedText_TemPrioridadeSobreDisco()
    {
        var f = new FileEntry
        {
            Id = "x", Filename = "a.txt", ContentType = "text/plain",
            ExtractedText = "do-entity", StoragePath = "/nonexistent",
        };
        var text = await AttachmentContentService.TryReadTextAsync(f);
        Assert.That(text, Is.EqualTo("do-entity"));
    }

    [Test]
    public async Task InjectTextFiles_FileIdsDaMensagem_SoNaDona()
    {
        var f = new FileEntry
        {
            Id = "m1", Filename = "m.txt", ContentType = "text/plain",
            ExtractedText = "conteúdo-m",
        };
        var msgs = new List<ChatCompletionMessage>
        {
            new("user", "primeira") { FileIds = ["m1"] },
            new("assistant", "resposta"),
            new("user", "segunda"),
        };
        var out_ = await AttachmentContentService.InjectTextFilesAsync(
            msgs, new Dictionary<string, FileEntry> { ["m1"] = f }, null,
            (fe, ct) => AttachmentContentService.TryReadTextAsync(fe, ct));
        Assert.Multiple(() =>
        {
            Assert.That(out_[0].Content, Does.Contain("<file name=\"m.txt\">"));
            Assert.That(out_[1].Content, Is.EqualTo("resposta"));
            Assert.That(out_[2].Content, Is.EqualTo("segunda"));
        });
    }
}
