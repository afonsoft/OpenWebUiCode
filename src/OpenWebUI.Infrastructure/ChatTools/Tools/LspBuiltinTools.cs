using System.Text;
using System.Text.Json;
using OpenWebUI.Infrastructure.Lsp;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools.Tools;

/// <summary>
/// Base das tools <c>builtin:lsp_*</c> (SPEC-20261009-lsp-diagnostics RF-002):
/// somente leitura (Low — <see cref="IBuiltinChatTool.RequiresApproval"/> false),
/// paths jailed por <see cref="WorkspaceFiles.ResolveInside"/>, respostas
/// compactas com cap <see cref="LspOptions.ResultCap"/>.
/// </summary>
public abstract class LspBuiltinToolBase(LspService lsp, WorkspaceRepoService repos)
    : IBuiltinChatTool
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public abstract string ParametersJson { get; }

    /// <inheritdoc />
    public abstract Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct);

    /// <inheritdoc />
    public ApprovalTier Tier => ApprovalTier.Read;

    /// <inheritdoc />
    public bool SafeInParallel => true;

    protected LspService Lsp { get; } = lsp;

    /// <summary>
    /// Gate comum: LSP habilitado + repo vinculado. Devolve mensagem de erro
    /// quando a tool não deve rodar.
    /// </summary>
    protected async Task<string?> GuardAsync(BuiltinToolContext context, CancellationToken ct)
    {
        if (!Lsp.Enabled)
        {
            return "LSP desabilitado (Lsp:Enabled=false).";
        }
        if (await repos.GetBindingAsync(context.UserId, ct) is null)
        {
            return "Nenhum repositório vinculado ao workspace — lsp_* exige repo bound.";
        }
        return null;
    }

    /// <summary>Resolve path relativo jailed; erro pronto quando falha.</summary>
    protected string? ResolvePath(JsonElement args, BuiltinToolContext context, out string? error)
    {
        var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
        var full = WorkspaceFiles.ResolveInside(context.WorkspacePath, path, out error);
        if (full is null)
        {
            return null;
        }
        if (!File.Exists(full))
        {
            error = $"Arquivo '{path}' não existe.";
            return null;
        }
        return full;
    }

    /// <summary>Client pronto para o arquivo; (null, erro) quando indisponível.</summary>
    protected async Task<(LspClient? Client, string? Error)> ClientForAsync(
        BuiltinToolContext context, string absolutePath, CancellationToken ct)
    {
        var language = Lsp.LanguageFor(absolutePath);
        if (language is null)
        {
            return (null,
                $"Extensão de '{Path.GetFileName(absolutePath)}' sem servidor LSP mapeado.");
        }
        try
        {
            return (await Lsp.GetClientAsync(context.WorkspacePath, language, ct), null);
        }
        catch (LspUnavailableException ex)
        {
            return (null, $"LSP '{language.ServerKey}' indisponível: {ex.Message}");
        }
    }

    /// <summary>line/col 1-based do agente → 0-based do protocolo.</summary>
    protected static int Position(JsonElement args, string name)
    {
        var v = args.TryGetProperty(name, out var p) && p.TryGetInt32(out var n) ? n : 1;
        return Math.Max(0, v - 1);
    }

    protected static JsonElement TextDoc(string absolutePath) =>
        JsonSerializer.SerializeToElement(new
        {
            textDocument = new { uri = LspClient.UriForPath(absolutePath) },
        });

    /// <summary>
    /// Request posicional (definition/references): valida guard+path+servidor
    /// e formata locations <c>path:line name</c>.
    /// </summary>
    protected async Task<string> PositionRequestAsync(
        BuiltinToolContext context, JsonElement args, string method, CancellationToken ct)
    {
        if (await GuardAsync(context, ct) is { } denied)
        {
            return denied;
        }
        var full = ResolvePath(args, context, out var error);
        if (full is null)
        {
            return error!;
        }
        var (client, clientErr) = await ClientForAsync(context, full, ct);
        if (client is null)
        {
            return clientErr!;
        }

        var result = await client.RequestAsync(method, new
        {
            textDocument = new { uri = LspClient.UriForPath(full) },
            position = new
            {
                line = Position(args, "line"),
                character = Position(args, "col"),
            },
            context = method == "textDocument/references"
                ? new { includeDeclaration = true }
                : null,
        }, ct);
        return LspResponse.FormatLocations(result, context.WorkspacePath,
            LspOptions.ResultCap);
    }

    protected static string SeverityName(int severity) => severity switch
    {
        1 => "error",
        2 => "warning",
        3 => "info",
        _ => "hint",
    };

    /// <summary>Formata diagnostics: <c>path:line severity message</c>.</summary>
    protected static string FormatDiagnostics(
        IReadOnlyList<LspDiagnostic> diags, string workdir, int cap)
    {
        if (diags.Count == 0)
        {
            return "Sem diagnostics.";
        }
        var sb = new StringBuilder();
        var shown = diags.OrderBy(d => d.Path).ThenBy(d => d.Line).Take(cap).ToList();
        foreach (var d in shown)
        {
            sb.Append(WorkspaceFiles.RelativeOf(workdir, d.Path))
                .Append(':').Append(d.Line + 1)
                .Append(' ').Append(SeverityName(d.Severity))
                .Append(' ').AppendLine(d.Message.ReplaceLineEndings(" "));
        }
        if (diags.Count > shown.Count)
        {
            sb.Append($"[...{diags.Count - shown.Count} mais]");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// <c>builtin:lsp_diagnostics</c> — diagnostics do arquivo (ou do workdir
/// inteiro quando <c>path</c> é omitido).
/// </summary>
public sealed class LspDiagnosticsBuiltinTool(LspService lsp, WorkspaceRepoService repos)
    : LspBuiltinToolBase(lsp, repos)
{
    /// <inheritdoc />
    public override string Name => "lsp_diagnostics";

    /// <inheritdoc />
    public override string Description =>
        "Language-server diagnostics (errors/warnings) — call for a file or the whole workspace.";

    /// <inheritdoc />
    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File relative to the workspace (omit for workspace-wide diagnostics)." }
          }
        }
        """;

    /// <inheritdoc />
    public override async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        if (await GuardAsync(context, ct) is { } denied)
        {
            return new BuiltinToolResult(denied);
        }

        var path = args.TryGetProperty("path", out var p) ? p.GetString() : null;
        if (path is not null)
        {
            var full = ResolvePath(args, context, out var error);
            if (full is null)
            {
                return new BuiltinToolResult(error!);
            }
            var (client, clientErr) = await ClientForAsync(context, full, ct);
            if (client is null)
            {
                return new BuiltinToolResult(clientErr!);
            }

            // didOpen com o texto real + espera publishDiagnostics (cap 10s).
            await client.DidOpenAsync(full, await File.ReadAllTextAsync(full, ct), ct);
            await client.AwaitDiagnosticsAsync(full, TimeSpan.FromSeconds(10), ct);
        }

        var diags = Lsp.Diagnostics(context.WorkspacePath, null, out var running);
        if (!running && path is null)
        {
            return new BuiltinToolResult(
                "Nenhum servidor LSP rodando — informe 'path' para analisar um arquivo.");
        }
        return new BuiltinToolResult(
            FormatDiagnostics(diags, context.WorkspacePath, LspOptions.ResultCap));
    }
}

/// <summary>
/// <c>builtin:lsp_symbols</c> — símbolos de um arquivo (documentSymbol).
/// </summary>
public sealed class LspSymbolsBuiltinTool(LspService lsp, WorkspaceRepoService repos)
    : LspBuiltinToolBase(lsp, repos)
{
    /// <inheritdoc />
    public override string Name => "lsp_symbols";

    /// <inheritdoc />
    public override string Description =>
        "Document symbols (classes/functions) — call to outline a file.";

    /// <inheritdoc />
    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File relative to the workspace." }
          },
          "required": ["path"]
        }
        """;

    /// <inheritdoc />
    public override async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        if (await GuardAsync(context, ct) is { } denied)
        {
            return new BuiltinToolResult(denied);
        }
        var full = ResolvePath(args, context, out var error);
        if (full is null)
        {
            return new BuiltinToolResult(error!);
        }
        var (client, clientErr) = await ClientForAsync(context, full, ct);
        if (client is null)
        {
            return new BuiltinToolResult(clientErr!);
        }

        await client.DidOpenAsync(full, await File.ReadAllTextAsync(full, ct), ct);
        var result = await client.RequestAsync(
            "textDocument/documentSymbol", TextDoc(full), ct);
        var items = LspResponse.FlattenSymbols(result, full, LspOptions.ResultCap);
        if (items.Count == 0)
        {
            return new BuiltinToolResult("Sem símbolos.");
        }
        var sb = new StringBuilder();
        foreach (var s in items)
        {
            sb.Append(WorkspaceFiles.RelativeOf(context.WorkspacePath, s.Path))
                .Append(':').Append(s.Line + 1)
                .Append(' ').Append(LspResponse.SymbolKindName(s.Kind))
                .Append(' ').AppendLine(s.Name);
        }
        return new BuiltinToolResult(sb.ToString().TrimEnd(),
            new { count = items.Count, truncated = items.Count >= LspOptions.ResultCap });
    }
}

/// <summary>
/// <c>builtin:lsp_workspace_symbols</c> — busca de símbolo no workspace
/// (workspace/symbol). Linguagem opcional: usa um servidor já rodando,
/// senão o da <c>language</c> (csharp/typescript/javascript/python/json).
/// </summary>
public sealed class LspWorkspaceSymbolsBuiltinTool(LspService lsp, WorkspaceRepoService repos)
    : LspBuiltinToolBase(lsp, repos)
{
    /// <inheritdoc />
    public override string Name => "lsp_workspace_symbols";

    /// <inheritdoc />
    public override string Description =>
        "Search symbols workspace-wide — call to locate a class/function by name.";

    /// <inheritdoc />
    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Symbol name to search." },
            "language": { "type": "string", "description": "Server key when none is running yet (csharp, typescript, javascript, python, json).", "default": "csharp" }
          },
          "required": ["query"]
        }
        """;

    /// <inheritdoc />
    public override async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        if (await GuardAsync(context, ct) is { } denied)
        {
            return new BuiltinToolResult(denied);
        }
        var query = args.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
        {
            return new BuiltinToolResult("Parâmetro 'query' é obrigatório.");
        }
        var language = args.TryGetProperty("language", out var l) ? l.GetString() : null;

        JsonElement result;
        try
        {
            result = await Lsp.WorkspaceSymbolAsync(
                context.WorkspacePath, language ?? "csharp", query, ct);
        }
        catch (LspUnavailableException ex)
        {
            return new BuiltinToolResult($"LSP indisponível: {ex.Message}");
        }
        var items = LspResponse.FlattenWorkspaceSymbols(
            result, context.WorkspacePath, LspOptions.ResultCap);
        if (items.Count == 0)
        {
            return new BuiltinToolResult($"Nenhum símbolo para '{query}'.");
        }
        var sb = new StringBuilder();
        foreach (var s in items)
        {
            sb.Append(s.Path).Append(':').Append(s.Line + 1)
                .Append(' ').Append(LspResponse.SymbolKindName(s.Kind))
                .Append(' ').AppendLine(s.Name);
        }
        return new BuiltinToolResult(sb.ToString().TrimEnd(),
            new { count = items.Count, truncated = items.Count >= LspOptions.ResultCap });
    }
}

/// <summary>
/// <c>builtin:lsp_definition</c> — go-to-definition (textDocument/definition).
/// </summary>
public sealed class LspDefinitionBuiltinTool(LspService lsp, WorkspaceRepoService repos)
    : LspBuiltinToolBase(lsp, repos)
{
    /// <inheritdoc />
    public override string Name => "lsp_definition";

    /// <inheritdoc />
    public override string Description =>
        "Go to definition — call with path+line+col to find where a symbol is declared.";

    /// <inheritdoc />
    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File relative to the workspace." },
            "line": { "type": "integer", "description": "1-based line." },
            "col": { "type": "integer", "description": "1-based column." }
          },
          "required": ["path", "line", "col"]
        }
        """;

    /// <inheritdoc />
    public override async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct) =>
        new(await PositionRequestAsync(context, args, "textDocument/definition", ct));
}

/// <summary>
/// <c>builtin:lsp_references</c> — referências do símbolo (textDocument/references).
/// </summary>
public sealed class LspReferencesBuiltinTool(LspService lsp, WorkspaceRepoService repos)
    : LspBuiltinToolBase(lsp, repos)
{
    /// <inheritdoc />
    public override string Name => "lsp_references";

    /// <inheritdoc />
    public override string Description =>
        "Find references — call with path+line+col to list usages of a symbol.";

    /// <inheritdoc />
    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File relative to the workspace." },
            "line": { "type": "integer", "description": "1-based line." },
            "col": { "type": "integer", "description": "1-based column." }
          },
          "required": ["path", "line", "col"]
        }
        """;

    /// <inheritdoc />
    public override async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct) =>
        new(await PositionRequestAsync(context, args, "textDocument/references", ct));
}

/// <summary>
/// <c>builtin:lsp_hover</c> — hover (textDocument/hover): assinatura/doc.
/// </summary>
public sealed class LspHoverBuiltinTool(LspService lsp, WorkspaceRepoService repos)
    : LspBuiltinToolBase(lsp, repos)
{
    /// <inheritdoc />
    public override string Name => "lsp_hover";

    /// <inheritdoc />
    public override string Description =>
        "Hover info — call with path+line+col for signature/docs of a symbol.";

    /// <inheritdoc />
    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File relative to the workspace." },
            "line": { "type": "integer", "description": "1-based line." },
            "col": { "type": "integer", "description": "1-based column." }
          },
          "required": ["path", "line", "col"]
        }
        """;

    /// <inheritdoc />
    public override async Task<BuiltinToolResult> ExecuteAsync(
        JsonElement args, BuiltinToolContext context, CancellationToken ct)
    {
        if (await GuardAsync(context, ct) is { } denied)
        {
            return new BuiltinToolResult(denied);
        }
        var full = ResolvePath(args, context, out var error);
        if (full is null)
        {
            return new BuiltinToolResult(error!);
        }
        var (client, clientErr) = await ClientForAsync(context, full, ct);
        if (client is null)
        {
            return new BuiltinToolResult(clientErr!);
        }

        var result = await client.RequestAsync("textDocument/hover", new
        {
            textDocument = new { uri = LspClient.UriForPath(full) },
            position = new
            {
                line = Position(args, "line"),
                character = Position(args, "col"),
            },
        }, ct);
        var text = LspResponse.HoverText(result);
        return new BuiltinToolResult(text ?? "Sem informação de hover.");
    }
}
