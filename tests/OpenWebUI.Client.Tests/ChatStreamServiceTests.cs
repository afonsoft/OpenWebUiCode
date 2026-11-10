using System.Net;
using System.Text;
using Microsoft.JSInterop;
using OpenWebUI.Client.Services;

namespace OpenWebUI.Client.Tests;

/// <summary>Testes do consumo SSE de /api/chat/completions (deltas de chat).</summary>
[TestFixture]
public class ChatStreamServiceTests
{
    private readonly List<IDisposable> _owned = [];

    /// <summary>Libera os HttpClients criados pelas factories de serviço.</summary>
    [TearDown]
    public void TearDown()
    {
        foreach (var disposable in _owned)
        {
            disposable.Dispose();
        }
        _owned.Clear();
    }

    [Test]
    public async Task Stream_ChunkComChoicesVazio_NaoLancaEIgnora()
    {
        // Gateways OpenAI-compatíveis (ex.: OmniRoute auto/*) emitem chunks
        // sem choice — só role/usage/keepalive. Indexar choices[0] num array
        // vazio lançava ArgumentOutOfRangeException no WASM.
        var sse = string.Join('\n',
            "data: {\"choices\":[]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Olá\"}}]}",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\" mundo\"}}]}",
            "",
            "data: {\"choices\":[],\"usage\":{\"total_tokens\":10}}",
            "",
            "data: [DONE]",
            "");

        var service = CreateService(sse);

        var deltas = new List<string>();
        await foreach (var delta in service.StreamCompletionAsync(
            new OpenWebUI.Application.Contracts.ChatCompletionRequest("m", [])))
        {
            deltas.Add(delta);
        }

        Assert.That(deltas, Is.EqualTo(new[] { "Olá", " mundo" }));
    }

    [Test]
    public async Task Stream_DataComJsonNaoObjeto_IgnoraENaoDerruba()
    {
        // Gateways podem emitir `data:` com JSON não-objeto (string solta,
        // número — keepalive/diagnóstico do provider). Indexar JsonValue por
        // nome lançava InvalidOperationException ("requires an element of
        // type 'Object'") e o stream morria no meio da resposta.
        var sse = string.Join('\n',
            "data: {\"choices\":[{\"delta\":{\"content\":\"Olá\"}}]}",
            "",
            "data: \"keepalive\"",
            "",
            "data: 42",
            "",
            "data: {\"choices\":[{\"delta\":{\"content\":\" mundo\"}}]}",
            "",
            "data: [DONE]",
            "");

        var service = CreateService(sse);

        var deltas = new List<string>();
        await foreach (var delta in service.StreamCompletionAsync(
            new OpenWebUI.Application.Contracts.ChatCompletionRequest("m", [])))
        {
            deltas.Add(delta);
        }

        Assert.That(deltas, Is.EqualTo(new[] { "Olá", " mundo" }));
    }

    [Test]
    public async Task Stream_ChunkDeErro_LancaInvalidOperation()
    {
        var sse = "data: {\"error\":\"provider caiu\"}\n\ndata: [DONE]\n";
        var service = CreateService(sse);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in service.StreamCompletionAsync(
                new OpenWebUI.Application.Contracts.ChatCompletionRequest("m", [])))
            {
                // Consome o stream até o provider falhar.
            }
        });
    }

    [Test]
    public async Task RunEvents_ChunkDeErro_ViraEventoError()
    {
        // O executor publica {"error":…} quando a run morre (provider
        // down). Antes isso lançava InvalidOperationException e o
        // ChatView engolia como queda de transporte — a run encerrava
        // muda e o chat parecia "travado" (incidente 2026-11-07).
        var sse = "data: {\"error\":\"server-error: provedor falhou\"}\n\n"
            + "data: [DONE]\n";
        var service = CreateService(sse);

        var eventos = new List<ChatStreamService.ChatStreamEvent>();
        await foreach (var evt in service.StreamRunEventsAsync("c1", "r1"))
        {
            eventos.Add(evt);
        }

        var erro = eventos
            .OfType<ChatStreamService.ChatStreamEvent.Error>().Single();
        Assert.That(erro.Message,
            Is.EqualTo("server-error: provedor falhou"));
    }

    [Test]
    public async Task RunEvents_ChunkDeErroObjeto_ExtraiMessage()
    {
        // Shape OpenAI: {"error":{"message":…}} (gateways/providers).
        var sse = "data: {\"error\":{\"message\":\"boom\"}}\n\ndata: [DONE]\n";
        var service = CreateService(sse);

        var eventos = new List<ChatStreamService.ChatStreamEvent>();
        await foreach (var evt in service.StreamRunEventsAsync("c1", "r1"))
        {
            eventos.Add(evt);
        }

        var erro = eventos
            .OfType<ChatStreamService.ChatStreamEvent.Error>().Single();
        Assert.That(erro.Message, Is.EqualTo("boom"));
    }

    [Test]
    public async Task RunEvents_Changes_ParseSnapshot()
    {
        var sse = string.Join('\n',
            "event: changes",
            "data: {\"changes\":[{\"path\":\"saida.txt\",\"added\":3,\"removed\":0,\"diff\":\"+a\"}]}",
            "",
            "event: status",
            "data: {\"status\":\"completed\"}",
            "");

        var service = CreateService(sse);
        var eventos = new List<ChatStreamService.ChatStreamEvent>();
        await foreach (var evt in service.StreamRunEventsAsync("c1", "r1"))
        {
            eventos.Add(evt);
        }

        var changes = eventos.OfType<ChatStreamService.ChatStreamEvent.Changes>()
            .Single();
        Assert.Multiple(() =>
        {
            Assert.That(changes.Snapshot.Changes, Has.Count.EqualTo(1));
            Assert.That(changes.Snapshot.Changes[0].Path, Is.EqualTo("saida.txt"));
            Assert.That(changes.Snapshot.Changes[0].Added, Is.EqualTo(3));
            Assert.That(changes.Snapshot.Changes[0].Removed, Is.EqualTo(0));
            Assert.That(changes.Snapshot.Changes[0].Diff, Is.EqualTo("+a"));
        });
    }

    [Test]
    public async Task RunEvents_IdSeq_VemNoEventoParaResume()
    {
        // O seq da linha `id:` é o checkpoint de resume do consumidor:
        // após queda do stream ele re-anexa com lastSeq e o replay não
        // duplica eventos (SPEC-20261007-chat-tool-streaming).
        var sse = string.Join('\n',
            "id: 7",
            "event: status",
            "data: {\"status\":\"running\",\"label\":\"gerando\"}",
            "",
            "id: 8",
            "data: {\"choices\":[{\"delta\":{\"content\":\"oi\"}}]}",
            "",
            "data: [DONE]",
            "");

        var service = CreateService(sse);
        var eventos = new List<ChatStreamService.ChatStreamEvent>();
        await foreach (var evt in service.StreamRunEventsAsync("c1", "r1"))
        {
            eventos.Add(evt);
        }

        Assert.Multiple(() =>
        {
            var phase = eventos.OfType<ChatStreamService.ChatStreamEvent.Phase>().Single();
            Assert.That(phase.Seq, Is.EqualTo(7));
            var delta = eventos.OfType<ChatStreamService.ChatStreamEvent.Delta>().Single();
            Assert.That(delta.Seq, Is.EqualTo(8));
        });
    }

    [Test]
    public async Task RunEvents_ComentarioDeHeartbeat_ViraEventoHeartbeat()
    {
        // `: hb` mantém o pipe vivo através de proxies — o consumidor usa
        // o evento para re-armar o watchdog de stall (sem renderizar).
        var sse = string.Join('\n',
            ": hb",
            "",
            "id: 1",
            "data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}",
            "",
            ": hb",
            "",
            "data: [DONE]",
            "");

        var service = CreateService(sse);
        var eventos = new List<ChatStreamService.ChatStreamEvent>();
        await foreach (var evt in service.StreamRunEventsAsync("c1", "r1"))
        {
            eventos.Add(evt);
        }

        Assert.Multiple(() =>
        {
            Assert.That(
                eventos.OfType<ChatStreamService.ChatStreamEvent.Heartbeat>().Count(),
                Is.EqualTo(2));
            Assert.That(
                eventos.OfType<ChatStreamService.ChatStreamEvent.Delta>().Single().Text,
                Is.EqualTo("a"));
        });
    }

    [Test]
    public void ParseEventBlock_BlocoSseCru_ViraEventosTipados()
    {
        // Polling-fallback (SPEC-20261110): o backlog vem como bloco SSE
        // cru (linhas event:/data:) — o parse reusa o caminho do stream.
        var service = CreateService("");

        var eventos = service.ParseEventBlock(
            "event: tasks\ndata: {\"tasks\":[{\"id\":\"t1\",\"content\":\"x\",\"status\":\"in_progress\"}]}",
            seq: 42).ToList();

        var tasks = eventos.OfType<ChatStreamService.ChatStreamEvent.Tasks>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(tasks.Seq, Is.EqualTo(42));
            Assert.That(tasks.Snapshot.Tasks[0].Id, Is.EqualTo("t1"));
        });
    }

    [Test]
    public void ParseEventBlock_DeltaOpenAiEDone_SoProduzDelta()
    {
        var service = CreateService("");

        var eventos = service.ParseEventBlock(
            "data: {\"choices\":[{\"delta\":{\"content\":\"oi\"}}]}", seq: 7)
            .Concat(service.ParseEventBlock("data: [DONE]", seq: 8))
            .ToList();

        var delta = eventos.OfType<ChatStreamService.ChatStreamEvent.Delta>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(delta.Text, Is.EqualTo("oi"));
            Assert.That(delta.Seq, Is.EqualTo(7));
        });
    }

    private ChatStreamService CreateService(string sseBody)
    {
        var http = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseBody, Encoding.UTF8, "text/event-stream"),
            })))
        {
            BaseAddress = new Uri("http://localhost/"),
        };
        _owned.Add(http);

        var js = new FakeJs();
        var auth = new AuthService(http, new BrowserStorage(js), new LocalizationService(http, js));
        return new ChatStreamService(http, auth);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _pending = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await handler(request, cancellationToken);
            _pending.Add(response);
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var response in _pending)
                {
                    response.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    private sealed class FakeJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
            => new();
    }
}
