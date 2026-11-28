using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261128-read-rich-tools (plano P1.17): <c>read_archive</c>
/// lista/extrai zip do workspace e <c>read_sqlite</c> inspeciona
/// um SQLite read-only.
/// </summary>
[TestFixture]
public sealed class ReadRichBuiltinToolsTests
{
    private string _workspace = null!;
    private BuiltinToolContext _ctx = null!;

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement;

    [SetUp]
    public void SetUp()
    {
        _workspace = Path.Join(Path.GetTempPath(), $"owui-read-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        _ctx = new BuiltinToolContext("u1", "c1", "r1", _workspace, "/tmp/up");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_workspace, true);

    private async Task MakeZipAsync(string name)
    {
        var full = Path.Join(_workspace, name);
        await using (var fs = File.Create(full))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("docs/readme.md");
            await using (var w = new StreamWriter(e.Open()))
            {
                await w.WriteAsync("conteúdo do membro");
            }
            zip.CreateEntry("src/a.txt");
        }
    }

    [Test]
    public async Task ReadArchive_ListaEntradas()
    {
        await MakeZipAsync("pack.zip");
        var r = await new ReadArchiveBuiltinTool().ExecuteAsync(
            Args("""{"path":"pack.zip"}"""), _ctx, default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("2 entradas"));
            Assert.That(r.Text, Does.Contain("docs/readme.md"));
        });
    }

    [Test]
    public async Task ReadArchive_ExtraiMembro()
    {
        await MakeZipAsync("pack.zip");
        var r = await new ReadArchiveBuiltinTool().ExecuteAsync(
            Args("""{"path":"pack.zip","member":"docs/readme.md"}"""), _ctx, default);
        Assert.That(r.Text, Does.Contain("conteúdo do membro"));
    }

    [Test]
    public async Task ReadArchive_ForaDoSandbox_Recusa()
    {
        var r = await new ReadArchiveBuiltinTool().ExecuteAsync(
            Args("""{"path":"../etc.zip"}"""), _ctx, default);
        Assert.That(r.Text, Does.StartWith("Erro"));
    }

    private async Task MakeDbAsync(string name)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Join(_workspace, name),
        }.ToString();
        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "CREATE TABLE t1 (id INTEGER PRIMARY KEY, name TEXT);"
                + "INSERT INTO t1 (name) VALUES ('alpha'), ('beta');";
        await cmd.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task ReadSqlite_ListaTabelas()
    {
        await MakeDbAsync("d.db");
        var r = await new ReadSqliteBuiltinTool().ExecuteAsync(
            Args("""{"path":"d.db"}"""), _ctx, default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("t1"));
            Assert.That(r.Text, Does.Contain("1 tabelas"));
        });
    }

    [Test]
    public async Task ReadSqlite_SelectRetornaLinhas()
    {
        await MakeDbAsync("d.db");
        var r = await new ReadSqliteBuiltinTool().ExecuteAsync(
            Args("""{"path":"d.db","query":"SELECT id, name FROM t1 ORDER BY id"}"""),
            _ctx, default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Text, Does.Contain("alpha"));
            Assert.That(r.Text, Does.Contain("beta"));
        });
    }

    [Test]
    public async Task ReadSqlite_RecusaNaoSelect()
    {
        await MakeDbAsync("d.db");
        var r = await new ReadSqliteBuiltinTool().ExecuteAsync(
            Args("""{"path":"d.db","query":"DELETE FROM t1"}"""), _ctx, default);
        Assert.That(r.Text, Does.Contain("apenas SELECT"));
    }
}
