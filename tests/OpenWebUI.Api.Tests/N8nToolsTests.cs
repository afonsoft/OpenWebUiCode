using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Testes do <see cref="N8nService"/> e das tools
/// <c>n8n_list_workflows</c>/<c>n8n_trigger</c> (SPEC-20261008-
/// tests-coverage-gate RF-002): configuração via kv/env, headers,
/// parsing da API pública, webhook de produção e tratamento de erros.
/// </summary>
public class N8nToolsTests
{
    private string _workspace = null!;
    private AppDbContext _db = null!;
    private HybridCache _cache = null!;
    private ConfigService _config = null!;
    private RoutingHandler _handler = null!;
    private N8nService _svc = null!;

    [SetUp]
    public void SetUp()
    {
        _workspace = Path.Join(Path.GetTempPath(), $"owui-n8n-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);
        var dbPath = Path.Join(_workspace, "t.db");
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options);
        DatabaseMigrator.MigrateAsync(_db).GetAwaiter().GetResult();
        _cache = TestCache.Create();
        _config = new ConfigService(_db, _cache);
        _handler = new RoutingHandler();
        _svc = new N8nService(new StubFactory(_handler), _config,
            new ConfigurationBuilder().Build());
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
                _handler.Dispose();
        try { Directory.Delete(_workspace, true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    private BuiltinToolContext Ctx() => new("u1", "c1", "r1", _workspace, _workspace);
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    // ---------------- N8nService ----------------

    [Test]
    public async Task BaseUrl_EApiKey_KvEnvFallbackENormalizacao()
    {
        // Nada configurado → null.
        Assert.Multiple(async () =>
        {
            Assert.That(await _svc.GetBaseUrlAsync(), Is.Null);
            Assert.That(await _svc.GetApiKeyAsync(), Is.Null);
            Assert.That(await _svc.IsConfiguredAsync(), Is.False);
        });

        // kv com barra final e espaços → normalizado.
        await _config.SetAsync<string?>(N8nService.BaseUrlKey, "  http://n8n.test/  ", default);
        await _config.SetAsync<string?>(N8nService.ApiKeyKey, " k-1 ", default);
        Assert.Multiple(async () =>
        {
            Assert.That(await _svc.GetBaseUrlAsync(), Is.EqualTo("http://n8n.test"));
            Assert.That(await _svc.GetApiKeyAsync(), Is.EqualTo("k-1"));
            Assert.That(await _svc.IsConfiguredAsync(), Is.True);
        });

        // env como fallback quando kv está vazio.
        var cache1 = TestCache.Create();
        var env = new N8nService(new StubFactory(_handler),
            new ConfigService(_db, cache1),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["N8N:BaseUrl"] = "http://env-n8n/",
                    ["N8N:ApiKey"] = "env-key",
                }).Build());
        // kv do teste anterior não está na cache do novo ConfigService —
        // mas o db é o mesmo: para isolar env-only, nova base.
        using var db2 = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path.Join(_workspace, "t2.db")}").Options);
        DatabaseMigrator.MigrateAsync(db2).GetAwaiter().GetResult();
        var cache2 = TestCache.Create();
        env = new N8nService(new StubFactory(_handler),
            new ConfigService(db2, cache2),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["N8N:BaseUrl"] = "http://env-n8n/",
                    ["N8N:ApiKey"] = "env-key",
                }).Build());
        Assert.Multiple(async () =>
        {
            Assert.That(await env.GetBaseUrlAsync(), Is.EqualTo("http://env-n8n"));
            Assert.That(await env.GetApiKeyAsync(), Is.EqualTo("env-key"));
        });
    }

    [Test]
    public async Task List_SemConfig_LancaInvalidOperation()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ListWorkflowsAsync(default));
        Assert.That(ex!.Message, Does.Contain("não configurado"));
    }

    [Test]
    public async Task List_SemApiKey_LancaInvalidOperation()
    {
        await _config.SetAsync<string?>(N8nService.BaseUrlKey, "http://n8n.test", default);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ListWorkflowsAsync(default));
        Assert.That(ex!.Message, Does.Contain("API key"));
    }

    [Test]
    public async Task List_Sucesso_ParsesDataArray()
    {
        await _config.SetAsync<string?>(N8nService.BaseUrlKey, "http://n8n.test", default);
        await _config.SetAsync<string?>(N8nService.ApiKeyKey, "k", default);
        _handler.Respond(HttpStatusCode.OK,
            "{\"data\":[{\"id\":\"w1\",\"name\":\"Deploy\",\"active\":true},"
            + "{\"id\":\"w2\",\"name\":\"Batch\",\"active\":false},"
            + "{\"id\":\"w3\"}]}"); // sem name → pulado
        var items = await _svc.ListWorkflowsAsync(default);
        Assert.Multiple(() =>
        {
            Assert.That(items.Select(i => i.Name), Is.EqualTo(new[] { "Deploy", "Batch" }));
            Assert.That(items[0].Active, Is.True);
            Assert.That(items[1].Active, Is.False);
            Assert.That(_handler.LastRequest!.RequestUri!.ToString(),
                Is.EqualTo("http://n8n.test/api/v1/workflows?limit=200"));
            Assert.That(_handler.LastRequest.Headers.GetValues("X-N8N-API-KEY"), Does.Contain("k"));
        });
    }

    [Test]
    public async Task List_RootSemData_LancaInvalidOperation()
    {
        await _config.SetAsync<string?>(N8nService.BaseUrlKey, "http://n8n.test", default);
        await _config.SetAsync<string?>(N8nService.ApiKeyKey, "k", default);
        _handler.Respond(HttpStatusCode.OK, "[{\"id\":\"w9\",\"name\":\"Root\"}]");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ListWorkflowsAsync(default));
    }

    [Test]
    public async Task List_NaoJson_LancaInvalidOperation()
    {
        await _config.SetAsync<string?>(N8nService.BaseUrlKey, "http://n8n.test", default);
        await _config.SetAsync<string?>(N8nService.ApiKeyKey, "k", default);
        _handler.Respond(HttpStatusCode.OK, "<html>x</html>", "text/plain");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ListWorkflowsAsync(default));
        Assert.That(ex!.Message, Does.Contain("JSON"));
    }

    [Test]
    public async Task List_ErroHttp_LancaInvalidOperationComStatus()
    {
        await _config.SetAsync<string?>(N8nService.BaseUrlKey, "http://n8n.test", default);
        await _config.SetAsync<string?>(N8nService.ApiKeyKey, "k", default);
        _handler.Respond(HttpStatusCode.Unauthorized, "denied", "text/plain");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ListWorkflowsAsync(default));
        Assert.That(ex!.Message, Does.Contain("401"));
    }

    // ---------------- TriggerAsync ----------------

    [Test]
    public async Task Trigger_Webhook_PostProducao()
    {
        await _config.SetAsync<string?>(N8nService.BaseUrlKey, "http://n8n.test/", default);
        _handler.Respond(HttpStatusCode.OK, "{\"ok\":true}", "text/plain");
        var r = await _svc.TriggerAsync(null, "deploy", JsonDocument.Parse("{\"a\":1}").RootElement, default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.True);
            Assert.That(r.StatusCode, Is.EqualTo(200));
            Assert.That(_handler.LastRequest!.RequestUri!.ToString(),
                Is.EqualTo("http://n8n.test/webhook/deploy"));
            Assert.That(_handler.LastRequest.Method, Is.EqualTo(HttpMethod.Post));
        });
    }

    [Test]
    public async Task Trigger_WorkflowId_ApiPublicaComKey()
    {
        await _config.SetAsync<string?>(N8nService.BaseUrlKey, "http://n8n.test", default);
        await _config.SetAsync<string?>(N8nService.ApiKeyKey, "k", default);
        _handler.Respond(HttpStatusCode.OK, "{\"executionId\":\"e1\"}", "text/plain");
        var r = await _svc.TriggerAsync("w1", null, null, default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.True);
            Assert.That(_handler.LastRequest!.RequestUri!.ToString(),
                Is.EqualTo("http://n8n.test/api/v1/workflows/w1/execute"));
        });
    }

    [Test]
    public async Task Trigger_WorkflowIdSemKey_Lanca()
    {
        await _config.SetAsync<string?>(N8nService.BaseUrlKey, "http://n8n.test", default);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.TriggerAsync("w1", null, null, default));
        Assert.That(ex!.Message, Does.Contain("n8n.api_key"));
    }

    [Test]
    public async Task Trigger_SemConfig_Lanca()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.TriggerAsync(null, "wh", null, default));
    }

    [Test]
    public async Task Trigger_ErroHttp_RetornaResultadoFalho()
    {
        await _config.SetAsync<string?>(N8nService.BaseUrlKey, "http://n8n.test", default);
        _handler.Respond(HttpStatusCode.InternalServerError, "boom", "text/plain");
        var r = await _svc.TriggerAsync(null, "wh", null, default);
        Assert.Multiple(() =>
        {
            Assert.That(r.Success, Is.False);
            Assert.That(r.StatusCode, Is.EqualTo(500));
            Assert.That(r.Body, Does.Contain("boom"));
        });
    }

    // ---------------- builtin tools ----------------

    [Test]
    public async Task Tool_List_ErrosEVazioESucesso()
    {
        var tool = new N8nListWorkflowsBuiltinTool(_svc);

        // Sem config → InvalidOperationException capturada → "Erro:".
        var r = await tool.ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("Erro:").And.Contain("não configurado"));

        // Lista vazia.
        await _config.SetAsync<string?>(N8nService.BaseUrlKey, "http://n8n.test", default);
        await _config.SetAsync<string?>(N8nService.ApiKeyKey, "k", default);
        _handler.Respond(HttpStatusCode.OK, "{\"data\":[]}");
        r = await tool.ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("não tem workflows"));

        // Sucesso com flag [ativo].
        _handler.Respond(HttpStatusCode.OK, "{\"data\":[{\"id\":\"w1\",\"name\":\"Deploy\",\"active\":true}]}");
        r = await tool.ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("Deploy").And.Contain("[ativo]"));
    }

    [Test]
    public async Task Tool_Trigger_ValidacaoEExecucao()
    {
        var tool = new N8nTriggerBuiltinTool(_svc);
        Assert.That(tool.Tier, Is.EqualTo(ApprovalTier.Exec));

        // Ambos ou nenhum alvo → erro de validação.
        var r = await tool.ExecuteAsync(Args("{}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("exatamente um"));
        r = await tool.ExecuteAsync(
            Args("{\"workflow_id\":\"w1\",\"webhook_path\":\"wh\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("exatamente um"));

        // Webhook com sucesso.
        await _config.SetAsync<string?>(N8nService.BaseUrlKey, "http://n8n.test", default);
        _handler.Respond(HttpStatusCode.OK, "done", "text/plain");
        r = await tool.ExecuteAsync(
            Args("{\"webhook_path\":\"wh\",\"payload\":{\"x\":1}}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("disparado").And.Contain("webhook wh"));

        // workflow_id sem api_key → erro de configuração.
        r = await tool.ExecuteAsync(Args("{\"workflow_id\":\"w1\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("n8n.api_key"));

        // workflow_id com key → sucesso.
        await _config.SetAsync<string?>(N8nService.ApiKeyKey, "k", default);
        _handler.Respond(HttpStatusCode.OK, "{\"executionId\":\"e1\"}", "text/plain");
        r = await tool.ExecuteAsync(Args("{\"workflow_id\":\"w1\"}"), Ctx(), default);
        Assert.That(r.Text, Does.Contain("disparado"));
    }

    // ---------------- helpers ----------------

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private byte[] _body = "{}"u8.ToArray();
        private string? _mediaType = "application/json";

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public void Respond(HttpStatusCode status, string body,
            string mediaType = "application/json")
        {
            _status = status;
            _body = Encoding.UTF8.GetBytes(body);
            _mediaType = mediaType;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            HttpContent fresh = _mediaType is null || _mediaType == "application/octet-stream"
                ? new ByteArrayContent(_body)
                : new StringContent(Encoding.UTF8.GetString(_body), Encoding.UTF8, _mediaType);
            if (fresh is ByteArrayContent && _mediaType is not null)
                fresh.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_mediaType);
            return new HttpResponseMessage(_status) { Content = fresh };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
