using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// <see cref="RepoCommandService"/> (SPEC-20261016-commands-v2): resolução de
/// commands no workdir, fallback <c>agent:</c>→mode, validação de
/// <c>mode:</c>/<c>model:</c>, <c>subagent:</c> e shell-block opt-in.
/// </summary>
[TestFixture]
public class RepoCommandServiceTests
{
    private string _root = null!;
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), $"owui-cmd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Join(_root, "test.db");
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            TestContext.Out.WriteLine($"cleanup do workdir falhou: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            TestContext.Out.WriteLine($"cleanup do workdir falhou: {ex.Message}");
        }
    }

    private RepoCommandService NewSvc(
        IConfiguration? cfg = null, IHttpClientFactory? factory = null)
    {
        cfg ??= new ConfigurationBuilder().Build();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        var config = new ConfigService(db, TestCache.Create());
        var providers = new ProviderService(
            factory ?? new StubFactory(), config,
            NullLogger<ProviderService>.Instance, TestCache.Create(), cfg);
        var agents = new AgentDefinitionService(
            new StubEnv(_root), config,
            NullLogger<AgentDefinitionService>.Instance);
        return new RepoCommandService(
            new SkillDiscoveryService(), agents, providers, cfg);
    }

    private async Task SetConnectionsAsync(
        IReadOnlyList<string> ollama, IReadOnlyList<string> openAi,
        IReadOnlyList<string> keys)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        var config = new ConfigService(db, TestCache.Create());
        await config.SetAsync("connections",
            new ConnectionsConfig(ollama, openAi, keys));
    }

    private void WriteCmd(string name, string frontmatter, string body)
    {
        var dir = Path.Join(_root, ".agents", "commands");
        Directory.CreateDirectory(dir);
        // Frontmatter vazio ("---\n\n---") não é parseado — fica no body.
        var text = string.IsNullOrWhiteSpace(frontmatter)
            ? body
            : $"---\n{frontmatter}---\n{body}";
        File.WriteAllText(Path.Join(dir, $"{name}.md"), text);
    }

    private void WriteAgent(string name)
    {
        var dir = Path.Join(_root, ".agents", "agents");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, $"{name}.agent.md"),
            $"---\nname: {name}\ndescription: agente de teste\n---\nPrompt do {name}.");
    }

    [Test]
    public async Task Expand_ComandoInexistente_RetornaNullSemErro()
    {
        var (result, error) = await NewSvc()
            .ExpandAsync("u1", _root, "zz-ausente", null, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null);
            Assert.That(error, Is.Null);
        });
    }

    [Test]
    public async Task Expand_TemplateArguments_SubstituiETrazEntry()
    {
        WriteCmd("zz-run", "description: roda coisas\n", "Execute $ARGUMENTS agora");
        var (result, error) = await NewSvc()
            .ExpandAsync("u1", _root, "zz-run", "a b c", CancellationToken.None);
        Assert.That(error, Is.Null);
        var r = result!;
        Assert.Multiple(() =>
        {
            Assert.That(r.Prompt, Is.EqualTo("Execute a b c agora"));
            Assert.That(r.Entry.Name, Is.EqualTo("zz-run"));
            Assert.That(r.Entry.Description, Is.EqualTo("roda coisas"));
            Assert.That(r.Agent, Is.Null);
            Assert.That(r.Mode, Is.Null);
            Assert.That(r.Model, Is.Null);
            Assert.That(r.Subagent, Is.False);
        });
    }

    [Test]
    public async Task Expand_SemPlaceholder_AppendArgsAposLinhaEmBranco()
    {
        WriteCmd("zz-fix", "", "Corpo fixo  ");
        var (result, error) = await NewSvc()
            .ExpandAsync("u1", _root, "zz-fix", "x y", CancellationToken.None);
        Assert.That(error, Is.Null);
        Assert.That(result!.Prompt, Is.EqualTo("Corpo fixo\n\nx y"));
    }

    [Test]
    public async Task Expand_AgentModoLiteral_ViraMode()
    {
        WriteCmd("zz-plan", "agent: plan\n", "Planeje $ARGUMENTS");
        var (result, error) = await NewSvc()
            .ExpandAsync("u1", _root, "zz-plan", "isto", CancellationToken.None);
        Assert.That(error, Is.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Mode, Is.EqualTo("plan"));
            Assert.That(result.Agent, Is.Null);
        });
    }

    [Test]
    public async Task Expand_AgentExistente_MantemAgent()
    {
        WriteAgent("zz-helper");
        WriteCmd("zz-withagent", "agent: zz-helper\n", "Rode $ARGUMENTS");
        // Agents de workspace ficam atrás de opt-in por usuário.
        await using (var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={_dbPath}").Options))
        {
            db.Database.EnsureCreated();
            await new ConfigService(db, TestCache.Create())
                .SetAsync("u:u1:agents.workspace.enabled", true);
        }
        var (result, error) = await NewSvc()
            .ExpandAsync("u1", _root, "zz-withagent", "t", CancellationToken.None);
        Assert.That(error, Is.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Agent, Is.EqualTo("zz-helper"));
            Assert.That(result.Mode, Is.Null);
        });
    }

    [Test]
    public async Task Expand_AgentDesconhecido_Erro()
    {
        WriteCmd("zz-badagent", "agent: zz-nao-existe\n", "corpo");
        var (result, error) = await NewSvc()
            .ExpandAsync("u1", _root, "zz-badagent", null, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null);
            Assert.That(error, Does.Contain("zz-nao-existe"));
        });
    }

    [Test]
    public async Task Expand_ModeInvalido_Erro()
    {
        WriteCmd("zz-badmode", "mode: bogus\n", "corpo");
        var (result, error) = await NewSvc()
            .ExpandAsync("u1", _root, "zz-badmode", null, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null);
            Assert.That(error, Does.Contain("bogus"));
        });
    }

    [Test]
    public async Task Expand_ModelSemConexoes_Indisponivel()
    {
        // Sem conexões configuradas, ListModelsAsync devolve vazio → qualquer
        // model declarado é "indisponível".
        WriteCmd("zz-model", "model: zz-nope\n", "corpo");
        var (result, error) = await NewSvc()
            .ExpandAsync("u1", _root, "zz-model", null, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Null);
            Assert.That(error, Does.Contain("zz-nope"));
        });
    }

    [Test]
    public async Task Expand_ModelDisponivel_Ok()
    {
        await SetConnectionsAsync([], ["http://mock.local"], ["sk-k"]);
        WriteCmd("zz-modelok", "model: gpt-1\n", "corpo");
        var (result, error) = await NewSvc(factory: new StubFactory(
            "{\"data\":[{\"id\":\"gpt-1\"}]}"))
            .ExpandAsync("u1", _root, "zz-modelok", null, CancellationToken.None);
        Assert.That(error, Is.Null);
        Assert.That(result!.Model, Is.EqualTo("gpt-1"));
    }

    [Test]
    public async Task Expand_SubagentTrue_MarcaSubagent()
    {
        WriteCmd("zz-sub", "subagent: true\n", "corpo");
        WriteCmd("zz-sub2", "subtask: agent\n", "corpo");
        var (r1, e1) = await NewSvc()
            .ExpandAsync("u1", _root, "zz-sub", null, CancellationToken.None);
        var (r2, e2) = await NewSvc()
            .ExpandAsync("u1", _root, "zz-sub2", null, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(e1, Is.Null);
            Assert.That(e2, Is.Null);
            Assert.That(r1!.Subagent, Is.True);
            Assert.That(r2!.Subagent, Is.True);
        });
    }

    [Test]
    public async Task Expand_ShellBlock_SemOptIn_OmiteBloco()
    {
        WriteCmd("zz-shell", "", "antes !`echo segredo` depois");
        var svc = NewSvc();
        Assert.That(svc.ShellExpansionEnabled, Is.False);
        var (result, error) = await svc
            .ExpandAsync("u1", _root, "zz-shell", null, CancellationToken.None);
        Assert.That(error, Is.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Prompt,
                Does.Contain(CommandExpansion.ShellDisabledLiteral));
            Assert.That(result.Prompt, Does.Not.Contain("segredo"));
            Assert.That(result.Prompt, Does.Not.Contain("<shell-output"));
        });
    }

    [Test]
    public async Task Expand_ShellBlock_ComOptIn_Executa()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RepoCommands:AllowShellExpansion"] = "true",
            })
            .Build();
        WriteCmd("zz-sh", "", "saida: !`echo oi-marcado` fim");
        var svc = NewSvc(cfg);
        Assert.That(svc.ShellExpansionEnabled, Is.True);
        var (result, error) = await svc
            .ExpandAsync("u1", _root, "zz-sh", null, CancellationToken.None);
        Assert.That(error, Is.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Prompt, Does.Contain("<shell-output"));
            Assert.That(result.Prompt, Does.Contain("oi-marcado"));
        });
    }

    private sealed class StubEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private sealed class StubFactory(string? body = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StubHandler(body));
    }

    private sealed class StubHandler(string? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    body ?? "{}", Encoding.UTF8, "application/json"),
            });
    }
}
