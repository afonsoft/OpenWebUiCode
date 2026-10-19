using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Api.Endpoints;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>Cobertura dos handlers de ChatEndpoints: import/export, clone, mensagens, tags, arquivo, share e pastas.</summary>
[TestFixture, IsolateEnvironment]
public class ChatEndpointsTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-chats-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();

        // O primeiro usuário vira admin e habilita "user" como papel padrão.
        var admin = await SignUpAsync("Admin", "admin@chats.local", "senha123");
        UseToken(admin.Token);
        var config = AdminConfig.Default with { DefaultUserRole = "user" };
        var updated = await _client.PostAsJsonAsync("/api/v1/auths/admin/config", config);
        Assert.That(updated.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await updated.Content.ReadAsStringAsync());
        _client.DefaultRequestHeaders.Authorization = null;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
        if (File.Exists(_dbPath))
        {
            TestInfra.DeleteDb(_dbPath);
        }
    }

    private async Task<AuthResponse> SignUpAsync(string name, string email, string password)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest(name, email, password));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private void UseToken(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<ChatResponse> CriarChatAsync(string titulo, List<ChatMessageModel> mensagens)
    {
        var created = await _client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest(titulo, ["llama3"], mensagens));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    private static List<ChatMessageModel> Mensagens(params string[] conteudos) =>
        conteudos
            .Select((c, i) => new ChatMessageModel($"m{i + 1}", i % 2 == 0 ? "user" : "assistant", c, null, 100 + i))
            .ToList();

    [Test]
    public async Task Import_CriaChatsComMensagensETituloPadrao()
    {
        var auth = await SignUpAsync("Import", "import@chats.local", "senha123");
        UseToken(auth.Token);

        var payload = new List<ChatUpsertRequest>
        {
            new("Importado", ["llama3"], Mensagens("pergunta", "resposta")),
            // Título vazio deve cair no fallback "New Chat".
            new("", [], Mensagens("oi")),
        };
        var imported = await _client.PostAsJsonAsync("/api/v1/chats/import", payload);
        Assert.That(imported.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var status = (await imported.Content.ReadFromJsonAsync<StatusResponse>())!;
        Assert.That(status.Success, Is.True);

        var list = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>("/api/v1/chats/");
        Assert.That(list, Has.Count.EqualTo(2));
        Assert.That(list!.Select(c => c.Title), Does.Contain("Importado"));
        Assert.That(list.Select(c => c.Title), Does.Contain("New Chat"));

        // As mensagens importadas ficam acessíveis no chat completo.
        var detalhe = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{list.First(c => c.Title == "Importado").Id}");
        Assert.That(detalhe!.Messages, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Import_ListaVazia_RetornaSucessoSemCriarNada()
    {
        var auth = await SignUpAsync("ImportVazio", "importvazio@chats.local", "senha123");
        UseToken(auth.Token);

        var imported = await _client.PostAsJsonAsync(
            "/api/v1/chats/import", new List<ChatUpsertRequest>());
        Assert.That(imported.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var list = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>("/api/v1/chats/");
        Assert.That(list, Is.Empty);
    }

    [Test]
    public async Task ExportAll_RetornaChatsComMensagensETimestamp()
    {
        var auth = await SignUpAsync("Export", "export@chats.local", "senha123");
        UseToken(auth.Token);

        await CriarChatAsync("Chat A", Mensagens("a1", "a2"));
        await CriarChatAsync("Chat B", Mensagens("b1"));

        var response = await _client.GetAsync("/api/v1/chats/all/db");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var chats = doc.RootElement.GetProperty("chats");
        Assert.Multiple(() =>
        {
            Assert.That(chats.GetArrayLength(), Is.EqualTo(2));
            Assert.That(doc.RootElement.GetProperty("exportedAt").GetInt64(), Is.GreaterThan(0));
        });
        var primeiro = chats.EnumerateArray()
            .First(c => c.GetProperty("title").GetString() == "Chat A");
        Assert.That(primeiro.GetProperty("messages").GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public async Task Clone_CopiaTituloTagsEMensagens()
    {
        var auth = await SignUpAsync("Clone", "clone@chats.local", "senha123");
        UseToken(auth.Token);

        var original = await CriarChatAsync("Original", Mensagens("m1", "m2", "m3"));
        await _client.PostAsJsonAsync(
            $"/api/v1/chats/{original.Id}/tags",
            new ChatEndpoints.TagUpdateRequest(["trabalho"]));

        var cloned = await _client.PostAsync($"/api/v1/chats/{original.Id}/clone", null);
        Assert.That(cloned.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var clone = (await cloned.Content.ReadFromJsonAsync<ChatResponse>())!;

        Assert.Multiple(() =>
        {
            Assert.That(clone.Id, Is.Not.EqualTo(original.Id));
            Assert.That(clone.Title, Is.EqualTo("Original (Clone)"));
            Assert.That(clone.Messages, Has.Count.EqualTo(3));
            Assert.That(clone.Messages.Select(m => m.Content), Is.EqualTo(new[] { "m1", "m2", "m3" }));
            Assert.That(clone.Tags, Is.EqualTo(new[] { "trabalho" }));
        });

        // O original permanece intacto após o clone.
        var depois = await _client.GetFromJsonAsync<ChatResponse>($"/api/v1/chats/{original.Id}");
        Assert.That(depois!.Messages, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task Clone_ChatInexistente_Retorna404()
    {
        var auth = await SignUpAsync("Clone404", "clone404@chats.local", "senha123");
        UseToken(auth.Token);

        var response = await _client.PostAsync("/api/v1/chats/nao-existe/clone", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task UpdateMessage_EditaConteudo_ERetorna404ParaCasosInvalidos()
    {
        var auth = await SignUpAsync("Msg", "msg@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Editável", Mensagens("pergunta", "resposta"));
        var messageId = chat.Messages[1].Id;

        var edited = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/messages/{messageId}",
            new MessageUpdateRequest("resposta corrigida"));
        Assert.That(edited.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var atualizado = (await edited.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.That(atualizado.Messages[1].Content, Is.EqualTo("resposta corrigida"));

        // Mensagem inexistente e chat inexistente retornam 404.
        var msg404 = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/messages/inexistente",
            new MessageUpdateRequest("x"));
        Assert.That(msg404.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var chat404 = await _client.PostAsJsonAsync(
            "/api/v1/chats/nao-existe/messages/m1",
            new MessageUpdateRequest("x"));
        Assert.That(chat404.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        // Chat de outro usuário também retorna 404.
        var outro = await SignUpAsync("MsgOutro", "msgoutro@chats.local", "senha123");
        UseToken(outro.Token);
        var alheio = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/messages/{messageId}",
            new MessageUpdateRequest("invasão"));
        Assert.That(alheio.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task DeleteMessage_RemoveDaMensagemEmDiante()
    {
        var auth = await SignUpAsync("DelMsg", "delmsg@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Cortável", Mensagens("a", "b", "c", "d"));
        var m2 = chat.Messages[1].Id;

        // Remover a segunda mensagem apaga ela e todas as seguintes.
        var removed = await _client.DeleteAsync($"/api/v1/chats/{chat.Id}/messages/{m2}");
        Assert.That(removed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var atualizado = (await removed.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.That(atualizado.Messages, Has.Count.EqualTo(1));
        Assert.That(atualizado.Messages[0].Content, Is.EqualTo("a"));

        var msg404 = await _client.DeleteAsync($"/api/v1/chats/{chat.Id}/messages/inexistente");
        Assert.That(msg404.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var chat404 = await _client.DeleteAsync("/api/v1/chats/nao-existe/messages/m1");
        Assert.That(chat404.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Tags_SetGetBuscaEClear_NormalizamDuplicatas()
    {
        var auth = await SignUpAsync("Tags", "tags@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Tagueado", Mensagens("oi"));

        // Tags são trimmadas, vazias descartadas e duplicatas case-insensitive removidas.
        var set = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/tags",
            new ChatEndpoints.TagUpdateRequest(["  Dev ", "dev", "", "Docs"]));
        Assert.That(set.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var comTags = (await set.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.That(comTags.Tags, Is.EqualTo(new[] { "Dev", "Docs" }));

        var get = await _client.GetFromJsonAsync<List<string>>($"/api/v1/chats/{chat.Id}/tags");
        Assert.That(get, Is.EqualTo(new[] { "Dev", "Docs" }));

        // Agregado global e busca por tag são case-insensitive.
        var allTags = await _client.GetFromJsonAsync<List<string>>("/api/v1/chats/all/tags");
        Assert.That(allTags, Does.Contain("Dev"));
        Assert.That(allTags, Does.Contain("Docs"));

        var porTag = await _client.PostAsJsonAsync(
            "/api/v1/chats/tags", new ChatEndpoints.TagQueryRequest(["dev"]));
        var filtrados = (await porTag.Content.ReadFromJsonAsync<List<ChatSummaryResponse>>())!;
        Assert.That(filtrados, Has.Count.EqualTo(1));
        Assert.That(filtrados[0].Id, Is.EqualTo(chat.Id));

        var semMatch = await _client.PostAsJsonAsync(
            "/api/v1/chats/tags", new ChatEndpoints.TagQueryRequest(["zzz"]));
        var vazio = (await semMatch.Content.ReadFromJsonAsync<List<ChatSummaryResponse>>())!;
        Assert.That(vazio, Is.Empty);

        var cleared = await _client.DeleteAsync($"/api/v1/chats/{chat.Id}/tags");
        Assert.That(cleared.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var depois = await _client.GetFromJsonAsync<List<string>>($"/api/v1/chats/{chat.Id}/tags");
        Assert.That(depois, Is.Empty);

        // Tag em chat inexistente → 404.
        var nf = await _client.PostAsJsonAsync(
            "/api/v1/chats/nao-existe/tags", new ChatEndpoints.TagUpdateRequest(["x"]));
        Assert.That(nf.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Meta_EndpointNaoMapeado_Retorna405()
    {
        var auth = await SignUpAsync("Meta", "meta@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Sem meta", Mensagens("oi"));

        // ChatMetaUpdateRequest existe nos contratos mas nenhuma rota /meta é mapeada:
        // o comportamento atual é 405 (lacuna documentada, não assert idealizado).
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/meta",
            new ChatMetaUpdateRequest("Novo título", ["tag"], null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.MethodNotAllowed));
    }

    [Test]
    public async Task Archived_ListaEContagem_RefletemArquivamento()
    {
        var auth = await SignUpAsync("Arch", "arch@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Arquivável", Mensagens("oi"));
        await CriarChatAsync("Visível", Mensagens("oi"));

        await _client.PostAsync($"/api/v1/chats/{chat.Id}/archive", null);

        var archived = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>(
            "/api/v1/chats/archived");
        Assert.That(archived, Has.Count.EqualTo(1));
        Assert.That(archived![0].Id, Is.EqualTo(chat.Id));

        var countResponse = await _client.GetAsync("/api/v1/chats/archived/count");
        var count = JsonDocument.Parse(await countResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("count").GetInt32();
        Assert.That(count, Is.EqualTo(1));

        var normais = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>("/api/v1/chats/");
        Assert.That(normais, Has.Count.EqualTo(1));

        // Desarquivar devolve o chat à listagem normal.
        await _client.PostAsync($"/api/v1/chats/{chat.Id}/archive", null);
        var archivedDepois = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>(
            "/api/v1/chats/archived");
        Assert.That(archivedDepois, Is.Empty);
    }

    [Test]
    public async Task ArchiveAll_UnarchiveAll_OperamEmMassa()
    {
        var auth = await SignUpAsync("Bulk", "bulk@chats.local", "senha123");
        UseToken(auth.Token);
        await CriarChatAsync("Um", Mensagens("oi"));
        await CriarChatAsync("Dois", Mensagens("oi"));

        var archiveAll = await _client.PostAsync("/api/v1/chats/archive/all", null);
        Assert.That(archiveAll.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var countResponse = await _client.GetAsync("/api/v1/chats/archived/count");
        var count = JsonDocument.Parse(await countResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("count").GetInt32();
        Assert.That(count, Is.EqualTo(2));

        var normais = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>("/api/v1/chats/");
        Assert.That(normais, Is.Empty);

        var unarchiveAll = await _client.PostAsync("/api/v1/chats/unarchive/all", null);
        Assert.That(unarchiveAll.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var restaurados = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>("/api/v1/chats/");
        Assert.That(restaurados, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Share_Unshare_ControlamAcessoPublico()
    {
        var auth = await SignUpAsync("Share", "share@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Compartilhado", Mensagens("segredo"));

        var shared = await _client.PostAsync($"/api/v1/chats/{chat.Id}/share", null);
        Assert.That(shared.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var comShare = (await shared.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.That(comShare.ShareId, Is.Not.Null.And.Not.Empty);

        // Listagem de chats compartilhados do próprio usuário.
        var sharedList = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>(
            "/api/v1/chats/shared");
        Assert.That(sharedList, Has.Count.EqualTo(1));

        // Acesso anônimo ao link público expõe título, autor e mensagens.
        _client.DefaultRequestHeaders.Authorization = null;
        var publico = await _client.GetAsync($"/api/v1/chats/share/{comShare.ShareId}");
        Assert.That(publico.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var doc = JsonDocument.Parse(await publico.Content.ReadAsStringAsync());
        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("title").GetString(), Is.EqualTo("Compartilhado"));
            Assert.That(doc.RootElement.GetProperty("messages").GetArrayLength(), Is.EqualTo(1));
            Assert.That(doc.RootElement.GetProperty("user").GetProperty("name").GetString(),
                Is.EqualTo("Share"));
        });
        UseToken(auth.Token);

        // Unshare remove o acesso público e tira da listagem.
        var unshared = await _client.DeleteAsync($"/api/v1/chats/{chat.Id}/share");
        Assert.That(unshared.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var sharedDepois = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>(
            "/api/v1/chats/shared");
        Assert.That(sharedDepois, Is.Empty);

        _client.DefaultRequestHeaders.Authorization = null;
        var publicoDepois = await _client.GetAsync($"/api/v1/chats/share/{comShare.ShareId}");
        Assert.That(publicoDepois.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        UseToken(auth.Token);
    }

    [Test]
    public async Task Folder_AssociaListaIncluiERemove()
    {
        var auth = await SignUpAsync("Pasta", "pasta@chats.local", "senha123");
        UseToken(auth.Token);

        var folder = await _client.PostAsJsonAsync(
            "/api/v1/folders/", new FolderUpsertRequest("Projetos", null));
        var pasta = (await folder.Content.ReadFromJsonAsync<FolderResponse>())!;
        var chat = await CriarChatAsync("Na pasta", Mensagens("oi"));

        var movido = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/folder",
            new ChatEndpoints.SetFolderRequest(pasta.Id));
        Assert.That(movido.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var movidoChat = (await movido.Content.ReadFromJsonAsync<ChatResponse>())!;
        Assert.That(movidoChat.FolderId, Is.EqualTo(pasta.Id));

        // Chat em pasta sai da listagem padrão e entra na da pasta.
        var raiz = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>("/api/v1/chats/");
        Assert.That(raiz, Is.Empty);
        var naPasta = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>(
            $"/api/v1/chats/folder/{pasta.Id}");
        Assert.That(naPasta, Has.Count.EqualTo(1));

        // includeFolders=true traz chats em pasta na listagem geral.
        var comPastas = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>(
            "/api/v1/chats/?includeFolders=true");
        Assert.That(comPastas, Has.Count.EqualTo(1));

        // FolderId vazio/nulo remove a associação.
        var removido = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/folder",
            new ChatEndpoints.SetFolderRequest(null));
        Assert.That(removido.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var raizDepois = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>("/api/v1/chats/");
        Assert.That(raizDepois, Has.Count.EqualTo(1));

        // Pasta inexistente → 404.
        var nf = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/folder",
            new ChatEndpoints.SetFolderRequest("pasta-inexistente"));
        Assert.That(nf.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Search_RotaDedicada_FiltraPorTituloEConteudo()
    {
        var auth = await SignUpAsync("Search", "search@chats.local", "senha123");
        UseToken(auth.Token);
        await CriarChatAsync("Relatório mensal", Mensagens("conteúdo irrelevante"));
        await CriarChatAsync("Outro assunto", Mensagens("fala sobre orçamento"));

        var porTitulo = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>(
            "/api/v1/chats/search?query=mensal");
        Assert.That(porTitulo, Has.Count.EqualTo(1));
        Assert.That(porTitulo![0].Title, Is.EqualTo("Relatório mensal"));

        // A busca também casa conteúdo de mensagens, não só título.
        var porConteudo = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>(
            "/api/v1/chats/search?query=orçamento");
        Assert.That(porConteudo, Has.Count.EqualTo(1));
        Assert.That(porConteudo![0].Title, Is.EqualTo("Outro assunto"));

        var semMatch = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>(
            "/api/v1/chats/search?query=inexistente");
        Assert.That(semMatch, Is.Empty);
    }

    [Test]
    public async Task Pinned_GetPinned_ListaPinned_EDeleteAll()
    {
        var auth = await SignUpAsync("Pin2", "pin2@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Fixável", Mensagens("oi"));

        var antes = JsonDocument.Parse(await (await _client.GetAsync(
            $"/api/v1/chats/{chat.Id}/pinned")).Content.ReadAsStringAsync());
        Assert.That(antes.RootElement.GetProperty("pinned").GetBoolean(), Is.False);

        await _client.PostAsync($"/api/v1/chats/{chat.Id}/pin", null);
        var depois = JsonDocument.Parse(await (await _client.GetAsync(
            $"/api/v1/chats/{chat.Id}/pinned")).Content.ReadAsStringAsync());
        Assert.That(depois.RootElement.GetProperty("pinned").GetBoolean(), Is.True);

        var pinned = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>(
            "/api/v1/chats/pinned");
        Assert.That(pinned, Has.Count.EqualTo(1));

        // DeleteAll remove todos os chats do usuário.
        var deleted = await _client.DeleteAsync("/api/v1/chats/");
        Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var list = await _client.GetFromJsonAsync<List<ChatSummaryResponse>>("/api/v1/chats/");
        Assert.That(list, Is.Empty);
    }

    // ---- SPEC-20261010-runs-hierarchy: GET /{id}/children ----

    [Test]
    public async Task Children_ChatSemFilhos_RetornaListaVazia()
    {
        var auth = await SignUpAsync("Kids0", "kids0@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("Sem filhos", Mensagens("oi"));

        var children = await _client.GetFromJsonAsync<List<ChatChildSummaryResponse>>(
            $"/api/v1/chats/{chat.Id}/children");
        Assert.That(children, Is.Not.Null.And.Empty);
    }

    [Test]
    public async Task Children_ChatInexistente_404()
    {
        var auth = await SignUpAsync("Kids404", "kids404@chats.local", "senha123");
        UseToken(auth.Token);

        var res = await _client.GetAsync("/api/v1/chats/nao-existe/children");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Children_ChatDeOutroUsuario_404()
    {
        var dono = await SignUpAsync("KidsA", "kidsa@chats.local", "senha123");
        UseToken(dono.Token);
        var chat = await CriarChatAsync("Privado", Mensagens("oi"));

        var outro = await SignUpAsync("KidsB", "kidsb@chats.local", "senha123");
        UseToken(outro.Token);
        var res = await _client.GetAsync($"/api/v1/chats/{chat.Id}/children");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- SPEC-20261010-chat-repo-binding: GET/PUT /{id}/workspace-repo ----

    [Test]
    public async Task WorkspaceRepo_SemBinding_SourceNone()
    {
        var auth = await SignUpAsync("RepoU", "repou@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("sem repo", Mensagens("oi"));

        var resp = await _client.GetFromJsonAsync<ChatWorkspaceRepoResponse>(
            $"/api/v1/chats/{chat.Id}/workspace-repo");
        Assert.Multiple(() =>
        {
            Assert.That(resp!.Source, Is.EqualTo("none"));
            Assert.That(resp.Binding, Is.Null);
        });
    }

    [Test]
    public async Task WorkspaceRepo_ChatInexistente_404()
    {
        var auth = await SignUpAsync("Repo404", "repo404@chats.local", "senha123");
        UseToken(auth.Token);

        Assert.That(
            (await _client.GetAsync("/api/v1/chats/nao-existe/workspace-repo"))
                .StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(
            (await _client.PutAsJsonAsync(
                "/api/v1/chats/nao-existe/workspace-repo",
                new WorkspaceRepoOpenRequest("a/b", "main")))
                .StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task WorkspaceRepo_ChatDeOutroUsuario_404()
    {
        var dono = await SignUpAsync("RepoA", "repoa@chats.local", "senha123");
        UseToken(dono.Token);
        var chat = await CriarChatAsync("repo privado", Mensagens("oi"));

        var outro = await SignUpAsync("RepoB", "repob@chats.local", "senha123");
        UseToken(outro.Token);
        Assert.That(
            (await _client.GetAsync($"/api/v1/chats/{chat.Id}/workspace-repo"))
                .StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task WorkspaceRepo_BindingPorChat_GetSourceChat_EPutLimpa()
    {
        var auth = await SignUpAsync("RepoSeed", "reposeed@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("com binding", Mensagens("oi"));

        // Binding por chat seedado direto no kv (clone real é nível de serviço).
        using var scope = _factory.Services.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
        await config.SetAsync(
            $"chat:{chat.Id}:workspace.repo",
            new WorkspaceRepoBinding("a/b", "main", "repos/a__b", null, null),
            default);

        var resp = await _client.GetFromJsonAsync<ChatWorkspaceRepoResponse>(
            $"/api/v1/chats/{chat.Id}/workspace-repo");
        Assert.Multiple(() =>
        {
            Assert.That(resp!.Source, Is.EqualTo("chat"));
            Assert.That(resp.Binding!.Repo, Is.EqualTo("a/b"));
        });

        // PUT vazio limpa o binding do chat → cai no fallback (none aqui).
        var put = await _client.PutAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/workspace-repo",
            new WorkspaceRepoOpenRequest(null, null));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await put.Content.ReadFromJsonAsync<ChatWorkspaceRepoResponse>();
        Assert.That(body!.Source, Is.EqualTo("none"));
    }

    [Test]
    public async Task WorkspaceRepo_PutSemBranch_400()
    {
        var auth = await SignUpAsync("RepoNB", "reponb@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("sem branch", Mensagens("oi"));

        var res = await _client.PutAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/workspace-repo",
            new WorkspaceRepoOpenRequest("a/b", null));
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task WorkspaceRepo_PutCloneInexistente_400()
    {
        var auth = await SignUpAsync("RepoNF", "reponf@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("clone falha", Mensagens("oi"));

        // Repo inexistente no github → OpenChatAsync falha → BadRequest.
        var res = await _client.PutAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/workspace-repo",
            new WorkspaceRepoOpenRequest(
                "devin-nao-existe/repo-inexistente-xyz", "main"));
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task WorkspaceRepo_PutVazio_LimpaBindingDoChat()
    {
        var auth = await SignUpAsync("RepoClr", "repoclr@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("limpa", Mensagens("oi"));

        var resp = await _client.PutAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/workspace-repo",
            new WorkspaceRepoOpenRequest(null, null));
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await resp.Content.ReadFromJsonAsync<ChatWorkspaceRepoResponse>();
        Assert.That(body!.Source, Is.EqualTo("none"));
    }

    // ---- SPEC-15: modo do agente — chat default; build exige binding ----

    [Test]
    public async Task Patch_Mode_ChatNovoDefaultChat_EBuildSemRepo_400()
    {
        var auth = await SignUpAsync("ModeNB", "modenb@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("modo", Mensagens("oi"));
        Assert.That(chat.Mode, Is.EqualTo("chat"));

        // Build sem repositório vinculado → 400.
        var build = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}",
            new ChatPatchRequest(null, null, "build"));
        Assert.That(build.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // chat e plan são válidos sem repo.
        var plan = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}",
            new ChatPatchRequest(null, null, "plan"));
        Assert.That(plan.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(
            (await plan.Content.ReadFromJsonAsync<ChatResponse>())!.Mode,
            Is.EqualTo("plan"));
    }

    [Test]
    public async Task Patch_ModeBuild_ComBinding_200_EUnbind_VoltaChat()
    {
        var auth = await SignUpAsync("ModeWB", "modewb@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("build com repo", Mensagens("oi"));

        // Binding por chat seedado no kv (o clone em si é nível de serviço).
        using var scope = _factory.Services.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<ConfigService>();
        await config.SetAsync(
            $"chat:{chat.Id}:workspace.repo",
            new WorkspaceRepoBinding("a/b", "main", "repos/a__b", null, null),
            default);

        var build = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}",
            new ChatPatchRequest(null, null, "build"));
        Assert.That(build.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(
            (await build.Content.ReadFromJsonAsync<ChatResponse>())!.Mode,
            Is.EqualTo("build"));

        // Desvincular derruba o modo persistido: build → chat (SPEC-15).
        var unbind = await _client.PutAsJsonAsync(
            $"/api/v1/chats/{chat.Id}/workspace-repo",
            new WorkspaceRepoOpenRequest(null, null));
        Assert.That(unbind.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var depois = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{chat.Id}");
        Assert.That(depois!.Mode, Is.EqualTo("chat"));

        // Sem binding o PATCH de build volta a ser recusado.
        var again = await _client.PatchAsJsonAsync(
            $"/api/v1/chats/{chat.Id}",
            new ChatPatchRequest(null, null, "build"));
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ── SPEC-20261019-chat-window-perf RF-001: paginação da janela ──

    [Test]
    public async Task GetChatTail_RetornaCaudaEHasMore()
    {
        var auth = await SignUpAsync("Tail", "tail@chats.local", "senha123");
        UseToken(auth.Token);
        var msgs = Enumerable.Range(1, 120)
            .Select(i => new ChatMessageModel($"m{i}", i % 2 == 1 ? "user" : "assistant", $"msg {i}", null, 100 + i))
            .ToList();
        var chat = await CriarChatAsync("tail", msgs);

        var tail = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{chat.Id}?tail=30");
        Assert.That(tail!.Messages, Has.Count.EqualTo(30));
        Assert.That(tail.MessagesHasMore, Is.True);
        Assert.That(tail.Messages[0].Id, Is.EqualTo("m91"));
        Assert.That(tail.Messages[^1].Id, Is.EqualTo("m120"));

        var full = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{chat.Id}");
        Assert.That(full!.Messages, Has.Count.EqualTo(120));
        Assert.That(full.MessagesHasMore, Is.False);
    }

    [Test]
    public async Task Paginacao_AntesDeTakeOrdemEHasMore()
    {
        var auth = await SignUpAsync("Page", "page@chats.local", "senha123");
        UseToken(auth.Token);
        var msgs = Enumerable.Range(1, 120)
            .Select(i => new ChatMessageModel($"m{i}", i % 2 == 1 ? "user" : "assistant", $"msg {i}", null, 100 + i))
            .ToList();
        var chat = await CriarChatAsync("page", msgs);

        var page = await _client.GetFromJsonAsync<ChatMessagesPage>(
            $"/api/v1/chats/{chat.Id}/messages?before=m91&take=50");
        Assert.That(page!.Messages, Has.Count.EqualTo(50));
        Assert.That(page.Messages[0].Id, Is.EqualTo("m41"));
        Assert.That(page.Messages[^1].Id, Is.EqualTo("m90"));
        Assert.That(page.HasMore, Is.True);
        Assert.That(page.Total, Is.EqualTo(120));

        // Esgotamento no início do caminho.
        var first = await _client.GetFromJsonAsync<ChatMessagesPage>(
            $"/api/v1/chats/{chat.Id}/messages?before=m41&take=50");
        Assert.That(first!.Messages, Has.Count.EqualTo(40));
        Assert.That(first.HasMore, Is.False);
    }

    [Test]
    public async Task Paginacao_NaoQuebraGrupoToolNoTopo()
    {
        var auth = await SignUpAsync("Snap", "snap@chats.local", "senha123");
        UseToken(auth.Token);
        // m3..m5 = grupo de tools; take cruza a fronteira no meio do grupo.
        var msgs = new List<ChatMessageModel>
        {
            new("m1", "user", "u1", null, 101),
            new("m2", "assistant", "a1", null, 102),
            new("m3", "tool", "t1", null, 103),
            new("m4", "tool", "t2", null, 104),
            new("m5", "tool", "t3", null, 105),
            new("m6", "assistant", "a2", null, 106),
            new("m7", "user", "u2", null, 107),
            new("m8", "assistant", "a3", null, 108),
        };
        var chat = await CriarChatAsync("snap", msgs);

        var page = await _client.GetFromJsonAsync<ChatMessagesPage>(
            $"/api/v1/chats/{chat.Id}/messages?before=m7&take=2");
        // Sem snap seria [m5(tool),m6] — feed nasceria cortado.
        Assert.That(page!.Messages.Select(m => m.Id),
            Is.EqualTo(new[] { "m3", "m4", "m5", "m6" }));
        Assert.That(page.HasMore, Is.True);

        var tail = await _client.GetFromJsonAsync<ChatResponse>(
            $"/api/v1/chats/{chat.Id}?tail=5");
        // tail=5 abriria em m4 (meio do grupo) — snap estende para m3.
        Assert.That(tail!.Messages.Select(m => m.Id),
            Is.EqualTo(new[] { "m3", "m4", "m5", "m6", "m7", "m8" }));
        Assert.That(tail.MessagesHasMore, Is.True);
    }

    [Test]
    public async Task Paginacao_BeforeInexistente_UsaFimDoCaminho()
    {
        var auth = await SignUpAsync("End", "end@chats.local", "senha123");
        UseToken(auth.Token);
        var chat = await CriarChatAsync("end", Mensagens("a", "b", "c"));

        var page = await _client.GetFromJsonAsync<ChatMessagesPage>(
            $"/api/v1/chats/{chat.Id}/messages?before=nope&take=2");
        Assert.That(page!.Messages, Has.Count.EqualTo(2));
        Assert.That(page.Messages[^1].Id, Is.EqualTo("m3"));
        Assert.That(page.Total, Is.EqualTo(3));
    }
}
