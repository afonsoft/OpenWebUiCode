using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.ChatTools.Tools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Tests;

/// <summary>
/// SPEC-20261031-tool-search-deferred: split opt-in (RF-001/RF-005),
/// índice leve (RF-002), tool_search (RF-003), merge por-run (RF-004) e
/// invariantes — chamada direta em deferida bloqueia; busca nunca expõe
/// tools fora do permitido.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ToolSearchTests
{
    private static Tool BuiltinTool(string name, string desc = "") => new()
    {
        Id = $"builtin:{name}",
        Name = name,
        Description = desc,
        Url = $"builtin://{name}",
        SpecJson = "{\"type\":\"function\",\"function\":{\"name\":\"builtin_" + name
            + "\",\"description\":\"" + desc
            + "\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}}",
    };

    private static Tool McpTool(string server, string name) => new()
    {
        Id = $"mcp:{server}:{name}",
        Name = name,
        Description = $"MCP {name}",
        Url = $"mcp://{server}/{name}",
        SpecJson = "{\"type\":\"function\",\"function\":{\"name\":\"mcp__" + server
            + "__" + name + "\",\"description\":\"" + name
            + "\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}}",
    };

    private static ToolIndexService IndexWith(params (string K, string V)[] kv)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(kv
                .Select(p => new KeyValuePair<string, string?>(p.K, p.V)))
            .Build();
        return new ToolIndexService(cfg);
    }

    private static AppDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"toolsearch-{Guid.NewGuid():N}")
            .Options;
        var db = new AppDbContext(opts);
        db.Database.EnsureCreated();
        return db;
    }

    [Test]
    public void Split_Optin_VazioNaoDefereNada()
    {
        var index = IndexWith();
        var tools = new List<Tool>
            { BuiltinTool("file_read"), BuiltinTool("file_write") };
        Assert.That(index.SplitDeferred(tools), Is.Empty);
    }

    [Test]
    public void Split_CsvDefereNomeados_NuncaLoaderOuModo()
    {
        var index = IndexWith(("BuiltinTools:Deferred",
            "file_read, builtin:file_grep, builtin_tool_search, plan_exit"));
        var tools = new List<Tool>
        {
            BuiltinTool("file_read"), BuiltinTool("file_grep"),
            BuiltinTool("file_write"), BuiltinTool("tool_search"),
            BuiltinTool("plan_exit"), BuiltinTool("run_result"),
        };
        var deferred = index.SplitDeferred(tools);
        Assert.Multiple(() =>
        {
            Assert.That(deferred.Select(t => t.Name),
                Is.EquivalentTo(new[] { "file_read", "file_grep" }));
            Assert.That(deferred.Any(t => t.Name == "tool_search"
                || t.Name == "plan_exit" || t.Name == "run_result"),
                Is.False, "loader/modo/result nunca deferidos");
        });
    }

    [Test]
    public void Split_McpAcimaDoThreshold_DefereForaDoCore()
    {
        var index = IndexWith(
            ("BuiltinTools:McpDeferThreshold", "2"),
            ("BuiltinTools:McpCore", "core1"));
        var tools = new List<Tool>
        {
            McpTool("core1", "a"), McpTool("s2", "b"), McpTool("s3", "c"),
            BuiltinTool("file_read"),
        };
        var deferred = index.SplitDeferred(tools);
        Assert.Multiple(() =>
        {
            Assert.That(deferred, Has.Count.EqualTo(2));
            Assert.That(deferred.All(t => t.Url.StartsWith("mcp://s")),
                Is.True, "servidores s2/s3 deferidos; core1 e builtins ficam");
        });
    }

    [Test]
    public void Index_BlocoLeve_ComCapEMaisN()
    {
        var index = IndexWith();
        var many = Enumerable.Range(0, 70)
            .Select(i => BuiltinTool($"tool_{i}", $"desc {i}")).ToList();
        var block = index.BuildIndex(many);
        Assert.Multiple(() =>
        {
            Assert.That(block, Does.Contain("## Available on demand"));
            Assert.That(block, Does.Contain("builtin_tool_0: desc 0"));
            Assert.That(block, Does.Contain("…and 10 more"));
            Assert.That(index.BuildIndex([]), Is.Null);
        });
    }

    [Test]
    public async Task ToolSearch_CarregaMatch_EExcluiJaCarregadas()
    {
        var db = NewDb();
        var state = new DeferredToolState();
        var index = IndexWith();
        var events = new RunEventWriter(db,
            NullLogger<RunEventWriter>.Instance);
        var tool = new ToolSearchBuiltinTool(state, index, events);

        state.Set("run1", "chat1",
            new[] { BuiltinTool("file_read", "read files"),
                BuiltinTool("file_grep", "search files") });

        var args = JsonDocument.Parse("""{"query":"file","limit":5}""")
            .RootElement;
        var ctx = new BuiltinToolContext("u1", "chat1", "run1", "/tmp", "/tmp");
        var result = await tool.ExecuteAsync(args, ctx, default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Does.Contain("builtin_file_read"));
            Assert.That(result.Text, Does.Contain("builtin_file_grep"));
            Assert.That(state.Deferred("run1"), Is.Empty,
                "ambas promovidas a loaded");
            Assert.That(state.Loaded("run1"), Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task ToolSearch_SemMatch_InformativeSemCarregar()
    {
        var db = NewDb();
        var state = new DeferredToolState();
        var index = IndexWith();
        var events = new RunEventWriter(db,
            NullLogger<RunEventWriter>.Instance);
        var tool = new ToolSearchBuiltinTool(state, index, events);
        state.Set("run1", null, new[] { BuiltinTool("file_read") });

        var args = JsonDocument.Parse("""{"query":"zzz"}""").RootElement;
        var ctx = new BuiltinToolContext("u1", null, "run1", "/tmp", "/tmp");
        var result = await tool.ExecuteAsync(args, ctx, default);

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Does.Contain("No deferred tools match"));
            Assert.That(state.Deferred("run1"), Has.Count.EqualTo(1),
                "não carrega nada sem match");
        });
    }

    [Test]
    public async Task ToolSearch_SemDeferral_AvisaQueTudoJaEstaCore()
    {
        var db = NewDb();
        var state = new DeferredToolState();
        var events = new RunEventWriter(db,
            NullLogger<RunEventWriter>.Instance);
        var tool = new ToolSearchBuiltinTool(state, IndexWith(), events);
        var args = JsonDocument.Parse("""{"query":"file"}""").RootElement;
        var ctx = new BuiltinToolContext("u1", null, "runX", "/tmp", "/tmp");
        var result = await tool.ExecuteAsync(args, ctx, default);
        Assert.That(result.Text, Does.Contain("No deferred tools"));
    }

    [Test]
    public void State_GateDeferida_LoadEclearPorRun()
    {
        var state = new DeferredToolState();
        var read = BuiltinTool("file_read");
        state.Set("r1", "c1", new[] { read });

        Assert.Multiple(() =>
        {
            Assert.That(state.IsDeferredOnly("r1", "builtin_file_read"), Is.True);
            Assert.That(state.IsDeferredOnly("r1", read), Is.True);
            Assert.That(state.IsDeferredOnly("r2", "builtin_file_read"),
                Is.False, "escopo é por run");
            Assert.That(state.IsDeferredOnly("c1", "builtin_file_read"),
                Is.True, "alias por chatId (contributor do prompt)");
        });

        Assert.That(state.Load("r1", "builtin_file_read"), Is.SameAs(read));
        Assert.That(state.IsDeferredOnly("r1", "builtin_file_read"), Is.False);
        Assert.That(state.Load("r1", "builtin_file_read"), Is.Null,
            "segunda carga é no-op");

        state.Clear("r1", "c1");
        Assert.That(state.IsActive("r1"), Is.False);
        Assert.That(state.IsActive("c1"), Is.False);
    }
}
