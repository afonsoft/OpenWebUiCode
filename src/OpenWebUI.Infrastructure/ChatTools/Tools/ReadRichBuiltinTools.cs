using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// <c>builtin read_archive</c> — lista o conteúdo de um arquivo
/// <c>.zip</c> no workspace ou extrai um membro como texto
/// (SPEC-20261128-read-rich-tools, plano P1.17: leitura rica estilo
/// oh-my-pi/Devin). Read-only, mesmo sandbox do <c>file_read</c>.
/// </summary>
public sealed class ReadArchiveBuiltinTool : IBuiltinChatTool
{
    /// <summary>Máximo de entradas listadas por chamada.</summary>
    private const int MaxEntries = 500;

    /// <summary>Cap de texto extraído de um membro.</summary>
    private const int MaxMemberChars = 64 * 1024;

    /// <inheritdoc />
    public string Name => "read_archive";

    /// <inheritdoc />
    public string Description =>
        "List the entries of a .zip archive in the workspace, or extract "
            + "one member as text (member: <name>). Read-only.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path of the .zip relative to the workspace." },
            "member": { "type": "string", "description": "Optional entry name to extract as text." }
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
        if (!args.TryGetProperty("path", out var pathEl) || string.IsNullOrWhiteSpace(pathEl.GetString()))
        {
            return new BuiltinToolResult("Erro: 'path' é obrigatório.");
        }
        var full = WorkspaceFiles.ResolveInsideOrExternal(
            context.WorkspacePath, pathEl.GetString(), null, context.RunId, out var error);
        if (full is null)
        {
            return new BuiltinToolResult($"Erro: {error}");
        }
        if (!File.Exists(full))
        {
            return new BuiltinToolResult($"Erro: arquivo '{pathEl.GetString()}' não encontrado.");
        }

        try
        {
            await using var stream = File.OpenRead(full);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var member = args.TryGetProperty("member", out var m)
                ? m.GetString()?.Trim()
                : null;
            if (member is not null)
            {
                var entry = zip.GetEntry(member);
                if (entry is null)
                {
                    return new BuiltinToolResult($"Erro: membro '{member}' não existe no arquivo.");
                }
                await using var ms = entry.Open();
                var buf = new byte[Math.Min(entry.Length, MaxMemberChars)];
                var read = 0;
                while (read < buf.Length)
                {
                    var n = await ms.ReadAsync(buf.AsMemory(read, buf.Length - read), ct);
                    if (n == 0) break;
                    read += n;
                }
                var text = Encoding.UTF8.GetString(buf, 0, read);
                if (text.Contains('\0'))
                {
                    return new BuiltinToolResult(
                        $"Erro: membro '{member}' parece binário — extraia no workspace.");
                }
                if (entry.Length > MaxMemberChars)
                {
                    text += $"\n… (truncado em {MaxMemberChars} de {entry.Length} bytes)";
                }
                return new BuiltinToolResult(text);
            }

            var sb = new StringBuilder();
            sb.AppendLine($"# {pathEl.GetString()} — {zip.Entries.Count} entradas");
            var shown = 0;
            foreach (var entry in zip.Entries)
            {
                if (shown++ >= MaxEntries)
                {
                    sb.AppendLine($"… +{zip.Entries.Count - MaxEntries} entradas");
                    break;
                }
                sb.AppendLine($"{entry.Length,10}  {entry.FullName}");
            }
            return new BuiltinToolResult(sb.ToString());
        }
        catch (InvalidDataException)
        {
            return new BuiltinToolResult($"Erro: '{pathEl.GetString()}' não é um zip válido.");
        }
    }
}

/// <summary>
/// <c>builtin read_sqlite</c> — inspeciona um SQLite do workspace:
/// sem <c>query</c> lista tabelas com contagem de linhas; com <c>query</c>
/// executa um SELECT read-only (cap 200 linhas × 20 colunas). Read-only,
/// conexão aberta em <c>Mode=ReadOnly</c> — não cria WAL nem toca o arquivo.
/// </summary>
public sealed class ReadSqliteBuiltinTool : IBuiltinChatTool
{
    /// <summary>Máximo de tabelas listadas.</summary>
    private const int MaxTables = 100;

    /// <summary>Cap de linhas/colunas retornadas por SELECT.</summary>
    private const int MaxRows = 200, MaxCols = 20;

    /// <inheritdoc />
    public string Name => "read_sqlite";

    /// <inheritdoc />
    public string Description =>
        "Inspect a SQLite database in the workspace: without 'query' it "
            + "lists tables with row counts; with 'query' it runs a "
            + "read-only SELECT (max 200 rows). Read-only.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path of the .db/.sqlite relative to the workspace." },
            "query": { "type": "string", "description": "Optional SELECT to run (read-only, 200 rows max)." }
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
        if (!args.TryGetProperty("path", out var pathEl) || string.IsNullOrWhiteSpace(pathEl.GetString()))
        {
            return new BuiltinToolResult("Erro: 'path' é obrigatório.");
        }
        var full = WorkspaceFiles.ResolveInsideOrExternal(
            context.WorkspacePath, pathEl.GetString(), null, context.RunId, out var error);
        if (full is null)
        {
            return new BuiltinToolResult($"Erro: {error}");
        }
        if (!File.Exists(full))
        {
            return new BuiltinToolResult($"Erro: arquivo '{pathEl.GetString()}' não encontrado.");
        }

        var query = args.TryGetProperty("query", out var q)
            ? q.GetString()?.Trim()
            : null;
        if (query is not null
            && !query.StartsWith("select", StringComparison.OrdinalIgnoreCase))
        {
            return new BuiltinToolResult("Erro: apenas SELECT é permitido.");
        }

        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = full,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();
            await using var conn = new SqliteConnection(cs);
            await conn.OpenAsync(ct);

            var sb = new StringBuilder();
            if (query is null)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT name FROM sqlite_master WHERE type='table' "
                        + "AND name NOT LIKE 'sqlite_%' ORDER BY name LIMIT "
                        + MaxTables;
                var tables = new List<string>();
                await using (var rd = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await rd.ReadAsync(ct))
                    {
                        tables.Add(rd.GetString(0));
                    }
                }
                sb.AppendLine($"# {pathEl.GetString()} — {tables.Count} tabelas");
                foreach (var t in tables)
                {
                    await using var countCmd = conn.CreateCommand();
                    countCmd.CommandText =
                        $"SELECT COUNT(*) FROM \"{t.Replace("\"", "\"\"")}\"";
                    var n = await countCmd.ExecuteScalarAsync(ct);
                    sb.AppendLine($"{n,10}  {t}");
                }
                return new BuiltinToolResult(sb.ToString());
            }

            await using var qcmd = conn.CreateCommand();
            qcmd.CommandText = query;
            await using var reader = await qcmd.ExecuteReaderAsync(ct);
            var cols = Math.Min(reader.FieldCount, MaxCols);
            for (var i = 0; i < cols; i++)
            {
                sb.Append(i == 0 ? "| " : " | ").Append(reader.GetName(i));
            }
            sb.AppendLine(" |");
            var rows = 0;
            while (rows < MaxRows && await reader.ReadAsync(ct))
            {
                for (var i = 0; i < cols; i++)
                {
                    sb.Append(i == 0 ? "| " : " | ")
                        .Append(reader.IsDBNull(i) ? "NULL" : reader.GetValue(i));
                }
                sb.AppendLine(" |");
                rows++;
            }
            if (await reader.ReadAsync(ct))
            {
                sb.AppendLine($"… (mais de {MaxRows} linhas)");
            }
            return new BuiltinToolResult(
                rows == 0 ? "Query retornou 0 linhas." : sb.ToString());
        }
        catch (SqliteException ex)
        {
            return new BuiltinToolResult($"Erro SQLite: {ex.Message}");
        }
    }
}

/// <summary>
/// <c>builtin read_pdf</c> — extrai texto de um PDF do workspace
/// (SPEC-20261128-read-pdf, plano P1.17). Cap de 20 páginas por chamada
/// e 64 KiB de texto; <c>startPage</c> (1-based) permite paginar.
/// Read-only, mesmo sandbox do <c>file_read</c>.
/// </summary>
public sealed class ReadPdfBuiltinTool : IBuiltinChatTool
{
    /// <summary>Máximo de páginas extraídas por chamada.</summary>
    private const int MaxPages = 20;

    /// <summary>Cap de texto extraído por chamada.</summary>
    private const int MaxChars = 64 * 1024;

    /// <inheritdoc />
    public string Name => "read_pdf";

    /// <inheritdoc />
    public string Description =>
        "Extract text from a PDF in the workspace. Use startPage (1-based) "
            + "to paginate large documents (max 20 pages / 64 KiB per call). Read-only.";

    /// <inheritdoc />
    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path of the PDF relative to the workspace." },
            "startPage": { "type": "integer", "description": "Optional 1-based first page to extract (default 1)." }
          },
          "required": ["path"]
        }
        """;

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public bool SafeInParallel => true;

    /// <inheritdoc />
    public async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        if (!args.TryGetProperty("path", out var pathEl)
            || pathEl.ValueKind is not JsonValueKind.String
            || string.IsNullOrWhiteSpace(pathEl.GetString()))
        {
            return new BuiltinToolResult("Erro: 'path' é obrigatório.");
        }
        var relative = pathEl.GetString()!;
        var full = WorkspaceFiles.ResolveInsideOrExternal(
            context.WorkspacePath, relative, null, context.RunId, out var error);
        if (full is null)
        {
            return new BuiltinToolResult($"Erro: {error}");
        }
        if (!File.Exists(full))
        {
            return new BuiltinToolResult($"Erro: arquivo '{relative}' não encontrado.");
        }

        var startPage = 1;
        if (args.TryGetProperty("startPage", out var startEl)
            && (startEl.ValueKind is not JsonValueKind.Number
                || !startEl.TryGetInt32(out startPage)
                || startPage < 1))
        {
            return new BuiltinToolResult("Erro: 'startPage' deve ser inteiro >= 1.");
        }

        try
        {
            return await Task.Run(() => Extract(full, relative, startPage), ct);
        }
        catch (PdfDocumentEncryptedException)
        {
            return new BuiltinToolResult($"Erro: PDF '{relative}' está criptografado.");
        }
        catch (PdfDocumentFormatException ex)
        {
            return new BuiltinToolResult($"Erro: PDF inválido em '{relative}': {ex.Message}");
        }
        catch (IOException ex)
        {
            return new BuiltinToolResult($"Erro ao ler '{relative}': {ex.Message}");
        }
    }

    private static BuiltinToolResult Extract(string full, string relative, int startPage)
    {
        using var document = PdfDocument.Open(full);
        var totalPages = document.NumberOfPages;
        if (startPage > totalPages)
        {
            return new BuiltinToolResult(
                $"Erro: 'startPage' {startPage} além do fim — '{relative}' tem {totalPages} página(s).");
        }

        var endPage = Math.Min(startPage + MaxPages - 1, totalPages);
        var sb = new StringBuilder();
        sb.AppendLine($"# {relative} — {totalPages} página(s), exibindo {startPage}-{endPage}");
        var chars = 0;
        var truncatedChars = false;
        var lastCompletePage = startPage - 1;
        for (var pageNumber = startPage; pageNumber <= endPage; pageNumber++)
        {
            var text = document.GetPage(pageNumber).Text ?? string.Empty;
            var block = $"\n## Página {pageNumber}\n{text.Trim()}\n";
            if (chars + block.Length > MaxChars)
            {
                var remaining = MaxChars - chars;
                if (remaining > 0)
                {
                    sb.Append(block[..remaining]);
                }
                truncatedChars = true;
                break;
            }
            sb.Append(block);
            chars += block.Length;
            lastCompletePage = pageNumber;
        }

        if (truncatedChars)
        {
            sb.AppendLine($"\n… (texto truncado em {MaxChars} chars)");
        }
        if (!truncatedChars && endPage < totalPages)
        {
            sb.AppendLine(
                $"\n… {totalPages - endPage} página(s) restantes — use startPage={endPage + 1}");
        }
        else if (truncatedChars && lastCompletePage < totalPages)
        {
            sb.AppendLine(
                $"… documento continua — use startPage={Math.Max(startPage, lastCompletePage + 1)}");
        }

        return new BuiltinToolResult(sb.ToString(), new
        {
            pages = totalPages,
            startPage,
            endPage = truncatedChars ? lastCompletePage : endPage,
            truncated = truncatedChars || endPage < totalPages,
        });
    }
}
