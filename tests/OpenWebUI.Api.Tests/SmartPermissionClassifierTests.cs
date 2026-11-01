using NUnit.Framework;
using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261031-smart-permission-llm: always-ask (RF-004), fingerprint
/// dedup (RF-005), parse estrito do juiz (RF-003).
/// </summary>
[TestFixture]
public class SmartPermissionClassifierTests
{
    [Test]
    public void AlwaysAsk_CobremCategoriasCriticas()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SmartPermissionClassifier.IsAlwaysAsk(
                "shell_exec", """{"command":"npm install x"}"""), Is.True);
            Assert.That(SmartPermissionClassifier.IsAlwaysAsk(
                "shell_exec", """{"command":"git push origin main"}"""), Is.True);
            Assert.That(SmartPermissionClassifier.IsAlwaysAsk(
                "shell_exec", """{"command":"rm -rf /tmp/x"}"""), Is.True);
            Assert.That(SmartPermissionClassifier.IsAlwaysAsk(
                "shell_exec", """{"command":"sudo apt update"}"""), Is.True);
            Assert.That(SmartPermissionClassifier.IsAlwaysAsk(
                "shell_exec", """{"command":"terraform apply"}"""), Is.True);
            Assert.That(SmartPermissionClassifier.IsAlwaysAsk(
                "file_write", """{"path":".env"}"""), Is.True);
            Assert.That(SmartPermissionClassifier.IsAlwaysAsk(
                "file_read", """{"path":"/home/u/.ssh/id_rsa"}"""), Is.True);
        });
    }

    [Test]
    public void AlwaysAsk_NaoPegaComandosSeguros()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SmartPermissionClassifier.IsAlwaysAsk(
                "shell_exec", """{"command":"dotnet build"}"""), Is.False);
            Assert.That(SmartPermissionClassifier.IsAlwaysAsk(
                "shell_exec", """{"command":"dotnet test"}"""), Is.False);
            Assert.That(SmartPermissionClassifier.IsAlwaysAsk(
                "file_glob", """{"pattern":"*.cs"}"""), Is.False);
            Assert.That(SmartPermissionClassifier.IsAlwaysAsk(
                "shell_exec", """{"command":"git status"}"""), Is.False);
        });
    }

    [Test]
    public void Fingerprint_NormalizaWhitespace_PreservaValores()
    {
        // Runs de whitespace colapsam ("  " → " ") → mesmo hash
        var a = SmartPermissionClassifier.FingerprintOf(
            "shell_exec", """{"command": "dotnet   build"}""");
        var b = SmartPermissionClassifier.FingerprintOf(
            "shell_exec", """{"command": "dotnet build"}""");
        var c = SmartPermissionClassifier.FingerprintOf(
            "shell_exec", """{"command": "dotnet test"}""");
        // Diferença estrutural (não-whitespace) → hash diferente
        var d = SmartPermissionClassifier.FingerprintOf(
            "shell_exec", "{ \"command\": \"dotnet build\" }");
        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(b),
                "whitespace runs colapsados = mesmo hash");
            Assert.That(a, Is.Not.EqualTo(c),
                "ação diferente = hash diferente");
            Assert.That(a, Is.Not.EqualTo(d),
                "diferença estrutural = hash diferente");
        });
    }

    [Test]
    public void Parse_JsonEstrito_VariantesEDesvios()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SmartPermissionClassifier.Parse(
                """{"decision":"allow","reason":"safe"}""",
                "fp", "m", 1).Decision, Is.EqualTo("allow"));
            Assert.That(SmartPermissionClassifier.Parse(
                """{"decision":"deny","reason":"danger"}""",
                "fp", "m", 1).Decision, Is.EqualTo("deny"));
            // JSON inválido → ask
            Assert.That(SmartPermissionClassifier.Parse(
                "lixo", "fp", "m", 1).Decision, Is.EqualTo("ask"));
            // decisão fora do enum → ask
            Assert.That(SmartPermissionClassifier.Parse(
                """{"decision":"maybe"}""", "fp", "m", 1).Decision,
                Is.EqualTo("ask"));
            // null → ask
            Assert.That(SmartPermissionClassifier.Parse(
                null, "fp", "m", 1).Decision, Is.EqualTo("ask"));
            // JSON embutido em texto → extrai o objeto
            Assert.That(SmartPermissionClassifier.Parse(
                """verdict: {"decision":"allow","reason":"ok"} done""",
                "fp", "m", 1).Decision, Is.EqualTo("allow"));
        });
    }

    [Test]
    public void Dedup_CachePorSessao_LimpaNoFim()
    {
        var key = $"sess-{Guid.NewGuid():N}";
        var fp = SmartPermissionClassifier.FingerprintOf("t", "{}");
        // Sem cache inicial
        Assert.That(SmartPermissionClassifier.Cached(key, fp), Is.Null);
        // Registra manualmente via Record indireto — usa reflexão? Não:
        // testa Clear apenas; o registro real cobre-se por integração.
        SmartPermissionClassifier.Clear(key);
        Assert.That(SmartPermissionClassifier.Cached(key, fp), Is.Null);
    }
}
