using NUnit.Framework;
using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261123-tool-arg-repair: reparo determinístico de
/// function.arguments — JSON truncado, vírgula sobrando, fence de
/// markdown e required ausente com default no schema.
/// </summary>
[TestFixture]
public sealed class ToolArgRepairTests
{
    [Test]
    public void JsonValido_NaoRepara()
    {
        var original = """{"path":"/tmp/a.txt","n":42}""";
        Assert.Multiple(() =>
        {
            Assert.That(ToolArgRepair.TryRepair(original, out var repaired), Is.False);
            Assert.That(repaired, Is.EqualTo(original));
        });
    }

    [Test]
    public void JsonTruncado_FechaEstruturas()
    {
        // Output cortado pelo limite de tokens do modelo.
        var broken = """{"path":"/tmp/a.txt","content":"abc""";
        Assert.That(ToolArgRepair.TryRepair(broken, out var repaired), Is.True);
        Assert.That(repaired, Is.EqualTo("""{"path":"/tmp/a.txt","content":"abc"}"""));
    }

    [Test]
    public void StringAberta_FechaAspasEEstrutura()
    {
        var broken = """{"content":"linha incompleta""";
        Assert.That(ToolArgRepair.TryRepair(broken, out var repaired), Is.True);
        Assert.That(System.Text.Json.JsonDocument.Parse(repaired)
            .RootElement.GetProperty("content").GetString(),
            Is.EqualTo("linha incompleta"));
    }

    [Test]
    public void VirgulaSobrando_Removida()
    {
        var broken = """{"a":1,"b":[1,2,],}""";
        Assert.That(ToolArgRepair.TryRepair(broken, out var repaired), Is.True);
        var doc = System.Text.Json.JsonDocument.Parse(repaired);
        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("a").GetInt32(), Is.EqualTo(1));
            Assert.That(doc.RootElement.GetProperty("b").GetArrayLength(), Is.EqualTo(2));
        });
    }

    [Test]
    public void FenceMarkdown_Removido()
    {
        var fenced = "```json\n{\"path\":\"/x\"}\n```";
        Assert.That(ToolArgRepair.TryRepair(fenced, out var repaired), Is.True);
        Assert.That(repaired, Is.EqualTo("""{"path":"/x"}"""));
    }

    [Test]
    public void Irrecuperavel_RetornaFalse()
    {
        Assert.That(ToolArgRepair.TryRepair("isto não é json {{{", out _), Is.False);
    }

    [Test]
    public void RequiredComDefault_Preenchido()
    {
        const string spec = """
            {"type":"object","required":["cmd"],"properties":{
              "cmd":{"type":"string","default":"ls"},
              "timeout":{"type":"number"}}}
            """;
        Assert.Multiple(() =>
        {
            Assert.That(
                ToolArgRepair.ApplySchemaDefaults("{}", spec, out var result), Is.True);
            Assert.That(result, Does.Contain("\"cmd\":\"ls\""));
        });
    }

    [Test]
    public void RequiredSemDefault_FalhaSemInventarValor()
    {
        const string spec = """
            {"type":"object","required":["path"],"properties":{
              "path":{"type":"string"}}}
            """;
        Assert.That(ToolArgRepair.ApplySchemaDefaults("{}", spec, out _), Is.False);
    }

    [Test]
    public void SpecCompleto_EncontraParametersAninhado()
    {
        const string spec = """
            {"type":"function","function":{"name":"exec","parameters":{
              "type":"object","required":["cmd"],
              "properties":{"cmd":{"type":"string","default":"ls"}}}}}
            """;
        Assert.That(
            ToolArgRepair.ApplySchemaDefaults("{}", spec, out var result), Is.True);
        Assert.That(result, Does.Contain("\"cmd\":\"ls\""));
    }

    [Test]
    public void RequiredJaPresente_NaoSobrescreve()
    {
        const string spec = """
            {"type":"object","required":["cmd"],"properties":{
              "cmd":{"type":"string","default":"ls"}}}
            """;
        Assert.That(ToolArgRepair.ApplySchemaDefaults(
            """{"cmd":"pwd"}""", spec, out var result), Is.True);
        Assert.That(result, Does.Contain("\"cmd\":\"pwd\""));
    }
}
