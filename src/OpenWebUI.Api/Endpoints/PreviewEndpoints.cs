using System.Net;
using System.Text.Json;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Preview de portas locais (SPEC-20261009-port-preview, E16 D4): proxy
/// reverso auth-only <c>ANY /preview/{port}/{**path}</c> →
/// <c>http://127.0.0.1:{port}/{path}?{query}</c> no host da API, para o app
/// que um terminal/job subiu aparecer num iframe do IDE — como o port
/// preview do Devin webapp.
/// <para>
/// Segurança: faixa permitida 1024–65535 (fora → 400); só loopback
/// 127.0.0.1 (SSRF fica confinado a portas TCP locais — risco documentado
/// na SPEC); o header <c>Authorization</c> do usuário NUNCA é encaminhado
/// ao upstream; sem redirects automáticos nem cookie jar compartilhado.
/// </para>
/// <para>
/// Flag <c>preview.enabled</c> (default ON — rollout aberto) via
/// <c>GET/PUT /api/v1/preview/config</c> (PUT admin-only), espelhando o
/// padrão de <c>/api/v1/ide|terminal/config</c>; override por
/// <c>Preview:Enabled</c>/<c>PREVIEW_ENABLED</c>. WebSocket (HMR) fica
/// fora de escopo — SSE passa por streaming normal.
/// </para>
/// </summary>
public static class PreviewEndpoints
{
    /// <summary>Nome do HttpClient do proxy (handler sem cookies/redirect).</summary>
    public const string HttpClientName = "preview-proxy";

    /// <summary>Porta mínima permitida (evita serviços de sistema).</summary>
    public const int MinPort = 1024;

    /// <summary>Porta máxima permitida.</summary>
    public const int MaxPort = 65535;

    /// <summary>Timeout para conectar e receber os headers do upstream.</summary>
    private static readonly TimeSpan UpstreamTimeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia as rotas de config (<c>/api/v1/preview/*</c>) e o proxy.</summary>
    public static void MapPreviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/preview").RequireAuthorization();
        group.MapGet("/config", ConfigGetAsync);
        group.MapPut("/config", ConfigPutAsync);

        app.Map("/preview/{port:int}/{**path}",
                (HttpContext http, int port, IHttpClientFactory httpFactory,
                    ConfigService config, IConfiguration configuration,
                    CancellationToken ct) =>
                    ProxyAsync(http, port, httpFactory, config, configuration, ct))
            .RequireAuthorization();
    }

    private static async Task<IResult> ConfigGetAsync(
        HttpContext http, ConfigService config, IConfiguration configuration,
        CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, ct);
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(new { enabled = await IsEnabledAsync(config, configuration, ct) });
    }

    private static async Task<IResult> ConfigPutAsync(
        HttpContext http, ConfigService config, AppDbContext db, CancellationToken ct)
    {
        var user = await CurrentUserAsync(http, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (user.Role != UserRoles.Admin)
        {
            return Results.Forbid();
        }

        var request = await JsonSerializer.DeserializeAsync<PreviewConfigRequest>(
            http.Request.Body, JsonOptions, ct);
        await config.SetAsync("preview.enabled", request?.Enabled == true, ct);
        return Results.Ok(new { enabled = request?.Enabled == true });
    }

    /// <summary>
    /// Encaminha a request a <c>http://127.0.0.1:{port}</c> preservando
    /// método, path+query, headers essenciais e body (streaming nos dois
    /// sentidos). Authorization inbound nunca sai; Host vira o upstream.
    /// </summary>
    private static async Task<IResult> ProxyAsync(
        HttpContext http, int port, IHttpClientFactory httpFactory,
        ConfigService config, IConfiguration configuration, CancellationToken ct)
    {
        if (!await IsEnabledAsync(config, configuration, ct))
        {
            return Results.NotFound(new { detail = "Preview de portas desativado." });
        }

        if (port is < MinPort or > MaxPort)
        {
            return Results.BadRequest(new
            {
                detail = $"Porta inválida — permitido {MinPort}–{MaxPort}.",
            });
        }

        var upstream = BuildUpstreamUri(http, port);
        using var request = BuildUpstreamRequest(http, upstream);

        var client = httpFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            // Timeout de 30s cobre connect+headers; depois disso o corpo
            // streama livre (SSE de dev servers fica aberto por design).
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                ct, http.RequestAborted);
            timeout.CancelAfter(UpstreamTimeout);
            response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested
            && !http.RequestAborted.IsCancellationRequested)
        {
            return PreviewError(
                HttpStatusCode.GatewayTimeout, port,
                "O app não respondeu em 30s.");
        }
        catch (HttpRequestException)
        {
            return PreviewError(
                HttpStatusCode.BadGateway, port,
                "Nada escutando nesta porta do host — o dev server está no ar?");
        }

        return new ProxiedResult(response, port);
    }

    /// <summary>
    /// URI alvo <c>http://127.0.0.1:{port}{subPath}{query}</c> — o path bruto
    /// preserva o percent-encoding do upstream alvo.
    /// </summary>
    private static Uri BuildUpstreamUri(HttpContext http, int port)
    {
        var prefix = $"/preview/{port}";
        var subPath = http.Request.Path.Value is { } raw && raw.StartsWith(prefix, StringComparison.Ordinal)
            ? raw[prefix.Length..]
            : "/";
        if (string.IsNullOrEmpty(subPath))
        {
            subPath = "/";
        }

        return new Uri(
            $"http://127.0.0.1:{port}{subPath}{http.Request.QueryString.Value}");
    }

    /// <summary>
    /// Request outbound: mesmo método, headers essenciais (sem hop-by-hop,
    /// <c>Host</c> nem <c>Authorization</c>) e body por streaming.
    /// </summary>
    private static HttpRequestMessage BuildUpstreamRequest(HttpContext http, Uri upstream)
    {
        var request = new HttpRequestMessage(
            new HttpMethod(http.Request.Method), upstream);
        foreach (var header in http.Request.Headers)
        {
            if (HopByHop.Contains(header.Key)
                || string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                request.Content?.Headers.TryAddWithoutValidation(
                    header.Key, header.Value.ToArray());
            }
        }

        if (http.Request.ContentLength is > 0
            || http.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(http.Request.Body);
            if (http.Request.ContentType is { } contentType)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
            if (http.Request.ContentLength is { } length)
            {
                request.Content.Headers.TryAddWithoutValidation(
                    "Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return request;
    }

    /// <summary>
    /// Flag: ligada por padrão (rollout aberto). Precedência:
    /// <c>Preview:Enabled</c>/<c>PREVIEW_ENABLED</c> → kv
    /// <c>preview.enabled</c> → default <c>true</c>.
    /// </summary>
    private static async Task<bool> IsEnabledAsync(
        ConfigService config, IConfiguration configuration, CancellationToken ct)
    {
        if (bool.TryParse(configuration["Preview:Enabled"], out var fromConfig)
            || bool.TryParse(Environment.GetEnvironmentVariable("PREVIEW_ENABLED"),
                out fromConfig))
        {
            return fromConfig;
        }

        return await config.GetAsync<bool?>("preview.enabled", null, ct) ?? true;
    }

    /// <summary>Página amigável de erro do preview (RF-001) — renderizada no iframe.</summary>
    private static IResult PreviewError(HttpStatusCode status, int port, string message) =>
        Results.Content(
            $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>Preview</title>
            <style>body{font-family:system-ui,sans-serif;display:flex;min-height:100vh;
            align-items:center;justify-content:center;margin:0;color:#555}
            main{text-align:center;max-width:28rem;padding:1rem}
            code{background:#eee;padding:.1em .4em;border-radius:.3em}</style></head>
            <body><main><h2>localhost:{{port}}</h2><p>{{message}}</p></main></body></html>
            """,
            "text/html; charset=utf-8", statusCode: (int)status);

    /// <summary>Headers hop-by-hop que não cruzam o proxy (RFC 2616 §13.5.1).</summary>
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "transfer-encoding", "te", "trailer",
        "proxy-authenticate", "proxy-authorization", "upgrade",
    };

    private static Task<User?> CurrentUserAsync(HttpContext http, CancellationToken ct)
    {
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        return AuthEndpoints.FindUserAsync(http, db, ct);
    }

    /// <summary>
    /// Result que copia status, headers e corpo (streaming) do upstream.
    /// Cookies <c>Set-Cookie</c> são reescritos para o subpath do proxy
    /// (RF-002): <c>Path=/…</c> vira <c>Path=/preview/{port}/…</c> e um
    /// <c>SameSite</c> restritivo sem <c>Secure</c> é removido — o iframe é
    /// same-origin, então cookies do app proxied continuam first-party.
    /// </summary>
    private sealed class ProxiedResult(HttpResponseMessage response, int port) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            using (response)
            {
                httpContext.Response.StatusCode = (int)response.StatusCode;
                foreach (var header in response.Headers.Concat(response.Content.Headers)
                             .Where(h => !HopByHop.Contains(h.Key)))
                {
                    httpContext.Response.Headers[header.Key] = header.Key switch
                    {
                        var k when string.Equals(k, "Set-Cookie", StringComparison.OrdinalIgnoreCase) =>
                            header.Value.Select(v => RewriteCookie(v, port)).ToArray(),
                        var k when string.Equals(k, "Location", StringComparison.OrdinalIgnoreCase) =>
                            header.Value.Select(v => RewriteLocation(v, port)).ToArray(),
                        _ => header.Value.ToArray(),
                    };
                }

                httpContext.Response.Headers.Remove("transfer-encoding");
                // SSE do upstream (HMR/dev servers) passa sem buffering.
                httpContext.Response.Headers["X-Accel-Buffering"] = "no";
                await response.Content.CopyToAsync(
                    httpContext.Response.Body, httpContext.RequestAborted);
            }
        }

        /// <summary>
        /// Redirect absoluto do upstream (<c>http://127.0.0.1:{port}/…</c> ou
        /// <c>localhost</c>/<c>[::1]</c>) é reescrito para o subpath do proxy —
        /// dev servers costumam redirecionar para a URL própria.
        /// </summary>
        private static string RewriteLocation(string? location, int port)
        {
            if (string.IsNullOrEmpty(location))
            {
                return string.Empty;
            }

            // S5332 (won't-fix documentado): http aqui é loopback-only —
            // dev servers locais (vite/next etc.) só falam http no
            // 127.0.0.1/localhost/[::1] que o proxy de preview espelha;
            // https loopback coberto por completude. Hosts externos nunca
            // casam a allowlist e passam intactos.
            foreach (var host in new[] { "127.0.0.1", "localhost", "[::1]" })
            {
                foreach (var scheme in (string[])["http", "https"])
                {
                    var prefix = $"{scheme}://{host}:{port}";
                    if (location.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var rest = location[prefix.Length..];
                        return $"/preview/{port}{(rest.StartsWith('/') ? rest : $"/{rest}")}";
                    }
                }
            }
            return location;
        }

        private static string RewriteCookie(string? cookie, int port)
        {
            if (string.IsNullOrEmpty(cookie))
            {
                return string.Empty;
            }

            var parts = cookie.Split(';')
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();
            var hasSecure = parts.Any(
                p => string.Equals(p, "Secure", StringComparison.OrdinalIgnoreCase));
            var rewritten = new List<string>(parts.Count + 1);
            var sawPath = false;
            foreach (var part in parts)
            {
                if (part.StartsWith("SameSite=", StringComparison.OrdinalIgnoreCase) && !hasSecure)
                {
                    continue; // SameSite=None/Lax/Strict sem Secure é descartado pelos browsers.
                }
                if (part.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                {
                    sawPath = true;
                    var path = part["Path=".Length..];
                    rewritten.Add(path.StartsWith("/", StringComparison.Ordinal)
                        ? $"Path=/preview/{port}{path}"
                        : $"Path=/preview/{port}/{path}");
                    continue;
                }
                rewritten.Add(part);
            }
            if (!sawPath)
            {
                rewritten.Add($"Path=/preview/{port}/");
            }
            return string.Join("; ", rewritten);
        }
    }

    private sealed record PreviewConfigRequest(bool Enabled);
}
