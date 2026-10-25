using System.Collections.Frozen;
using System.Text;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// SPEC-20261018-attachments-v2 RF-001: anexos de texto/código entram no
/// prompt como blocos <c>&lt;file name="…"&gt;</c> dentro da própria
/// mensagem (paridade opencode v2) — antes só <c>image/*</c> chegava ao
/// modelo. Resolve decode (UTF-8 com fallback latin1), detecta binário
/// por NUL bytes, aplica caps por arquivo e por mensagem e marca
/// truncamento com <c>[…truncated]</c>.
/// </summary>
public static class AttachmentContentService
{
    /// <summary>Máximo de caracteres lidos de um único arquivo (256 KB).</summary>
    public const int MaxFileChars = 256 * 1024;

    /// <summary>Máximo agregado de anexos de texto por mensagem (1 MB).</summary>
    public const int MaxMessageChars = 1024 * 1024;

    /// <summary>Bytes inspecionados no sniff de binário (presença de NUL).</summary>
    private const int BinarySniffBytes = 8192;

    /// <summary>Marcador de truncamento dentro do bloco de arquivo.</summary>
    private const string TruncatedMarker = "[…truncated]";

    /// <summary>Marcador de arquivo binário omitido do prompt.</summary>
    private const string BinaryMarker = "[binary file — content omitted]";

    /// <summary>
    /// Extensões tratadas como texto (superset da whitelist do upload —
    /// mesmas regras de <see cref="Endpoints.FileEndpoints"/> mais as de
    /// código comuns do v2).
    /// </summary>
    private static readonly FrozenSet<string> TextExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl",
        ".xml", ".html", ".htm", ".log", ".yaml", ".yml", ".toml", ".ini",
        ".cfg", ".env", ".cs", ".razor", ".cshtml", ".py", ".js", ".ts",
        ".tsx", ".jsx", ".mjs", ".cjs", ".css", ".scss", ".less", ".sql",
        ".sh", ".bash", ".zsh", ".ps1", ".bat", ".cmd", ".java", ".kt",
        ".go", ".rs", ".rb", ".php", ".c", ".h", ".cpp", ".hpp", ".swift",
        ".lua", ".r", ".scala", ".fs", ".fsx", ".vb", ".pl", ".pm",
        ".dockerfile", ".gitignore", ".editorconfig", ".sln", ".slnx",
        ".csproj", ".props", ".targets", ".diff", ".patch",
    }.ToFrozenSet();

    /// <summary>MIME types de aplicação que carregam como texto.</summary>
    private static readonly HashSet<string> TextAppMimes = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "application/json", "application/xml", "application/yaml",
        "application/x-yaml", "application/csv", "application/javascript",
        "application/typescript", "application/x-sh", "application/toml",
        "application/x-ndjson", "application/sql",
    };

    /// <summary>
    /// O arquivo é candidato a bloco de texto? <c>text/*</c>, MIME de
    /// aplicação textual, extensão da whitelist ou <see cref="FileEntry.
    /// ExtractedText"/> já extraído pelo upload.
    /// </summary>
    public static bool IsTextCandidate(FileEntry file)
    {
        if (file.ExtractedText is not null)
        {
            return true;
        }
        var mime = file.ContentType;
        if (!string.IsNullOrWhiteSpace(mime)
            && (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || TextAppMimes.Contains(mime)))
        {
            return true;
        }
        return TextExtensions.Contains(Path.GetExtension(file.Filename));
    }

    /// <summary>
    /// Lê o conteúdo textual do anexo — <see cref="FileEntry.ExtractedText"/>
    /// quando já extraído, senão decodifica do storage (UTF-8 estrito →
    /// latin1). Binário (NUL nos primeiros 8KB) retorna null.
    /// </summary>
    public static async Task<string?> TryReadTextAsync(
        FileEntry file, CancellationToken ct = default)
    {
        if (file.ExtractedText is { } extracted)
        {
            return extracted;
        }
        if (!IsTextCandidate(file) || !File.Exists(file.StoragePath))
        {
            return null;
        }
        var info = new FileInfo(file.StoragePath);
        var sniffLen = (int)Math.Min(info.Length, BinarySniffBytes);
        var sniff = new byte[sniffLen];
        await using (var fs = File.OpenRead(file.StoragePath))
        {
            var read = 0;
            while (read < sniffLen)
            {
                read += await fs.ReadAsync(
                    sniff.AsMemory(read, sniffLen - read), ct);
            }
        }
        if (sniff.AsSpan().IndexOf((byte)0) >= 0)
        {
            return null; // binário — não vai pro prompt.
        }
        string text;
        try
        {
            text = await File.ReadAllTextAsync(
                file.StoragePath, new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true), ct);
        }
        catch (DecoderFallbackException)
        {
            text = await File.ReadAllTextAsync(
                file.StoragePath, Encoding.Latin1, ct);
        }
        return text;
    }

    /// <summary>
    /// Envolve o texto no bloco <c>&lt;file name="…"&gt;</c> com cap por
    /// arquivo e marcador de truncamento.
    /// </summary>
    public static string WrapFile(string name, string text)
    {
        var sb = new StringBuilder();
        sb.Append("<file name=\"").Append(name.Replace("\"", "'")).Append("\">\n");
        if (text.Length > MaxFileChars)
        {
            sb.Append(text, 0, MaxFileChars).Append('\n')
                .Append(TruncatedMarker).Append('\n');
        }
        else
        {
            sb.Append(text);
            if (!text.EndsWith('\n'))
            {
                sb.Append('\n');
            }
        }
        return sb.Append("</file>").ToString();
    }

    /// <summary>Bloco marcador para anexo binário (o modelo fica ciente).</summary>
    public static string WrapBinary(string name) =>
        $"<file name=\"{name.Replace("\"", "'")}\">{BinaryMarker}</file>";

    /// <summary>
    /// Injeta os anexos de texto nas mensagens donas (RF-001): cada
    /// <see cref="ChatCompletionMessage"/> com <c>FileIds</c> ganha os
    /// blocos <c>&lt;file&gt;</c> ao fim do <c>Content</c>, respeitando
    /// <see cref="MaxMessageChars"/> por mensagem. Binários viram
    /// marcador; arquivos ilegíveis são omitidos.
    /// </summary>
    /// <param name="messages">Mensagens mutáveis do pipeline.</param>
    /// <param name="entries">Anexos resolvidos (por id) do dono.</param>
    /// <param name="lastUserFileIds">
    /// FileIds do request (pertencem à última mensagem do usuário).
    /// </param>
    /// <param name="reader">Leitor de conteúdo (seam de teste).</param>
    /// <param name="ct">Cancelamento.</param>
    /// <returns>Mensagens atualizadas (mesma lista quando nada a injetar).</returns>
    public static async Task<List<ChatCompletionMessage>> InjectTextFilesAsync(
        List<ChatCompletionMessage> messages,
        IReadOnlyDictionary<string, FileEntry> entries,
        IReadOnlyList<string>? lastUserFileIds,
        Func<FileEntry, CancellationToken, Task<string?>> reader,
        CancellationToken ct = default)
    {
        var lastUserIdx = messages.FindLastIndex(m => m.Role == "user");
        for (var i = 0; i < messages.Count; i++)
        {
            var ids = messages[i].FileIds?.ToList() ?? [];
            if (i == lastUserIdx && lastUserFileIds is not null)
            {
                ids.AddRange(lastUserFileIds);
            }
            if (ids.Count == 0)
            {
                continue;
            }
            var injected = await BuildBlocksAsync(ids, entries, reader, ct);
            if (injected.Length > 0)
            {
                var m = messages[i];
                messages[i] = m with { Content = (m.Content ?? "") + injected };
            }
        }
        return messages;
    }

    /// <summary>Monta os blocos &lt;file&gt; de uma mensagem dentro do cap de 1 MB.</summary>
    private static async Task<StringBuilder> BuildBlocksAsync(
        List<string> ids,
        IReadOnlyDictionary<string, FileEntry> entries,
        Func<FileEntry, CancellationToken, Task<string?>> reader,
        CancellationToken ct)
    {
        var budget = MaxMessageChars;
        var sb = new StringBuilder();
        foreach (var fid in ids.Distinct())
        {
            var block = await BlockForAsync(fid, entries, reader, ct);
            if (block is null)
            {
                continue;
            }
            if (block.Length > budget)
            {
                block = block[..budget] + TruncatedMarker;
                budget = 0;
            }
            else
            {
                budget -= block.Length;
            }
            sb.Append('\n').Append(block);
            if (budget <= 0)
            {
                break;
            }
        }
        return sb;
    }

    /// <summary>Bloco de um anexo: texto → &lt;file&gt;, binário → marcador, ilegível → null.</summary>
    private static async Task<string?> BlockForAsync(
        string fid,
        IReadOnlyDictionary<string, FileEntry> entries,
        Func<FileEntry, CancellationToken, Task<string?>> reader,
        CancellationToken ct)
    {
        if (!entries.TryGetValue(fid, out var file)
            || file.ContentType?.StartsWith("image/") == true)
        {
            return null; // imagens já viajam no payload multimodal.
        }
        if (!IsTextCandidate(file))
        {
            return WrapBinary(file.Filename);
        }
        string? text;
        try
        {
            text = await reader(file, ct);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException or OperationCanceledException)
        {
            return null; // ilegível — omitido, não derruba a run.
        }
        return text is null ? WrapBinary(file.Filename) : WrapFile(file.Filename, text);
    }
}
