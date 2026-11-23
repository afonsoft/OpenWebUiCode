using NUnit.Framework;
using OpenWebUI.Api.Runs;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261123-cache-keepalive: o ping reusa o prefixo cacheado da run
/// (system + tools do RequestJson) — só assim renova o TTL de caches
/// por prefixo (Anthropic cache_control).
/// </summary>
[TestFixture]
public sealed class CacheKeepaliveTests
{
    [Test]
    public void Ping_ReusaSystemEToolsDoRequest()
    {
        var requestJson = """
            {"model":"m1","cacheFormat":"anthropic",
             "messages":[
               {"role":"system","content":"SYS"},
               {"role":"user","content":"pergunta"},
               {"role":"assistant","content":"resp"}],
             "tools":[{"type":"function","function":{"name":"x"}}]}
            """;
        var ping = ChatRunExecutor.BuildKeepaliveRequest("m1", requestJson);
        Assert.Multiple(() =>
        {
            Assert.That(ping.Messages, Has.Count.EqualTo(2));
            Assert.That(ping.Messages[0].Role, Is.EqualTo("system"));
            Assert.That(ping.Messages[0].Content, Is.EqualTo("SYS"));
            Assert.That(ping.Messages[1].Content, Is.EqualTo("."));
            Assert.That(ping.CacheFormat, Is.EqualTo("anthropic"));
            Assert.That(ping.Stream, Is.False);
            Assert.That(ping.Tools, Is.Not.Null.And.Not.Empty,
                "o bloco de tools entra no prefixo cacheado");
        });
    }

    [Test]
    public void Ping_SemCacheFormat_DefaultAnthropic()
    {
        var requestJson = """
            {"model":"m1","messages":[{"role":"system","content":"S"}]}
            """;
        var ping = ChatRunExecutor.BuildKeepaliveRequest("m1", requestJson);
        Assert.That(ping.CacheFormat, Is.EqualTo("anthropic"));
    }

    [Test]
    public void Ping_RequestJsonInvalido_PingMinimo()
    {
        var ping = ChatRunExecutor.BuildKeepaliveRequest("m1", "{lixo");
        Assert.Multiple(() =>
        {
            Assert.That(ping.Messages, Has.Count.EqualTo(1));
            Assert.That(ping.Model, Is.EqualTo("m1"));
            Assert.That(ping.CacheFormat, Is.Null);
        });
    }

    [Test]
    public void Ping_SemRequest_PingMinimo()
    {
        var ping = ChatRunExecutor.BuildKeepaliveRequest("m1", null);
        Assert.Multiple(() =>
        {
            Assert.That(ping.Messages, Has.Count.EqualTo(1));
            Assert.That(ping.Messages[0].Content, Is.EqualTo("."));
        });
    }
}
