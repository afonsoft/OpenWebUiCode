using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:notebook_read</c> — lista as células de um .ipynb com
/// índice, tipo e preview (SPEC-20261103-notebook-edit-tool RF-001).
/// Somente leitura; barato antes de um notebook_edit.
/// </summary>
public sealed class NotebookReadBuiltinTool : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "notebook_read";

    /// <inheritdoc />
    public string Description =>
        "List cells of a .ipynb notebook (index, type, preview, outputs "
        + "count) — call before notebook_edit to find the cell address.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path relative to the workspace (.ipynb)." },
            "max_lines": { "type": "integer", "description": "Preview lines per cell (default 3, max 20).", "default": 3 }
          },
          "required": ["path"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;
    /// <inheritdoc />
    public bool SafeInParallel => true;

    /// <inheritdoc />
    public Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var (full, rel, error) = NotebookOps.ResolveNotebookPath(
            args, context.WorkspacePath);
        if (full is null)
        {
            return Task.FromResult(new BuiltinToolResult(error!));
        }
        var (doc, parseError) = NotebookOps.LoadNotebook(full, rel);
        if (doc is null)
        {
            return Task.FromResult(new BuiltinToolResult(parseError!));
        }

        var maxLines = args.TryGetProperty("max_lines", out var ml)
            && ml.TryGetInt32(out var mlv)
            ? Math.Clamp(mlv, 1, 20) : 3;
        var cells = doc["cells"]!.AsArray();
        var sb = new StringBuilder($"{rel} — {cells.Count} célula(s)\n");
        var list = new List<object>();
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i]!;
            var type = NotebookOps.CellType(cell);
            var src = NotebookOps.SourceText(cell);
            var preview = string.Join("\\n",
                src.Split('\n').Take(maxLines)).TrimEnd();
            var outputs = cell["outputs"] is JsonArray o ? o.Count : 0;
            var id = cell["id"]?.GetValue<string>();
            sb.AppendLine(
                $"[{i}] {type}{(id is null ? "" : $" id={id}")} " +
                $"lines={src.Split('\n').Length} outputs={outputs}: {preview}");
            list.Add(new
            {
                index = i, cell_type = type, id, preview,
                source_lines = src.Split('\n').Length,
                outputs_count = outputs,
            });
        }
        return Task.FromResult(new BuiltinToolResult(
            sb.ToString().TrimEnd(), new { cells = list }));
    }
}

/// <summary>
/// <c>builtin:notebook_edit</c> — replace/insert/delete de células .ipynb
/// preservando metadados nbformat (SPEC-20261103-notebook-edit-tool
/// RF-002/003/004): editar <c>code</c> zera outputs/execution_count, ids e
/// metadados não-alvo intactos, escrita atômica, confinado ao workspace.
/// </summary>
public sealed class NotebookEditBuiltinTool : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "notebook_edit";

    /// <inheritdoc />
    public string Description =>
        "Edit a .ipynb cell — replace source, insert, or delete by index "
        + "or cell_id. Preserves nbformat metadata; editing a code cell "
        + "clears its outputs/execution_count.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path relative to the workspace (.ipynb)." },
            "operation": { "type": "string", "enum": ["replace", "insert", "delete"] },
            "index": { "type": "integer", "description": "0-based cell index (replace/delete, or insert position — default end)." },
            "cell_id": { "type": "string", "description": "Alternative address: cell 'id' (replace/delete)." },
            "cell_type": { "type": "string", "enum": ["code", "markdown"], "description": "Required for insert." },
            "source": { "type": "string", "description": "New cell source (replace/insert)." }
          },
          "required": ["path", "operation"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Write;

    /// <inheritdoc />
    public Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var (full, rel, resolveError) = NotebookOps.ResolveNotebookPath(
            args, context.WorkspacePath);
        if (full is null)
        {
            return Task.FromResult(new BuiltinToolResult(resolveError!));
        }
        var operation = args.TryGetProperty("operation", out var op)
            ? op.GetString() : null;
        var (doc, loadError) = NotebookOps.LoadNotebook(full, rel);
        if (doc is null)
        {
            return Task.FromResult(new BuiltinToolResult(loadError!));
        }
        var cells = doc["cells"]!.AsArray();

        var error = operation switch
        {
            "replace" => NotebookOps.Replace(cells, args),
            "insert" => NotebookOps.Insert(cells, args),
            "delete" => NotebookOps.Delete(cells, args),
            _ => $"operation '{operation}' inválida — use replace|insert|delete.",
        };
        if (error is not null)
        {
            return Task.FromResult(new BuiltinToolResult(error));
        }

        // Validação final antes da escrita atômica — nunca grava nbformat
        // estruturalmente quebrado.
        if (JsonNode.Parse(doc.ToJsonString()) is not JsonObject check
            || check["cells"] is not JsonArray)
        {
            return Task.FromResult(new BuiltinToolResult(
                "Resultado inválido — notebook não gravado."));
        }
        var tmp = full + ".tmp";
        File.WriteAllText(tmp, doc.ToJsonString());
        File.Move(tmp, full, overwrite: true);
        return Task.FromResult(new BuiltinToolResult(
            $"ok — {operation} aplicado em '{rel}' ({cells.Count} células)."));
    }
}

/// <summary>Helpers nbformat compartilhados pelas tools de notebook.</summary>
internal static class NotebookOps
{
    /// <summary>Resolve path confinado ao workspace, exigindo extensão .ipynb.</summary>
    public static (string? Full, string Rel, string? Error) ResolveNotebookPath(
        JsonElement args, string workspace)
    {
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
        var full = WorkspaceFiles.ResolveInside(workspace, path, out var error);
        if (full is null)
        {
            return (null, path ?? "?", error);
        }
        if (!full.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase))
        {
            return (null, path!, $"'{path}' não é .ipynb — só notebooks Jupyter.");
        }
        var rel = WorkspaceFiles.RelativeOf(workspace, full);
        if (!File.Exists(full))
        {
            return (null, rel, $"Arquivo '{rel}' não existe.");
        }
        return (full, rel, null);
    }

    /// <summary>Lê e valida o JSON do notebook (exige objeto com cells[]).</summary>
    public static (JsonObject? Doc, string? Error) LoadNotebook(
        string full, string rel)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(full)) is JsonObject doc
                && doc["cells"] is JsonArray)
            {
                return (doc, null);
            }
            return (null, $"'{rel}' não é um notebook nbformat válido (cells ausente).");
        }
        catch (JsonException ex)
        {
            return (null, $"'{rel}' não é JSON válido: {ex.Message}");
        }
    }

    /// <summary>Endereça a célula por index (0-based) ou cell_id.</summary>
    public static int? CellIndex(JsonArray cells, JsonElement args)
    {
        if (args.TryGetProperty("cell_id", out var cid)
            && cid.GetString() is { Length: > 0 } id)
        {
            for (var i = 0; i < cells.Count; i++)
            {
                if (cells[i]?["id"]?.GetValue<string>() == id)
                {
                    return i;
                }
            }
            return -1;
        }
        if (args.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var i2))
        {
            return i2;
        }
        return null;
    }

    /// <summary>replace: novo source na célula endereçada; code zera
    /// outputs/execution_count.</summary>
    public static string? Replace(JsonArray cells, JsonElement args)
    {
        var idx = CellIndex(cells, args);
        if (idx is null)
        {
            return "Informe 'index' ou 'cell_id' para replace.";
        }
        if (idx < 0 || idx >= cells.Count)
        {
            return $"Índice {idx} fora de faixa (0..{cells.Count - 1}).";
        }
        if (!args.TryGetProperty("source", out var s) || s.GetString() is not { } src)
        {
            return "'source' é obrigatório para replace.";
        }
        var cell = (JsonObject)cells[idx.Value]!;
        cell["source"] = SourceNode(src);
        if (CellType(cell) == "code")
        {
            cell["outputs"] = new JsonArray();
            cell["execution_count"] = null;
        }
        return null;
    }

    /// <summary>insert: nova célula (code/markdown) no índice (default fim)
    /// com id único.</summary>
    public static string? Insert(JsonArray cells, JsonElement args)
    {
        var type = args.TryGetProperty("cell_type", out var t)
            ? t.GetString() : null;
        if (type is not ("code" or "markdown"))
        {
            return "'cell_type' é obrigatório para insert (code|markdown).";
        }
        if (!args.TryGetProperty("source", out var s) || s.GetString() is not { } src)
        {
            return "'source' é obrigatório para insert.";
        }
        var idx = args.TryGetProperty("index", out var ix)
            && ix.TryGetInt32(out var i2) ? i2 : cells.Count;
        if (idx < 0 || idx > cells.Count)
        {
            return $"Índice de inserção {idx} fora de faixa (0..{cells.Count}).";
        }

        var cell = new JsonObject
        {
            ["id"] = NewCellId(cells),
            ["cell_type"] = type,
            ["metadata"] = new JsonObject(),
            ["source"] = SourceNode(src),
        };
        if (type == "code")
        {
            cell["outputs"] = new JsonArray();
            cell["execution_count"] = null;
        }
        cells.Insert(idx, cell);
        return null;
    }

    /// <summary>delete: remove a célula endereçada.</summary>
    public static string? Delete(JsonArray cells, JsonElement args)
    {
        var idx = CellIndex(cells, args);
        if (idx is null)
        {
            return "Informe 'index' ou 'cell_id' para delete.";
        }
        if (idx < 0 || idx >= cells.Count)
        {
            return $"Índice {idx} fora de faixa (0..{cells.Count - 1}).";
        }
        cells.RemoveAt(idx.Value);
        return null;
    }

    /// <summary>Tipo da célula (code/markdown/raw).</summary>
    public static string CellType(JsonNode cell) =>
        cell["cell_type"]?.GetValue<string>() ?? "code";

    /// <summary>Source como texto — nbformat aceita string ou string[].</summary>
    public static string SourceText(JsonNode cell) =>
        cell["source"] switch
        {
            JsonArray a => string.Concat(a.Select(n => n?.GetValue<string>())),
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => string.Empty,
        };

    /// <summary>Grava source no formato canônico nbformat (array de linhas
    /// com \n preservado, última sem \n forçada).</summary>
    private static JsonNode SourceNode(string src)
    {
        var arr = new JsonArray();
        var lines = src.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            arr.Add(i < lines.Length - 1 ? lines[i] + "\n" : lines[i]);
        }
        return arr;
    }

    /// <summary>id de célula único no notebook (nbformat 4.5 exige ids).</summary>
    private static string NewCellId(JsonArray cells)
    {
        var existing = cells
            .Select(c => c?["id"]?.GetValue<string>())
            .Where(i => i is not null).ToHashSet();
        string id;
        do
        {
            id = $"cell-{Guid.NewGuid():N}"[..13];
        } while (existing.Contains(id));
        return id;
    }
}
