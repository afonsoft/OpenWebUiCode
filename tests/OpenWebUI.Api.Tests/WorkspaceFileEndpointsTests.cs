using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura da Workspace File API (SPEC-20261009-workspace-file-api /
/// E16 S1): endpoints <c>/api/v1/workspace/repo/{tree,file,mkdir,rename,
/// delete}</c> sobre o workdir do repo vinculado — jail ResolveInside,
/// symlink, paginação da tree, etag/If-Match, binary-guard, 404 sem
/// binding e trilha de auditoria nas escritas.
/// </summary>
[TestFixture, IsolateEnvironment]
public class WorkspaceFileEndpointsTests
{
    private string _apiDir = null!;

    [SetUp]
    public void SetUp() => _apiDir = FindApiDir();

    // ---------- RF-004 + auth ----------

    [Test]
    public async Task Endpoints_SemAuth_401()
    {
        using var dbScope = TestInfra.UseDb();
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        foreach (var uri in new[]
        {
            "/api/v1/workspace/repo/tree?path=",
            "/api/v1/workspace/repo/file?path=a.txt",
        })
        {
            var response = await client.GetAsync(uri);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized), uri);
        }
    }

    [Test]
    public async Task Endpoints_SemRepoVinculado_404BoundFalse()
    {
        using var ctx = await NewAppAsync(bound: false);
        foreach (var uri in new[]
        {
            "/api/v1/workspace/repo/tree",
            "/api/v1/workspace/repo/file?path=a.txt",
        })
        {
            var get = await ctx.Client.GetAsync(uri);
            Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), uri);
            var body = await get.Content.ReadFromJsonAsync<JsonElement>();
            Assert.That(body.GetProperty("bound").GetBoolean(), Is.False, uri);
        }

        var put = await ctx.Client.PutAsJsonAsync("/api/v1/workspace/repo/file",
            new WorkspaceFileWriteRequest("a.txt", "x"));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        foreach (var (uri, body) in new (string, object)[]
        {
            ("/api/v1/workspace/repo/mkdir", new WorkspaceFileMkdirRequest("d")),
            ("/api/v1/workspace/repo/rename", new WorkspaceFileRenameRequest("a", "b")),
            ("/api/v1/workspace/repo/delete", new WorkspaceFileDeleteRequest("a")),
        })
        {
            var post = await ctx.Client.PostAsJsonAsync(uri, body);
            Assert.That(post.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), uri);
        }
    }

    // ---------- RF-001: tree ----------

    [Test]
    public async Task Tree_Raiz_DirsPrimeiroEDirsComunsColapsados()
    {
        using var ctx = await NewAppAsync(bound: true);
        var w = ctx.Workdir!;
        Directory.CreateDirectory(Path.Join(w, "src"));
        Directory.CreateDirectory(Path.Join(w, "bin"));
        File.WriteAllText(Path.Join(w, "readme.md"), "oi\n");
        File.WriteAllText(Path.Join(w, "a.cs"), "class A {}\n");

        var tree = await ctx.Client.GetFromJsonAsync<WorkspaceFileTreeResponse>(
            "/api/v1/workspace/repo/tree?path=&depth=1");
        Assert.That(tree, Is.Not.Null);
        var names = tree!.Entries.Select(e => e.Name).ToList();
        Assert.Multiple(() =>
        {
            // dirs primeiro, ordem de nome; .git da fixture é colapsado
            Assert.That(names[0], Is.EqualTo(".git"));
            Assert.That(names, Does.Contain("src").And.Contain("bin")
                .And.Contain("a.cs").And.Contain("readme.md"));
            Assert.That(tree.Entries.First(e => e.Name == ".git").Collapsed, Is.True);
            Assert.That(tree.Entries.First(e => e.Name == "bin").Collapsed, Is.True);
            Assert.That(tree.Entries.First(e => e.Name == "src").Collapsed, Is.False);
            Assert.That(tree.Entries.First(e => e.Name == "a.cs").Type, Is.EqualTo("file"));
            Assert.That(tree.Entries.First(e => e.Name == "a.cs").Size, Is.EqualTo(11));
        });
    }

    [Test]
    public async Task Tree_SubdirEDepth2()
    {
        using var ctx = await NewAppAsync(bound: true);
        var w = ctx.Workdir!;
        Directory.CreateDirectory(Path.Join(w, "src", "inner"));
        File.WriteAllText(Path.Join(w, "src", "inner", "f.cs"), "x");

        var tree = await ctx.Client.GetFromJsonAsync<WorkspaceFileTreeResponse>(
            "/api/v1/workspace/repo/tree?path=src&depth=1");
        Assert.That(tree!.Entries.Select(e => e.Path), Does.Contain("src/inner"));

        // src/inner tem profundidade 2; o arquivo dentro dele, profundidade 3.
        var two = await ctx.Client.GetFromJsonAsync<WorkspaceFileTreeResponse>(
            "/api/v1/workspace/repo/tree?path=&depth=2");
        Assert.That(two!.Entries.Select(e => e.Path),
            Does.Contain("src/inner").And.Not.Contain("src/inner/f.cs"));
        var deep = await ctx.Client.GetFromJsonAsync<WorkspaceFileTreeResponse>(
            "/api/v1/workspace/repo/tree?path=&depth=3");
        Assert.That(deep!.Entries.Select(e => e.Path), Does.Contain("src/inner/f.cs"));
    }

    [Test]
    public async Task Tree_EscapePaths_400()
    {
        using var ctx = await NewAppAsync(bound: true);
        foreach (var p in new[] { "..", "../x", "/etc", "~/x", "$HOME/x" })
        {
            var response = await ctx.Client.GetAsync(
                $"/api/v1/workspace/repo/tree?path={Uri.EscapeDataString(p)}");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), p);
        }
    }

    [Test]
    public async Task Tree_SymlinkParaFora_Rejeitado()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Ignore("symlink sem privilege no Windows CI.");
        }
        using var ctx = await NewAppAsync(bound: true);
        var outside = Path.Join(Path.GetTempPath(), $"owui-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Join(ctx.Workdir!, "link-out"), outside);
            var response = await ctx.Client.GetAsync("/api/v1/workspace/repo/tree?path=link-out");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Test]
    public async Task Tree_SymlinkDir_NaoListaFilhosForaDoJail()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Ignore("symlink sem privilege no Windows CI.");
        }
        using var ctx = await NewAppAsync(bound: true);
        var outside = Path.Join(Path.GetTempPath(), $"owui-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Join(outside, "segredo.txt"), "não olhar");
        try
        {
            Directory.CreateSymbolicLink(Path.Join(ctx.Workdir!, "link-out"), outside);
            var tree = await ctx.Client.GetFromJsonAsync<WorkspaceFileTreeResponse>(
                "/api/v1/workspace/repo/tree?path=&depth=3");
            Assert.That(tree, Is.Not.Null);
            Assert.Multiple(() =>
            {
                // o symlink aparece como dir colapsado...
                Assert.That(tree!.Entries.First(e => e.Name == "link-out").Collapsed, Is.True);
                // ...e nenhum filho do alvo fora do jail é listado
                Assert.That(tree.Entries.Any(e => e.Name == "segredo.txt"), Is.False);
            });
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    // ---------- RF-002: read ----------

    [Test]
    public async Task Read_PaginaLinhas_RetornaFatiaETag()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, "f.txt"),
            string.Join("\n", Enumerable.Range(1, 10).Select(i => $"l{i}")) + "\n");

        var response = await ctx.Client.GetAsync(
            "/api/v1/workspace/repo/file?path=f.txt&startLine=3&maxLines=4");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.ETag, Is.Not.Null);

        var body = await response.Content.ReadFromJsonAsync<WorkspaceFileReadResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(body!.Path, Is.EqualTo("f.txt"));
            Assert.That(body.Content, Is.EqualTo("l3\nl4\nl5\nl6"));
            Assert.That(body.TotalLines, Is.EqualTo(10));
            Assert.That(body.Truncated, Is.True);
            Assert.That(body.ETag, Does.StartWith("\""));
        });
    }

    [Test]
    public async Task Read_Binario_415_E_Grande_413()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllBytes(Path.Join(ctx.Workdir!, "bin.dat"), new byte[] { 0, 1, 2, 3 });

        var bin = await ctx.Client.GetAsync("/api/v1/workspace/repo/file?path=bin.dat");
        Assert.That(bin.StatusCode, Is.EqualTo(HttpStatusCode.UnsupportedMediaType));

        var big = Path.Join(ctx.Workdir!, "big.txt");
        File.WriteAllText(big, new string('a', 600 * 1024));
        var bigResponse = await ctx.Client.GetAsync("/api/v1/workspace/repo/file?path=big.txt");
        Assert.That(bigResponse.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        var body = await bigResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("size").GetInt64(), Is.EqualTo(600 * 1024));
    }

    [Test]
    public async Task Read_NaoExiste_404_E_Escape_400()
    {
        using var ctx = await NewAppAsync(bound: true);
        var nf = await ctx.Client.GetAsync("/api/v1/workspace/repo/file?path=nada.txt");
        Assert.That(nf.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var esc = await ctx.Client.GetAsync(
            "/api/v1/workspace/repo/file?path=..%2FProgram.cs");
        Assert.That(esc.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---------- RF-003: write ops ----------

    [Test]
    public async Task Write_CriaArquivo_ERespondeETag()
    {
        using var ctx = await NewAppAsync(bound: true);
        var put = await ctx.Client.PutAsJsonAsync("/api/v1/workspace/repo/file",
            new WorkspaceFileWriteRequest("novo/dir/f.txt", "conteúdo\n"));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await put.Content.ReadFromJsonAsync<WorkspaceFileWriteResponse>();
        Assert.Multiple(() =>
        {
            Assert.That(body!.Ok, Is.True);
            Assert.That(body.Path, Is.EqualTo("novo/dir/f.txt"));
            Assert.That(body.ETag, Does.StartWith("\""));
        });
        Assert.That(File.ReadAllText(Path.Join(ctx.Workdir!, "novo/dir/f.txt")),
            Is.EqualTo("conteúdo\n"));
    }

    [Test]
    public async Task Write_IfMatchDivergente_409()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, "f.txt"), "v1\n");

        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/workspace/repo/file")
        {
            Content = JsonContent.Create(new WorkspaceFileWriteRequest("f.txt", "v2\n")),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"0-0\"");
        var response = await ctx.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        // etag atual vem no conflito para a UI oferecer merge/descartar
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("etag").GetString(), Does.StartWith("\""));
        Assert.That(File.ReadAllText(Path.Join(ctx.Workdir!, "f.txt")), Is.EqualTo("v1\n"));
    }

    [Test]
    public async Task Write_IfMatchCorreto_Sobrescreve()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, "f.txt"), "v1\n");
        var read = await ctx.Client.GetFromJsonAsync<WorkspaceFileReadResponse>(
            "/api/v1/workspace/repo/file?path=f.txt");

        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/workspace/repo/file")
        {
            Content = JsonContent.Create(new WorkspaceFileWriteRequest("f.txt", "v2\n")),
        };
        request.Headers.TryAddWithoutValidation("If-Match", read!.ETag);
        var response = await ctx.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(File.ReadAllText(Path.Join(ctx.Workdir!, "f.txt")), Is.EqualTo("v2\n"));
    }

    [Test]
    public async Task Write_ConteudoBinario_415()
    {
        using var ctx = await NewAppAsync(bound: true);
        var put = await ctx.Client.PutAsJsonAsync("/api/v1/workspace/repo/file",
            new WorkspaceFileWriteRequest("b.bin", "a b"));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.UnsupportedMediaType));
    }

    [Test]
    public async Task Mkdir_Rename_Delete_Fluxo()
    {
        using var ctx = await NewAppAsync(bound: true);
        var w = ctx.Workdir!;
        File.WriteAllText(Path.Join(w, "orig.txt"), "x\n");

        var mk = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/mkdir",
            new WorkspaceFileMkdirRequest("novo/sub"));
        Assert.That(mk.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Directory.Exists(Path.Join(w, "novo/sub")), Is.True);

        var rn = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/rename",
            new WorkspaceFileRenameRequest("orig.txt", "novo/movido.txt"));
        Assert.That(rn.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Join(w, "novo/movido.txt")), Is.True);
            Assert.That(File.Exists(Path.Join(w, "orig.txt")), Is.False);
        });

        // rename para destino existente → 409
        File.WriteAllText(Path.Join(w, "existe.txt"), "y\n");
        var rn2 = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/rename",
            new WorkspaceFileRenameRequest("novo/movido.txt", "existe.txt"));
        Assert.That(rn2.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));

        var del = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/delete",
            new WorkspaceFileDeleteRequest("novo"));
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Directory.Exists(Path.Join(w, "novo")), Is.False);
    }

    [Test]
    public async Task Rename_ParaDentroDeSi_409()
    {
        using var ctx = await NewAppAsync(bound: true);
        Directory.CreateDirectory(Path.Join(ctx.Workdir!, "pai"));
        var rn = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/rename",
            new WorkspaceFileRenameRequest("pai", "pai/filho"));
        Assert.That(rn.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(Directory.Exists(Path.Join(ctx.Workdir!, "pai")), Is.True);
    }

    [Test]
    public async Task Write_PathEscape_400()
    {
        using var ctx = await NewAppAsync(bound: true);
        var put = await ctx.Client.PutAsJsonAsync("/api/v1/workspace/repo/file",
            new WorkspaceFileWriteRequest("../fora.txt", "x"));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        var rn = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/rename",
            new WorkspaceFileRenameRequest("a", "../b"));
        Assert.That(rn.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---------- RF-005: auditoria ----------

    [Test]
    public async Task Write_RegistraAuditoria()
    {
        var spy = new SpyLoggerProvider();
        var dbPath = Path.Join(Path.GetTempPath(), $"openwebui-wf-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={dbPath}");
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(spy)));
        var (client, workdir, uid) = await SignUpAndBindAsync(factory, dbPath);
        try
        {
            var put = await client.PutAsJsonAsync("/api/v1/workspace/repo/file",
                new WorkspaceFileWriteRequest("audit.txt", "x\n"));
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(
                spy.Records.Any(r => r.Contains("audit.txt") && r.Contains(uid)),
                Is.True, $"auditoria ausente; registros: {string.Join(" | ", spy.Records)}");
        }
        finally
        {
            CleanupBoundWorkspace(uid);
        }
    }

    // ---------- S2: GET /workspace/repo/git + /api/v1/ide/config ----------

    [Test]
    public async Task Git_SemAuth_401_E_SemBinding_404()
    {
        using var dbScope = TestInfra.UseDb();
        using var factory = new WebApplicationFactory<Program>();
        using var anon = factory.CreateClient();
        var unauth = await anon.GetAsync("/api/v1/workspace/repo/git");
        Assert.That(unauth.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        using var ctx = await NewAppAsync(bound: false);
        var res = await ctx.Client.GetAsync("/api/v1/workspace/repo/git");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("bound").GetBoolean(), Is.False);
    }

    [Test]
    public async Task Git_RepoVinculado_RetornaSnapshot()
    {
        using var ctx = await NewAppAsync(bound: true);
        // A fixture cria .git como dir vazio — o guard toplevel==workdir
        // (SPEC-20261011-ide-search-scm) rejeita .git falso; repo real
        // com commit inicial exercita o caminho IsRepo=true.
        await InitRepoAsync(ctx.Workdir!);

        var res = await ctx.Client.GetAsync("/api/v1/workspace/repo/git");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await res.Content.ReadFromJsonAsync<WorkspaceGitResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Git, Is.True);
    }

    /// <summary>.git vazio (sem repo real) → Git=false — não escala pra
    /// repo pai quando o workdir mora dentro de um clone (SPEC Fase C).</summary>
    [Test]
    public async Task Git_DotGitFalso_NaoEscalaProRepoPai()
    {
        using var ctx = await NewAppAsync(bound: true);
        var res = await ctx.Client.GetAsync("/api/v1/workspace/repo/git");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await res.Content.ReadFromJsonAsync<WorkspaceGitResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Git, Is.False);
    }

    [Test]
    public async Task IdeConfig_SemAuth_401_E_DefaultOn()
    {
        using var dbScope = TestInfra.UseDb();
        using var factory = new WebApplicationFactory<Program>();
        using var anon = factory.CreateClient();
        var unauth = await anon.GetAsync("/api/v1/ide/config");
        Assert.That(unauth.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        using var ctx = await NewAppAsync(bound: false);
        var res = await ctx.Client.GetAsync("/api/v1/ide/config");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.That(body.GetProperty("enabled").GetBoolean(), Is.True);

        // O primeiro signup vira admin — pra exercitar o gate admin-only,
        // abaixa o DefaultUserRole e cria um segundo usuário (role "user").
        var cfg = await ctx.Client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        Assert.That(cfg.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var previousAuth = ctx.Client.DefaultRequestHeaders.Authorization;
        ctx.Client.DefaultRequestHeaders.Authorization = null;
        try
        {
            var tag2 = Guid.NewGuid().ToString("N")[..6];
            var signup2 = await ctx.Client.PostAsJsonAsync(
                "/api/v1/auths/signup", new SignUpRequest($"W2{tag2}", $"w2{tag2}@wf.local", "senha123"));
            Assert.That(signup2.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var auth2 = (await signup2.Content.ReadFromJsonAsync<AuthResponse>())!;

            ctx.Client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", auth2.Token);
            var put = await ctx.Client.PutAsJsonAsync("/api/v1/ide/config", new { enabled = false });
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }
        finally
        {
            ctx.Client.DefaultRequestHeaders.Authorization = previousAuth;
        }
    }


    // ---------- SPEC-20261010-workspace-chatid-scope: ?chatId= ----------

    /// <summary>Binding do chat tem precedência sobre o global: tree e git
    /// com <c>?chatId=</c> veem o workdir do chat, sem o parâmetro veem o global.</summary>
    [Test]
    public async Task Tree_Git_ChatId_UsaBindingDoChat()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, "marcador-global.txt"), "g");

        var chatId = await CriarChatAsync(ctx.Client);
        var chatDir = Path.Join(_apiDir, "data", "workspaces", ctx.UserId, "repos/chat__repo");
        Directory.CreateDirectory(Path.Join(chatDir, ".git"));
        File.WriteAllText(Path.Join(chatDir, "marcador-chat.txt"), "c");
        await SeedChatBindingAsync(ctx.DbPath, chatId, "chat/repo", "repos/chat__repo");

        var treeChat = await ctx.Client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/workspace/repo/tree?chatId={chatId}");
        var namesChat = treeChat.GetProperty("entries")
            .EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.That(namesChat, Does.Contain("marcador-chat.txt"));
        Assert.That(namesChat, Does.Not.Contain("marcador-global.txt"));

        var treeGlobal = await ctx.Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/workspace/repo/tree");
        var namesGlobal = treeGlobal.GetProperty("entries")
            .EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.That(namesGlobal, Does.Contain("marcador-global.txt"));

        var gitChat = await ctx.Client.GetAsync(
            $"/api/v1/workspace/repo/git?chatId={chatId}");
        Assert.That(gitChat.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    /// <summary>chatId de outro usuário → 404 (mesmo com binding válido lá).</summary>
    [Test]
    public async Task Tree_ChatIdDeOutroUsuario_404()
    {
        using var ctx = await NewAppAsync(bound: true);

        // libera signup direto pra role "user" (default pode ser "pending")
        var cfg = await ctx.Client.PostAsJsonAsync("/api/v1/auths/admin/config",
            AdminConfig.Default with { DefaultUserRole = "user" });
        Assert.That(cfg.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // segundo usuário no mesmo banco, com chat próprio
        var client2 = ctx.Factory.CreateClient();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var signup = await client2.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest($"WF{tag}", $"wf{tag}@wf.local", "senha123"));
        Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await signup.Content.ReadFromJsonAsync<AuthResponse>())!.Token);
        var chatIdOutro = await CriarChatAsync(client2);

        var res = await ctx.Client.GetAsync(
            $"/api/v1/workspace/repo/tree?chatId={chatIdOutro}");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        var skills = await ctx.Client.GetAsync(
            $"/api/v1/workspace/repo/skills?chatId={chatIdOutro}");
        Assert.That(skills.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    /// <summary>chatId inexistente → 404.</summary>
    [Test]
    public async Task Tree_ChatIdInexistente_404()
    {
        using var ctx = await NewAppAsync(bound: true);
        var res = await ctx.Client.GetAsync(
            "/api/v1/workspace/repo/tree?chatId=chat-nao-existe");
        Assert.That(res.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    /// <summary>PUT test-command com chatId escreve na chave do chat e
    /// preserva o binding global.</summary>
    [Test]
    public async Task TestCommand_ChatId_EscreveNaChaveDoChat()
    {
        using var ctx = await NewAppAsync(bound: true);
        var chatId = await CriarChatAsync(ctx.Client);
        await SeedChatBindingAsync(ctx.DbPath, chatId, "chat/repo", "repos/chat__repo");

        var put = await ctx.Client.PutAsJsonAsync(
            $"/api/v1/workspace/repo/test-command?chatId={chatId}",
            new TestCommandRequest("dotnet test"));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={ctx.DbPath}").Options);
        var chatKv = await db.ConfigEntries.AsNoTracking()
            .SingleAsync(e => e.Key == $"chat:{chatId}:workspace.repo");
        var chatBinding = JsonSerializer.Deserialize<WorkspaceRepoBinding>(chatKv.ValueJson, JsonInsensitive)!;
        Assert.That(chatBinding.TestCommand, Is.EqualTo("dotnet test"));

        var userKv = await db.ConfigEntries.AsNoTracking()
            .SingleAsync(e => e.Key == $"u:{ctx.UserId}:workspace.repo");
        var userBinding = JsonSerializer.Deserialize<WorkspaceRepoBinding>(userKv.ValueJson, JsonInsensitive)!;
        Assert.That(userBinding.TestCommand, Is.Null);
    }

    private static readonly JsonSerializerOptions JsonInsensitive =
        new() { PropertyNameCaseInsensitive = true };

    private async Task<string> CriarChatAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync(
            "/api/v1/chats/", new ChatUpsertRequest("chat-scope", ["llama3"], []));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<ChatResponse>())!.Id;
    }

    /// <summary>Semeia o kv <c>chat:{id}:workspace.repo</c> direto no SQLite.</summary>
    private static async Task SeedChatBindingAsync(
        string dbPath, string chatId, string repo, string dir)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options);
        var json = JsonSerializer.Serialize(new WorkspaceRepoBinding(repo, "main", dir));
        db.ConfigEntries.Add(new ConfigEntry { Key = $"chat:{chatId}:workspace.repo", ValueJson = json });
        await db.SaveChangesAsync();
    }

    // ---------- SPEC-20261011-ide-search-scm (Fase C): /search + /commit ----------

    /// <summary>Match textual (case-insensitive) no arquivo → linha/col certas.</summary>
    [Test]
    public async Task Search_EncontraMatch_LinhaECol()
    {
        using var ctx = await NewAppAsync(bound: true);
        var put = await ctx.Client.PutAsJsonAsync("/api/v1/workspace/repo/file",
            new WorkspaceFileWriteRequest("a.txt", "hello\nworld HELLO\n"));
        Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var r = await ctx.Client.GetFromJsonAsync<WorkspaceSearchResponse>(
            "/api/v1/workspace/repo/search?q=hello");
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Matches.Count, Is.EqualTo(2));
        Assert.That(r.Matches[0].Path, Is.EqualTo("a.txt"));
        Assert.That(r.Matches[0].Line, Is.EqualTo(0));
        Assert.That(r.Matches[0].Col, Is.EqualTo(0));
        Assert.That(r.Matches[1].Line, Is.EqualTo(1));
        Assert.That(r.Matches[1].Col, Is.EqualTo(6));
    }

    /// <summary>Arquivo em diretório colapsado (.git) é ignorado pela busca.</summary>
    [Test]
    public async Task Search_PulaCollapsedDirs()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, ".git", "hidden.txt"), "needle\n");

        var r = await ctx.Client.GetFromJsonAsync<WorkspaceSearchResponse>(
            "/api/v1/workspace/repo/search?q=needle");
        Assert.That(r!.Matches, Is.Empty);
    }

    /// <summary>q vazio → resposta vazia (sem varrer o disco).</summary>
    [Test]
    public async Task Search_QueryVazia_Vazio()
    {
        using var ctx = await NewAppAsync(bound: true);
        var r = await ctx.Client.GetFromJsonAsync<WorkspaceSearchResponse>(
            "/api/v1/workspace/repo/search?q=");
        Assert.That(r!.Matches, Is.Empty);
        Assert.That(r.Truncated, Is.False);
    }

    /// <summary>Path fora do workdir → 400 (jail ResolveInsideFinal).</summary>
    [Test]
    public async Task Search_PathForaDoJail_400()
    {
        using var ctx = await NewAppAsync(bound: true);
        var r = await ctx.Client.GetAsync("/api/v1/workspace/repo/search?q=x&path=/etc");
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>GET /git informa GitWrite=false por default (flag off).</summary>
    [Test]
    public async Task Git_Default_GitWriteFalse()
    {
        using var ctx = await NewAppAsync(bound: true);
        var r = await ctx.Client.GetFromJsonAsync<WorkspaceGitResponse>(
            "/api/v1/workspace/repo/git");
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.GitWrite, Is.False);
    }

    /// <summary>POST /commit sem a flag ide.git_write → 403.</summary>
    [Test]
    public async Task Commit_FlagOff_403()
    {
        using var ctx = await NewAppAsync(bound: true);
        var r = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/repo/commit",
            new WorkspaceCommitRequest("teste"));
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    /// <summary>Com Ide:GitWrite=true o commit cria HEAD e responde o sha.</summary>
    [Test]
    public async Task Commit_FlagOn_CriaCommit()
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"openwebui-wf-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={dbPath}");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
            b => b.ConfigureAppConfiguration(
                (_, cfg) => cfg.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Ide:GitWrite"] = "true" })));
        var (client, workdir, uid) = await SignUpAndBindAsync(factory, dbPath);
        try
        {
            // A fixture cria .git como dir vazio — promove a repo real.
            await InitRepoAsync(workdir!);

            var put = await client.PutAsJsonAsync("/api/v1/workspace/repo/file",
                new WorkspaceFileWriteRequest("novo.txt", "conteudo\n"));
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));

            var r = await client.PostAsJsonAsync("/api/v1/workspace/repo/commit",
                new WorkspaceCommitRequest("feat: novo arquivo"));
            Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = await r.Content.ReadFromJsonAsync<WorkspaceCommitResponse>();
            Assert.That(body, Is.Not.Null);
            Assert.That(body!.Ok, Is.True);
            Assert.That(body.Commit, Is.Not.Null.And.Not.Empty);
            Assert.That(File.Exists(Path.Join(workdir!, ".git", "HEAD")), Is.True);
        }
        finally
        {
            client.Dispose();
            factory.Dispose();
            CleanupBoundWorkspace(uid);
        }
    }

    /// <summary><c>git init</c> + commit vazio — repo real com HEAD válido
    /// (abbrev-ref HEAD falha em repo sem commits).</summary>
    private static async Task InitRepoAsync(string workdir)
    {
        foreach (var (label, args) in new (string, string)[]
        {
            ("init", "init"),
            ("commit", "-c user.name=t -c user.email=t@t commit --allow-empty -m init"),
        })
        {
            var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("git", args)
                {
                    WorkingDirectory = workdir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })!;
            await p.WaitForExitAsync();
            Assert.That(p.ExitCode, Is.EqualTo(0), $"git {label} falhou");
        }
    }

    /// <summary>Roda git com ArgumentList (args com espaços seguros) e
    /// falha o teste em exit code != 0.</summary>
    private static async Task GitAsync(string workdir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
        Assert.That(p.ExitCode, Is.EqualTo(0), $"git {string.Join(' ', args)} falhou");
    }

    // ---------- SPEC-20261011-ide-intellisense (Fase D): /lsp/symbols + /lsp/format ----------

    /// <summary>/symbols sem servidor LSP (workdir fake) → available:false, 200.</summary>
    [Test]
    public async Task LspSymbols_SemServidor_AvailableFalse()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, "a.unknownext"), "x\n");
        var r = await ctx.Client.GetFromJsonAsync<LspSymbolsResponse>(
            "/api/v1/workspace/lsp/symbols?path=a.unknownext");
        Assert.That(r, Is.Not.Null);
        Assert.That(r!.Available, Is.False);
        Assert.That(r.Symbols, Is.Empty);
    }

    /// <summary>/symbols fora do jail → 400.</summary>
    [Test]
    public async Task LspSymbols_PathFora_400()
    {
        using var ctx = await NewAppAsync(bound: true);
        var r = await ctx.Client.GetAsync("/api/v1/workspace/lsp/symbols?path=/etc/passwd");
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>/format sem servidor → available:false, text null.</summary>
    [Test]
    public async Task LspFormat_SemServidor_AvailableFalse()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, "a.unknownext"), "x\n");
        var r = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/lsp/format",
            new LspFormatRequest("a.unknownext"));
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await r.Content.ReadFromJsonAsync<LspFormatResponse>();
        Assert.That(body!.Available, Is.False);
        Assert.That(body.Text, Is.Null);
    }

    /// <summary>/format em arquivo inexistente → 404; fora do jail → 400.</summary>
    [Test]
    public async Task LspFormat_InexistenteEFora_404E400()
    {
        using var ctx = await NewAppAsync(bound: true);
        var miss = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/lsp/format",
            new LspFormatRequest("nao-existe.txt"));
        Assert.That(miss.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var outside = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/lsp/format",
            new LspFormatRequest("/etc/passwd"));
        Assert.That(outside.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---------- SPEC-20261011-ide-bundle (Fase E): /lsp/completion|definition|references|rename ----------

    /// <summary>/completion com servidor não rodando (extensão mapeada, sem
    /// pylsp) → available:false, from null, items vazio — nunca 5xx.</summary>
    [Test]
    public async Task LspCompletion_SemServidor_AvailableFalse()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, "a.py"), "x=1\n");
        var r = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/lsp/completion",
            new LspPosRequest("a.py", 0, 0));
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await r.Content.ReadFromJsonAsync<LspCompletionResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(body!.Available, Is.False);
            Assert.That(body.From, Is.Null);
            Assert.That(body.Items, Is.Empty);
        });
    }

    /// <summary>/completion fora do jail → 400.</summary>
    [Test]
    public async Task LspCompletion_PathFora_400()
    {
        using var ctx = await NewAppAsync(bound: true);
        var r = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/lsp/completion",
            new LspPosRequest("/etc/passwd", 0, 0));
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>/definition e /references sem servidor → available:false,
    /// locations vazio (degrade compartilhado de LocationsAsync).</summary>
    [Test]
    public async Task LspDefinition_References_SemServidor_AvailableFalse()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, "a.py"), "x=1\n");
        var def = await ctx.Client.GetAsync(
            "/api/v1/workspace/lsp/definition?path=a.py&line=0&col=0");
        Assert.That(def.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var defBody = await def.Content.ReadFromJsonAsync<LspLocationsResponse>();
        Assert.That(defBody!.Available, Is.False);
        Assert.That(defBody.Locations, Is.Empty);

        var refs = await ctx.Client.GetAsync(
            "/api/v1/workspace/lsp/references?path=a.py&line=0&col=0");
        Assert.That(refs.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var refsBody = await refs.Content.ReadFromJsonAsync<LspLocationsResponse>();
        Assert.That(refsBody!.Available, Is.False);
        Assert.That(refsBody.Locations, Is.Empty);
    }

    /// <summary>/definition fora do jail → 400.</summary>
    [Test]
    public async Task LspDefinition_PathFora_400()
    {
        using var ctx = await NewAppAsync(bound: true);
        var r = await ctx.Client.GetAsync(
            "/api/v1/workspace/lsp/definition?path=/etc/passwd&line=0&col=0");
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>/rename sem servidor → available:false sem erro (degrade);
    /// nome vazio → available:false com error "Nome vazio.".</summary>
    [Test]
    public async Task LspRename_SemServidor_ENomeVazio()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, "a.py"), "x=1\n");

        var rename = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/lsp/rename",
            new LspRenameRequest("a.py", 0, 0, "novoNome", "a.py"));
        Assert.That(rename.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await rename.Content.ReadFromJsonAsync<LspRenameResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(body!.Available, Is.False);
            Assert.That(body.Error, Is.Null);
            Assert.That(body.ClientEdits, Is.Empty);
            Assert.That(body.FilesUpdated, Is.Empty);
        });

        var vazio = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/lsp/rename",
            new LspRenameRequest("a.py", 0, 0, "  ", "a.py"));
        Assert.That(vazio.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var vazioBody = await vazio.Content.ReadFromJsonAsync<LspRenameResponse>();
        Assert.That(vazioBody!.Error, Is.EqualTo("Nome vazio."));
    }

    /// <summary>/rename fora do jail → 400.</summary>
    [Test]
    public async Task LspRename_PathFora_400()
    {
        using var ctx = await NewAppAsync(bound: true);
        var r = await ctx.Client.PostAsJsonAsync("/api/v1/workspace/lsp/rename",
            new LspRenameRequest("/etc/passwd", 0, 0, "novo", null));
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ---------- SPEC-20261011-ide-bundle (Fase E): GET /repo/file-head ----------

    /// <summary>/file-head fora do jail → 400.</summary>
    [Test]
    public async Task FileHead_PathFora_400()
    {
        using var ctx = await NewAppAsync(bound: true);
        var r = await ctx.Client.GetAsync("/api/v1/workspace/repo/file-head?path=/etc/passwd");
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>/file-head com .git fake (não é raiz de repo) → 404
    /// "Sem conteúdo no HEAD" — IsRepoRootAsync bloqueia escala pro repo pai.</summary>
    [Test]
    public async Task FileHead_SemRepoReal_404()
    {
        using var ctx = await NewAppAsync(bound: true);
        File.WriteAllText(Path.Join(ctx.Workdir!, "a.txt"), "v1\n");
        var r = await ctx.Client.GetAsync("/api/v1/workspace/repo/file-head?path=a.txt");
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    /// <summary>/file-head em repo real devolve o conteúdo do HEAD — não o
    /// do working tree (é a base do diff overlay da IDE).</summary>
    [Test]
    public async Task FileHead_ComRepoReal_ConteudoDoHead()
    {
        using var ctx = await NewAppAsync(bound: true);
        await InitRepoAsync(ctx.Workdir!);
        File.WriteAllText(Path.Join(ctx.Workdir!, "a.txt"), "v1\n");
        await GitAsync(ctx.Workdir!, "add", "a.txt");
        await GitAsync(ctx.Workdir!, "-c", "user.name=t", "-c", "user.email=t@t",
            "commit", "-m", "f1");

        // working tree diverge do HEAD — /file-head deve devolver v1.
        File.WriteAllText(Path.Join(ctx.Workdir!, "a.txt"), "v2\n");
        var r = await ctx.Client.GetAsync("/api/v1/workspace/repo/file-head?path=a.txt");
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await r.Content.ReadFromJsonAsync<WorkspaceFileReadResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(body!.Path, Is.EqualTo("a.txt"));
            Assert.That(body.Content, Is.EqualTo("v1\n"));
        });
    }

    /// <summary>/file-head de arquivo não trackeado (repo real, sem commit
    /// do arquivo) → 404.</summary>
    [Test]
    public async Task FileHead_NaoTrackeado_404()
    {
        using var ctx = await NewAppAsync(bound: true);
        await InitRepoAsync(ctx.Workdir!);
        File.WriteAllText(Path.Join(ctx.Workdir!, "b.txt"), "novo\n");
        var r = await ctx.Client.GetAsync("/api/v1/workspace/repo/file-head?path=b.txt");
        Assert.That(r.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---------- fixture helpers ----------


    private sealed class Ctx : IDisposable
    {
        public Ctx(WebApplicationFactory<Program> factory, HttpClient client,
            string? workdir, string userId, string dbPath)
        {
            Factory = factory;
            Client = client;
            Workdir = workdir;
            UserId = userId;
            DbPath = dbPath;
        }

        public WebApplicationFactory<Program> Factory { get; }
        public HttpClient Client { get; }
        public string? Workdir { get; }
        public string UserId { get; }
        public string DbPath { get; }

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
            CleanupBoundWorkspace(UserId);
        }
    }

    /// <summary>App real + usuário; quando <paramref name="bound"/>, semeia binding + workdir.</summary>
    private async Task<Ctx> NewAppAsync(bool bound)
    {
        var dbPath = Path.Join(Path.GetTempPath(), $"openwebui-wf-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={dbPath}");
        var factory = new WebApplicationFactory<Program>();
        var (client, workdir, uid) = await SignUpAndBindAsync(factory, dbPath, bound);
        return new Ctx(factory, client, workdir, uid, dbPath);
    }

    private async Task<(HttpClient Client, string? Workdir, string UserId)> SignUpAndBindAsync(
        WebApplicationFactory<Program> factory, string dbPath, bool bound = true)
    {
        var client = factory.CreateClient();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var signup = await client.PostAsJsonAsync(
            "/api/v1/auths/signup", new SignUpRequest($"WF{tag}", $"wf{tag}@wf.local", "senha123"));
        Assert.That(signup.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await signup.Content.ReadAsStringAsync());
        var auth = (await signup.Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", auth.Token);

        var uid = auth.User.Id;
        string? workdir = null;
        if (bound)
        {
            const string dir = "repos/test__repo";
            workdir = Path.Join(_apiDir, "data", "workspaces", uid, dir);
            Directory.CreateDirectory(Path.Join(workdir, ".git"));
            await SeedBindingAsync(dbPath, uid, dir);
        }
        return (client, workdir, uid);
    }

    /// <summary>Semeia o kv <c>u:{uid}:workspace.repo</c> direto no SQLite da app.</summary>
    private static async Task SeedBindingAsync(string dbPath, string uid, string dir)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options);
        var json = JsonSerializer.Serialize(
            new WorkspaceRepoBinding("test/repo", "main", dir));
        db.ConfigEntries.Add(new ConfigEntry { Key = $"u:{uid}:workspace.repo", ValueJson = json });
        await db.SaveChangesAsync();
    }

    /// <summary>Remove o workdir semeado sob o content root da API (gitignored).</summary>
    private static void CleanupBoundWorkspace(string uid)
    {
        try
        {
            var dir = Path.Join(FindApiDir(), "data", "workspaces", uid);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>Resolve <c>src/OpenWebUI.Api</c> subindo a partir do bin de testes.</summary>
    private static string FindApiDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Join(dir.FullName, "src", "OpenWebUI.Api")))
        {
            dir = dir.Parent;
        }
        Assert.That(dir, Is.Not.Null, "repo root não encontrado a partir do bin de testes");
        return Path.Join(dir!.FullName, "src", "OpenWebUI.Api");
    }

    /// <summary>Captura Log* renderizados para o assert de auditoria (RF-005).</summary>
    private sealed class SpyLoggerProvider : ILoggerProvider
    {
        public List<string> Records { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Spy(Records);

        public void Dispose() { }

        private sealed class Spy(List<string> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (records)
                {
                    records.Add(formatter(state, exception));
                }
            }
        }
    }
}
