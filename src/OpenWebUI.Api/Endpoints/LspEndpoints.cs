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
    private const string RangeKey = "range";
    private const string StartKey = "start";
    private const string ConstantKind = "constant";
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
        group.MapPost("/completion", CompletionAsync);
        group.MapGet("/definition", DefinitionAsync);
        group.MapGet("/references", ReferencesAsync);
        group.MapPost("/rename", RenameAsync);
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
            var language = LspService.LanguageFor(full);
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
            var range = s.TryGetProperty(RangeKey, out var direct) ? direct : default;
            if (range.ValueKind != JsonValueKind.Object
                && s.TryGetProperty("location", out var loc))
            {
                loc.TryGetProperty(RangeKey, out range);
            }
            if (range.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var (line, col) = Pos(range, StartKey);
            var (endLine, endCol) = Pos(range, "end");
            output.Add(new LspOutlineItem(name, detail, kind, line, col, endLine, endCol, depth));
            if (s.TryGetProperty("children", out var children))
            {
                FlattenSymbols(children, depth + 1, output);
            }
        }
    }

    private static (int Line, int Col) Pos(JsonElement range, string key)
    {
        if (!range.TryGetProperty(key, out var p))
        {
            return (0, 0);
        }
        var line = p.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
        var col = p.TryGetProperty("character", out var c) ? c.GetInt32() : 0;
        return (line, col);
    }

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
            var language = LspService.LanguageFor(full);
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
            .Where(e => e.TryGetProperty(RangeKey, out var r) && r.ValueKind == JsonValueKind.Object)
            .Select(e =>
            {
                var r = e.GetProperty(RangeKey);
                var (sl, sc) = Pos(r, StartKey);
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
            var language = LspService.LanguageFor(path);
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
                        synced = LspService.LanguageFor(full) is not null
                            && lsp.StateOf(workdir, LspService.LanguageFor(full)!.ServerKey)
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
            var language = LspService.LanguageFor(full);
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
        HttpContext http, [AsParameters] LspDeps deps, CancellationToken ct) =>
        WithWorkdirAsync(http, deps.Db, deps.Repos, chatId, ct, async (uid, workdir) =>
        {
            var full = WorkspaceFiles.ResolveInside(workdir, path, out var error);
            if (full is null)
            {
                return Results.BadRequest(new { detail = error });
            }

            var language = LspService.LanguageFor(full);
            if (language is null)
            {
                return Results.Ok(new LspHoverResponse(null));
            }
            try
            {
                var client = await deps.Lsp.GetClientAsync(workdir, language, ct);
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

    // ---------- Fase E: completion / definition / references / rename ----------

    /// <summary>Guard LSP compartilhado: jail + linguagem + servidor Running.
    /// Retorna (full, language) ou null quando deve degradar.</summary>
    private static (string Full, Infrastructure.Lsp.LspLanguage Language)? LspReady(
        LspService lsp, string workdir, string? path, out IResult? early)
    {
        early = null;
        var full = WorkspaceFiles.ResolveInside(workdir, path, out var error);
        if (full is null)
        {
            early = Results.BadRequest(new { detail = error });
            return null;
        }
        var language = LspService.LanguageFor(full);
        if (language is null
            || lsp.StateOf(workdir, language.ServerKey) != LspServerState.Running)
        {
            return null; // degrade — sem erro
        }
        return (full, language);
    }

    /// <summary>POST /completion: textDocument/completion → itens mapeados
    /// pro shape do CM autocomplete (From = início do range de replace).</summary>
    private static Task<IResult> CompletionAsync(
        LspPosRequest? request, string? chatId, HttpContext http,
        AppDbContext db, WorkspaceRepoService repos, LspService lsp,
        CancellationToken ct) =>
        WithWorkdirAsync(http, db, repos, chatId, ct, async (uid, workdir) =>
        {
            var ready = LspReady(lsp, workdir, request?.Path, out var early);
            if (early is not null)
            {
                return early;
            }
            if (ready is null)
            {
                return Results.Ok(new LspCompletionResponse(false, null, []));
            }
            try
            {
                var res = await lsp.RequestAsync(workdir, ready.Value.Full,
                    "textDocument/completion",
                    PosParams(ready.Value.Full, request!.Line, request.Col), ct);
                var items = res;
                if (res.ValueKind != JsonValueKind.Array)
                {
                    if (res.TryGetProperty("items", out JsonElement arr))
                    {
                        items = arr;
                    }
                }
                var list = new List<LspCompletionItem>();
                LspPosition? from = null;
                if (items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var it in items.EnumerateArray())
                    {
                        var label = it.TryGetProperty("label", out JsonElement l) ? l.GetString() : null;
                        if (label is null)
                        {
                            continue;
                        }
                        var kind = it.TryGetProperty("kind", out JsonElement k) && k.ValueKind == JsonValueKind.Number
                            ? k.GetInt32() : 0;
                        var detail = it.TryGetProperty("detail", out JsonElement d) ? d.GetString() : null;
                        string? info = null;
                        if (it.TryGetProperty("documentation", out JsonElement doc))
                        {
                            if (doc.ValueKind == JsonValueKind.String)
                            {
                                info = doc.GetString();
                            }
                            else if (doc.TryGetProperty("value", out var dv))
                            {
                                info = dv.GetString();
                            }
                        }
                        var apply = it.TryGetProperty("insertText", out JsonElement ins) ? ins.GetString() : label;
                        // range explícito (textEdit) → from único; senão o client
                        // usa a palavra sob o cursor.
                        JsonElement textEdit = default, textRange = default;
                        if (from is null
                            && it.TryGetProperty("textEdit", out textEdit)
                            && textEdit.ValueKind == JsonValueKind.Object
                            && textEdit.TryGetProperty(RangeKey, out textRange))
                        {
                            var (sl, sc) = Pos(textRange, StartKey);
                            from = new LspPosition(sl, sc);
                        }
                        list.Add(new LspCompletionItem(
                            label, KindToCmType(kind), detail, info, apply));
                    }
                }
                return Results.Ok(new LspCompletionResponse(
                    true, from ?? new LspPosition(request!.Line, Math.Max(0, request.Col - 1)), list));
            }
            catch (Exception ex) when (ex is LspUnavailableException or LspRequestException
                or IOException or TimeoutException)
            {
                return Results.Ok(new LspCompletionResponse(false, null, []));
            }
        });

    /// <summary>LSP CompletionItemKind → tipo do CM autocomplete.</summary>
    private static string? KindToCmType(int kind) => kind switch
    {
        2 or 3 => "function",
        4 => "function",    // constructor
        5 or 6 => "variable", // field / variable
        7 => "class",
        8 => "interface",
        9 => "namespace",   // module
        10 => "property",
        11 => ConstantKind,   // unit → enum member-ish
        12 or 13 => "variable",
        14 => ConstantKind,   // keyword → enum
        15 => "text",
        17 => ConstantKind,   // file
        18 => "namespace",  // reference
        19 => ConstantKind,   // folder
        20 or 21 or 22 or 23 => ConstantKind,
        24 => "type",
        25 => "keyword",
        _ => null,
    };

    /// <summary>GET /definition e /references: Location|Location[]|LocationLink[]
    /// → lista normalizada com path relativo ao workdir (uri file:// só se
    /// dentro do jail).</summary>
    private static Task<IResult> DefinitionAsync(
        string? path, int? line, int? col, string? chatId, HttpContext http,
        [AsParameters] LspDeps deps, CancellationToken ct) =>
        LocationsAsync(new LocationQuery(path, line, col, chatId, false),
            "textDocument/definition", http, deps, ct);

    private static Task<IResult> ReferencesAsync(
        string? path, int? line, int? col, string? chatId, HttpContext http,
        [AsParameters] LspDeps deps, CancellationToken ct) =>
        LocationsAsync(new LocationQuery(path, line, col, chatId, true),
            "textDocument/references", http, deps, ct);

    private sealed record LocationQuery(
        string? Path, int? Line, int? Col, string? ChatId, bool IncludeDecl);

    /// <summary>Bundle DI dos endpoints LSP (S107).</summary>
    internal sealed record LspDeps(
        AppDbContext Db, WorkspaceRepoService Repos, LspService Lsp);

    private static Task<IResult> LocationsAsync(
        LocationQuery q, string method, HttpContext http, LspDeps deps,
        CancellationToken ct) =>
        WithWorkdirAsync(http, deps.Db, deps.Repos, q.ChatId, ct, async (uid, workdir) =>
        {
            var ready = LspReady(deps.Lsp, workdir, q.Path, out var early);
            if (early is not null)
            {
                return early;
            }
            if (ready is null)
            {
                return Results.Ok(new LspLocationsResponse(false, []));
            }
            try
            {
                var p = PosParams(ready.Value.Full, q.Line ?? 0, q.Col ?? 0);
                var @params = method == "textDocument/references"
                    ? new
                    {
                        textDocument = p.textDocument,
                        position = p.position,
                        context = new { includeDeclaration = q.IncludeDecl },
                    }
                    : (object)p;
                var res = await deps.Lsp.RequestAsync(workdir, ready.Value.Full, method, @params, ct);
                var list = new List<LspFileLocation>();
                foreach (var loc in EnumerateLocations(res))
                {
                    var rel = RelPath(workdir, loc.Uri);
                    if (rel is not null)
                    {
                        list.Add(new LspFileLocation(rel, loc.Range));
                    }
                }
                return Results.Ok(new LspLocationsResponse(true, list));
            }
            catch (Exception ex) when (ex is LspUnavailableException or LspRequestException
                or IOException or TimeoutException)
            {
                return Results.Ok(new LspLocationsResponse(false, []));
            }
        });

    /// <summary>Normaliza Location | Location[] | LocationLink[].</summary>
    private static IEnumerable<(string Uri, LspRange Range)> EnumerateLocations(JsonElement res)
    {
        if (res.ValueKind == JsonValueKind.Null || res.ValueKind == JsonValueKind.Undefined)
        {
            yield break;
        }
        var arr = res.ValueKind == JsonValueKind.Array ? res.EnumerateArray()
            : new[] { res }.AsEnumerable();
        foreach (var loc in arr)
        {
            string? uri = null;
            JsonElement range = default;
            if (loc.TryGetProperty("uri", out var u))
            {
                uri = u.GetString();
                loc.TryGetProperty(RangeKey, out range);
            }
            else if (loc.TryGetProperty("targetUri", out var tu))
            {
                uri = tu.GetString();
                loc.TryGetProperty("targetRange", out range);
            }
            if (uri is null || range.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var (sl, sc) = Pos(range, StartKey);
            var (el, ec) = Pos(range, "end");
            yield return (uri, new LspRange(sl, sc, el, ec));
        }
    }

    /// <summary>file:// uri → path relativo ao workdir (null se fora).</summary>
    private static string? RelPath(string workdir, string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme != "file")
        {
            return null;
        }
        var full = Path.GetFullPath(u.LocalPath);
        var root = Path.GetFullPath(workdir);
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal))
        {
            return null;
        }
        return full[(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1)..]
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>POST /rename: workspace/applyEdit equivalente — edits do
    /// arquivo ativo voltam pro client (replaceRange, preserva dirty); os
    /// demais arquivos são escritos direto no disco (mesmo jail).</summary>
    private static Task<IResult> RenameAsync(
        LspRenameRequest? request, string? chatId, HttpContext http,
        AppDbContext db, WorkspaceRepoService repos, LspService lsp,
        CancellationToken ct) =>
        WithWorkdirAsync(http, db, repos, chatId, ct, async (uid, workdir) =>
        {
            var ready = LspReady(lsp, workdir, request?.Path, out var early);
            if (early is not null)
            {
                return early;
            }
            if (ready is null || string.IsNullOrWhiteSpace(request?.NewName))
            {
                return Results.Ok(new LspRenameResponse(false, [], [],
                    string.IsNullOrWhiteSpace(request?.NewName) ? "Nome vazio." : null));
            }
            try
            {
                var p = PosParams(ready.Value.Full, request.Line, request.Col);
                var res = await lsp.RequestAsync(workdir, ready.Value.Full,
                    "textDocument/rename",
                    new
                    {
                        textDocument = p.textDocument,
                        position = p.position,
                        newName = request.NewName,
                    }, ct);

                var activeRel = request.ActivePath?.Replace('\\', '/');
                var clientEdits = new List<LspRenameFileEdits>();
                var updated = new List<string>();
                foreach (var (uri, edits) in WorkspaceEdits(res))
                {
                    var rel = RelPath(workdir, uri);
                    if (rel is null || edits.Count == 0)
                    {
                        continue;
                    }
                    if (string.Equals(rel, activeRel, StringComparison.Ordinal))
                    {
                        clientEdits.Add(new LspRenameFileEdits(rel, edits));
                        continue;
                    }
                    var full = WorkspaceFiles.ResolveInside(workdir, rel, out _);
                    if (full is null || !File.Exists(full))
                    {
                        continue;
                    }
                    var text = await File.ReadAllTextAsync(full, ct);
                    var applied = text;
                    var arr = JsonSerializer.SerializeToElement(
                        edits.Select(e => new
                        {
                            range = new
                            {
                                start = new { line = e.Range.Line, character = e.Range.Col },
                                end = new { line = e.Range.EndLine, character = e.Range.EndCol },
                            },
                            newText = e.NewText,
                        }).ToArray(), JsonOptions);
                    applied = ApplyTextEdits(applied, arr);
                    if (applied != text)
                    {
                        await File.WriteAllTextAsync(full, applied, ct);
                        updated.Add(rel);
                    }
                }
                return Results.Ok(new LspRenameResponse(true, clientEdits, updated, null));
            }
            catch (Exception ex) when (ex is LspUnavailableException or LspRequestException
                or IOException or TimeoutException)
            {
                return Results.Ok(new LspRenameResponse(false, [], [], ex.Message));
            }
        });

    /// <summary>WorkspaceEdit: {changes:{uri:[edit]}} ou {documentChanges:[
    /// {textDocument:{uri}, edits:[...]}]} — só textDocument edits (ops de
    /// recurso como rename-file são ignorados).</summary>
    private static IEnumerable<(string Uri, List<LspRenameEdit> Edits)> WorkspaceEdits(JsonElement res)
    {
        if (res.TryGetProperty("changes", out var changes)
            && changes.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in changes.EnumerateObject())
            {
                var edits = ParseEdits(prop.Value);
                if (edits.Count > 0)
                {
                    yield return (prop.Name, edits);
                }
            }
        }
        if (res.TryGetProperty("documentChanges", out var docs)
            && docs.ValueKind == JsonValueKind.Array)
        {
            foreach (var dc in docs.EnumerateArray())
            {
                if (!dc.TryGetProperty("textDocument", out var td)
                    || !td.TryGetProperty("uri", out var u)
                    || !dc.TryGetProperty("edits", out var es))
                {
                    continue;
                }
                var edits = ParseEdits(es);
                var uri = u.GetString();
                if (uri is not null && edits.Count > 0)
                {
                    yield return (uri, edits);
                }
            }
        }
    }

    private static List<LspRenameEdit> ParseEdits(JsonElement arr)
    {
        var list = new List<LspRenameEdit>();
        if (arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        foreach (var e in arr.EnumerateArray())
        {
            if (!e.TryGetProperty(RangeKey, out var r) || r.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var (sl, sc) = Pos(r, StartKey);
            var (el, ec) = Pos(r, "end");
            var nt = e.TryGetProperty("newText", out var n) ? n.GetString() ?? "" : "";
            list.Add(new LspRenameEdit(new LspRange(sl, sc, el, ec), nt));
        }
        return list;
    }

    /// <summary>Params comuns textDocument+position (0-based, direto).</summary>
    private static dynamic PosParams(string full, int line, int col) => new
    {
        textDocument = new { uri = LspClient.UriForPath(full) },
        position = new
        {
            line = Math.Max(0, line),
            character = Math.Max(0, col),
        },
    };

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
