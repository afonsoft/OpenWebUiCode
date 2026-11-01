using System.Text;
using System.Text.Json;
using OpenWebUI.Infrastructure.ChatTools;

namespace OpenWebUI.Infrastructure.Lsp;

/// <summary>
/// Normalização das respostas LSP para o formato compacto das tools/endpoints
/// (SPEC-20261009-lsp-diagnostics): symbols/documentSymbol (hierárquico ou
/// plano), workspace/symbol, locations (definition/references) e hover —
/// sempre com cap de itens.
/// </summary>
public static class LspResponse
{
    private const string RangeKey = "range";
    /// <summary>Location | Location[] | LocationLink[] → lista plana.</summary>
    public static List<LspLocation> FlattenLocations(JsonElement result, int cap)
    {
        var list = new List<LspLocation>();
        if (result.ValueKind == JsonValueKind.Null || result.ValueKind == JsonValueKind.Undefined)
        {
            return list;
        }
        if (result.ValueKind == JsonValueKind.Object)
        {
            AddLocation(result, list, cap);
            return list;
        }
        if (result.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in result.EnumerateArray())
            {
                if (list.Count >= cap)
                {
                    break;
                }
                AddLocation(item, list, cap);
            }
        }
        return list;
    }

    private static void AddLocation(JsonElement item, List<LspLocation> list, int cap)
    {
        if (list.Count >= cap || item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // LocationLink: {targetUri, targetSelectionRange|targetRange}
        var uri = item.TryGetProperty("uri", out var u) ? u.GetString()
            : item.TryGetProperty("targetUri", out var tu) ? tu.GetString()
            : null;
        var range = item.TryGetProperty(RangeKey, out var r) ? r
            : item.TryGetProperty("targetSelectionRange", out var tsr) ? tsr
            : item.TryGetProperty("targetRange", out var tr) ? tr
            : (JsonElement?)null;
        var path = LspClient.PathForUri(uri);
        if (path is null || range is null)
        {
            return;
        }
        var (line, col, endLine, endCol) = ReadRange(range.Value);
        list.Add(new LspLocation(path, line, col, endLine, endCol));
    }

    /// <summary>Locations como texto <c>relpath:line</c> (cap aplicado).</summary>
    public static string FormatLocations(JsonElement result, string workdir, int cap)
    {
        var locations = FlattenLocations(result, cap);
        if (locations.Count == 0)
        {
            return "Nenhuma localização.";
        }
        var sb = new StringBuilder();
        foreach (var loc in locations)
        {
            sb.Append(RelPath(workdir, loc.Path))
                .Append(':').Append(loc.Line + 1)
                .Append(':').Append(loc.Col + 1)
                .AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// documentSymbol → lista plana (SymbolInformation[] ou
    /// DocumentSymbol[] hierárquico — desce <c>children</c> com Container).
    /// </summary>
    public static List<LspSymbolItem> FlattenSymbols(JsonElement result, string path, int cap)
    {
        var list = new List<LspSymbolItem>();
        if (result.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        foreach (var sym in result.EnumerateArray())
        {
            FlattenSymbol(sym, path, null, list, cap);
            if (list.Count >= cap)
            {
                break;
            }
        }
        return list;
    }

    private static void FlattenSymbol(JsonElement sym, string path, string? container,
        List<LspSymbolItem> list, int cap)
    {
        if (list.Count >= cap || sym.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        var name = sym.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (name is null)
        {
            return;
        }
        var kind = sym.TryGetProperty("kind", out var k) && k.TryGetInt32(out var kv) ? kv : 0;

        // DocumentSymbol: {range|selectionRange}; SymbolInformation: {location:{uri,range}}
        (int Line, int Col) pos = (0, 0);
        var itemPath = path;
        if (sym.TryGetProperty("location", out var loc)
            && loc.ValueKind == JsonValueKind.Object)
        {
            if (loc.TryGetProperty("uri", out var lu))
            {
                itemPath = LspClient.PathForUri(lu.GetString()) ?? path;
            }
            if (loc.TryGetProperty(RangeKey, out var lr))
            {
                pos = StartPos(lr);
            }
        }
        else if (sym.TryGetProperty("selectionRange", out var sr)
            || sym.TryGetProperty(RangeKey, out sr))
        {
            pos = StartPos(sr);
        }
        var childContainer = sym.TryGetProperty("containerName", out var cn)
            ? cn.GetString() : container;
        list.Add(new LspSymbolItem(name, kind, itemPath, pos.Line, pos.Col, childContainer));

        if (sym.TryGetProperty("children", out var children)
            && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                FlattenSymbol(child, itemPath, name, list, cap);
            }
        }
    }

    /// <summary>workspace/symbol → lista plana (SymbolInformation[]).</summary>
    public static List<LspSymbolItem> FlattenWorkspaceSymbols(
        JsonElement result, string workdir, int cap)
    {
        var list = new List<LspSymbolItem>();
        if (result.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        foreach (var sym in result.EnumerateArray())
        {
            if (list.Count >= cap)
            {
                break;
            }
            if (sym.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var name = sym.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is null
                || !sym.TryGetProperty("location", out var loc)
                || loc.ValueKind != JsonValueKind.Object
                || !loc.TryGetProperty("uri", out var u))
            {
                continue;
            }
            var raw = LspClient.PathForUri(u.GetString());
            if (raw is null)
            {
                continue;
            }
            var (line, col) = loc.TryGetProperty(RangeKey, out var r) ? StartPos(r) : (0, 0);
            var kind = sym.TryGetProperty("kind", out var k) && k.TryGetInt32(out var kv) ? kv : 0;
            var container = sym.TryGetProperty("containerName", out var cn)
                ? cn.GetString() : null;
            list.Add(new LspSymbolItem(
                name, kind, RelPath(workdir, raw), line, col, container));
        }
        return list;
    }

    /// <summary>Hover → texto (MarkupContent|MarkedString|MarkedString[]).</summary>
    public static string? HoverText(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("contents", out var contents))
        {
            return null;
        }
        var text = MarkupText(contents);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? MarkupText(JsonElement contents) => contents.ValueKind switch
    {
        JsonValueKind.String => contents.GetString(),
        JsonValueKind.Array => string.Join("\n", contents.EnumerateArray()
            .Select(MarkupText).Where(t => !string.IsNullOrWhiteSpace(t))),
        JsonValueKind.Object => contents.TryGetProperty("value", out var v)
            ? v.GetString()
            : null,
        _ => null,
    };

    /// <summary>Nome do SymbolKind LSP (1-26) — desconhecido → "symbol".</summary>
    public static string SymbolKindName(int kind) => kind switch
    {
        1 => "file", 2 => "module", 3 => "namespace", 4 => "package",
        5 => "class", 6 => "method", 7 => "property", 8 => "field",
        9 => "constructor", 10 => "enum", 11 => "interface", 12 => "function",
        13 => "variable", 14 => "constant", 15 => "string", 16 => "number",
        17 => "boolean", 18 => "array", 19 => "object", 20 => "key",
        21 => "null", 22 => "enummember", 23 => "struct", 24 => "event",
        25 => "operator", 26 => "typeparam",
        _ => "symbol",
    };

    private static (int Line, int Col) StartPos(JsonElement range)
    {
        if (range.TryGetProperty("start", out var start))
        {
            var line = start.TryGetProperty("line", out var l) && l.TryGetInt32(out var li) ? li : 0;
            var col = start.TryGetProperty("character", out var c) && c.TryGetInt32(out var ch) ? ch : 0;
            return (line, col);
        }
        return (0, 0);
    }

    private static (int Line, int Col, int EndLine, int EndCol) ReadRange(JsonElement range)
    {
        var (line, col) = StartPos(range);
        var (endLine, endCol) = (line, col);
        if (range.TryGetProperty("end", out var end))
        {
            endLine = end.TryGetProperty("line", out var l) && l.TryGetInt32(out var li) ? li : line;
            endCol = end.TryGetProperty("character", out var c) && c.TryGetInt32(out var ch) ? ch : col;
        }
        return (line, col, endLine, endCol);
    }

    /// <summary>Path relativo ao workdir quando possível (fallback: absoluto).</summary>
    public static string RelPath(string workdir, string path)
    {
        try
        {
            return WorkspaceFiles.RelativeOf(workdir, path);
        }
        catch (Exception) when (!Directory.Exists(workdir))
        {
            return path;
        }
        catch (IOException)
        {
            return path;
        }
    }
}
