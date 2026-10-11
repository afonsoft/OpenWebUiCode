using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;
using OpenWebUI.Infrastructure.Lsp;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Api.Endpoints;

/// <summary>
/// Endpoints LSP do editor IDE (SPEC-20261009-lsp-diagnostics): status por
/// arquivo (degrade limpo quando a linguagem não tem servidor), sync de
/// documento (didOpen/didChange/didClose vindos do editor) e consultas
/// (diagnostics, hover) usadas pelo painel Problems e pelo tooltip.
/// Tudo atrás do guard de repo vinculado (<c>BoundWorkdirAsync</c>).
/// </summary>
public static class LspEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Mapeia as rotas LSP do workspace.</summary>
    public static RouteGroupBuilder MapLspEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/workspace/lsp").RequireAuthorization();
        group.MapGet("/status", StatusAsync);
        group.MapPost("/doc", DocSyncAsync);
        group.MapGet("/diagnostics", DiagnosticsAsync);
        group.MapGet("/hover", HoverAsync);
        group.MapGet("/symbols", SymbolsAsync);
        group.MapPost("/format", FormatAsync);
        return group;
    }

    // ---------- GET /symbols?path= (SPEC-20261011-ide-intellisense) ----------

    /// <summary>Outline do arquivo: documentSymbol → lista plana com Depth.
    /// Sem servidor → <c>available:false</c> (degrade, nunca 5xx).</summary>
    private static Task<IResult> SymbolsAsync(
        string? path, string? chatId, HttpContext http, AppDbContext db,
        WorkspaceRepoService repos, LspService lsp, CancellationToken ct) =>
        WithWorkdirAsync(http, db, repos, chatId, ct, async (uid, workdir) =>
        {
            var full = WorkspaceFiles.ResolveInside(workdir, path, out var error);
            if (full is null)
            {
                return Results.BadRequest(new { detail = error });
            }
            var language = lsp.LanguageFor(full);
            if (language is null
                || lsp.StateOf(workdir, language.ServerKey) != LspServerState.Running)
            {
                return Results.Ok(new LspSymbolsResponse(false, []));
            }
            try
            {
                await lsp.OpenDocumentAsync(workdir, full, null, ct);
                var result = await lsp.RequestAsync(workdir, full,
                    "textDocument/documentSymbol",
                    new { textDocument = new { uri = LspClient.UriForPath(full) } }, ct);
                var symbols = new List<LspOutlineItem>();
                FlattenSymbols(result, 0, symbols);
                return Results.Ok(new LspSymbolsResponse(true, symbols));
            }
            catch (LspUnavailableException)
            {
                return Results.Ok(new LspSymbolsResponse(false, []));
            }
        });

    /// <summary>Achata DocumentSymbol[] (recursivo) ou SymbolInformation[] (flat).</summary>
    private static void FlattenSymbols(JsonElement node, int depth, List<LspOutlineItem> output)
    {
        if (node.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var s in node.EnumerateArray())
        {
            var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
            var detail = s.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() : null;
            var kind = s.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.Number
                ? k.GetInt32() : 0;
            // DocumentSymbol: range+selectionRange; SymbolInformation: location.range.
            var range = s.TryGetProperty("range", out var r) ? r
                : s.TryGetProperty("location", out var loc) && loc.TryGetProperty("range", out r) ? r
                : default;
            if (range.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var (line, col) = Pos(range, "start");
            var (endLine, endCol) = Pos(range, "end");
            output.Add(new LspOutlineItem(name, detail, kind, line, col, endLine, endCol, depth));
            if (s.TryGetProperty("children", out var children))
            {
                FlattenSymbols(children, depth + 1, output);
            }
        }
    }

    private static (int Line, int Col) Pos(JsonElement range, string key) =>
        range.TryGetProperty(key, out var p)
            ? ((p.TryGetProperty("line", out var l) ? l.GetInt32() : 0),
               (p.TryGetProperty("character", out var c) ? c.GetInt32() : 0))
            : (0, 0);

    // ---------- POST /format (SPEC-20261011-ide-intellisense) ----------

    /// <summary>Format Document: textDocument/formatting → aplica os TextEdits
    /// no conteúdo e devolve o texto novo — escrita fica no fluxo PUT /file.</summary>
    private static Task<IResult> FormatAsync(
        LspFormatRequest? request, string? chatId, HttpContext http,
        AppDbContext db, WorkspaceRepoService repos, LspService lsp,
        CancellationToken ct) =>
        WithWorkdirAsync(http, db, repos, chatId, ct, async (uid, workdir) =>
        {
            var full = WorkspaceFiles.ResolveInside(workdir, request?.Path, out var error);
            if (full is null)
            {
                return Results.BadRequest(new { detail = error });
            }
            if (!File.Exists(full))
            {
                return Results.NotFound(new { detail = "Arquivo não existe." });
            }
            var language = lsp.LanguageFor(full);
            if (language is null
                || lsp.StateOf(workdir, language.ServerKey) != LspServerState.Running)
            {
                return Results.Ok(new LspFormatResponse(false, null, false));
            }
            try
            {
                await lsp.OpenDocumentAsync(workdir, full, null, ct);
                var edits = await lsp.RequestAsync(workdir, full,
                    "textDocument/formatting",
                    new
                    {
                        textDocument = new { uri = LspClient.UriForPath(full) },
                        options = new { tabSize = 4, insertSpaces = true },
                    }, ct);
                var original = await File.ReadAllTextAsync(full, ct);
                var formatted = ApplyTextEdits(original, edits);
                return Results.Ok(new LspFormatResponse(
                    true, formatted, formatted != original));
            }
            catch (LspUnavailableException)
            {
                return Results.Ok(new LspFormatResponse(false, null, false));
            }
        });

    /// <summary>Aplica LSP TextEdits (range start/end 0-based) de trás pra
    /// frente sobre o texto — posições são UTF-16 code units.</summary>
    private static string ApplyTextEdits(string text, JsonElement edits)
    {
        if (edits.ValueKind != JsonValueKind.Array)
        {
            return text;
        }
        var lineStart = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lineStart.Add(i + 1);
            }
        }
        int Off(int line, int col) =>
            line >= lineStart.Count ? text.Length : Math.Min(lineStart[line] + col, text.Length);

        var ordered = edits.EnumerateArray()
            .Where(e => e.TryGetProperty("range", out var r) && r.ValueKind == JsonValueKind.Object)
            .Select(e =>
            {
                var r = e.GetProperty("range");
                var (sl, sc) = Pos(r, "start");
                var (el, ec) = Pos(r, "end");
                return (Start: Off(sl, sc), End: Off(el, ec),
                    Text: e.TryGetProperty("newText", out var nt) ? nt.GetString() ?? "" : "");
            })
            .OrderByDescending(e => e.Start).ToList();
        foreach (var e in ordered)
        {
            text = string.Concat(text.AsSpan(0, e.Start), e.Text, text.AsSpan(e.End));
        }
        return text;
    }

    // ---------- GET /status?path= ----------

    /// <summary>
    /// Linguagem/estado do servidor do arquivo — UI usa para decidir se
    /// renderiza Problems/squiggles (state "unavailable"/null → nada).
    /// </summary>
    private static Task<IResult> StatusAsync(
        string? path, string? chatId, HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        LspService lsp, CancellationToken ct) =>
        WithWorkdirAsync(http, db, repos, chatId, ct, (uid, workdir) =>
        {
            var language = lsp.LanguageFor(path);
            if (language is null)
            {
                return Task.FromResult(Results.Ok(
                    new LspStatusResponse(lsp.Enabled, null, "unmapped", null)));
            }
            return Task.FromResult(Results.Ok(new
            {
                enabled = lsp.Enabled,
                language = language.ServerKey,
                state = lsp.StateOf(workdir, language.ServerKey).ToString().ToLowerInvariant(),
                error = lsp.ErrorOf(workdir, language.ServerKey),
            }));
        });

    // ---------- POST /doc ----------

    /// <summary>
    /// Sync do documento a partir do editor: <c>kind</c> open|change|close.
    /// didOpen/didChange carregam o texto (v1 full-sync); close é best-effort.
    /// Responde 200 mesmo sem servidor (degrade) — o campo
    /// <c>synced:false</c> informa que nada foi enviado.
    /// </summary>
    private static Task<IResult> DocSyncAsync(
        [FromQuery] string? chatId, HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        LspService lsp, CancellationToken ct) =>
        WithWorkdirAsync(http, db, repos, chatId, ct, async (uid, workdir) =>
        {
            var request = await JsonSerializer.DeserializeAsync<LspDocSyncRequest>(
                http.Request.Body, JsonOptions, ct);
            if (request is null || string.IsNullOrWhiteSpace(request.Path))
            {
                return Results.BadRequest(new { detail = "'path' é obrigatório." });
            }

            var full = WorkspaceFiles.ResolveInside(workdir, request.Path, out var error);
            if (full is null)
            {
                return Results.BadRequest(new { detail = error });
            }
            if (request.Kind is not "close" && !File.Exists(full) && request.Text is null)
            {
                return Results.NotFound(new { detail = "Arquivo não existe." });
            }

            var synced = false;
            try
            {
                switch (request.Kind)
                {
                    case "open":
                        synced = await lsp.OpenDocumentAsync(
                            workdir, full, request.Text, ct) is not null;
                        break;
                    case "change":
                        if (request.Text is null)
                        {
                            return Results.BadRequest(new { detail = "'text' é obrigatório." });
                        }
                        await lsp.ChangeDocumentAsync(workdir, full, request.Text, ct);
                        synced = lsp.LanguageFor(full) is not null
                            && lsp.StateOf(workdir, lsp.LanguageFor(full)!.ServerKey)
                                == LspServerState.Running;
                        break;
                    case "close":
                        await lsp.CloseDocumentAsync(workdir, full, ct);
                        synced = true;
                        break;
                    default:
                        return Results.BadRequest(
                            new { detail = "'kind' deve ser open|change|close." });
                }
            }
            catch (LspUnavailableException ex)
            {
                return Results.Ok(new { synced = false, detail = ex.Message });
            }
            return Results.Ok(new { synced });
        });

    // ---------- GET /diagnostics?path= ----------

    /// <summary>
    /// Diagnostics do arquivo (ou do workdir sem <c>path</c>) no formato
    /// compacto do editor: <c>path, line, col, endLine, endCol, severity,
    /// code, source, message</c> — cap <see cref="LspOptions.ResultCap"/>.
    /// </summary>
    private static Task<IResult> DiagnosticsAsync(
        string? path, string? chatId, HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        LspService lsp, CancellationToken ct) =>
        WithWorkdirAsync(http, db, repos, chatId, ct, async (uid, workdir) =>
        {
            string? full = null;
            if (path is not null)
            {
                full = WorkspaceFiles.ResolveInside(workdir, path, out var error);
                if (full is null)
                {
                    return Results.BadRequest(new { detail = error });
                }
            }

            // Se o doc ainda não foi publicado, espera o primeiro push (cap 8s)
            // — cobre o fluxo open→diagnostics do editor sem poling.
            var language = lsp.LanguageFor(full);
            if (full is not null && language is not null
                && lsp.StateOf(workdir, language.ServerKey) == LspServerState.Running)
            {
                try
                {
                    var client = await lsp.GetClientAsync(workdir, language, ct);
                    await client.AwaitDiagnosticsAsync(full, TimeSpan.FromSeconds(8), ct);
                }
                catch (LspUnavailableException)
                {
                    // servidor caiu entre status e diagnostics — responde vazio.
                }
            }

            var diags = lsp.Diagnostics(workdir, full, out _);
            var capped = diags.Take(LspOptions.ResultCap).Select(d => new LspDiagnosticItem(
                LspResponse.RelPath(workdir, d.Path),
                d.Line, d.Col, d.EndLine, d.EndCol,
                d.Severity, d.Code, d.Source, d.Message)).ToList();
            return Results.Ok(new LspDiagnosticsResponse(
                capped, diags.Count > LspOptions.ResultCap, diags.Count));
        });

    // ---------- GET /hover?path=&line=&col= ----------

    /// <summary>Tooltip do editor — hover do servidor (texto ou null).</summary>
    private static Task<IResult> HoverAsync(
        string? path, int? line, int? col, string? chatId,
        HttpContext http, AppDbContext db, WorkspaceRepoService repos,
        LspService lsp, CancellationToken ct) =>
        WithWorkdirAsync(http, db, repos, chatId, ct, async (uid, workdir) =>
        {
            var full = WorkspaceFiles.ResolveInside(workdir, path, out var error);
            if (full is null)
            {
                return Results.BadRequest(new { detail = error });
            }

            var language = lsp.LanguageFor(full);
            if (language is null)
            {
                return Results.Ok(new LspHoverResponse(null));
            }
            try
            {
                var client = await lsp.GetClientAsync(workdir, language, ct);
                var result = await client.RequestAsync("textDocument/hover", new
                {
                    textDocument = new { uri = LspClient.UriForPath(full) },
                    position = new
                    {
                        line = Math.Max(0, (line ?? 1) - 1),
                        character = Math.Max(0, (col ?? 1) - 1),
                    },
                }, ct);
                return Results.Ok(new LspHoverResponse(LspResponse.HoverText(result)));
            }
            catch (Exception ex) when (ex is LspUnavailableException or LspRequestException
                or IOException or TimeoutException)
            {
                return Results.Ok(new LspHoverResponse(null));
            }
        });

    // ---------- guard comum ----------

    /// <summary>Repo vinculado → executa o handler com (userId, workdir).</summary>
    private static async Task<IResult> WithWorkdirAsync(
        HttpContext http, AppDbContext db, WorkspaceRepoService repos, string? chatId,
        CancellationToken ct, Func<string, string, Task<IResult>> handler)
    {
        var user = await AuthEndpoints.FindUserAsync(http, db, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }
        if (chatId is not null
            && !await db.Chats.AsNoTracking().AnyAsync(c => c.Id == chatId && c.UserId == user.Id, ct))
        {
            return Results.NotFound(new { detail = "Chat não encontrado." });
        }
        if ((await repos.ResolveBindingAsync(user.Id, chatId, ct)).Binding is null)
        {
            return Results.NotFound(
                new { detail = "Nenhum repositório vinculado.", bound = false });
        }
        return await handler(user.Id, await repos.ResolveWorkdirAsync(user.Id, chatId, ct));
    }

}
