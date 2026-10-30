using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Fluxo e2e do SPEC-20261009-agent-modes-plan-build: chat em modo
/// <c>plan</c> anuncia as tools gated (SPEC-20261030-plan-exec-analysis:
/// escrita só de SPECs/planos + exec via preset) mas esconde as negadas,
/// a call <c>builtin:plan_exit</c> pergunta "Executar este plano?" e a
/// aprovação promove o chat a <c>build</c> (persistido + evento
/// <c>mode</c>); chamada de escrita fora do escopo de SPECs em plan
/// devolve erro estruturado e o modo permanece. O mock local
/// (HttpListener) faz de Ollama.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class AgentModeE2ETests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _mockBase = null!;
    private string _userId = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;

    /// <summary>Corpos das chamadas /api/chat recebidas pelo mock.</summary>
    private readonly ConcurrentBag<string> _chatBodies = new();

    /// <summary>Resposta do /api/chat a alternar por teste.</summary>
    private volatile string _emit = "plan_exit";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-modes-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _mockBase = StartMock();

        var admin = await SignUpAsync("Admin", "admin@modes.local");
        _userId = admin.User.Id;
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());

        var connResponse = await _client.PostAsJsonAsync(
            "/api/v1/configs/connections", new ConnectionsConfig([_mockBase], [], []));
        Assert.That(connResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
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
        // Workdir criado pelo plan_exit dentro do content root da API.
        try
        {
            var dir = Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..",
                "..", "src", "OpenWebUI.Api", "data", "workspaces", _userId);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Limpeza best-effort — data/ é ignorada pelo git.
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
                _chatBodies.Add(body);
                json = body.Contains("\"tool_call_id\"", StringComparison.Ordinal)
                    ? "{\"message\":{\"role\":\"assistant\",\"content\":\"resposta final\"}}"
                    : _emit switch
                    {
                        // Em plan: pede o plan_exit com uma SPEC SDD mínima
                        // válida (SPEC-20261015-plan-spec-sdd RF-002).
                        "file_write" =>
                            "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"id\":\"call-fw\",\"function\":{\"name\":\"builtin:file_write\",\"arguments\":{\"path\":\"x.txt\",\"content\":\"v\"}}}]}}",
                        _ =>
                            "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"id\":\"call-pe\",\"function\":{\"name\":\"builtin:plan_exit\",\"arguments\":{\"plan\":\"# SPEC-20261015-x\\n## 0. Metadata\\nStatus: Draft\\n## 1. User Story\\nx\\n## 2. Scope\\nx\\n## 4. Requirements\\nRF-001 1. Ler\\n## 6. Acceptance Criteria\\nx\\n## 7. Task Plan\\nx\",\"title\":\"T\"}}}]}}",
                    };
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

    private async Task<ChatResponse> CriarChatAsync(string? mode = null)
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("Chat modos", ["fake:1"], []));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
        if (mode is not null)
        {
            var patched = await _client.PatchAsJsonAsync(
                $"/api/v1/chats/{chat.Id}", new ChatPatchRequest(null, Mode: mode));
            Assert.That(patched.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                await patched.Content.ReadAsStringAsync());
            chat = (await patched.Content.ReadFromJsonAsync<ChatResponse>())!;
        }
        return chat;
    }

    private async Task<ChatRunResponse> EnfileirarAsync(
        string chatId, IReadOnlyList<string>? toolIds = null)
    {
        // Corpos de /api/chat ficam por teste — a bag é compartilhada.
        _chatBodies.Clear();
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chatId}/messages",
            new EnqueueChatRunRequest("planeje", "fake:1", ToolIds: toolIds));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChatRunResponse>())!;
    }

    /// <summary>Lê o stream da run; ao ver <c>question_asked</c> aprova.</summary>
    private async Task<List<(string Event, string Data)>> LerStreamAteFecharAsync(
        string chatId, string runId, bool aprovarPergunta, CancellationToken ct)
    {
        var eventos = new List<(string, string)>();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/chats/{chatId}/runs/{runId}/stream?lastSeq=0");
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);

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
                if ((evento == "question_asked" || evento == "approval_asked")
                    && aprovarPergunta)
                {
                    var callId = JsonDocument.Parse(payload).RootElement
                        .GetProperty("callId").GetString()!;
                    var decide = await _client.PostAsJsonAsync(
                        $"/api/v1/chats/{chatId}/runs/{runId}/approvals/{callId}",
                        new RunApprovalDecisionRequest("approve", false, "Executar o plano"), ct);
                    Assert.That(decide.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                        await decide.Content.ReadAsStringAsync(ct));
                }
                if (evento == "status"
                    && (payload.Contains("\"completed\"") || payload.Contains("\"failed\"")
                        || payload.Contains("\"stopped\"") || payload.Contains("\"interrupted\"")))
                {
                    break;
                }
                evento = "";
            }
        }
        return eventos;
    }

    // ---- PATCH mode (RF-001) ----

    [Test]
    public async Task Patch_Mode_PersisteEValida()
    {
        var chat = await CriarChatAsync();
        // SPEC-15: chat novo nasce em "chat" (sem repo → sem workspace).
        Assert.That(chat.Mode, Is.EqualTo("chat"));

        var plan = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}", new ChatPatchRequest(null, Mode: "plan"));
        Assert.That(plan.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(
            (await plan.Content.ReadFromJsonAsync<ChatResponse>())!.Mode,
            Is.EqualTo("plan"));

        var invalido = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}", new ChatPatchRequest(null, Mode: "yolo"));
        Assert.That(invalido.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var reload = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{chat.Id}");
        Assert.That(reload!.Mode, Is.EqualTo("plan"));
    }

    // ---- Fluxo plan → plan_exit → aprovação → build (RF-002/RF-003) ----

    [Test]
    public async Task PlanMode_PlanExitAprovado_PromoveBuild()
    {
        _emit = "plan_exit";
        var chat = await CriarChatAsync("plan");

        // SPEC-15: build exige repo vinculado — seed do binding por chat no
        // kv para a promoção plan→build resolver (clone é nível de serviço).
        using (var scope = _factory.Services.CreateScope())
        {
            var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
            await config.SetAsync(
                $"chat:{chat.Id}:workspace.repo",
                new WorkspaceRepoBinding("a/b", "main", "repos/a__b", null, null),
                default);
        }
        var run = await EnfileirarAsync(chat.Id,
            ["builtin:file_read", "builtin:file_write", "builtin:job_kill"]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var eventos = await LerStreamAteFecharAsync(chat.Id, run.Id, true, cts.Token);

        Assert.Multiple(() =>
        {
            // 1. Plan anuncia a escrita gated (SPEC-20261030) e o
            //    plan_exit; a tool negada (job_kill) fica de fora.
            var primeira = _chatBodies.First(b => !b.Contains("\"tool_call_id\""));
            Assert.That(primeira, Does.Contain("builtin_file_write"),
                "file_write gated é anunciada em plan");
            Assert.That(primeira, Does.Not.Contain("builtin_job_kill"),
                "job_kill segue negada — não anunciada em plan");
            Assert.That(primeira, Does.Contain("builtin_plan_exit"),
                "plan_exit precisa ser anunciado em plan");
            Assert.That(primeira, Does.Contain("builtin_file_read"));

            // 2. Pergunta "Executar este plano?" emitida pelo gate.
            Assert.That(eventos.Any(e => e.Event == "question_asked"
                && e.Data.Contains("Executar este plano")), Is.True);

            // 3. Evento mode=build publicado após a aprovação.
            Assert.That(eventos.Any(e => e.Event == "mode"
                && e.Data.Contains("\"build\"")), Is.True);

            // 4. Rodada pós-aprovação já anunciou file_write (modo build).
            var segunda = _chatBodies.LastOrDefault(b => b.Contains("\"tool_call_id\""));
            Assert.That(segunda, Is.Not.Null);
            Assert.That(segunda!, Does.Contain("builtin_file_write"),
                "após a promoção o spec anunciado volta a ter file_write");
        });

        // 5. Chat persistiu a promoção; plano gravado no workdir.
        var reload = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{chat.Id}");
        Assert.That(reload!.Mode, Is.EqualTo("build"));

        var planDir = Path.Join(AppContext.BaseDirectory, "..", "..", "..", "..",
            "..", "src", "OpenWebUI.Api", "data", "workspaces", _userId,
            ".openwebui", "plans");
        Assert.That(Directory.Exists(planDir), Is.True, "diretório de planos não criado");
        var planFiles = Directory.GetFiles(planDir, "*.md");
        Assert.That(planFiles, Is.Not.Empty);
        Assert.That(File.ReadAllText(planFiles[0]), Does.Contain("1. Ler"));
    }

    [Test]
    public async Task PlanMode_FileWriteCall_ErroEstruturadoEModoSeguePlan()
    {
        _emit = "file_write";
        try
        {
            var chat = await CriarChatAsync("plan");
            var run = await EnfileirarAsync(chat.Id, ["builtin:file_write"]);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var eventos = await LerStreamAteFecharAsync(chat.Id, run.Id, false, cts.Token);

            Assert.Multiple(() =>
            {
                // Call não anunciada que escapa devolve erro estruturado
                // (tool_result com ok=false, texto "Erro: ... bloqueada").
                Assert.That(eventos.Any(e => e.Event == "tool_result"
                    && e.Data.Contains("bloqueada")), Is.True,
                    "esperava erro estruturado do ruleset no tool_result");
                // Sem prompt de aprovação — a negação do modo é direta.
                Assert.That(eventos.Any(e => e.Event == "approval_asked"), Is.False);
                Assert.That(eventos.Any(e => e.Event == "question_asked"), Is.False);
            });

            var reload = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{chat.Id}");
            Assert.That(reload!.Mode, Is.EqualTo("plan"), "modo permanece plan");
        }
        finally
        {
            _emit = "plan_exit";
        }
    }

    // ---- Overrides persistentes do dono (SPEC-20261013-approval-tiers RF-002) ----

    /// <summary>Chat em modo build (binding semeado) com preset opcional.</summary>
    private async Task<ChatResponse> CriarChatBuildAsync(string? preset = null)
    {
        var chat = await CriarChatAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
            await config.SetAsync(
                $"chat:{chat.Id}:workspace.repo",
                new WorkspaceRepoBinding("a/b", "main", "repos/a__b", null, null),
                default);
        }
        var patched = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}",
            new ChatPatchRequest(preset, Mode: "build"));
        Assert.That(patched.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await patched.Content.ReadAsStringAsync());
        return (await patched.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    private async Task SetOverrideAsync(string tool, string? action)
    {
        var response = await _client.PutAsJsonAsync(
            "/api/v1/users/user/tool-approvals",
            new UpdateToolApprovalsRequest(
                new Dictionary<string, string?> { [tool] = action }));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task OverrideDeny_FileWriteCall_ErroSemPrompt()
    {
        _emit = "file_write";
        try
        {
            var chat = await CriarChatBuildAsync();
            await SetOverrideAsync("file_write", "deny");

            var run = await EnfileirarAsync(chat.Id, ["builtin:file_write"]);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var eventos = await LerStreamAteFecharAsync(chat.Id, run.Id, false, cts.Token);

            Assert.Multiple(() =>
            {
                // O override deny vira tool_result estruturado, sem prompt.
                Assert.That(eventos.Any(e => e.Event == "tool_result"
                    && e.Data.Contains("bloqueada pelo dono")), Is.True,
                    "esperava erro estruturado do override deny");
                Assert.That(eventos.Any(e => e.Event == "approval_asked"), Is.False);
                Assert.That(eventos.Any(e => e.Event == "question_asked"), Is.False);
            });
        }
        finally
        {
            await SetOverrideAsync("file_write", null);
            _emit = "plan_exit";
        }
    }

    [Test]
    public async Task OverrideAllow_FileWriteCall_ExecutaSemPrompt()
    {
        _emit = "file_write";
        try
        {
            // Preset padrão approve-mutations pediria aprovação para
            // file_write — o override allow tem de curto-circuitar o gate.
            var chat = await CriarChatBuildAsync();
            await SetOverrideAsync("file_write", "allow");

            var run = await EnfileirarAsync(chat.Id, ["builtin:file_write"]);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var eventos = await LerStreamAteFecharAsync(chat.Id, run.Id, false, cts.Token);

            Assert.Multiple(() =>
            {
                Assert.That(eventos.Any(e => e.Event == "approval_asked"), Is.False,
                    "override allow não pode pedir aprovação");
                Assert.That(eventos.Any(e => e.Event == "question_asked"), Is.False);
                // A tool rodou: resultado voltou ao provider (2ª /api/chat
                // com tool_call_id) e a run fechou completed.
                Assert.That(eventos.Any(e => e.Event == "tool_result"), Is.True);
                Assert.That(_chatBodies.Any(b => b.Contains("\"tool_call_id\"")),
                    Is.True, "tool_result não voltou ao provider");
                Assert.That(eventos.Any(e => e.Event == "status"
                    && e.Data.Contains("\"completed\"")), Is.True);
            });
        }
        finally
        {
            await SetOverrideAsync("file_write", null);
            _emit = "plan_exit";
        }
    }

    [Test]
    public async Task OverridePrompt_EmAlwaysAllow_PerguntaMesmoAssim()
    {
        _emit = "file_write";
        try
        {
            // always-allow liberaria direto — o override prompt força a
            // pergunta mesmo assim (RF-002: prompt vence preset).
            var chat = await CriarChatBuildAsync("always-allow");
            await SetOverrideAsync("file_write", "prompt");

            var run = await EnfileirarAsync(chat.Id, ["builtin:file_write"]);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var eventos = await LerStreamAteFecharAsync(chat.Id, run.Id, true, cts.Token);

            Assert.Multiple(() =>
            {
                Assert.That(eventos.Any(e => e.Event == "approval_asked"
                    && e.Data.Contains("builtin:file_write")), Is.True,
                    "override prompt tinha de emitir approval_asked");
                // Aprovada → executou e a run completou.
                Assert.That(eventos.Any(e => e.Event == "status"
                    && e.Data.Contains("\"completed\"")), Is.True);
            });
        }
        finally
        {
            await SetOverrideAsync("file_write", null);
            _emit = "plan_exit";
        }
    }
}
