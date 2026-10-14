using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin:file_list</c> — lista entradas de um diretório do workspace
/// (SPEC-20261007-chat-agent-parity RF-011). Somente leitura.
/// </summary>
public sealed class FileListBuiltinTool : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "file_list";

    /// <inheritdoc />
    public string Description =>
        "List a directory — call to see workspace files/sizes.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Directory relative to the workspace (default '.').", "default": "." },
            "recursive": { "type": "boolean", "description": "true = descend into subdirectories (up to 500 entries).", "default": false }
          }
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
        var full = WorkspaceFiles.ResolveInside(context.WorkspacePath, path, out var error);
        if (full is null)
        {
            return Task.FromResult(new BuiltinToolResult(error));
        }

        if (!Directory.Exists(full))
        {
            return Task.FromResult(new BuiltinToolResult(
                File.Exists(full)
                    ? $"'{path}' é um arquivo — use file_read."
                    : $"Diretório '{path ?? "."}' não existe."));
        }

        var recursive = args.TryGetProperty("recursive", out var r)
            && r.ValueKind is JsonValueKind.True;
        var root = Path.GetFullPath(context.WorkspacePath);
        var sb = new StringBuilder();
        var count = 0;

        var pending = new Queue<(string Dir, int Depth)>();
        pending.Enqueue((full, 0));
        while (pending.Count > 0 && count < WorkspaceFiles.MaxEntries)
        {
            var (dir, depth) = pending.Dequeue();
            List<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir)
                    .OrderByDescending(Directory.Exists)
                    .ThenBy(e => e, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                if (count >= WorkspaceFiles.MaxEntries)
                {
                    break;
                }

                var indent = new string(' ', depth * 2);
                if (Directory.Exists(entry))
                {
                    sb.Append(indent).Append(Path.GetFileName(entry)).Append('/').Append('\n');
                    if (recursive
                        && !Path.GetFileName(entry).Equals(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        pending.Enqueue((entry, depth + 1));
                    }
                }
                else
                {
                    var size = new FileInfo(entry).Length;
                    sb.Append(indent).Append(Path.GetFileName(entry))
                        .Append("  (").Append(FormatSize(size)).Append(')').Append('\n');
                }

                count++;
            }
        }

        var text = sb.Length == 0 ? "(diretório vazio)" : sb.ToString().TrimEnd();
        if (count >= WorkspaceEntriesCap)
        {
            text += $"\n[listagem truncada em {WorkspaceEntriesCap} entradas]";
        }

        return Task.FromResult(new BuiltinToolResult(text, new
        {
            path = WorkspaceFiles.RelativeOf(root, full),
            entries = count,
        }));
    }

    private const int WorkspaceEntriesCap = WorkspaceFiles.MaxEntries;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}

/// <summary>
/// <c>builtin:file_read</c> — lê um arquivo do workspace com numeração de
/// linhas e paginação (<paramref>offset</paramref>/<paramref>limit</paramref>).
/// Somente leitura.
/// </summary>
public sealed class FileReadBuiltinTool(HistoryResourceService history) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "file_read";

    /// <inheritdoc />
    public string Description =>
        "Read a file (numbered, paged) — call to inspect code/text. "
        + "Also accepts history://{chatId|current}[/tail:{n}|/msg:{id}|/call:{id}] "
        + "to read the persisted chat transcript (including messages compacted "
        + "out of the context window).";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path relative to the workspace, or a history:// URI." },
            "offset": { "type": "integer", "description": "First line (1-based, default 1).", "default": 1 },
            "limit": { "type": "integer", "description": "Lines to read (default 400, max 2000).", "default": 400 }
          },
          "required": ["path"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
        var full = WorkspaceFiles.ResolveInside(context.WorkspacePath, path, out var error);
        if (full is null)
        {
            return new BuiltinToolResult(error);
        }

        if (Directory.Exists(full))
        {
            return new BuiltinToolResult($"'{path}' é um diretório — use file_list.");
        }

        if (!File.Exists(full))
        {
            return new BuiltinToolResult($"Arquivo '{path}' não existe.");
        }

        if (new FileInfo(full).Length > WorkspaceFiles.MaxFileBytes)
        {
            return new BuiltinToolResult(
                $"Arquivo '{path}' excede {WorkspaceFiles.MaxFileBytes / 1024}KB — leia por partes "
                + "com offset/limit após um file_grep para achar a região.");
        }

        if (WorkspaceFiles.LooksBinary(full))
        {
            return new BuiltinToolResult($"Arquivo '{path}' parece binário — leitura negada.");
        }

        var offset = args.TryGetProperty("offset", out var o) && o.TryGetInt32(out var off)
            ? Math.Max(1, off) : 1;
        var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lim)
            ? Math.Clamp(lim, 1, 2000) : 400;

        var lines = (await File.ReadAllLinesAsync(full, ct));
        var rel = WorkspaceFiles.RelativeOf(context.WorkspacePath, full);
        var sb = new StringBuilder($"{rel} ({lines.Length} linhas)\n");
        for (var i = offset - 1; i < lines.Length && i < offset - 1 + limit; i++)
        {
            sb.Append($"{i + 1,6}: {lines[i]}\n");
        }

        if (offset - 1 + limit < lines.Length)
        {
            sb.Append($"[truncado — restam {lines.Length - (offset - 1 + limit)} linhas; "
                + $"continue com offset={offset + limit}]");
        }

        return new BuiltinToolResult(sb.ToString().TrimEnd(), new
        {
            path = rel,
            totalLines = lines.Length,
            offset,
            limit,
        });
    }
}

/// <summary>
/// <c>builtin:file_grep</c> — busca regex no workspace retornando
/// <c>path:linha: conteúdo</c>. Somente leitura.
/// </summary>
public sealed class FileGrepBuiltinTool(HistoryResourceService history) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "file_grep";

    /// <inheritdoc />
    public string Description =>
        "Regex-search file contents — call to find text/code. "
        + "path also accepts a history:// URI to grep the persisted chat transcript.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Regex to search for." },
            "path": { "type": "string", "description": "Relative directory/file (default '.').", "default": "." },
            "glob": { "type": "string", "description": "File filter (e.g. '*.cs', 'src/**')." },
            "ignore_case": { "type": "boolean", "default": true },
            "max_results": { "type": "integer", "description": "Match cap (default 100).", "default": 100 }
          },
          "required": ["pattern"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var pattern = args.TryGetProperty("pattern", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new BuiltinToolResult("Parâmetro 'pattern' é obrigatório.");
        }

        Regex regex;
        try
        {
            var ic = !args.TryGetProperty("ignore_case", out var icEl)
                || icEl.ValueKind is not JsonValueKind.False;
            regex = new Regex(pattern,
                RegexOptions.Compiled | (ic ? RegexOptions.IgnoreCase : RegexOptions.None));
        }
        catch (ArgumentException ex)
        {
            return new BuiltinToolResult($"Regex inválida: {ex.Message}");
        }

        var max = args.TryGetProperty("max_results", out var m) && m.TryGetInt32(out var mx)
            ? Math.Clamp(mx, 1, 500) : 100;

        var path = args.TryGetProperty("path", out var pEl) ? pEl.GetString() : null;
        if (HistoryResourceService.IsHistoryUri(path))
        {
            var (transcript, histError) = await history.ResolveAsync(
                path!, context.UserId, context.ChatId, ct);
            if (transcript is null)
            {
                return new BuiltinToolResult(histError ?? "histórico vazio.");
            }
            var hits = transcript.Split('\n')
                .Where(line => regex.IsMatch(line))
                .Take(max)
                .ToList();
            return new BuiltinToolResult(hits.Count == 0
                ? "Sem matches no histórico."
                : string.Join('\n', hits.Select(h => $"history: {h}")));
        }
        var full = WorkspaceFiles.ResolveInside(context.WorkspacePath, path, out var error);
        if (full is null)
        {
            return new BuiltinToolResult(error);
        }

        var globPattern = args.TryGetProperty("glob", out var g) ? g.GetString() : null;
        Regex? glob = !string.IsNullOrWhiteSpace(globPattern)
            ? WorkspaceFiles.GlobToRegex(globPattern)
            : null;
        // Sem '/' o glob casa o basename (convenção rg/gitignore); com '/', o relativo inteiro.
        var globOnBasename = globPattern?.Contains('/') == false;

        IEnumerable<string> files = File.Exists(full)
            ? [full]
            : Directory.Exists(full) ? WorkspaceFiles.EnumerateFiles(full) : [];
        if (glob is not null)
        {
            files = files.Where(f => glob.IsMatch(globOnBasename
                ? Path.GetFileName(f)
                : WorkspaceFiles.RelativeOf(context.WorkspacePath, f)));
        }

        var root = Path.GetFullPath(context.WorkspacePath);
        var sb = new StringBuilder();
        var matches = 0;
        var scanned = 0;
        foreach (var file in files)
        {
            if (matches >= max)
            {
                break;
            }

            if (new FileInfo(file).Length > WorkspaceFiles.MaxFileBytes
                || WorkspaceFiles.LooksBinary(file))
            {
                continue;
            }

            scanned++;
            var rel = WorkspaceFiles.RelativeOf(root, file);
            var lineNo = 0;
            await foreach (var line in ReadLinesAsync(file, ct))
            {
                lineNo++;
                if (regex.IsMatch(line))
                {
                    sb.Append(rel).Append(':').Append(lineNo).Append(": ").Append(line).Append('\n');
                    if (++matches >= max)
                    {
                        break;
                    }
                }
            }
        }

        var text = matches == 0
            ? $"Nenhum match de '{pattern}' ({scanned} arquivos lidos)."
            : sb.ToString().TrimEnd()
              + (matches >= max ? $"\n[truncado em {max} matches]" : string.Empty);
        return new BuiltinToolResult(text, new { pattern, matches, scanned });
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(
        string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            yield return line;
        }
    }
}

/// <summary>
/// <c>builtin:file_glob</c> — casamento de arquivos por glob
/// (<c>**/*.cs</c>, <c>{a,b}</c>, <c>[abc]</c>) no workspace. Somente leitura.
/// </summary>
public sealed class FileGlobBuiltinTool : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "file_glob";

    /// <inheritdoc />
    public string Description =>
        "Find files by glob — call to locate e.g. 'src/**/*.cs'.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "description": "Glob (supports **, *, ?, {}, [])." },
            "path": { "type": "string", "description": "Relative directory to search from (default '.').", "default": "." }
          },
          "required": ["pattern"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var pattern = args.TryGetProperty("pattern", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Task.FromResult(new BuiltinToolResult("Parâmetro 'pattern' é obrigatório."));
        }

        var path = args.TryGetProperty("path", out var pEl) ? pEl.GetString() : null;
        var full = WorkspaceFiles.ResolveInside(context.WorkspacePath, path, out var error);
        if (full is null)
        {
            return Task.FromResult(new BuiltinToolResult(error));
        }

        if (!Directory.Exists(full))
        {
            return Task.FromResult(new BuiltinToolResult($"Diretório '{path ?? "."}' não existe."));
        }

        var regex = WorkspaceFiles.GlobToRegex(pattern);
        var matches = WorkspaceFiles.EnumerateFiles(full)
            .Select(f => WorkspaceFiles.RelativeOf(context.WorkspacePath, f))
            .Where(rel => regex.IsMatch(rel))
            .OrderBy(rel => rel, StringComparer.OrdinalIgnoreCase)
            .Take(WorkspaceFiles.MaxEntries)
            .ToList();

        var text = matches.Count == 0
            ? $"Nenhum arquivo casa '{pattern}'."
            : string.Join('\n', matches);
        return Task.FromResult(new BuiltinToolResult(text, new
        {
            pattern,
            matches = matches.Count,
            files = matches,
        }));
    }
}

/// <summary>
/// <c>builtin:file_write</c> — cria ou sobrescreve um arquivo do
/// workspace; retorna o diff unificado da mudança. Mutável → gate de
/// aprovação. Depois da escrita roda o format hook
/// (<see cref="FormatHookService"/>) — falha vira warning no result.
/// </summary>
/// <param name="lsp">Notificações didChange do servidor LSP (S8).</param>
/// <param name="formatHook">Format hook pós-escrita (RF-004).</param>
public sealed class FileWriteBuiltinTool(
    Lsp.LspService? lsp = null,
    FormatHookService? formatHook = null) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "file_write";

    /// <inheritdoc />
    public string Description =>
        "Create/overwrite a file — call to write full content.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path relative to the workspace (dirs are created)." },
            "content": { "type": "string", "description": "Full file content." }
          },
          "required": ["path", "content"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Write;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
        var content = args.TryGetProperty("content", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(path) || content is null)
        {
            return new BuiltinToolResult("Parâmetros 'path' e 'content' são obrigatórios.");
        }

        var full = WorkspaceFiles.ResolveInside(context.WorkspacePath, path, out var error);
        if (full is null)
        {
            return new BuiltinToolResult(error);
        }

        if (Directory.Exists(full))
        {
            return new BuiltinToolResult($"'{path}' é um diretório — escrita negada.");
        }

        if (Encoding.UTF8.GetByteCount(content) > WorkspaceFiles.MaxFileBytes)
        {
            return new BuiltinToolResult(
                $"Conteúdo excede {WorkspaceFiles.MaxFileBytes / 1024}KB — escrita negada.");
        }

        var existed = File.Exists(full);
        var oldText = existed ? await File.ReadAllTextAsync(full, ct) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, ct);
        // LSP didChange — best-effort (SPEC S8): o servidor vê conteúdo real.
        if (lsp is not null)
        {
            await lsp.NotifyFileWrittenAsync(context.WorkspacePath, full, ct);
        }

        var rel = WorkspaceFiles.RelativeOf(context.WorkspacePath, full);
        var diff = UnifiedDiff.Compute(rel, oldText, content);
        var lines = content.Replace("\r\n", "\n").Split('\n').Length;
        var text = existed
            ? $"Arquivo '{rel}' atualizado (+{diff.Added}/-{diff.Removed} linhas)."
            : $"Arquivo '{rel}' criado ({lines} linhas).";
        if (diff.Text.Length > 0)
        {
            text += "\n" + Truncate(diff.Text, 8192);
        }

        // Format hook (RF-004): falha → warning, nunca erro da tool.
        var formatWarning = formatHook is null ? null : await formatHook.RunForUserAsync(
            context.UserId, context.WorkspacePath, [rel], ct, context.ChatId);
        if (formatWarning is not null)
        {
            text += $"\n[format] {formatWarning}";
        }

        return new BuiltinToolResult(text, new
        {
            path = rel,
            created = !existed,
            added = diff.Added,
            removed = diff.Removed,
            diff = Truncate(diff.Text, 8192),
            formatWarning,
        });
    }

    internal static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "\n[diff truncado]";
}

/// <summary>
/// <c>builtin:file_edit</c> — substituição exata de texto num arquivo do
/// workspace (estilo opencode/Claude edit): <c>old_string</c> deve casar
/// exatamente uma vez (ou <c>replace_all</c>). Retorna o diff unificado.
/// Mutável → gate de aprovação. Depois da edição roda o format hook
/// (<see cref="FormatHookService"/>) — falha vira warning no result.
/// </summary>
/// <param name="lsp">Notificações didChange do servidor LSP (S8).</param>
/// <param name="formatHook">Format hook pós-escrita (RF-004).</param>
public sealed class FileEditBuiltinTool(
    Lsp.LspService? lsp = null,
    FormatHookService? formatHook = null) : IBuiltinChatTool
{
    /// <inheritdoc />
    public string Name => "file_edit";

    /// <inheritdoc />
    public string Description =>
        "Replace exact text in a file — call for targeted edits.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path relative to the workspace." },
            "old_string": { "type": "string", "description": "Exact snippet to replace." },
            "new_string": { "type": "string", "description": "Replacement snippet." },
            "replace_all": { "type": "boolean", "description": "true = replace every occurrence.", "default": false }
          },
          "required": ["path", "old_string", "new_string"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Write;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
        var oldString = args.TryGetProperty("old_string", out var o) ? o.GetString() : null;
        var newString = args.TryGetProperty("new_string", out var n) ? n.GetString() : null;
        if (string.IsNullOrEmpty(path) || oldString is null || newString is null)
        {
            return new BuiltinToolResult(
                "Parâmetros 'path', 'old_string' e 'new_string' são obrigatórios.");
        }

        var full = WorkspaceFiles.ResolveInside(context.WorkspacePath, path, out var error);
        if (full is null)
        {
            return new BuiltinToolResult(error);
        }

        if (!File.Exists(full))
        {
            return new BuiltinToolResult($"Arquivo '{path}' não existe — use file_write para criar.");
        }

        var oldText = await File.ReadAllTextAsync(full, ct);
        var occurrences = Occurrences(oldText, oldString);
        var replaceAll = args.TryGetProperty("replace_all", out var ra)
            && ra.ValueKind is JsonValueKind.True;
        var guard = CheckOccurrences(occurrences, replaceAll, path);
        if (guard is not null)
        {
            return guard;
        }

        var newText = replaceAll
            ? oldText.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceFirst(oldText, oldString, newString);
        await File.WriteAllTextAsync(full, newText, ct);
        // LSP didChange — best-effort (SPEC S8).
        if (lsp is not null)
        {
            await lsp.NotifyFileWrittenAsync(context.WorkspacePath, full, ct);
        }

        return await BuildResultAsync(context, full, oldText, newText, occurrences, ct);
    }

    /// <summary>Guarda de ocorrências: 0 → erro de não-achado; &gt;1 sem replace_all → erro de ambiguidade.</summary>
    private static BuiltinToolResult? CheckOccurrences(int occurrences, bool replaceAll, string path)
    {
        if (occurrences == 0)
        {
            return new BuiltinToolResult(
                $"'old_string' não encontrado em '{path}' — releia com file_read e tente "
                + "um trecho exato (atenção a indentação e quebras de linha).");
        }

        if (occurrences > 1 && !replaceAll)
        {
            return new BuiltinToolResult(
                $"'old_string' aparece {occurrences}× em '{path}' — inclua mais contexto "
                + "para um trecho único, ou replace_all=true para trocar todas.");
        }

        return null;
    }

    /// <summary>Diff + format hook + resultado estruturado da edição.</summary>
    private async Task<BuiltinToolResult> BuildResultAsync(
        BuiltinToolContext context, string full, string oldText, string newText,
        int occurrences, CancellationToken ct)
    {
        var rel = WorkspaceFiles.RelativeOf(context.WorkspacePath, full);
        var diff = UnifiedDiff.Compute(rel, oldText, newText);
        var text = $"Arquivo '{rel}' editado (+{diff.Added}/-{diff.Removed} linhas).";
        if (diff.Text.Length > 0)
        {
            text += "\n" + FileWriteBuiltinTool.Truncate(diff.Text, 8192);
        }

        // Format hook (RF-004): falha → warning, nunca erro da tool.
        var formatWarning = formatHook is null ? null : await formatHook.RunForUserAsync(
            context.UserId, context.WorkspacePath, [rel], ct, context.ChatId);
        if (formatWarning is not null)
        {
            text += $"\n[format] {formatWarning}";
        }

        return new BuiltinToolResult(text, new
        {
            path = rel,
            added = diff.Added,
            removed = diff.Removed,
            occurrences,
            diff = FileWriteBuiltinTool.Truncate(diff.Text, 8192),
            formatWarning,
        });
    }

    private static int Occurrences(string haystack, string needle)
    {
        if (needle.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var i = text.IndexOf(oldValue, StringComparison.Ordinal);
        return i < 0 ? text : string.Concat(text.AsSpan(0, i), newValue, text.AsSpan(i + oldValue.Length));
    }
}
