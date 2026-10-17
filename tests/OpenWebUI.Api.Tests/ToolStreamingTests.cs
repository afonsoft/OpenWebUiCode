using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Api.Runs;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura do SPEC-20261007-chat-tool-streaming: eventos tipados no SSE
/// (tool_call/tool_result/status/approval_asked), gate de aprovação por
/// preset, endpoint de decisão e persistência das mensagens do loop.
/// O mock local (HttpListener) faz de Ollama + endpoint da tool HTTP.
/// </summary>
[TestFixture, IsolateEnvironment]
public class ToolStreamingTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _mockBase = null!;
    private string _adminToken = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-tools-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _mockBase = StartMock();

        var admin = await SignUpAsync("Admin", "admin@tools.local");
        _adminToken = admin.Token;
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());

        // Ollama fake: /api/tags lista modelos; /api/chat pede a tool
        // "eco" na primeira chamada e devolve conteúdo quando o histórico
        // já tem mensagem role=tool.
        var connections = new ConnectionsConfig([_mockBase], [], []);
        var connResponse = await _client.PostAsJsonAsync("/api/v1/configs/connections", connections);
        Assert.That(connResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _mockCts.Cancel();
        _mock.Stop();
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private string StartMock()
    {
        var random = new Random();
        var port = 0;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            port = random.Next(40000, 60000);
            _mock = new HttpListener();
            _mock.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _mock.Start();
                break;
            }
            catch (HttpListenerException)
            {
                _mock.Close();
            }
        }
        if (!_mock.IsListening)
        {
            throw new InvalidOperationException("Nenhuma porta livre para o mock.");
        }
        _mockCts = new CancellationTokenSource();
        _ = Task.Run(() => MockLoopAsync(_mockCts.Token));
        return $"http://localhost:{port}";
    }

    private async Task MockLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _mock.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            string body = string.Empty;
            if (ctx.Request.HasEntityBody)
            {
                using var reader = new StreamReader(ctx.Request.InputStream);
                body = await reader.ReadToEndAsync(ct);
            }

            var path = ctx.Request.Url!.AbsolutePath;
            string json;
            if (path == "/api/tags")
            {
                json = "{\"models\":[{\"model\":\"fake:1\",\"name\":\"fake:1\"}]}";
            }
            else if (path == "/api/chat")
            {
                // Run sem tools (chat puro — SPEC-20261015): sem o campo
                // "tools" no payload o mock responde NDJSON com
                // message.thinking — o provider traduz para
                // delta.reasoning (canal separado do content).
                // Todas as respostas reportam usage (prompt_eval_count/
                // eval_count) — a run agrega em meta.usage por step
                // (SPEC-20261016-agent-steps-usage RF-004).
                json = !body.Contains("\"tools\"", StringComparison.Ordinal)
                    ? "{\"message\":{\"role\":\"assistant\","
                        + "\"thinking\":\"raciocinio interno\","
                        + "\"content\":\"resposta final\"},\"done\":true,"
                        + "\"prompt_eval_count\":10,\"eval_count\":5}"
                // Segunda chamada do loop: histórico já tem role=tool.
                // Casa pelo nome exato anunciado no tools spec (builtin_{name}) —
                // substring solta colide com o system prompt padrão (que cita
                // "ask_user") e desviava TODA run para o braço errado.
                : body.Contains("\"tool_call_id\"", StringComparison.Ordinal)
                    ? "{\"message\":{\"role\":\"assistant\",\"content\":\"resposta pós-tool\"},"
                        + "\"done\":true,\"prompt_eval_count\":10,\"eval_count\":5}"
                    // SPEC-20261016-question-forms: prompt "multi-form" dispara
                    // a forma questions[] (3 itens: single, multiple, livre).
                    : body.Contains("builtin_ask_user", StringComparison.Ordinal)
                        && body.Contains("multi-form", StringComparison.Ordinal)
                        ? "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"id\":\"call-q\",\"function\":{\"name\":\"builtin:ask_user\",\"arguments\":{\"questions\":[{\"question\":\"Qual env?\",\"header\":\"Ambiente\",\"options\":[{\"label\":\"dev\",\"description\":\"desenvolvimento\"},\"prod\"],\"required\":true},{\"question\":\"Quais regioes?\",\"options\":[\"a\",\"b\",\"c\"],\"multiple\":true,\"required\":false},{\"question\":\"Detalhes?\",\"required\":true}]}}}]}}"
                    : body.Contains("builtin_ask_user", StringComparison.Ordinal)
                        ? "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"id\":\"call-q\",\"function\":{\"name\":\"builtin:ask_user\",\"arguments\":{\"question\":\"Qual env?\",\"options\":[\"dev\",\"prod\"]}}}]}}"
                        : body.Contains("builtin_file_write", StringComparison.Ordinal)
                        ? "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"id\":\"call-fw\",\"function\":{\"name\":\"builtin:file_write\",\"arguments\":{\"path\":\"saida.txt\",\"content\":\"gerado\"}}}]}}"
                        : body.Contains("builtin_delegate_task", StringComparison.Ordinal)
                            ? "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"id\":\"call-dl\",\"function\":{\"name\":\"builtin:delegate_task\",\"arguments\":{\"prompt\":\"resuma o arquivo\"}}}]}}"
                            : body.Contains("builtin_browser_screenshot", StringComparison.Ordinal)
                                ? $"{{\"message\":{{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{{\"id\":\"call-bs\",\"function\":{{\"name\":\"builtin:browser_screenshot\",\"arguments\":{{\"url\":\"{_mockBase}/api/tags\",\"width\":640,\"height\":480}}}}}}]}}}}"
                                : "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"id\":\"call-1\",\"function\":{\"name\":\"eco\",\"arguments\":{\"texto\":\"oi\"}}}]}}";
            }
            else if (path == "/tool/eco")
            {
                json = "resultado-eco";
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(bytes, ct);
            ctx.Response.Close();
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, "senha123"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<ChatResponse> CriarChatAsync()
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Chat tools", ["fake:1"], []));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;

        // SPEC-15: chats nascem em "chat" (sem workspace) — estes testes
        // exercitam tools de workspace, então o chat ganha binding + build.
        using var scope = _factory.Services.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
        await config.SetAsync(
            $"chat:{chat.Id}:workspace.repo",
            new WorkspaceRepoBinding("a/b", "main", "repos/a__b", null, null),
            default);
        var patch = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}", new ChatPatchRequest(null, Mode: "build"));
        Assert.That(patch.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await patch.Content.ReadAsStringAsync());
        return chat;
    }

    private async Task<string> CriarToolAsync(bool requiresApproval = false)
    {
        var spec = "{\"type\":\"function\",\"function\":{\"name\":\"eco\","
            + "\"description\":\"Eco de teste\",\"parameters\":{\"type\":\"object\","
            + "\"properties\":{\"texto\":{\"type\":\"string\"}}}}}";
        var response = await _client.PostAsJsonAsync("/api/v1/tools/",
            new ToolUpsertRequest("Eco", "eco", spec, $"{_mockBase}/tool/eco",
                RequiresApproval: requiresApproval));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        var tool = await response.Content.ReadFromJsonAsync<ToolResponse>();
        return tool!.Id;
    }

    private async Task<ChatRunResponse> EnfileirarAsync(
        string chatId, string content, IReadOnlyList<string>? toolIds = null,
        string model = "fake:1")
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chatId}/messages",
            new EnqueueChatRunRequest(content, model, ToolIds: toolIds));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChatRunResponse>())!;
    }

    /// <summary>
    /// Lê o stream de attach até a run fechar (ou timeout), retornando os
    /// pares (evento, payload) na ordem — replay incluído.
    /// </summary>
    private async Task<List<(string Event, string Data)>> LerStreamAteFecharAsync(
        string chatId, string runId, CancellationToken ct)
    {
        var eventos = new List<(string, string)>();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/chats/{chatId}/runs/{runId}/stream?lastSeq=0");
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var evento = "";
        while (await reader.ReadLineAsync(ct) is { } linha)
        {
            if (linha.StartsWith("event:", StringComparison.Ordinal))
            {
                evento = linha["event:".Length..].Trim();
            }
            else if (linha.StartsWith("data:", StringComparison.Ordinal))
            {
                var payload = linha["data:".Length..].Trim();
                eventos.Add((evento, payload));
                if (evento == "status"
                    && (payload.Contains("\"completed\"") || payload.Contains("\"failed\"")
                        || payload.Contains("\"stopped\"") || payload.Contains("\"interrupted\"")))
                {
                    break; // status terminal
                }
                evento = "";
            }
        }
        return eventos;
    }

    private static async Task<ChatRunResponse> AguardarFinalAsync(
        HttpClient client, string chatId, string runId)
    {
        var limite = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < limite)
        {
            var run = await client.GetFromJsonAsync<ChatRunResponse>(
                $"/api/v1/chats/{chatId}/runs/{runId}");
            Assert.That(run, Is.Not.Null);
            if (run!.Status is not ("queued" or "running"))
            {
                return run;
            }
            await Task.Delay(150);
        }
        Assert.Fail($"Run {runId} não finalizou em 60s.");
        return null!;
    }

    // ---- ChatRunApprovals (unitário) ----

    [Test]
    public async Task Approval_ResolveAprova_LiberaWaitComTrue()
    {
        var approvals = new ChatRunApprovals();
        var wait = approvals.WaitAsync("run-1", "chat-1", "call-1", "eco", CancellationToken.None);

        Assert.That(approvals.Resolve("run-1", "chat-1", "call-1", approved: true, remember: false), Is.True);
        Assert.That((await wait).Approved, Is.True);
        Assert.That(approvals.IsPending("run-1", "call-1"), Is.False);
    }

    [Test]
    public async Task Approval_ResolveNega_LiberaWaitComFalse()
    {
        var approvals = new ChatRunApprovals();
        var wait = approvals.WaitAsync("run-1", "chat-1", "call-1", "eco", CancellationToken.None);

        Assert.That(approvals.Resolve("run-1", "chat-1", "call-1", approved: false, remember: false), Is.True);
        Assert.That((await wait).Approved, Is.False);
    }

    [Test]
    public async Task Approval_ResolveNegaComMensagem_CarregaInstrucao()
    {
        var approvals = new ChatRunApprovals();
        var wait = approvals.WaitAsync("run-1", "chat-1", "call-1", "eco", CancellationToken.None);

        Assert.That(approvals.Resolve(
            "run-1", "chat-1", "call-1", approved: false, remember: false,
            message: "rode ls antes"), Is.True);
        var result = await wait;
        Assert.That(result.Approved, Is.False);
        Assert.That(result.Message, Is.EqualTo("rode ls antes"));
    }

    [Test]
    public async Task Approval_ResolveComRemember_MarcaToolNoChat()
    {
        var approvals = new ChatRunApprovals();
        var wait = approvals.WaitAsync("run-1", "chat-1", "call-1", "eco", CancellationToken.None);

        Assert.That(
            approvals.Resolve("run-1", "chat-1", "call-1", approved: true, remember: true), Is.True);
        Assert.That((await wait).Approved, Is.True);
        Assert.That(approvals.IsRemembered("chat-1", "eco"), Is.True);
        // "Lembrar" é por chat — outro chat não herda.
        Assert.That(approvals.IsRemembered("chat-2", "eco"), Is.False);
    }

    [Test]
    public void Approval_ResolveSemPendente_RetornaFalse()
    {
        var approvals = new ChatRunApprovals();
        Assert.That(
            approvals.Resolve("run-1", "chat-1", "call-x", approved: true, remember: false), Is.False);
    }

    [Test]
    public async Task Approval_Cancel_NegaPendentesDaRun()
    {
        var approvals = new ChatRunApprovals();
        var wait = approvals.WaitAsync("run-1", "chat-1", "call-1", "eco", CancellationToken.None);

        approvals.Cancel("run-1");
        Assert.That((await wait).Approved, Is.False);
        Assert.That(approvals.IsPending("run-1", "call-1"), Is.False);
    }

    // ---- Endpoints ----

    [Test]
    public async Task Patch_PresetValido_AtualizaChat()
    {
        var auth = await SignUpAsync("PatchA", "patcha@tools.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}", new ChatPatchRequest("always-allow"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        var updated = await response.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.That(updated!.ApprovalPreset, Is.EqualTo("always-allow"));
    }

    [Test]
    public async Task Patch_Titulo_RenomeiaSemEnviarChatTodo()
    {
        var auth = await SignUpAsync("PatchT", "patcht@tools.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}", new ChatPatchRequest(null, "  Novo título  "));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        var updated = await response.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.That(updated!.Title, Is.EqualTo("Novo título"));
    }

    [Test]
    public async Task Patch_PresetInvalido_Retorna400()
    {
        var auth = await SignUpAsync("PatchB", "patchb@tools.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}", new ChatPatchRequest("yolo"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Patch_ChatDeOutroUsuario_Retorna404()
    {
        var dono = await SignUpAsync("PatchC", "patchc@tools.local");
        UseToken(dono.Token);
        var chat = await CriarChatAsync();

        var intruso = await SignUpAsync("PatchD", "patchd@tools.local");
        UseToken(intruso.Token);
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}", new ChatPatchRequest("always-allow"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task DecideApproval_CallNaoPendente_Retorna404()
    {
        var auth = await SignUpAsync("ApprA", "appra@tools.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();

        var run = await EnfileirarAsync(chat.Id, "oi");
        await AguardarFinalAsync(_client, chat.Id, run.Id); // sem provider a run falha rápido

        // Run já terminada: decisão em run fechada → 410; call que nunca
        // existiu numa run aberta também não se aplica.
        var closed = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/approvals/call-x",
            new RunApprovalDecisionRequest("approve"));
        Assert.That(closed.StatusCode, Is.EqualTo(HttpStatusCode.Gone));
    }

    [Test]
    public async Task DecideApproval_DecisaoInvalida_Retorna400()
    {
        var auth = await SignUpAsync("ApprB", "apprb@tools.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();
        var run = await EnfileirarAsync(chat.Id, "oi");

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/approvals/call-x",
            new RunApprovalDecisionRequest("talvez"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---- Loop de tools ponta a ponta ----

    [Test]
    public async Task ToolLoop_EmiteEventosEPersisteMensagens()
    {
        var auth = await SignUpAsync("LoopA", "loopa@tools.local");
        UseToken(auth.Token);
        var toolId = await CriarToolAsync();
        var chat = await CriarChatAsync();

        var run = await EnfileirarAsync(chat.Id, "chama a tool", [toolId]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var eventos = await LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token);

        Assert.Multiple(() =>
        {
            Assert.That(eventos.Any(e => e.Event == "tool_call" && e.Data.Contains("\"eco\"")),
                Is.True, "faltou event: tool_call");
            Assert.That(eventos.Any(e => e.Event == "tool_result"
                    && e.Data.Contains("resultado-eco") && e.Data.Contains("\"ok\":true")),
                Is.True, "faltou event: tool_result");
            Assert.That(eventos.Any(e => e.Event == "status" && e.Data.Contains("running_tool")),
                Is.True, "faltou status running_tool");
        });

        var final = await AguardarFinalAsync(_client, chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);

        // Mensagens do loop persistidas: assistant com tool_calls + role=tool.
        var detalhe = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{chat.Id}");
        var roles = detalhe!.Messages.Select(m => m.Role).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(roles, Does.Contain("tool"));
            Assert.That(detalhe.Messages.Any(m => m.Role == "assistant"
                && m.ToolCallsJson is not null), Is.True, "faltou tool_calls persistido");
            Assert.That(detalhe.Messages.Last(m => m.Role == "assistant").Content,
                Is.EqualTo("resposta pós-tool"));
        });
    }

    [Test]
    public async Task Approval_Aprova_ExecutaTool()
    {
        var auth = await SignUpAsync("GateA", "gatea@tools.local");
        UseToken(auth.Token);
        var toolId = await CriarToolAsync(requiresApproval: true);
        var chat = await CriarChatAsync(); // preset default approve-mutations

        var run = await EnfileirarAsync(chat.Id, "executa com gate", [toolId]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Espera o approval_asked no stream, depois aprova e lê o resto.
        var streamTask = Task.Run(() => LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token));
        var limite = DateTime.UtcNow.AddSeconds(30);
        var temPedido = false;
        while (DateTime.UtcNow < limite && !temPedido)
        {
            // Enquanto a call não está pendente o endpoint devolve 404 —
            // 200 marca que o gate pediu aprovação.
            var decidido = await _client.PostAsJsonAsync(
                $"/api/v1/chats/{chat.Id}/runs/{run.Id}/approvals/call-1",
                new RunApprovalDecisionRequest("approve"));
            temPedido = decidido.StatusCode == HttpStatusCode.OK;
            if (!temPedido)
            {
                await Task.Delay(200, cts.Token);
            }
        }
        Assert.That(temPedido, Is.True, "gate não pediu aprovação em 30s");

        var eventos = await streamTask;
        Assert.Multiple(() =>
        {
            Assert.That(eventos.Any(e => e.Event == "approval_asked"), Is.True);
            Assert.That(eventos.Any(e => e.Event == "tool_result"
                    && e.Data.Contains("resultado-eco")), Is.True,
                "tool não executou após aprovação");
        });
    }

    // ---- agent-steps-usage (SPEC-20261016) ----

    [Test]
    public async Task Run_UsageAgregada_MetaUsageECustoESseSteps()
    {
        var auth = await SignUpAsync("UsageA", "usagea@tools.local");
        UseToken(auth.Token);
        // ModelEntry com custo para "fake:1" → costUsd no meta da run.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ModelEntries.Add(new ModelEntry
            {
                UserId = "public",
                Name = "fake:1",
                BaseModelId = "fake:1",
                MetaJson = """{"cost":{"input":1.0,"output":2.0}}""",
            });
            await db.SaveChangesAsync();
        }
        var toolId = await CriarToolAsync();
        var chat = await CriarChatAsync();

        var run = await EnfileirarAsync(chat.Id, "executa com usage", [toolId]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var eventos = await LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token);
        var final = await AguardarFinalAsync(_client, chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);

        // SSE: generating carrega step/maxSteps (RF-006) — 1 e 2 de 25.
        var generating = eventos
            .Where(e => e.Event == "status" && e.Data.Contains("generating"))
            .Select(e => JsonDocument.Parse(e.Data).RootElement)
            .Where(e => e.TryGetProperty("step", out var s)
                && s.ValueKind == JsonValueKind.Number)
            .ToList();
        Assert.Multiple(() =>
        {
            Assert.That(generating, Has.Count.GreaterThanOrEqualTo(2));
            Assert.That(
                generating.Select(e => e.GetProperty("step").GetInt32()),
                Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(generating.All(
                e => e.GetProperty("maxSteps").GetInt32() == 25), Is.True);
        });

        // meta.usage (RF-004/RF-005): 2 steps executados; só o step final
        // reportou usage → agregado de 10/5 + perStep com 1 entrada.
        Assert.That(final.Meta, Is.Not.Null);
        var usage = final.Meta!.Value.GetProperty("usage");
        Assert.Multiple(() =>
        {
            Assert.That(usage.GetProperty("steps").GetInt32(), Is.EqualTo(2));
            Assert.That(usage.GetProperty("promptTokens").GetInt32(), Is.EqualTo(10));
            Assert.That(usage.GetProperty("completionTokens").GetInt32(), Is.EqualTo(5));
            Assert.That(usage.TryGetProperty("reasoningTokens", out _), Is.False);
            var perStep = usage.GetProperty("perStep");
            Assert.That(perStep.GetArrayLength(), Is.EqualTo(1));
            Assert.That(perStep[0].GetProperty("step").GetInt32(), Is.EqualTo(2));
            // custo = 10*1.0/1M + 5*2.0/1M = 0.00002 USD.
            Assert.That(usage.GetProperty("costUsd").GetDouble(),
                Is.EqualTo(0.00002).Within(1e-9));
        });
    }

    [Test]
    public async Task Run_SemModelCost_MetaSemCostUsd()
    {
        // Sem ModelEntry para o modelo da run: usage agregado presente,
        // costUsd omitido (RF-005 — custo parcial/ausente nunca inventa 0).
        var auth = await SignUpAsync("UsageB", "usageb@tools.local");
        UseToken(auth.Token);
        var toolId = await CriarToolAsync();
        var chat = await CriarChatAsync();

        var run = await EnfileirarAsync(
            chat.Id, "sem custo", [toolId], model: "nobiling:1");
        var final = await AguardarFinalAsync(_client, chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);

        Assert.That(final.Meta, Is.Not.Null);
        var usage = final.Meta!.Value.GetProperty("usage");
        Assert.Multiple(() =>
        {
            Assert.That(usage.GetProperty("steps").GetInt32(), Is.EqualTo(2));
            Assert.That(usage.GetProperty("promptTokens").GetInt32(), Is.EqualTo(10));
            Assert.That(usage.TryGetProperty("costUsd", out _), Is.False);
        });
    }

    [Test]
    public async Task Approval_Nega_InjetaResultadoNegado()
    {
        var auth = await SignUpAsync("GateB", "gateb@tools.local");
        UseToken(auth.Token);
        var toolId = await CriarToolAsync(requiresApproval: true);
        var chat = await CriarChatAsync();

        var run = await EnfileirarAsync(chat.Id, "nega a tool", [toolId]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var streamTask = Task.Run(() => LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token));
        var limite = DateTime.UtcNow.AddSeconds(30);
        var negou = false;
        while (DateTime.UtcNow < limite && !negou)
        {
            var resp = await _client.PostAsJsonAsync(
                $"/api/v1/chats/{chat.Id}/runs/{run.Id}/approvals/call-1",
                new RunApprovalDecisionRequest("deny"));
            negou = resp.StatusCode == HttpStatusCode.OK;
            if (!negou)
            {
                await Task.Delay(200, cts.Token);
            }
        }
        Assert.That(negou, Is.True, "gate não pediu aprovação em 30s");

        var eventos = await streamTask;
        Assert.That(eventos.Any(e => e.Event == "tool_result"
            && e.Data.Contains("\"denied\":true")), Is.True, "faltou tool_result negado");

        var final = await AguardarFinalAsync(_client, chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);
    }

    [Test]
    public async Task Approval_NegaComMensagem_InstrucaoVaiComoResultado()
    {
        var auth = await SignUpAsync("GateD", "gated@tools.local");
        UseToken(auth.Token);
        var toolId = await CriarToolAsync(requiresApproval: true);
        var chat = await CriarChatAsync();

        var run = await EnfileirarAsync(chat.Id, "nega com instrução", [toolId]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var streamTask = Task.Run(() => LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token));
        var limite = DateTime.UtcNow.AddSeconds(30);
        var negou = false;
        while (DateTime.UtcNow < limite && !negou)
        {
            var resp = await _client.PostAsJsonAsync(
                $"/api/v1/chats/{chat.Id}/runs/{run.Id}/approvals/call-1",
                new RunApprovalDecisionRequest("deny", Message: "use outra abordagem"));
            negou = resp.StatusCode == HttpStatusCode.OK;
            if (!negou)
            {
                await Task.Delay(200, cts.Token);
            }
        }
        Assert.That(negou, Is.True, "gate não pediu aprovação em 30s");

        var eventos = await streamTask;
        Assert.That(eventos.Any(e => e.Event == "tool_result"
            && e.Data.Contains("\"denied\":true")
            && e.Data.Contains("use outra abordagem")), Is.True,
            "instrução da negação não virou resultado da tool");

        var final = await AguardarFinalAsync(_client, chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);
    }

    [Test]
    public async Task AskUser_EmiteQuestionAsked_RespostaViraResultado()
    {
        var auth = await SignUpAsync("AskQ", "askq@tools.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();

        var run = await EnfileirarAsync(chat.Id, "pergunta ask_user",
            ["builtin:ask_user"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Espera o question_asked, responde via mesmo endpoint de aprovação.
        var streamTask = Task.Run(() => LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token));
        var limite = DateTime.UtcNow.AddSeconds(30);
        var respondeu = false;
        while (DateTime.UtcNow < limite && !respondeu)
        {
            var resp = await _client.PostAsJsonAsync(
                $"/api/v1/chats/{chat.Id}/runs/{run.Id}/approvals/call-q",
                new RunApprovalDecisionRequest("approve", Message: "prod"));
            respondeu = resp.StatusCode == HttpStatusCode.OK;
            if (!respondeu)
            {
                await Task.Delay(200, cts.Token);
            }
        }
        Assert.That(respondeu, Is.True, "ask_user não pediu resposta em 30s");

        var eventos = await streamTask;
        Assert.Multiple(() =>
        {
            Assert.That(eventos.Any(e => e.Event == "question_asked"
                    && e.Data.Contains("Qual env?")), Is.True,
                "faltou question_asked no stream");
            Assert.That(eventos.Any(e => e.Event == "tool_result"
                    && e.Data.Contains("prod")), Is.True,
                "resposta não virou tool_result");
        });

        var final = await AguardarFinalAsync(_client, chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);
    }

    [Test]
    public async Task AskUser_Skip_RunSegueComNaoRespondeu()
    {
        var auth = await SignUpAsync("AskS", "asks@tools.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();

        var run = await EnfileirarAsync(chat.Id, "pergunta ask_user",
            ["builtin:ask_user"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var streamTask = Task.Run(() => LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token));
        var limite = DateTime.UtcNow.AddSeconds(30);
        var pulou = false;
        while (DateTime.UtcNow < limite && !pulou)
        {
            var resp = await _client.PostAsJsonAsync(
                $"/api/v1/chats/{chat.Id}/runs/{run.Id}/approvals/call-q",
                new RunApprovalDecisionRequest("deny"));
            pulou = resp.StatusCode == HttpStatusCode.OK;
            if (!pulou)
            {
                await Task.Delay(200, cts.Token);
            }
        }
        Assert.That(pulou, Is.True, "ask_user não pediu resposta em 30s");

        var eventos = await streamTask;
        // "não" serializa escapado no SSE (ã) — casa o trecho ASCII.
        Assert.That(eventos.Any(e => e.Event == "tool_result"
                && e.Data.Contains("respondeu")), Is.True,
            "skip não virou tool_result 'não respondeu'");

        var final = await AguardarFinalAsync(_client, chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);
    }

    [Test]
    public async Task AskUser_MultiForm_EmiteQuestionsArray_RespostaViraJson()
    {
        var auth = await SignUpAsync("AskM", "askm@tools.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();

        var run = await EnfileirarAsync(chat.Id, "multi-form pergunta",
            ["builtin:ask_user"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var streamTask = Task.Run(() => LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token));
        var limite = DateTime.UtcNow.AddSeconds(30);
        var respondeu = false;
        while (DateTime.UtcNow < limite && !respondeu)
        {
            var resp = await _client.PostAsJsonAsync(
                $"/api/v1/chats/{chat.Id}/runs/{run.Id}/approvals/call-q",
                new RunApprovalDecisionRequest(
                    "approve",
                    Message: "{\"answers\":[\"prod\",[\"a\",\"b\"],\"detalhe x\"]}"));
            respondeu = resp.StatusCode == HttpStatusCode.OK;
            if (!respondeu)
            {
                await Task.Delay(200, cts.Token);
            }
        }
        Assert.That(respondeu, Is.True, "ask_user multi-form não pediu resposta em 30s");

        var eventos = await streamTask;
        Assert.Multiple(() =>
        {
            Assert.That(eventos.Any(e => e.Event == "question_asked"), Is.True,
                "faltou question_asked no stream");
            var asked = eventos.First(e => e.Event == "question_asked");
            Assert.That(asked.Data, Does.Contain("\"questions\""),
                "form multi sem array questions");
            Assert.That(asked.Data, Does.Contain("Quais regioes"),
                "segunda pergunta ausente no evento");
            Assert.That(asked.Data, Does.Contain("Ambiente"),
                "header da primeira pergunta ausente");
            Assert.That(eventos.Any(e => e.Event == "tool_result"
                    && e.Data.Contains("prod") && e.Data.Contains("detalhe x")), Is.True,
                "answers JSON não virou tool_result");
        });

        var final = await AguardarFinalAsync(_client, chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);
    }

    [Test]
    public async Task AskUser_MultiForm_Deny_DevolveRecusa()
    {
        var auth = await SignUpAsync("AskR", "askr@tools.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();

        var run = await EnfileirarAsync(chat.Id, "multi-form recusa",
            ["builtin:ask_user"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var streamTask = Task.Run(() => LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token));
        var limite = DateTime.UtcNow.AddSeconds(30);
        var recusou = false;
        while (DateTime.UtcNow < limite && !recusou)
        {
            var resp = await _client.PostAsJsonAsync(
                $"/api/v1/chats/{chat.Id}/runs/{run.Id}/approvals/call-q",
                new RunApprovalDecisionRequest("deny"));
            recusou = resp.StatusCode == HttpStatusCode.OK;
            if (!recusou)
            {
                await Task.Delay(200, cts.Token);
            }
        }
        Assert.That(recusou, Is.True, "ask_user multi-form não pediu resposta em 30s");

        var eventos = await streamTask;
        Assert.That(eventos.Any(e => e.Event == "tool_result"
                && e.Data.Contains("recusou")), Is.True,
            "dismiss não virou tool_result de recusa");

        var final = await AguardarFinalAsync(_client, chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);
    }

    [Test]
    public async Task Auto_MediumRisk_ExecutaComNoticeSemPerguntar()
    {
        var auth = await SignUpAsync("AutoM", "autom@tools.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();
        var patch = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}", new ChatPatchRequest("auto"));
        Assert.That(patch.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var run = await EnfileirarAsync(chat.Id, "escreve o arquivo",
            ["builtin:file_write"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var eventos = await LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token);

        Assert.Multiple(() =>
        {
            Assert.That(eventos.Any(e => e.Event == "status"
                    && e.Data.Contains("auto_approved")), Is.True,
                "faltou notice auto_approved");
            Assert.That(eventos.Any(e => e.Event == "approval_asked"), Is.False,
                "risco médio não pode pedir aprovação");
            Assert.That(eventos.Any(e => e.Event == "tool_result"
                    && e.Data.Contains("\"ok\":true")), Is.True,
                "file_write não executou");
        });
        var final = await AguardarFinalAsync(_client, chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);
    }

    [Test]
    public async Task Changes_FileWrite_PublicaSnapshotAcumulado()
    {
        // RF-015: file_write/file_edit acumulam {path,+a,-d,diff} e o
        // executor republica `event: changes` — alimenta a aba Changes.
        var auth = await SignUpAsync("Chg", "chg@tools.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();
        var patch = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}", new ChatPatchRequest("auto"));
        Assert.That(patch.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var run = await EnfileirarAsync(chat.Id, "escreve o arquivo",
            ["builtin:file_write"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var eventos = await LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token);

        Assert.That(eventos.Any(e => e.Event == "changes"), Is.True,
            "faltou evento changes");
        var change = eventos.First(e => e.Event == "changes");
        Assert.Multiple(() =>
        {
            Assert.That(change.Data, Does.Contain("\"path\":\"saida.txt\""));
            Assert.That(change.Data, Does.Contain("\"added\":"));
            Assert.That(change.Data, Does.Contain("\"removed\":"));
        });

        var final = await AguardarFinalAsync(_client, chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);
    }

    [Test]
    public async Task Auto_HighRisk_PedeAprovacao()
    {
        var auth = await SignUpAsync("AutoH", "autoh@tools.local");
        UseToken(auth.Token);
        var toolId = await CriarToolAsync(requiresApproval: true);
        var chat = await CriarChatAsync();
        var patch = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}", new ChatPatchRequest("auto"));
        Assert.That(patch.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var run = await EnfileirarAsync(chat.Id, "tool arbitrária", [toolId]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var streamTask = Task.Run(() => LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token));
        var limite = DateTime.UtcNow.AddSeconds(30);
        var temPedido = false;
        while (DateTime.UtcNow < limite && !temPedido)
        {
            var decidido = await _client.PostAsJsonAsync(
                $"/api/v1/chats/{chat.Id}/runs/{run.Id}/approvals/call-1",
                new RunApprovalDecisionRequest("approve"));
            temPedido = decidido.StatusCode == HttpStatusCode.OK;
            if (!temPedido)
            {
                await Task.Delay(200, cts.Token);
            }
        }
        Assert.That(temPedido, Is.True, "HIGH não pediu aprovação em 30s");

        var eventos = await streamTask;
        Assert.Multiple(() =>
        {
            Assert.That(eventos.Any(e => e.Event == "approval_asked"), Is.True);
            Assert.That(eventos.Any(e => e.Event == "tool_result"
                    && e.Data.Contains("resultado-eco")), Is.True,
                "tool não executou após aprovação");
        });
    }

    // ---- ChatRunPauses (unitário, RF-013) ----

    [Test]
    public async Task Pausa_BloqueiaCheckpoint_RetomadaLibera()
    {
        var pauses = new ChatRunPauses();
        pauses.Pause("r1");

        var wait = pauses.WaitIfPausedAsync("r1", CancellationToken.None);
        Assert.That(wait.IsCompleted, Is.False, "checkpoint não bloqueou pausado");

        pauses.Resume("r1");
        await wait;

        // O gate é pass-through: checkpoints seguintes passam direto.
        await pauses.WaitIfPausedAsync("r1", CancellationToken.None);
    }

    [Test]
    public void Pausa_CheckpointCancelado_PropagaOCE()
    {
        var pauses = new ChatRunPauses();
        pauses.Pause("r1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.That(
            async () => await pauses.WaitIfPausedAsync("r1", cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Pausa_Idempotente_ForgetLimpaGate()
    {
        var pauses = new ChatRunPauses();
        pauses.Pause("r2");
        pauses.Pause("r2"); // pausar duas vezes não empilha
        pauses.Resume("r2");
        await pauses.WaitIfPausedAsync("r2", CancellationToken.None);

        pauses.Forget("r2"); // fim da run: gate novo começa aberto
        await pauses.WaitIfPausedAsync("r2", CancellationToken.None);
    }

    // ---- Pause/resume ponta a ponta ----

    [Test]
    public async Task Pausa_DuranteAprovacao_SuspendeAteResume()
    {
        var auth = await SignUpAsync("PauseA", "pausea@tools.local");
        UseToken(auth.Token);
        var toolId = await CriarToolAsync(requiresApproval: true);
        var chat = await CriarChatAsync();

        var run = await EnfileirarAsync(chat.Id, "pausa no meio", [toolId]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Lê o stream ao vivo até o approval_asked — aí a run está
        // deterministicamente bloqueada no gate de aprovação.
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/chats/{chat.Id}/runs/{run.Id}/stream?lastSeq=0");
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var eventos = new List<(string Event, string Data)>();
        var evento = "";
        var viuPedido = false;
        while (!viuPedido && await reader.ReadLineAsync(cts.Token) is { } linha)
        {
            if (linha.StartsWith("event:", StringComparison.Ordinal))
            {
                evento = linha["event:".Length..].Trim();
            }
            else if (linha.StartsWith("data:", StringComparison.Ordinal))
            {
                eventos.Add((evento, linha["data:".Length..].Trim()));
                viuPedido = evento == "approval_asked";
            }
        }
        Assert.That(viuPedido, Is.True, "gate não pediu aprovação");

        // Pausa com a run esperando aprovação → status paused no servidor.
        var pausa = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/pause", null, cts.Token);
        Assert.That(pausa.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await pausa.Content.ReadAsStringAsync());

        // Aprovação ainda funciona na run pausada, mas ela não passa do
        // próximo checkpoint — segue "paused" depois da decisão.
        var aprovou = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/approvals/call-1",
            new RunApprovalDecisionRequest("approve"), cts.Token);
        Assert.That(aprovou.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await Task.Delay(500, cts.Token);
        var travada = await _client.GetFromJsonAsync<ChatRunResponse>(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}", cts.Token);
        Assert.That(travada!.Status, Is.EqualTo("paused"),
            "run aprovada tinha que continuar parada no checkpoint de pausa");

        var retomada = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/resume", null, cts.Token);
        Assert.That(retomada.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Continua lendo o stream até o status terminal.
        evento = "";
        while (await reader.ReadLineAsync(cts.Token) is { } linha)
        {
            if (linha.StartsWith("event:", StringComparison.Ordinal))
            {
                evento = linha["event:".Length..].Trim();
            }
            else if (linha.StartsWith("data:", StringComparison.Ordinal))
            {
                var payload = linha["data:".Length..].Trim();
                eventos.Add((evento, payload));
                if (evento == "status" && payload.Contains("\"completed\""))
                {
                    break;
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(eventos.Any(e => e.Event == "status"
                    && e.Data.Contains("\"paused\"")), Is.True, "faltou status paused");
            Assert.That(eventos.Any(e => e.Event == "status"
                    && e.Data.Contains("\"resumed\"")), Is.True, "faltou status resumed");
            Assert.That(eventos.Any(e => e.Event == "tool_result"
                    && e.Data.Contains("resultado-eco")), Is.True,
                "tool aprovada não executou após resume");
        });

        var final = await AguardarFinalAsync(_client, chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);
    }

    [Test]
    public async Task PauseResume_RunFinalizada_Retorna409()
    {
        var auth = await SignUpAsync("PauseB", "pauseb@tools.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();

        var run = await EnfileirarAsync(chat.Id, "termina logo");
        await AguardarFinalAsync(_client, chat.Id, run.Id);

        var pausa = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/pause", null);
        var retomada = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/resume", null);
        Assert.Multiple(() =>
        {
            Assert.That(pausa.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(retomada.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        });
    }

    [Test]
    public async Task PauseResume_IsolamentoPorUsuario()
    {
        var dono = await SignUpAsync("PauseC", "pausec@tools.local");
        UseToken(dono.Token);
        var chat = await CriarChatAsync();
        var run = await EnfileirarAsync(chat.Id, "minha run");

        var intruso = await SignUpAsync("PauseD", "paused@tools.local");
        UseToken(intruso.Token);

        var pausa = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/pause", null);
        var retomada = await _client.PostAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/resume", null);
        Assert.Multiple(() =>
        {
            Assert.That(pausa.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(retomada.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task Preset_AlwaysAllow_PulaGate()
    {
        var auth = await SignUpAsync("GateC", "gatec@tools.local");
        UseToken(auth.Token);
        var toolId = await CriarToolAsync(requiresApproval: true);
        var chat = await CriarChatAsync();

        var patch = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}", new ChatPatchRequest("always-allow"));
        Assert.That(patch.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var run = await EnfileirarAsync(chat.Id, "sem gate", [toolId]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var eventos = await LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token);

        Assert.Multiple(() =>
        {
            Assert.That(eventos.Any(e => e.Event == "approval_asked"), Is.False,
                "always-allow não pode pedir aprovação");
            Assert.That(eventos.Any(e => e.Event == "tool_result"
                    && e.Data.Contains("resultado-eco")), Is.True);
        });
    }

    // ---- delegate_task (RF-016) ----

    [Test]
    public async Task Delegate_CriaRunFilhaEmChatProprioEAggregaResultado()
    {
        // O pai pede builtin:delegate_task → a tool cria um chat "delegado:"
        // com run própria (dispatcher serializa por chat — filho no mesmo
        // chat deadlockaria), aguarda e devolve o link ao transcript.
        var auth = await SignUpAsync("Del", "del@tools.local");
        UseToken(auth.Token);
        var toolId = await CriarToolAsync();
        var chat = await CriarChatAsync();
        var patch = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}", new ChatPatchRequest("always-allow"));
        Assert.That(patch.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Binding repo por chat no pai — o filho deve HERDAR
        // (SPEC-20261010-chat-repo-binding).
        using (var scope = _factory.Services.CreateScope())
        {
            var cfg = scope.ServiceProvider.GetRequiredService<ConfigService>();
            await cfg.SetAsync(
                $"chat:{chat.Id}:workspace.repo",
                new WorkspaceRepoBinding("a/pai", "main", "repos/a__pai", null, null),
                default);
        }

        var run = await EnfileirarAsync(chat.Id, "delegue a subtarefa",
            ["builtin:delegate_task", toolId]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var eventos = await LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token);

        Assert.Multiple(() =>
        {
            Assert.That(eventos.Any(e => e.Event == "tool_call"
                    && e.Data.Contains("delegate_task")), Is.True,
                "faltou tool_call do delegate");
            Assert.That(eventos.Any(e => e.Event == "tool_result"
                    && e.Data.Contains("childChatId")
                    && e.Data.Contains("\"ok\":true")), Is.True,
                "faltou tool_result com childChatId");
        });

        var final = await AguardarFinalAsync(_client, chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);

        // Chat filho "delegado:" existe e sua run terminou completed —
        // a filha herdou o toolId (menos o próprio delegate): o mock
        // devolve a call `eco` e o loop fecha com "resposta pós-tool".
        var chats = await _client.GetFromJsonAsync<List<ChatResponse>>("/api/v1/chats/");
        // Filho DIRETO via ParentChatId — com SPEC-20261010-async-delegate o
        // filho também pode delegar (depth < MaxDepth), gerando netos/bisnetos
        // "delegado:" que não podem confundir o lookup por título.
        var filho = chats!.SingleOrDefault(c => c.ParentChatId == chat.Id);
        Assert.That(filho, Is.Not.Null, "chat filho não foi criado");

        // Herança de binding (SPEC-20261010-chat-repo-binding + SPEC-15):
        // o kv do filho aponta para o mesmo repo/branch do pai, mas com
        // worktree PRÓPRIO por conversa — não escreve no worktree do pai.
        using (var scope = _factory.Services.CreateScope())
        {
            var cfg = scope.ServiceProvider.GetRequiredService<ConfigService>();
            var herdado = await cfg.GetAsync<WorkspaceRepoBinding?>(
                $"chat:{filho!.Id}:workspace.repo", null, default);
            Assert.Multiple(() =>
            {
                Assert.That(herdado, Is.Not.Null, "filho não herdou o binding do chat pai");
                Assert.That(herdado!.Repo, Is.EqualTo("a/pai"));
                Assert.That(herdado.Dir,
                    Is.EqualTo($"worktrees/a__pai/{filho.Id}"));
            });
        }

        var runs = await _client.GetFromJsonAsync<List<ChatRunResponse>>(
            $"/api/v1/chats/{filho!.Id}/runs");
        Assert.That(runs, Has.Count.EqualTo(1));
        Assert.That(runs![0].Status, Is.EqualTo("completed"), runs[0].Error);

        // Hierarquia (SPEC-20261010-runs-hierarchy): filho linkado ao pai
        // nos dois sentidos — Chat.ParentChatId, ChatRun.ParentRunId e
        // GET /{pai}/children listando a subtarefa com status da run.
        var filhoDetalhe = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{filho.Id}");
        Assert.Multiple(() =>
        {
            Assert.That(filhoDetalhe!.ParentChatId, Is.EqualTo(chat.Id));
            Assert.That(filhoDetalhe.ParentTitle, Is.EqualTo(chat.Title));
            Assert.That(runs[0].ParentRunId, Is.EqualTo(run.Id));
        });

        var children = await _client.GetFromJsonAsync<List<ChatChildSummaryResponse>>(
            $"/api/v1/chats/{chat.Id}/children");
        Assert.That(children, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(children![0].Id, Is.EqualTo(filho.Id));
            Assert.That(children[0].LastRunStatus, Is.EqualTo("completed"));
        });

        // O resumo do pai carrega childrenCount pra sidebar agrupar.
        var summaries = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>(
            "/api/v1/chats/");
        var paiResumo = summaries!.Single(c => c.Id == chat.Id);
        var filhoResumo = summaries!.Single(c => c.Id == filho.Id);
        Assert.Multiple(() =>
        {
            Assert.That(paiResumo.ChildrenCount, Is.EqualTo(1));
            Assert.That(filhoResumo.ParentChatId, Is.EqualTo(chat.Id));
        });
    }

    // ---- browser_screenshot (RF-017) ----

    [Test]
    public async Task BrowserScreenshot_Desabilitada_RecusaComAviso()
    {
        // Flag off por padrão (RF-017): a tool explica como habilitar em
        // vez de executar. Garante estado deterministicamente via PUT.
        var auth = await SignUpAsync("BsOff", "bsoff@tools.local");
        UseToken(_adminToken);
        var off = await _client.PutAsJsonAsync(
            "/api/v1/browser/config", new { enabled = false });
        Assert.That(off.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        UseToken(auth.Token);

        var chat = await CriarChatAsync();
        var run = await EnfileirarAsync(chat.Id, "tira um print",
            ["builtin:browser_screenshot"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var eventos = await LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token);

        Assert.That(eventos.Any(e => e.Event == "tool_result"
                && e.Data.Contains("desabilitada")), Is.True,
            "tool desabilitada deveria recusar com aviso");
        var final = await AguardarFinalAsync(_client, chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);
    }

    [Test]
    public async Task BrowserScreenshot_Habilitada_CapturaEServePng()
    {
        // Admin liga a flag → a tool captura o mock Ollama via loopback
        // (único host privado permitido) e a imagem fica servível em
        // /api/v1/files/{id}/content. Sem browser headless no ambiente
        // o teste é ignorado (Assert.Ignore), não quebra CI.
        var auth = await SignUpAsync("BsOn", "bson@tools.local");
        UseToken(_adminToken);
        var on = await _client.PutAsJsonAsync(
            "/api/v1/browser/config", new { enabled = true });
        Assert.That(on.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        UseToken(auth.Token);

        var chat = await CriarChatAsync();
        var run = await EnfileirarAsync(chat.Id, "tira um print",
            ["builtin:browser_screenshot"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var eventos = await LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token);

        var resultado = eventos.FirstOrDefault(e => e.Event == "tool_result"
            && e.Data.Contains("browser_screenshot")).Data
            ?? eventos.FirstOrDefault(e => e.Event == "tool_result").Data;
        Assert.That(resultado, Is.Not.Null, "faltou tool_result");
        if (resultado!.Contains("Nenhum browser headless"))
        {
            Assert.Ignore("Ambiente sem browser headless instalado.");
        }

        Assert.Multiple(() =>
        {
            Assert.That(resultado, Does.Contain("imagePath"),
                "resultado deveria carregar imagePath");
            Assert.That(resultado, Does.Contain("\"ok\":true"));
        });

        using var doc = JsonDocument.Parse(resultado!);
        var imagePath = doc.RootElement.GetProperty("imagePath").GetString()!;
        var img = await _client.GetAsync(imagePath);
        Assert.That(img.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            $"GET {imagePath} falhou");
        var png = await img.Content.ReadAsByteArrayAsync();
        Assert.That(png.Length, Is.GreaterThan(100));
        Assert.That(png[0], Is.EqualTo(0x89), "não é PNG");

        // Limpa a flag pra não vazar estado entre testes.
        UseToken(_adminToken);
        await _client.PutAsJsonAsync("/api/v1/browser/config", new { enabled = false });
    }

    // ---- SPEC-20261015-chat-thinking-stream: fases + canal reasoning ----

    [Test]
    public async Task RunSemTools_EmiteFasesDeProgressoEReasoning()
    {
        var auth = await SignUpAsync("Think", "think@tools.local");
        UseToken(auth.Token);

        // Chat puro (modo "chat", sem ToolIds): cai no caminho sem-tools.
        // RF-005: preparing_context → connecting_model → generating →
        // answering; o mock devolve message.thinking (→ delta.reasoning).
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Chat thinking", ["fake:1"], []));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;

        var run = await EnfileirarAsync(chat.Id, "oi");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var eventos = await LerStreamAteFecharAsync(chat.Id, run.Id, cts.Token);

        // event: status carrega dois shapes — {phase,label} de progresso
        // e {status} de lifecycle (running/completed). Filtra o primeiro.
        var fases = eventos.Where(e => e.Event == "status")
            .Select(e => JsonDocument.Parse(e.Data).RootElement)
            .Where(r => r.TryGetProperty("phase", out _))
            .Select(r => r.GetProperty("phase").GetString())
            .ToList();
        Assert.Multiple(() =>
        {
            Assert.That(fases, Does.Contain("preparing_context"),
                "faltou fase preparing_context");
            Assert.That(fases, Does.Contain("connecting_model"),
                "faltou fase connecting_model");
            Assert.That(fases, Does.Contain("generating"),
                "faltou fase generating");
            Assert.That(fases, Does.Contain("answering"),
                "faltou fase answering");
            Assert.That(fases.IndexOf("preparing_context"),
                Is.LessThan(fases.IndexOf("connecting_model")));
            Assert.That(fases.IndexOf("connecting_model"),
                Is.LessThan(fases.IndexOf("generating")));
            Assert.That(fases.IndexOf("generating"),
                Is.LessThan(fases.IndexOf("answering")));
        });

        // Canal separado: thinking sai como delta.reasoning — nunca
        // mistura no content que vai pro histórico.
        var deltas = eventos.Where(e => e.Event == "").Select(e => e.Data).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(deltas.Any(d => d.Contains("\"reasoning\":\"raciocinio interno\"")),
                Is.True, "faltou delta.reasoning no stream");
            Assert.That(deltas.Any(d => d.Contains("\"content\":\"resposta final\"")),
                Is.True, "faltou delta.content no stream");
        });

        var final = await AguardarFinalAsync(_client, chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"), final.Error);
    }
}
