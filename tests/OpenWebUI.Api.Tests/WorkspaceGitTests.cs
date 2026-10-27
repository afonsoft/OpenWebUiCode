using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// Cobertura do RF-018 (git bar / snapshot git do workspace):
/// <see cref="WorkspaceGitService"/> num workdir temporário e o endpoint
/// <c>GET /api/v1/chats/{id}/runs/{runId}/diff</c> com repo real criado
/// dentro de <c>data/workspaces/{userId}</c>.
/// </summary>
[TestFixture, IsolateEnvironment]
public class WorkspaceGitTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private string _dbPath = null!;
    private string _contentRoot = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"openwebui-git-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={_dbPath}");
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
        _contentRoot = _factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;

        var admin = await SignUpAsync("Admin", "admin@git.local");
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

    // ---------------- WorkspaceGitService ----------------

    [Test]
    public async Task Servico_SemRepo_RetornaGitFalse()
    {
        var dir = Path.Join(Path.GetTempPath(), $"owui-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var info = await new WorkspaceGitService().GetInfoAsync(dir, CancellationToken.None);
        Assert.That(info.IsRepo, Is.False);
    }

    [Test]
    public async Task Servico_ComRepo_RetornaBranchNumstatEUntracked()
    {
        RequireGit();
        var dir = Path.Join(Path.GetTempPath(), $"owui-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Git(dir, "init");
            File.WriteAllText(Path.Join(dir, "a.txt"), "linha1\n");
            Git(dir, "-c", "user.email=t@t", "-c", "user.name=t", "add", ".");
            Git(dir, "-c", "user.email=t@t", "-c", "user.name=t",
                "commit", "-m", "base");

            File.WriteAllText(Path.Join(dir, "a.txt"), "linha1\nlinha2\n");
            File.WriteAllText(Path.Join(dir, "novo.txt"), "n1\nn2\nn3\n");

            var info = await new WorkspaceGitService()
                .GetInfoAsync(dir, CancellationToken.None);

            Assert.That(info.IsRepo, Is.True);
            Assert.That(info.Branch, Is.Not.Null.And.Not.Empty);
            var tracked = info.Files.First(f => f.Path == "a.txt");
            Assert.That(tracked.Status, Is.EqualTo("M"));
            Assert.That(tracked.Added, Is.EqualTo(1));
            var untracked = info.Files.First(f => f.Path == "novo.txt");
            Assert.That(untracked.Status, Is.EqualTo("A"));
            Assert.That(untracked.Added, Is.EqualTo(3));
            Assert.That(info.Added, Is.EqualTo(4));
            Assert.That(info.Diff, Does.Contain("+linha2"));
            Assert.That(info.DiffTruncated, Is.False);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------- SPEC-20261017-workspace-changes-run-diff ----------------

    /// <summary>
    /// RF-001 (o bug reportado): agente commita na branch da run e deixa um
    /// arquivo untracked — a aba Changes deve listar TODOS (commitados +
    /// untracked), não só o untracked.
    /// </summary>
    [Test]
    public async Task Servico_CommitsNaBranch_ListaCommitadosEUntracked()
    {
        RequireGit();
        var dir = Path.Join(Path.GetTempPath(), $"owui-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Git(dir, "init");
            File.WriteAllText(Path.Join(dir, "a.txt"), "linha1\n");
            File.WriteAllText(Path.Join(dir, "velho.txt"), "v\n");
            CommitAll(dir, "base");
            var baseBranch = GitOut(dir, "branch", "--show-current");

            Git(dir, "checkout", "-b", "agent-run");
            File.WriteAllText(Path.Join(dir, "a.txt"), "linha1\nlinha2\n");
            File.WriteAllText(Path.Join(dir, "b.txt"), "b1\nb2\n");
            File.Delete(Path.Join(dir, "velho.txt"));
            CommitAll(dir, "trabalho do agente");

            File.WriteAllText(Path.Join(dir, "novo.spec.md"), "s1\ns2\ns3\n");

            var info = await new WorkspaceGitService()
                .GetInfoAsync(dir, CancellationToken.None, baseRef: baseBranch);

            Assert.That(info.IsRepo, Is.True);
            var paths = info.Files.Select(f => f.Path).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(paths, Does.Contain("a.txt"), "modificação commitada");
                Assert.That(paths, Does.Contain("b.txt"), "arquivo novo commitado");
                Assert.That(paths, Does.Contain("velho.txt"), "deleção commitada");
                Assert.That(paths, Does.Contain("novo.spec.md"), "untracked pós-commit");
            });
            Assert.That(info.Files.First(f => f.Path == "a.txt").Status, Is.EqualTo("M"));
            Assert.That(info.Files.First(f => f.Path == "velho.txt").Status, Is.EqualTo("D"));
            Assert.That(info.Files.First(f => f.Path == "novo.spec.md").Status, Is.EqualTo("A"));
            Assert.That(info.Files.First(f => f.Path == "novo.spec.md").Added, Is.EqualTo(3));
            Assert.Multiple(() =>
            {
                Assert.That(info.Diff, Does.Contain("+linha2"), "diff cobre commit do agente");
                Assert.That(info.Diff, Does.Contain("+s1"), "diff cobre conteúdo do untracked");
                Assert.That(info.BaseRef, Is.EqualTo(baseBranch));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>RF-002: sem baseRef/base inexistente → cai no diff vs HEAD (regressão zero).</summary>
    [Test]
    public async Task Servico_SemBaseRef_CaiNoHead()
    {
        RequireGit();
        var dir = Path.Join(Path.GetTempPath(), $"owui-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Git(dir, "init");
            File.WriteAllText(Path.Join(dir, "a.txt"), "linha1\n");
            CommitAll(dir, "base");

            File.WriteAllText(Path.Join(dir, "a.txt"), "linha1\nlinha2\n");
            CommitAll(dir, "agente commita");
            File.WriteAllText(Path.Join(dir, "sujo.txt"), "x\n");

            var info = await new WorkspaceGitService()
                .GetInfoAsync(dir, CancellationToken.None, baseRef: "nao-existe");

            Assert.That(info.IsRepo, Is.True);
            Assert.That(info.BaseSha, Is.EqualTo("HEAD"));
            // vs HEAD: só o untracked aparece (comportamento antigo preservado).
            Assert.That(info.Files.Select(f => f.Path), Is.EqualTo(new[] { "sujo.txt" }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>RF-003: falha/fluxo normal — index pré-existente é restaurado após o intent-to-add.</summary>
    [Test]
    public async Task Servico_IndexPreExistente_EhRestaurado()
    {
        RequireGit();
        var dir = Path.Join(Path.GetTempPath(), $"owui-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Git(dir, "init");
            File.WriteAllText(Path.Join(dir, "a.txt"), "linha1\n");
            CommitAll(dir, "base");
            var baseBranch = GitOut(dir, "branch", "--show-current");

            Git(dir, "checkout", "-b", "run");
            File.WriteAllText(Path.Join(dir, "staged.txt"), "staged\n");
            Git(dir, "add", "staged.txt");          // index sujo ANTES do snapshot
            File.WriteAllText(Path.Join(dir, "novo.txt"), "n\n"); // untracked

            _ = await new WorkspaceGitService()
                .GetInfoAsync(dir, CancellationToken.None, baseRef: baseBranch);

            var stillStaged = GitOut(dir, "diff", "--cached", "--name-only");
            Assert.That(stillStaged, Does.Contain("staged.txt"),
                "index pré-existente deve ser restaurado após o intent-to-add");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void CommitAll(string dir, string message)
    {
        Git(dir, "-c", "user.email=t@t", "-c", "user.name=t", "add", "-A");
        Git(dir, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", message);
    }

    private static string GitOut(string workdir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        using var process = Process.Start(psi);
        var output = process!.StandardOutput.ReadToEnd();
        process.WaitForExit(15000);
        return output.Trim();
    }

    // ---------------- Endpoint /runs/{id}/diff ----------------

    [Test]
    public async Task Diff_RunInexistente_404()
    {
        var auth = await SignUpAsync("G404", "g404@git.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();
        var response = await _client.GetAsync($"/api/v1/chats/{chat.Id}/runs/{Guid.NewGuid()}/diff");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Diff_SemRepo_RetornaGitFalse()
    {
        var auth = await SignUpAsync("GNR", "gnr@git.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();
        var run = await EnfileirarAsync(chat.Id, "oi");

        var info = await _client.GetFromJsonAsync<WorkspaceGitResponse>(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/diff");
        Assert.That(info, Is.Not.Null);
        Assert.That(info!.Git, Is.False);
    }

    [Test]
    public async Task Diff_ComRepo_RetornaBranchEArquivos()
    {
        RequireGit();
        var auth = await SignUpAsync("GREPO", "grepo@git.local");
        UseToken(auth.Token);
        var chat = await CriarChatAsync();
        var run = await EnfileirarAsync(chat.Id, "oi");

        var workdir = Path.Join(
            _contentRoot, "data", "workspaces", auth.User.Id, "scratch", chat.Id);
        Directory.CreateDirectory(workdir);
        try
        {
            Git(workdir, "init");
            File.WriteAllText(Path.Join(workdir, "readme.md"), "oi\n");
            Git(workdir, "-c", "user.email=t@t", "-c", "user.name=t", "add", ".");
            Git(workdir, "-c", "user.email=t@t", "-c", "user.name=t",
                "commit", "-m", "base");
            File.WriteAllText(Path.Join(workdir, "readme.md"), "oi\nmundo\n");

            var info = await _client.GetFromJsonAsync<WorkspaceGitResponse>(
                $"/api/v1/chats/{chat.Id}/runs/{run.Id}/diff");
            Assert.That(info, Is.Not.Null);
            Assert.That(info!.Git, Is.True);
            Assert.That(info.Branch, Is.Not.Null.And.Not.Empty);
            Assert.That(info.Files.Select(f => f.Path), Does.Contain("readme.md"));
            Assert.That(info.Added, Is.EqualTo(1));
            Assert.That(info.Diff, Does.Contain("+mundo"));
        }
        finally
        {
            Directory.Delete(workdir, recursive: true);
        }
    }

    [Test]
    public async Task Diff_DeOutroUsuario_404()
    {
        var dono = await SignUpAsync("GOWN", "gown@git.local");
        UseToken(dono.Token);
        var chat = await CriarChatAsync();
        var run = await EnfileirarAsync(chat.Id, "oi");

        var intruso = await SignUpAsync("GINT", "gint@git.local");
        UseToken(intruso.Token);
        var response = await _client.GetAsync(
            $"/api/v1/chats/{chat.Id}/runs/{run.Id}/diff");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---------------- helpers ----------------

    private static void RequireGit()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            p!.WaitForExit(5000);
            if (p.ExitCode != 0)
            {
                Assert.Ignore("git indisponível neste ambiente.");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Ignore("git indisponível neste ambiente.");
        }
        catch (InvalidOperationException)
        {
            Assert.Ignore("git indisponível neste ambiente.");
        }
    }

    private static void Git(string workdir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        process!.WaitForExit(15000);
        Assert.That(process.ExitCode, Is.EqualTo(0),
            $"git {string.Join(' ', args)} falhou: {process.StandardError.ReadToEnd()}");
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
            "/api/v1/chats/", new ChatUpsertRequest("Chat git", ["llama3"], []));
        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<ChatResponse>())!;
    }

    private async Task<ChatRunResponse> EnfileirarAsync(string chatId, string content)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/chats/{chatId}/messages",
            new EnqueueChatRunRequest(content, "llama3"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted),
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ChatRunResponse>())!;
    }
}
