using Microsoft.Extensions.Configuration;

namespace OpenWebUI.Infrastructure.Lsp;

/// <summary>
/// Tipos do cliente Language Server Protocol (SPEC-20261009-lsp-diagnostics,
/// E16 S8): opções de configuração, mapa extensão→servidor e DTOs compactos
/// devolvidos às tools/endpoints (coordenadas 0-based, como no LSP).
/// </summary>
public sealed record LspServerSpec(string Command, IReadOnlyList<string> Args);

/// <summary>Servidor LSP associado a uma extensão de arquivo.</summary>
public sealed record LspLanguage(string ServerKey, string LanguageId);

/// <summary>Estado de um servidor por (workdir, linguagem).</summary>
public enum LspServerState
{
    /// <summary>Nunca spawnado (ainda não demandado).</summary>
    NotStarted,
    /// <summary>Rodando e inicializado.</summary>
    Running,
    /// <summary>Morreu e será respawnado na próxima requisição (restart ≤3).</summary>
    Crashed,
    /// <summary>Binário ausente, init falhou ou restarts esgotados.</summary>
    Unavailable,
}

/// <summary>Diagnóstico publicado por um servidor (publishDiagnostics).</summary>
public sealed record LspDiagnostic(
    string Path,
    int Line,
    int Col,
    int EndLine,
    int EndCol,
    int Severity,
    string? Code,
    string? Source,
    string Message);

/// <summary>Localização (definition/references) — path relativo + range.</summary>
public sealed record LspLocation(
    string Path, int Line, int Col, int EndLine, int EndCol);

/// <summary>Símbolo achatado (documentSymbol / workspaceSymbol).</summary>
public sealed record LspSymbolItem(
    string Name, int Kind, string Path, int Line, int Col, string? Container);

/// <summary>Conteúdo de hover com o range associado.</summary>
public sealed record LspHoverInfo(
    string Contents, int Line, int Col, int EndLine, int EndCol);

/// <summary>Falha de request JSON-RPC reportada pelo servidor.</summary>
public sealed class LspRequestException(string method, int code, string message)
    : InvalidOperationException($"LSP '{method}' falhou ({code}): {message}");

/// <summary>IDs de linguagem LSP suportados pelo mapa extensão→servidor.</summary>
internal static class LspLanguageIds
{
    internal const string CSharp = "csharp";
    internal const string TypeScript = "typescript";
    internal const string JavaScript = "javascript";
    internal const string Python = "python";
}

/// <summary>Servidor indisponível (binário ausente / init falhou ou restarts esgotados).</summary>
public sealed class LspUnavailableException(string detail)
    : InvalidOperationException(detail);

/// <summary>
/// Opções do subsistema LSP: <c>Lsp:Enabled</c> (default on),
/// <c>Lsp:RequestTimeoutSeconds</c> (30) e o mapa
/// <c>Lsp:Servers:{lang}:{Command,Args}</c> — sem entrada no config vale o
/// default embutido (csharp-ls, typescript-language-server, pylsp,
/// vscode-json-languageserver). Binários NUNCA são instalados em runtime.
/// </summary>
public sealed class LspOptions
{
    /// <summary>Cap global de itens em respostas de tools/endpoints.</summary>
    public const int ResultCap = 50;

    /// <summary>Ctor padrão (testes e config programática).</summary>
    public LspOptions() { }

    public bool Enabled { get; init; } = true;
    public int RequestTimeoutSeconds { get; init; } = 30;
    public int MaxRestartAttempts { get; init; } = 3;
    public int RestartBaseDelayMs { get; init; } = 250;
    public int ShutdownTimeoutSeconds { get; init; } = 4;
    public int MaxServersPerWorkdir { get; init; } = 2;
    public IReadOnlyDictionary<string, LspServerSpec> Servers { get; init; } = Defaults();

    /// <summary>Mapa padrão de servidores por linguagem.</summary>
    public static IReadOnlyDictionary<string, LspServerSpec> Defaults() =>
        new Dictionary<string, LspServerSpec>(StringComparer.OrdinalIgnoreCase)
        {
            [LspLanguageIds.CSharp] = new("csharp-ls", []),
            [LspLanguageIds.TypeScript] = new("typescript-language-server", ["--stdio"]),
            [LspLanguageIds.JavaScript] = new("typescript-language-server", ["--stdio"]),
            [LspLanguageIds.Python] = new("pylsp", []),
            ["json"] = new("vscode-json-languageserver", ["--stdio"]),
        };

    /// <summary>Carrega do <see cref="IConfiguration"/> (Lsp:* / LSP_* env).</summary>
    public static LspOptions Load(IConfiguration configuration)
    {
        var options = new LspOptions
        {
            Enabled = ParseBool(configuration, "Lsp:Enabled", "LSP_ENABLED") ?? true,
            RequestTimeoutSeconds = Math.Clamp(ParseInt(
                configuration, "Lsp:RequestTimeoutSeconds", "LSP_REQUEST_TIMEOUT_SECONDS") ?? 30, 1, 300),
            MaxRestartAttempts = Math.Clamp(ParseInt(
                configuration, "Lsp:MaxRestartAttempts", "LSP_MAX_RESTART_ATTEMPTS") ?? 3, 0, 10),
            MaxServersPerWorkdir = Math.Clamp(ParseInt(
                configuration, "Lsp:MaxServersPerWorkdir", "LSP_MAX_SERVERS_PER_WORKDIR") ?? 2, 1, 8),
        };

        // Config sobrepõe defaults por chave (merge, não replace).
        var servers = new Dictionary<string, LspServerSpec>(
            Defaults(), StringComparer.OrdinalIgnoreCase);
        var section = configuration.GetSection("Lsp:Servers");
        foreach (var child in section.GetChildren())
        {
            var command = child["Command"];
            if (string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            var args = child.GetSection("Args").Get<string[]>();
            if (args is null)
            {
                args = child["Args"] is { } flat
                    ? flat.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [];
            }
            servers[child.Key] = new LspServerSpec(command.Trim(), args);
        }

        return new LspOptions
        {
            Enabled = options.Enabled,
            RequestTimeoutSeconds = options.RequestTimeoutSeconds,
            MaxRestartAttempts = options.MaxRestartAttempts,
            RestartBaseDelayMs = options.RestartBaseDelayMs,
            ShutdownTimeoutSeconds = options.ShutdownTimeoutSeconds,
            MaxServersPerWorkdir = options.MaxServersPerWorkdir,
            Servers = servers,
        };
    }

    private static bool? ParseBool(IConfiguration configuration, string key, string env)
    {
        if (bool.TryParse(configuration[key], out var v))
        {
            return v;
        }
        if (bool.TryParse(Environment.GetEnvironmentVariable(env), out v))
        {
            return v;
        }
        return null;
    }

    private static int? ParseInt(IConfiguration configuration, string key, string env)
    {
        if (int.TryParse(configuration[key], out var v))
        {
            return v;
        }
        if (int.TryParse(Environment.GetEnvironmentVariable(env), out v))
        {
            return v;
        }
        return null;
    }
}

/// <summary>
/// Mapa extensão → (servidor, languageId LSP). Aliases como
/// <c>.tsx→typescriptreact</c> vivem aqui — o nome do servidor no config é a
/// chave do mapa (csharp, typescript, javascript, python, json).
/// </summary>
public static class LspLanguageMap
{
    private static readonly IReadOnlyDictionary<string, LspLanguage> ByExtension =
        new Dictionary<string, LspLanguage>(StringComparer.OrdinalIgnoreCase)
        {
            [".cs"] = new(LspLanguageIds.CSharp, LspLanguageIds.CSharp),
            [".csx"] = new(LspLanguageIds.CSharp, LspLanguageIds.CSharp),
            [".ts"] = new(LspLanguageIds.TypeScript, LspLanguageIds.TypeScript),
            [".mts"] = new(LspLanguageIds.TypeScript, LspLanguageIds.TypeScript),
            [".cts"] = new(LspLanguageIds.TypeScript, LspLanguageIds.TypeScript),
            [".tsx"] = new(LspLanguageIds.TypeScript, "typescriptreact"),
            [".js"] = new(LspLanguageIds.JavaScript, LspLanguageIds.JavaScript),
            [".mjs"] = new(LspLanguageIds.JavaScript, LspLanguageIds.JavaScript),
            [".cjs"] = new(LspLanguageIds.JavaScript, LspLanguageIds.JavaScript),
            [".jsx"] = new(LspLanguageIds.JavaScript, "javascriptreact"),
            [".py"] = new(LspLanguageIds.Python, LspLanguageIds.Python),
            [".pyi"] = new(LspLanguageIds.Python, LspLanguageIds.Python),
            [".json"] = new("json", "json"),
            [".jsonc"] = new("json", "jsonc"),
        };

    /// <summary>Linguagem LSP do arquivo, ou null quando não mapeada.</summary>
    public static LspLanguage? ForPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var ext = Path.GetExtension(path);
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var language)
            ? language : null;
    }
}
