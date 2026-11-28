using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Client.Components;
using OpenWebUI.Client.Services;

namespace OpenWebUI.Client.Tests;

/// <summary>
/// SPEC-20261010-chat-activity-feed: ToolActivityMap (nome → linha amigável)
/// e RunActivityFeed (linhas live, collapse "N passos", detalhes por linha).
/// </summary>
[TestFixture]
public sealed class ChatActivityFeedTests
{
    private static BunitContext Setup()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        ctx.Services.AddSingleton(http);
        ctx.Services.AddSingleton(new LocalizationService(http, ctx.JSInterop.JSRuntime));
        return ctx;
    }

    private static ToolCallView Live(string id, string name, string? args = null,
        bool? ok = null, bool denied = false) => new()
        {
            Call = new RunToolCallEvent(id, name, args),
            Result = ok is null ? null
                : new RunToolResultEvent(id, name, ok.Value, "preview", Denied: denied),
        };

    [Test]
    public void Map_Categorias_EAlvo()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ToolActivityMap.Map("builtin:web_search", "{\"query\":\"release notes dotnet\"}"),
                Is.EqualTo(("chat.activity.web_search", "release notes dotnet")).AsCollection);
            Assert.That(ToolActivityMap.Map("builtin:file_edit", "{\"path\":\"src/x.cs\"}"),
                Is.EqualTo(("chat.activity.edit", "src/x.cs")).AsCollection);
            Assert.That(ToolActivityMap.Map("builtin:shell_exec", "{\"command\":\"ls -la\"}"),
                Is.EqualTo(("chat.activity.exec", "ls -la")).AsCollection);
            Assert.That(ToolActivityMap.Map("builtin:fetch_url", "{\"url\":\"https://ex.com/a\"}"),
                Is.EqualTo(("chat.activity.fetch", "https://ex.com/a")).AsCollection);
            Assert.That(ToolActivityMap.Map("builtin:delegate_task", null).Item1,
                Is.EqualTo("chat.activity.delegate"));
            Assert.That(ToolActivityMap.Map("builtin:file_grep", null).Item1,
                Is.EqualTo("chat.activity.search_code"));
            Assert.That(ToolActivityMap.Map("builtin:generate_image", null).Item1,
                Is.EqualTo("chat.activity.image"));
            Assert.That(ToolActivityMap.Map("builtin:memory_search", null).Item1,
                Is.EqualTo("chat.activity.memory"));
            Assert.That(ToolActivityMap.Map("builtin:ask_user", null).Item1,
                Is.EqualTo("chat.activity.tool"));
            Assert.That(ToolActivityMap.Map("mcp:github/create_issue", null).Item1,
                Is.EqualTo("chat.activity.tool"));
        });
    }

    [Test]
    public void Map_AlvoTruncado_Em40Chars()
    {
        var longQuery = new string('q', 60);
        var (_, target) = ToolActivityMap.Map(
            "builtin:web_search", $"{{\"query\":\"{longQuery}\"}}");
        Assert.That(target, Has.Length.EqualTo(41)); // 40 + reticência
    }

    [Test]
    public void Feed_Live_LinhaAmigavel_ESpinner()
    {
        using var ctx = Setup();
        var calls = new List<ToolCallView>
        {
            Live("1", "builtin:web_search", "{\"query\":\"dotnet 10\"}"),
            Live("2", "builtin:file_edit", "{\"path\":\"a.cs\"}", ok: true),
        };
        var cut = ctx.Render<RunActivityFeed>(p => p
            .Add(c => c.Calls, calls)
            .Add(c => c.Streaming, true)
            .Add(c => c.Collapsed, false));

        Assert.Multiple(() =>
        {
            // Linhas amigáveis + painel auto-aberto da call em execução
            // (padrão Devin web: nome da tool e args visíveis enquanto roda).
            Assert.That(cut.Markup, Does.Contain("chat.activity.web_search"));
            Assert.That(cut.Markup, Does.Contain("dotnet 10"));
            // 1 spinner na linha + 1 no painel auto-aberto da call rodando.
            Assert.That(cut.FindAll("svg.animate-spin"), Has.Count.EqualTo(2));
            Assert.That(cut.Find("div[role='log']"), Is.Not.Null);
        });
    }

    [Test]
    public void Feed_Collapsed_MostraNPassos_EExpande()
    {
        using var ctx = Setup();
        var calls = new List<ToolCallView>
        {
            Live("1", "builtin:web_search", null, ok: true),
            Live("2", "builtin:file_edit", null, ok: true),
            Live("3", "builtin:shell_exec", null, ok: false, denied: true),
        };
        var cut = ctx.Render<RunActivityFeed>(p => p
            .Add(c => c.Calls, calls)
            .Add(c => c.Streaming, false)
            .Add(c => c.Collapsed, true));

        // Colapsado: só o resumo; marca âmbar do denied.
        Assert.That(cut.Markup, Does.Contain("chat.worklog.steps"));
        Assert.That(cut.Markup, Does.Not.Contain("chat.activity.web_search"));
        Assert.That(cut.FindAll("span.bg-amber-500"), Has.Count.EqualTo(1));

        cut.Find("button[aria-expanded]").Click();
        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("chat.activity.web_search"));
            Assert.That(cut.Markup, Does.Contain("chat.activity.exec"));
        });
    }

    [Test]
    public void Feed_LinhaExpande_DetalhesDoCard()
    {
        using var ctx = Setup();
        var calls = new List<ToolCallView>
        {
            Live("1", "builtin:file_edit", "{\"path\":\"a.cs\"}", ok: true),
        };
        var cut = ctx.Render<RunActivityFeed>(p => p
            .Add(c => c.Calls, calls)
            .Add(c => c.Streaming, false)
            .Add(c => c.Collapsed, false));

        // Nome da tool só aparece dentro dos detalhes (clique na linha).
        Assert.That(cut.Markup, Does.Not.Contain("builtin:file_edit"));
        cut.Find("button[aria-expanded]").Click();
        Assert.That(cut.Markup, Does.Contain("builtin:file_edit"));
        // segundo clique recolhe
        cut.Find("button[aria-expanded]").Click();
        Assert.That(cut.Markup, Does.Not.Contain("builtin:file_edit"));
    }

    [Test]
    public void Feed_Rodando_AbrePainelDaCall_ECliqueEsconde()
    {
        // Padrão Devin web: a call em execução mostra o painel
        // automaticamente; clique na linha esconde enquanto roda.
        using var ctx = Setup();
        var calls = new List<ToolCallView>
        {
            Live("1", "builtin:shell_exec", "{\"command\":\"ls -la\"}"),
            Live("2", "builtin:web_search", "{\"query\":\"x\"}", ok: true),
        };
        var cut = ctx.Render<RunActivityFeed>(p => p
            .Add(c => c.Calls, calls)
            .Add(c => c.Streaming, true)
            .Add(c => c.Collapsed, false));

        // Painel do shell aberto sozinho; web_search concluída fica só na linha.
        Assert.That(cut.FindAll("[data-testid='tool-terminal-output'], .bg-gray-950"),
            Has.Count.EqualTo(1));
        Assert.That(cut.Markup, Does.Contain("ls -la"));

        // Clique na linha em execução esconde o painel.
        cut.FindAll("button[aria-expanded]")[0].Click();
        Assert.That(cut.FindAll(".bg-gray-950"), Has.Count.EqualTo(0));
    }

    [Test]
    public void Persisted_Delegate_ReidrataChildChatId()
    {
        // Histórico: delegate_task grava "(chat filho: /c/{id})" no texto —
        // FromPersisted reidrata o payload pro painel da sub-conversa.
        var msg = new ChatMessageModel("m1", "tool",
            "Subtarefa delegada em background — run filha r1 (chat filho: /c/abc123). " +
            "Chame builtin_run_result com run_id=r1 para colher o resultado.",
            null, 0, ToolCallId: "builtin:delegate_task");

        var view = ToolCallView.FromPersisted(msg);
        Assert.Multiple(() =>
        {
            Assert.That(view.Result?.Result?.ValueKind,
                Is.EqualTo(JsonValueKind.Object));
            Assert.That(view.Result!.Result!.Value
                .GetProperty("childChatId").GetString(), Is.EqualTo("abc123"));
        });

        // Tool comum não produz payload estruturado.
        var other = ToolCallView.FromPersisted(
            new ChatMessageModel("m2", "tool", "ok", null, 0,
                ToolCallId: "builtin:web_search"));
        Assert.That(other.Result?.Result, Is.Null);
    }


    [Test]
    public void Card_OutputLongo_CabecalhoResumidoPorTipoECollapse()
    {
        using var ctx = Setup();
        var output = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"linha {i}"));
        var cut = ctx.Render<ToolCallCard>(p => p
            .Add(c => c.Name, "builtin:web_search")
            .Add(c => c.ArgsPreview, "{\"query\":\"dotnet\"}")
            .Add(c => c.ResultPreview, output)
            .Add(c => c.Ok, true));

        cut.Find("button[aria-expanded]").Click();
        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("tool-output-summary"));
            Assert.That(cut.Markup, Does.Contain("12"));
            Assert.That(cut.Markup, Does.Not.Contain("linha 12"));
        });
        cut.Find("[data-testid='tool-output-toggle']").Click();
        Assert.That(cut.Markup, Does.Contain("linha 12"));
    }

    [Test]
    public void Card_OutputDiff_ResumoContaMaisMenos()
    {
        using var ctx = Setup();
        var diff = "diff --git a/a.cs b/a.cs\n@@ -1 +1 @@\n-a\n+b\n+c";
        var cut = ctx.Render<ToolCallCard>(p => p
            .Add(c => c.Name, "builtin:file_edit")
            .Add(c => c.ResultPreview, diff)
            .Add(c => c.Ok, true));

        cut.Find("button[aria-expanded]").Click();
        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("tool-output-summary"));
            Assert.That(cut.Markup, Does.Contain("data-output-kind=\"diff +2"));
            Assert.That(cut.Markup, Does.Contain("/−1"));
        });
    }

    [Test]
    public async Task Card_Shell_AutoAbreRodando_ColapsaAoTerminar()
    {
        using var ctx = Setup();
        var cut = ctx.Render<ToolCallCard>(p => p
            .Add(c => c.Name, "builtin:shell_exec")
            .Add(c => c.ArgsPreview, "{\"command\":\"dotnet test\"}")
            .Add(c => c.Running, true));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Find("button[aria-expanded]")
                .GetAttribute("aria-expanded"), Is.EqualTo("true"));
            Assert.That(cut.Markup, Does.Contain("bg-gray-950"));
            Assert.That(cut.Markup, Does.Contain("dotnet test"));
        });

        // Resultado chegou → painel colapsa sozinho (linha continua clicável).
        await cut.InvokeAsync(() => cut.Instance.SetParametersAsync(
            Microsoft.AspNetCore.Components.ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    ["Running"] = false,
                    ["ResultPreview"] = "saida do comando",
                    ["Ok"] = true,
                })));
        Assert.Multiple(() =>
        {
            Assert.That(cut.Find("button[aria-expanded]")
                .GetAttribute("aria-expanded"), Is.EqualTo("false"));
            Assert.That(cut.Markup, Does.Not.Contain("bg-gray-950"));
        });

        // Clique reabre mostrando a saída no terminal com scroll.
        cut.Find("button[aria-expanded]").Click();
        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("bg-gray-950"));
            Assert.That(cut.Markup, Does.Contain("saida do comando"));
            Assert.That(cut.Markup, Does.Contain("max-h-60 overflow-y-auto"));
        });

        // Segundo clique fecha de novo — escolha do usuário persiste.
        cut.Find("button[aria-expanded]").Click();
        Assert.That(cut.Markup, Does.Not.Contain("bg-gray-950"));
    }
}
