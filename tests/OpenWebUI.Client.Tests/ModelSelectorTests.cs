using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OpenWebUI.Client.Components;
using OpenWebUI.Client.Services;

namespace OpenWebUI.Client.Tests;

/// <summary>
/// Regressão do bug reportado: ao trocar de chat e voltar, o seletor de
/// modelo voltava para o primeiro do catálogo (ou para o último pick
/// global do localStorage) em vez do modelo salvo no próprio chat.
/// O default do storage só pode preencher o seletor quando o pai ainda
/// não forneceu um Value (chat novo).
/// </summary>
[TestFixture]
public sealed class ModelSelectorTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> route)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(route(request));
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(body,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            Encoding.UTF8, "application/json"),
    };

    private static BunitContext Setup()
    {
        var ctx = new BunitContext();
        // Loose: localStorage.getItem devolve null — simula storage vazio,
        // o caminho que derrubava o seletor para o primeiro modelo.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var http = new HttpClient(new FakeHandler(req =>
            req.RequestUri!.AbsolutePath == "/api/models"
                ? Json(new
                {
                    data = new object[]
                    {
                        new { id = "auto/glm", name = "GLM", provider = "openai", ownedBy = "omniroute" },
                        new { id = "auto/deepseek", name = "DeepSeek", provider = "openai", ownedBy = "omniroute" },
                    },
                })
                : new HttpResponseMessage(HttpStatusCode.NotFound)))
        { BaseAddress = new Uri("http://localhost/") };

        var l10n = new LocalizationService(http, ctx.JSInterop.JSRuntime);
        var auth = new AuthService(http, new BrowserStorage(ctx.JSInterop.JSRuntime), l10n);
        ctx.Services.AddSingleton(l10n);
        ctx.Services.AddSingleton(auth);
        ctx.Services.AddSingleton(new ApiService(http, auth));
        ctx.Services.AddSingleton(new BrowserStorage(ctx.JSInterop.JSRuntime));
        return ctx;
    }

    [Test]
    public void ModelRestauradoDoChat_NaoESobrescritoPorDefault()
    {
        // ChatView restaura chat.Models[0] em Value antes do seletor
        // terminar a carga — o default do storage/catálogo não pode
        // sobrescrever (era o reset "para o primeiro" reportado).
        using var ctx = Setup();
        var changed = new List<string?>();
        var cut = ctx.Render<ModelSelector>(p => p
            .Add(c => c.Value, "auto/deepseek")
            .Add(c => c.ValueChanged,
                EventCallback.Factory.Create<string?>(this, v => changed.Add(v))));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Instance.Value, Is.EqualTo("auto/deepseek")));

        Assert.That(changed, Is.Empty,
            "o modelo do chat não pode ser sobrescrito pelo default global");
    }

    [Test]
    public void ChatSemModelo_DefaultDoCatalogoAplicado()
    {
        // Chat novo (sem Value): o fallback do catálogo continua valendo.
        using var ctx = Setup();
        var changed = new List<string?>();
        var cut = ctx.Render<ModelSelector>(p => p
            .Add(c => c.Value, (string?)null)
            .Add(c => c.ValueChanged,
                EventCallback.Factory.Create<string?>(this, v => changed.Add(v))));

        cut.WaitForAssertion(() => Assert.That(changed, Is.Not.Empty));
        Assert.That(changed[^1], Is.EqualTo("auto/glm"));
    }
}
