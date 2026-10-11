using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura dos endpoints LSP com um <b>servidor LSP fake</b> (python via
/// stdio JSON-RPC, <c>Lsp:Servers:python=*</c>): exercita os caminhos felizes
/// de completion/definition/references/rename/format/symbols/hover/diagnostics
/// — incluindo o handshake initialize do <see cref="LspClient"/> e o mapping
/// das respostas — além dos degrades e do guard de jail/binding.
/// </summary>
[TestFixture, IsolateEnvironment]
public class LspFakeServerTests
{
    /// <summary>Servidor fake: responde cada request com payload estático;
    /// publica diagnostics ao didOpen; ignora notificações.</summary>
    private const string FakeServer = """
        import json, sys

        def read_msg():
            headers = {}
            while True:
                line = sys.stdin.buffer.readline()
                if not line:
                    return None
                line = line.strip()
                if not line:
                    break
                k, _, v = line.partition(b':')
                headers[k.strip().lower().decode()] = v.strip().decode()
            n = int(headers.get('content-length', '0'))
            if n <= 0:
                return None
            return json.loads(sys.stdin.buffer.read(n))

        def send(payload):
            body = json.dumps(payload).encode()
            sys.stdout.buffer.write(b'Content-Length: ' + str(len(body)).encode() + b'\r\n\r\n' + body)
            sys.stdout.buffer.flush()

        root = ''
        while True:
            msg = read_msg()
            if msg is None:
                break
            method = msg.get('method', '')
            mid = msg.get('id')
            if method == 'exit':
                break
            if method == 'initialize':
                root = msg['params'].get('rootUri', '')
                send({'jsonrpc': '2.0', 'id': mid, 'result': {'capabilities': {
                    'completionProvider': {}, 'definitionProvider': True,
                    'referencesProvider': True, 'renameProvider': True,
                    'hoverProvider': True, 'documentSymbolProvider': True,
                    'documentFormattingProvider': True}}})
            elif mid is None:
                if method == 'textDocument/didOpen':
                    uri = msg['params']['textDocument']['uri']
                    send({'jsonrpc': '2.0', 'method': 'textDocument/publishDiagnostics',
                          'params': {'uri': uri, 'diagnostics': [{
                              'range': {'start': {'line': 0, 'character': 0},
                                        'end': {'line': 0, 'character': 5}},
                              'severity': 2, 'message': 'fake warning', 'source': 'faketest'}]}})
            elif method == 'textDocument/completion':
                send({'jsonrpc': '2.0', 'id': mid, 'result': {'items': [
                    {'label': 'alpha', 'kind': 3, 'detail': 'fn alpha',
                     'documentation': 'alpha docs', 'insertText': 'alpha()',
                     'textEdit': {'range': {'start': {'line': 0, 'character': 0},
                                            'end': {'line': 0, 'character': 3}},
                                  'newText': 'alpha()'}},
                    {'label': 'beta', 'kind': 6, 'detail': 'var beta'},
                    {'nolabel': True}]}})
            elif method == 'textDocument/definition':
                send({'jsonrpc': '2.0', 'id': mid, 'result': {
                    'uri': root + '/defs.py',
                    'range': {'start': {'line': 4, 'character': 1},
                              'end': {'line': 4, 'character': 6}}}})
            elif method == 'textDocument/references':
                send({'jsonrpc': '2.0', 'id': mid, 'result': [
                    {'uri': root + '/defs.py',
                     'range': {'start': {'line': 4, 'character': 1},
                               'end': {'line': 4, 'character': 6}}},
                    {'uri': root + '/app.py',
                     'range': {'start': {'line': 0, 'character': 0},
                               'end': {'line': 0, 'character': 5}}},
                    {'uri': 'file:///etc/passwd',
                     'range': {'start': {'line': 0, 'character': 0},
                               'end': {'line': 0, 'character': 1}}}]})
            elif method == 'textDocument/rename':
                td = msg['params']['textDocument']['uri']
                edit = {'range': {'start': {'line': 0, 'character': 0},
                                  'end': {'line': 0, 'character': 5}},
                        'newText': 'beta'}
                send({'jsonrpc': '2.0', 'id': mid, 'result': {'changes': {
                    td: [edit], root + '/other.py': [edit]}}})
            elif method == 'textDocument/formatting':
                send({'jsonrpc': '2.0', 'id': mid, 'result': [
                    {'range': {'start': {'line': 0, 'character': 0},
                               'end': {'line': 99, 'character': 0}},
                     'newText': '# formatted\n'}]})
            elif method == 'textDocument/documentSymbol':
                rng = {'start': {'line': 0, 'character': 0},
                       'end': {'line': 5, 'character': 0}}
                sel = {'start': {'line': 0, 'character': 4},
                       'end': {'line': 0, 'character': 8}}
                send({'jsonrpc': '2.0', 'id': mid, 'result': [
                    {'name': 'main', 'kind': 12, 'detail': 'def main',
                     'range': rng, 'selectionRange': sel, 'children': [
                         {'name': 'inner', 'kind': 6,
                          'range': {'start': {'line': 1, 'character': 4},
                                    'end': {'line': 1, 'character': 9}},
                          'selectionRange': {'start': {'line': 1, 'character': 4},
                                             'end': {'line': 1, 'character': 9}}}]}]})
            elif method == 'textDocument/hover':
                send({'jsonrpc': '2.0', 'id': mid, 'result': {
                    'contents': {'kind': 'markdown', 'value': '**alpha** docs'}}})
            else:
                send({'jsonrpc': '2.0', 'id': mid, 'result': None})
        """;

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _dataRoot = null!;
    private string _binDir = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-lspfake-{Guid.NewGuid():N}.db");
        _dataRoot = Path.Join(Path.GetTempPath(), $"openwebui-lspfakeroot-{Guid.NewGuid():N}");
        _binDir = Path.Join(_dataRoot, "bin");
        Directory.CreateDirectory(_binDir);
        var script = Path.Join(_binDir, "fake_lsp.py");
        await File.WriteAllTextAsync(script, FakeServer);

        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        Environment.SetEnvironmentVariable("DATA_ROOT", _dataRoot);
        Environment.SetEnvironmentVariable("Lsp__Servers__python__Command", "python3");
        Environment.SetEnvironmentVariable("Lsp__Servers__python__Args__0", script);

        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        var admin = await SignUpAsync("admin@lspfake.local");
        UseToken(admin.Token);
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        Environment.SetEnvironmentVariable("DATA_ROOT", null);
        Environment.SetEnvironmentVariable("Lsp__Servers__python__Command", null);
        Environment.SetEnvironmentVariable("Lsp__Servers__python__Args__0", null);
        TestInfra.DeleteDb(_dbPath);
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TestContext.Progress.WriteLine($"cleanup best-effort: {ex.Message}");
        }
    }

    private async Task<AuthResponse> SignUpAsync(string email)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest("User", email, "senha123456"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task BindRepoAsync(string userId)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        var cache = TestCache.Create();
        var config = new ConfigService(db, cache);
        await config.SetAsync($"u:{userId}:workspace.repo",
            new WorkspaceRepoBinding("o/b", "main", "repos/o__b"));
    }

    private string WorkdirOf(string userId) =>
        Path.Join(_dataRoot, "workspaces", userId);

    /// <summary>Setup por teste: usuário novo + binding + app.py/other.py no workdir.</summary>
    private async Task<string> BoundUserWithFilesAsync()
    {
        var user = await SignUpAsync($"fake-{Guid.NewGuid():N}@t.local");
        UseToken(user.Token);
        await BindRepoAsync(user.User.Id);
        var dir = WorkdirOf(user.User.Id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Join(dir, "app.py"), "alpha\n");
        await File.WriteAllTextAsync(Path.Join(dir, "other.py"), "alpha\n");
        return user.User.Id;
    }

    /// <summary>/doc open — spawna o server fake (espera o handshake).</summary>
    private async Task<JsonElement> OpenDocAsync(string path = "app.py")
    {
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/doc",
            new LspDocSyncRequest(path, "open", null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    // ---------- handshake + doc-sync ----------

    [Test]
    public async Task DocOpen_SpawnaServidor_Synced()
    {
        await BoundUserWithFilesAsync();
        var json = await OpenDocAsync();
        Assert.That(json.GetProperty("synced").GetBoolean(), Is.True);
    }

    [Test]
    public async Task DocChange_ComServidor_Synced()
    {
        await BoundUserWithFilesAsync();
        await OpenDocAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/doc",
            new LspDocSyncRequest("app.py", "change", "alpha\nbeta\n"));
        var json = await ReadJsonAsync(response);
        Assert.That(json.GetProperty("synced").GetBoolean(), Is.True);
    }

    [Test]
    public async Task DocClose_FechaDoc_Synced()
    {
        await BoundUserWithFilesAsync();
        await OpenDocAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/doc",
            new LspDocSyncRequest("app.py", "close", null));
        var json = await ReadJsonAsync(response);
        Assert.That(json.GetProperty("synced").GetBoolean(), Is.True);
    }

    // ---------- completion ----------

    [Test]
    public async Task Completion_ServidorFake_ItensMapeados()
    {
        await BoundUserWithFilesAsync();
        await OpenDocAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/completion",
            new LspPosRequest("app.py", 0, 3));
        var json = await ReadJsonAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(json.GetProperty("available").GetBoolean(), Is.True);
            var items = json.GetProperty("items");
            Assert.That(items.GetArrayLength(), Is.EqualTo(2));
            Assert.That(items[0].GetProperty("label").GetString(), Is.EqualTo("alpha"));
            Assert.That(items[0].GetProperty("type").GetString(), Is.EqualTo("function"));
            Assert.That(items[0].GetProperty("applyText").GetString(), Is.EqualTo("alpha()"));
            // from vem do textEdit.range.start (0,0).
            Assert.That(json.GetProperty("from").GetProperty("line").GetInt32(), Is.EqualTo(0));
            Assert.That(json.GetProperty("from").GetProperty("col").GetInt32(), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Completion_SemDocAberto_Degrada()
    {
        await BoundUserWithFilesAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/completion",
            new LspPosRequest("app.py", 0, 3));
        var json = await ReadJsonAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(json.GetProperty("available").GetBoolean(), Is.False);
            Assert.That(json.GetProperty("items").GetArrayLength(), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Completion_JailEscape_400()
    {
        await BoundUserWithFilesAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/completion",
            new LspPosRequest("../fora.py", 0, 0));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---------- definition / references ----------

    [Test]
    public async Task Definition_ServidorFake_LocationNormalizada()
    {
        await BoundUserWithFilesAsync();
        await OpenDocAsync();
        var response = await _client.GetAsync(
            "/api/v1/workspace/lsp/definition?path=app.py&line=0&col=3");
        var json = await ReadJsonAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(json.GetProperty("available").GetBoolean(), Is.True);
            var locs = json.GetProperty("locations");
            Assert.That(locs.GetArrayLength(), Is.EqualTo(1));
            Assert.That(locs[0].GetProperty("path").GetString(), Is.EqualTo("defs.py"));
            Assert.That(locs[0].GetProperty("range").GetProperty("line").GetInt32(), Is.EqualTo(4));
        });
    }

    [Test]
    public async Task References_ServidorFake_FiltraForaDoJail()
    {
        await BoundUserWithFilesAsync();
        await OpenDocAsync();
        var response = await _client.GetAsync(
            "/api/v1/workspace/lsp/references?path=app.py&line=0&col=3");
        var json = await ReadJsonAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(json.GetProperty("available").GetBoolean(), Is.True);
            var locs = json.GetProperty("locations");
            // file:///etc/passwd é descartado pelo RelPath (fora do jail).
            Assert.That(locs.GetArrayLength(), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Definition_SemDocAberto_Degrada()
    {
        await BoundUserWithFilesAsync();
        var response = await _client.GetAsync(
            "/api/v1/workspace/lsp/definition?path=app.py&line=0&col=3");
        var json = await ReadJsonAsync(response);
        Assert.That(json.GetProperty("available").GetBoolean(), Is.False);
    }

    // ---------- rename ----------

    [Test]
    public async Task Rename_ServidorFake_ClientEditsEEscreveDisco()
    {
        var uid = await BoundUserWithFilesAsync();
        await OpenDocAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/rename",
            new LspRenameRequest("app.py", 0, 1, "beta", "app.py"));
        var json = await ReadJsonAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(json.GetProperty("available").GetBoolean(), Is.True);
            var edits = json.GetProperty("clientEdits");
            Assert.That(edits.GetArrayLength(), Is.EqualTo(1));
            Assert.That(edits[0].GetProperty("path").GetString(), Is.EqualTo("app.py"));
            Assert.That(edits[0].GetProperty("edits")[0]
                .GetProperty("newText").GetString(), Is.EqualTo("beta"));
            Assert.That(json.GetProperty("filesUpdated")[0].GetString(), Is.EqualTo("other.py"));
        });
        // other.py (não-ativo) foi reescrito no disco pelo endpoint.
        var disk = await File.ReadAllTextAsync(Path.Join(WorkdirOf(uid), "other.py"));
        Assert.That(disk, Is.EqualTo("beta\n"));
    }

    [Test]
    public async Task Rename_NomeVazio_Erro()
    {
        await BoundUserWithFilesAsync();
        await OpenDocAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/rename",
            new LspRenameRequest("app.py", 0, 1, "  ", "app.py"));
        var json = await ReadJsonAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(json.GetProperty("available").GetBoolean(), Is.False);
            Assert.That(json.GetProperty("error").GetString(), Is.EqualTo("Nome vazio."));
        });
    }

    // ---------- format / symbols / hover / diagnostics ----------

    [Test]
    public async Task Format_ServidorFake_TextoFormatado()
    {
        await BoundUserWithFilesAsync();
        await OpenDocAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/format",
            new LspFormatRequest("app.py"));
        var json = await ReadJsonAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(json.GetProperty("available").GetBoolean(), Is.True);
            Assert.That(json.GetProperty("changed").GetBoolean(), Is.True);
            Assert.That(json.GetProperty("text").GetString(), Is.EqualTo("# formatted\n"));
        });
    }

    [Test]
    public async Task Symbols_ServidorFake_AchataHierarquia()
    {
        await BoundUserWithFilesAsync();
        await OpenDocAsync();
        var response = await _client.GetAsync("/api/v1/workspace/lsp/symbols?path=app.py");
        var json = await ReadJsonAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(json.GetProperty("available").GetBoolean(), Is.True);
            var items = json.GetProperty("symbols");
            Assert.That(items.GetArrayLength(), Is.EqualTo(2));
            Assert.That(items[0].GetProperty("name").GetString(), Is.EqualTo("main"));
            Assert.That(items[0].GetProperty("depth").GetInt32(), Is.EqualTo(0));
            Assert.That(items[1].GetProperty("name").GetString(), Is.EqualTo("inner"));
            Assert.That(items[1].GetProperty("depth").GetInt32(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Hover_ServidorFake_TextoMarkdown()
    {
        await BoundUserWithFilesAsync();
        await OpenDocAsync();
        var response = await _client.GetAsync("/api/v1/workspace/lsp/hover?path=app.py&line=0&col=1");
        var json = await ReadJsonAsync(response);
        Assert.That(json.GetProperty("hover").GetString(), Does.Contain("alpha"));
    }

    [Test]
    public async Task Diagnostics_PublishNoDidOpen_Item()
    {
        await BoundUserWithFilesAsync();
        await OpenDocAsync();
        // O fake publica diagnostics ao didOpen — o endpoint lê o snapshot.
        await Task.Delay(400);
        var response = await _client.GetAsync("/api/v1/workspace/lsp/diagnostics?path=app.py");
        var json = await ReadJsonAsync(response);
        Assert.Multiple(() =>
        {
            var items = json.GetProperty("diagnostics");
            Assert.That(items.GetArrayLength(), Is.GreaterThanOrEqualTo(1));
            Assert.That(items[0].GetProperty("message").GetString(), Is.EqualTo("fake warning"));
        });
    }

    // ---------- ramos extras (degrade/validação) ----------

    [Test]
    public async Task Completion_ExtensaoNaoMapeada_Degrada()
    {
        var uid = await BoundUserWithFilesAsync();
        await File.WriteAllTextAsync(Path.Join(WorkdirOf(uid), "a.xyz"), "x\n");
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/completion",
            new LspPosRequest("a.xyz", 0, 0));
        var json = await ReadJsonAsync(response);
        Assert.That(json.GetProperty("available").GetBoolean(), Is.False);
    }

    [Test]
    public async Task DocOpen_KindInvalido_400()
    {
        await BoundUserWithFilesAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/doc",
            new LspDocSyncRequest("app.py", "foo", null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task DocOpen_PathVazio_400()
    {
        await BoundUserWithFilesAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/doc",
            new LspDocSyncRequest(" ", "open", null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task DocOpen_ArquivoInexistente_404()
    {
        await BoundUserWithFilesAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/doc",
            new LspDocSyncRequest("naoexiste.py", "open", null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task DocChange_SemTexto_400()
    {
        await BoundUserWithFilesAsync();
        await OpenDocAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/doc",
            new LspDocSyncRequest("app.py", "change", null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Rename_SemActivePath_TodosEscrevemEmDisco()
    {
        var uid = await BoundUserWithFilesAsync();
        await OpenDocAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/rename",
            new LspRenameRequest("app.py", 0, 1, "beta", null));
        var json = await ReadJsonAsync(response);
        Assert.Multiple(() =>
        {
            Assert.That(json.GetProperty("available").GetBoolean(), Is.True);
            Assert.That(json.GetProperty("clientEdits").GetArrayLength(), Is.EqualTo(0));
            Assert.That(json.GetProperty("filesUpdated").GetArrayLength(), Is.EqualTo(2));
        });
        var disk = await File.ReadAllTextAsync(Path.Join(WorkdirOf(uid), "other.py"));
        Assert.That(disk, Is.EqualTo("beta\n"));
    }

    [Test]
    public async Task Definition_ParamsNulos_UsaDefault()
    {
        await BoundUserWithFilesAsync();
        await OpenDocAsync();
        var response = await _client.GetAsync("/api/v1/workspace/lsp/definition?path=app.py");
        var json = await ReadJsonAsync(response);
        Assert.That(json.GetProperty("available").GetBoolean(), Is.True);
    }

    [Test]
    public async Task Symbols_ExtensaoNaoMapeada_Degrada()
    {
        var uid = await BoundUserWithFilesAsync();
        await File.WriteAllTextAsync(Path.Join(WorkdirOf(uid), "a.xyz"), "x\n");
        var response = await _client.GetAsync("/api/v1/workspace/lsp/symbols?path=a.xyz");
        var json = await ReadJsonAsync(response);
        Assert.That(json.GetProperty("available").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Format_SemDocAberto_Degrada()
    {
        await BoundUserWithFilesAsync();
        var response = await _client.PostAsJsonAsync("/api/v1/workspace/lsp/format",
            new LspFormatRequest("app.py"));
        var json = await ReadJsonAsync(response);
        Assert.That(json.GetProperty("available").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Hover_ExtensaoNaoMapeada_Null()
    {
        var uid = await BoundUserWithFilesAsync();
        await File.WriteAllTextAsync(Path.Join(WorkdirOf(uid), "a.xyz"), "x\n");
        var response = await _client.GetAsync("/api/v1/workspace/lsp/hover?path=a.xyz&line=0&col=1");
        var json = await ReadJsonAsync(response);
        Assert.That(json.GetProperty("hover").ValueKind, Is.EqualTo(JsonValueKind.Null));
    }

    // ---------- file-head (WorkspaceFileEndpoints) ----------

    [Test]
    public async Task FileHead_SemGit_404()
    {
        await BoundUserWithFilesAsync();
        var response = await _client.GetAsync("/api/v1/workspace/repo/file-head?path=app.py");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task FileHead_JailEscape_400()
    {
        await BoundUserWithFilesAsync();
        var response = await _client.GetAsync("/api/v1/workspace/repo/file-head?path=../fora.py");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task FileHead_SemBinding_404()
    {
        var user = await SignUpAsync($"nb-{Guid.NewGuid():N}@t.local");
        UseToken(user.Token);
        var response = await _client.GetAsync("/api/v1/workspace/repo/file-head?path=app.py");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
