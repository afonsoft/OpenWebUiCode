using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261128-task-history: snapshots do <c>todo_write</c> persistem
/// append-only em <c>chat_task_lists</c>, o endpoint
/// <c>GET /api/v1/chats/{id}/task-lists</c> devolve a última lista por run
/// e a run seguinte recebe a lista anterior como nota
/// <c>&lt;previous_tasks&gt;</c>. O mock (HttpListener) faz de Ollama:
/// emite <c>builtin:todo_write</c> até a última mensagem ser role=tool.
/// </summary>
[TestFixture, IsolateEnvironment]
[NonParallelizable]
public class TaskListHistoryTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _mockBase = null!;
    private HttpListener _mock = null!;
    private CancellationTokenSource _mockCts = null!;

    /// <summary>Bodies recebidos pelo mock em <c>/api/chat</c>.</summary>
    private readonly ConcurrentBag<string> _bodies = new();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-taskhist-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _mockBase = StartMock();

        var admin = await SignUpAsync("Admin", "admin@taskhist.local");
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
                _bodies.Add(body);
                if (body.Contains("run advisor watching", StringComparison.Ordinal))
                {
                    json = "{\"message\":{\"role\":\"assistant\",\"content\":\"resposta final\"}}";
                }
                else
                {
                    var hasTools = body.Contains("\"tools\"", StringComparison.Ordinal);
                    var lastRole = body.LastIndexOf("\"role\":\"", StringComparison.Ordinal);
                    // A nota efêmera <context_usage> senta no fim do request
                    // — a mensagem efetiva é a anterior a ela.
                    if (lastRole >= 0
                        && body.AsSpan(lastRole).StartsWith(
                            "\"role\":\"system\"", StringComparison.Ordinal)
                        && body.Contains("context_usage", StringComparison.Ordinal))
                    {
                        lastRole = body.LastIndexOf(
                            "\"role\":\"", lastRole - 1, StringComparison.Ordinal);
                    }
                    var lastIsTool = lastRole >= 0
                        && body.AsSpan(lastRole).StartsWith(
                            "\"role\":\"tool\"", StringComparison.Ordinal);
                    json = hasTools && !lastIsTool
                        ? "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"id\":\"call-todo\",\"function\":{\"name\":\"builtin:todo_write\",\"arguments\":{\"todos\":[{\"content\":\"tarefa A\",\"status\":\"in_progress\"},{\"content\":\"tarefa B\",\"status\":\"pending\"}]}}}]}}"
                        : "{\"message\":{\"role\":\"assistant\",\"content\":\"resposta final\"}}";
                }
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

    /// <summary>Chat em modo build + always-allow (binding fake de repo),
    /// mesmo setup do fixture AgentLoopAutoChain.</summary>
    private async Task<ChatResponse> CriarChatAsync(string usuario)
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest($"Hist {usuario}", ["fake:1"], []));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        var chat = (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
        using (var scope = _factory.Services.CreateScope())
        {
            var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
            await config.SetAsync(
                $"chat:{chat.Id}:workspace.repo",
                new WorkspaceRepoBinding("a/b", "main", $"repos/hist_{usuario}", null, null),
                default);
        }
        using var req = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/v1/chats/{chat.Id}")
        {
            Content = JsonContent.Create(
                new ChatPatchRequest("always-allow", Mode: "build")),
        };
        using var resp = await _client.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await resp.Content.ReadAsStringAsync());
        return chat;
    }

    private async Task<ChatRunResponse> EnfileirarAsync(string chatId)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chatId}/messages",
            new EnqueueChatRunRequest("planeje", "fake:1",
                ToolIds: ["builtin:todo_write"]));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChatRunResponse>())!;
    }

    private async Task<ChatRunResponse> AguardarFinalAsync(string chatId, string runId)
    {
        var limite = DateTime.UtcNow.AddSeconds(90);
        ChatRunResponse? last = null;
        while (DateTime.UtcNow < limite)
        {
            last = await _client.GetFromJsonAsync<ChatRunResponse>(
                $"/api/v1/chats/{chatId}/runs/{runId}");
            if (last!.Status is not ("queued" or "running"))
            {
                return last;
            }
            await Task.Delay(150);
        }
        Assert.Fail($"Run {runId} não finalizou em 90s — "
            + $"último status={last?.Status} erro={last?.Error}");
        return null!;
    }

    [Test]
    public async Task TodoWrite_PersisteSnapshotEExpoeNoEndpoint()
    {
        var auth = await SignUpAsync("TH1", "th1@taskhist.local");
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.Token);
        var chat = await CriarChatAsync("th1");

        var run = await EnfileirarAsync(chat.Id);
        var final = await AguardarFinalAsync(chat.Id, run.Id);
        Assert.That(final.Status, Is.EqualTo("completed"));

        // Snapshot persistido append-only no banco.
        List<ChatTaskList> rows;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            rows = await db.ChatTaskLists.AsNoTracking()
                .Where(t => t.RunId == run.Id)
                .ToListAsync();
        }
        Assert.That(rows, Is.Not.Empty, "todo_write não gravou snapshot");
        Assert.Multiple(() =>
        {
            Assert.That(rows.All(t => t.ChatId == chat.Id), Is.True);
            Assert.That(rows.All(t => t.UserId == auth.User.Id), Is.True);
            Assert.That(rows.Last().ItemsJson, Does.Contain("tarefa A"));
        });

        // Endpoint devolve a última lista por run.
        var lists = await _client.GetFromJsonAsync<List<ChatTaskListEntry>>(
            $"/api/v1/chats/{chat.Id}/task-lists");
        Assert.That(lists, Is.Not.Null);
        var entry = lists!.Single(e => e.RunId == run.Id);
        Assert.Multiple(() =>
        {
            Assert.That(entry.ChatId, Is.EqualTo(chat.Id));
            Assert.That(entry.Items.Select(i => i.Content),
                Is.EqualTo(new[] { "tarefa A", "tarefa B" }));
            Assert.That(entry.Items.First().Status, Is.EqualTo("in_progress"));
        });

        // Isolamento: outro usuário não lê o histórico do chat.
        var outro = await SignUpAsync("TH2", "th2@taskhist.local");
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", outro.Token);
        var negado = await _client.GetAsync(
            $"/api/v1/chats/{chat.Id}/task-lists");
        Assert.That(negado.StatusCode,
            Is.AnyOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound));
    }

    [Test]
    public async Task SegundaRun_RecebePreviousTasksDoChat()
    {
        var auth = await SignUpAsync("TH3", "th3@taskhist.local");
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.Token);
        var chat = await CriarChatAsync("th3");

        var run1 = await EnfileirarAsync(chat.Id);
        var final1 = await AguardarFinalAsync(chat.Id, run1.Id);
        Assert.That(final1.Status, Is.EqualTo("completed"));

        _bodies.Clear();
        var run2 = await EnfileirarAsync(chat.Id);
        var final2 = await AguardarFinalAsync(chat.Id, run2.Id);
        Assert.That(final2.Status, Is.EqualTo("completed"));

        // A primeira request da run 2 carrega a nota de continuidade com a
        // lista da run 1 — a run continua do plano anterior.
        var first = _bodies.FirstOrDefault(b =>
            !b.Contains("run advisor watching", StringComparison.Ordinal));
        Assert.That(first, Is.Not.Null, "mock não recebeu request da run 2");
        Assert.Multiple(() =>
        {
            Assert.That(first, Does.Contain("previous_tasks"),
                "run 2 sem a nota <previous_tasks>");
            Assert.That(first, Does.Contain("tarefa A"));
        });
    }
}
