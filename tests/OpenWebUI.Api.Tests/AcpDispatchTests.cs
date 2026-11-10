using System.Net;
using System.Text.Json;
using NUnit.Framework;
using OpenWebUI.Acp;
using OpenWebUI.Api.Tests.Providers;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261021-harness-seams RF-C3: dispatch JSON-RPC do ACP —
/// initialize/session/new/session/prompt/cancel, erro -32601/-32603,
/// replay SSE → session/update e fallback de polling.
/// </summary>
[TestFixture, NonParallelizable] // RunStdinLoopAsync redireciona Console.In
public class AcpDispatchTests
{
    private static JsonElement Rpc(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>initialize com token → reply com protocolVersion e
    /// capabilities; sem token → -32001.</summary>
    [Test]
    public async Task Initialize_ComESemToken()
    {
        object? reply = null; (int Code, string Msg)? error = null;
        await AcpDispatch.HandleAsync(
            Rpc("""{"id":1,"method":"initialize"}"""), "tok",
            (_, r) => reply = r,
            (_, c, m) => error = (c, m),
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Not.Null);
            Assert.That(error, Is.Null);
            var json = JsonSerializer.Serialize(reply);
            Assert.That(json, Does.Contain("protocolVersion"));
            Assert.That(json, Does.Contain("open-webui"));
        });

        reply = null; error = null;
        await AcpDispatch.HandleAsync(
            Rpc("""{"id":1,"method":"initialize"}"""), null,
            (_, r) => reply = r,
            (_, c, m) => error = (c, m),
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Null);
            Assert.That(error!.Value.Code, Is.EqualTo(-32001));
        });
    }

    /// <summary>session/new e session/prompt são delegados aos handlers
    /// com id e params; session/cancel responde objeto vazio.</summary>
    [Test]
    public async Task SessionMethods_DelegamEAck()
    {
        JsonElement? newId = null;
        JsonElement? promptId = null;
        JsonElement? promptParms = null;
        object? reply = null;

        await AcpDispatch.HandleAsync(
            Rpc("""{"id":2,"method":"session/new"}"""), "tok",
            (_, r) => reply = r, (_, _, _) => { },
            id => { newId = id; return Task.CompletedTask; },
            (id, p) => { promptId = id; promptParms = p; return Task.CompletedTask; });
        Assert.That(newId, Is.Not.Null);
        Assert.That(newId!.Value.GetInt32(), Is.EqualTo(2));

        await AcpDispatch.HandleAsync(
            Rpc("""{"id":3,"method":"session/prompt","params":{"sessionId":"s1"}}"""),
            "tok",
            (_, r) => reply = r, (_, _, _) => { },
            _ => Task.CompletedTask,
            (id, p) => { promptId = id; promptParms = p; return Task.CompletedTask; });
        Assert.Multiple(() =>
        {
            Assert.That(promptId!.Value.GetInt32(), Is.EqualTo(3));
            Assert.That(promptParms!.Value.GetProperty("sessionId").GetString(),
                Is.EqualTo("s1"));
        });

        reply = null;
        await AcpDispatch.HandleAsync(
            Rpc("""{"id":4,"method":"session/cancel"}"""), "tok",
            (_, r) => reply = r, (_, _, _) => { },
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        Assert.That(reply, Is.Not.Null);
    }

    /// <summary>Método desconhecido com id → -32601; sem id (notificação)
    /// → silêncio (spec JSON-RPC).</summary>
    [Test]
    public async Task MetodoDesconhecido_32601OuSilencio()
    {
        (int Code, string Msg)? error = null;
        var replies = 0;
        await AcpDispatch.HandleAsync(
            Rpc("""{"id":7,"method":"foo/bar"}"""), "tok",
            (_, _) => replies++, (_, c, m) => error = (c, m),
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        Assert.Multiple(() =>
        {
            Assert.That(error!.Value.Code, Is.EqualTo(-32601));
            Assert.That(error.Value.Msg, Does.Contain("foo/bar"));
        });

        error = null; replies = 0;
        await AcpDispatch.HandleAsync(
            Rpc("""{"method":"foo/bar"}"""), "tok",
            (_, _) => replies++, (_, c, m) => error = (c, m),
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask);
        Assert.Multiple(() =>
        {
            Assert.That(error, Is.Null);
            Assert.That(replies, Is.EqualTo(0));
        });
    }

    /// <summary>Handler que lança → -32603 com a mensagem da exceção.</summary>
    [Test]
    public async Task HandlerLanca_32603()
    {
        (int Code, string Msg)? error = null;
        await AcpDispatch.HandleAsync(
            Rpc("""{"id":8,"method":"session/prompt","params":{}}"""), "tok",
            (_, _) => { }, (_, c, m) => error = (c, m),
            _ => Task.CompletedTask,
            (_, _) => throw new InvalidOperationException("falhou no prompt"));

        Assert.Multiple(() =>
        {
            Assert.That(error!.Value.Code, Is.EqualTo(-32603));
            Assert.That(error.Value.Msg, Does.Contain("falhou no prompt"));
        });
    }

    /// <summary>ExtractPrompt junta blocos text com \n e ignora os demais
    /// tipos; sem blocos text → null.</summary>
    [Test]
    public void ExtractPrompt_BlocosText()
    {
        var parms = Rpc("""{"prompt":[{"type":"text","text":"um"},{"type":"image"},{"type":"text","text":"dois"}]}""");
        Assert.Multiple(() =>
        {
            Assert.That(AcpDispatch.ExtractPrompt(parms), Is.EqualTo("um\ndois"));
            Assert.That(AcpDispatch.ExtractPrompt(Rpc("{}")), Is.Null);
            Assert.That(AcpDispatch.ExtractPrompt(
                Rpc("""{"prompt":[]}""")), Is.Null);
            Assert.That(AcpDispatch.ExtractPrompt(null), Is.Null);
        });
    }

    /// <summary>Relay do SSE da run: content.delta → session/update;
    /// run.completed → end_turn.</summary>
    [Test]
    public async Task RelayRun_SseDeltasECompleted()
    {
        var http = new StubHttpHandler();
        http.Enqueue(HttpStatusCode.OK,
            "data: {\"type\":\"content.delta\",\"delta\":\"oi \"}\n\n" +
            "data: {\"type\":\"content_delta\",\"delta\":\"mundo\"}\n\n" +
            "data: {lixo nao-json}\n\n" +
            "event: heartbeat\n\n" +
            "data: {\"type\":\"run.completed\"}\n\n");
        var sent = new List<object>();
        using var client = new HttpClient(http)
        { BaseAddress = new Uri("http://acp.test") };

        var stop = await AcpDispatch.RelayRunAsync(
            client, sent.Add,
            (_, _, _) => Task.FromResult<JsonElement?>(null),
            "sess-1", "run-1", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(stop, Is.EqualTo("end_turn"));
            Assert.That(sent, Has.Count.EqualTo(2));
            var upd = JsonSerializer.Serialize(sent[0]);
            Assert.That(upd, Does.Contain("session/update"));
            Assert.That(upd, Does.Contain("oi "));
            Assert.That(upd, Does.Contain("sess-1"));
        });
    }

    /// <summary>run.failed → "error"; run.stopped → "cancelled".</summary>
    [Test]
    public async Task RelayRun_TerminaisEFalha()
    {
        var httpFail = new StubHttpHandler();
        httpFail.Enqueue(HttpStatusCode.OK,
            "data: {\"type\":\"run.failed\"}\n\n");
        using var c1 = new HttpClient(httpFail)
        { BaseAddress = new Uri("http://acp.test") };
        var r1 = await AcpDispatch.RelayRunAsync(
            c1, _ => { },
            (_, _, _) => Task.FromResult<JsonElement?>(null),
            "s", "r", CancellationToken.None);

        var httpStop = new StubHttpHandler();
        httpStop.Enqueue(HttpStatusCode.OK,
            "data: {\"type\":\"run.stopped\"}\n\n");
        using var c2 = new HttpClient(httpStop)
        { BaseAddress = new Uri("http://acp.test") };
        var r2 = await AcpDispatch.RelayRunAsync(
            c2, _ => { },
            (_, _, _) => Task.FromResult<JsonElement?>(null),
            "s", "r", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(r1, Is.EqualTo("error"));
            Assert.That(r2, Is.EqualTo("cancelled"));
        });
    }

    /// <summary>Stream indisponível (http quebra) → fallback de polling:
    /// status do runs.get decide o motivo de término.</summary>
    [Test]
    public async Task RelayRun_FallbackPolling_StatusDecide()
    {
        var http = new StubHttpHandler();
        // Sem respostas enfileiradas → 404 → EnsureSuccessStatusCode lança.
        using var client = new HttpClient(http)
        { BaseAddress = new Uri("http://acp.test") };
        var stop = await AcpDispatch.RelayRunAsync(
            client, _ => { },
            (_, _, _) => Task.FromResult<JsonElement?>(
                Rpc("""{"status":"completed"}""")),
            "s", "r", CancellationToken.None);

        Assert.That(stop, Is.EqualTo("end_turn"));
    }

    /// <summary>Loop do stdin: linhas não-JSON → -32700 e segue; EOF
    /// termina.</summary>
    [Test]
    public async Task RunStdinLoop_ParseErrorSegueEofTermina()
    {
        var sent = new List<object>();
        var handled = new List<string>();
        var original = Console.In;
        using var reader = new StringReader(
            "lixo-nao-json\n\n{\"id\":1}\n{\"id\":2}\n");
        try
        {
            Console.SetIn(reader);
            await AcpDispatch.RunStdinLoopAsync(sent.Add,
                root =>
                {
                    handled.Add(root.GetRawText());
                    return Task.CompletedTask;
                });
        }
        finally
        {
            Console.SetIn(original);
        }

        Assert.Multiple(() =>
        {
            Assert.That(handled, Has.Count.EqualTo(2));
            Assert.That(handled[0], Does.Contain("\"id\":1"));
            var errJson = JsonSerializer.Serialize(sent[0]);
            Assert.That(errJson, Does.Contain("-32700"));
            Assert.That(sent, Has.Count.EqualTo(1));
        });
    }
}
