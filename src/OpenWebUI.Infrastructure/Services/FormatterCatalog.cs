using System.Collections.Frozen;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Uma entrada de formatter: extensões cobertas, comando com placeholders
/// <c>{files}</c>/<c>{file}</c>, binário usado na detecção, variáveis de
/// ambiente extras e timeout opcional (default global quando null).
/// </summary>
public sealed record FormatterEntry(
    string Name,
    IReadOnlyList<string> Extensions,
    string Command,
    string Binary,
    bool Builtin = true,
    IReadOnlyDictionary<string, string>? Env = null,
    int? TimeoutSeconds = null);

/// <summary>Formatter efetivo resolvido para uma extensão.</summary>
/// <param name="Entry">Entrada escolhida (ou null quando nada aplica).</param>
/// <param name="Source">custom-binding | custom-user | builtin | none.</param>
public sealed record ResolvedFormatter(FormatterEntry? Entry, string Source);

/// <summary>
/// SPEC-20261018-formatters-catalog RF-001/RF-002: catálogo de
/// formatters por extensão com fallback ordenado — comando custom do
/// binding (um por repo, aplica a tudo) → custom global do usuário
/// (<c>Format:Commands:{ext}</c>) → catálogo builtin detectado por
/// binário. Sem match → no-op.
/// </summary>
public static class FormatterCatalog
{
    /// <summary>Catálogo builtin — só usado quando o binário existe no PATH.</summary>
    public static readonly IReadOnlyList<FormatterEntry> Builtins =
    [
        new("dotnet-format", [".cs", ".razor", ".csproj"],
            "dotnet format --include {files}", "dotnet"),
        new("csharpier", [".cs", ".razor"],
            "csharpier format {files}", "csharpier"),
        new("ruff", [".py"], "ruff format {files}", "ruff"),
        new("black", [".py"], "black --quiet {files}", "black"),
        new("prettier",
            [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".json",
             ".md", ".css", ".scss", ".html", ".yaml", ".yml"],
            "prettier --write {files}", "prettier"),
        new("dprint",
            [".ts", ".tsx", ".js", ".jsx", ".json", ".md"],
            "dprint fmt {files}", "dprint"),
        new("gofmt", [".go"], "gofmt -w {files}", "gofmt"),
        new("rustfmt", [".rs"], "rustfmt --edition 2021 {files}", "rustfmt"),
        new("shfmt", [".sh", ".bash"], "shfmt -w {files}", "shfmt"),
        new("stylua", [".lua"], "stylua {files}", "stylua"),
        new("pg-format", [".sql"], "pg_format -i {files}", "pg_format"),
    ];

    /// <summary>Extensão (com ponto, lower) → entries builtin que a cobrem.</summary>
    private static readonly FrozenDictionary<string, List<FormatterEntry>> ByExt =
        BuildIndex();

    private static FrozenDictionary<string, List<FormatterEntry>> BuildIndex()
    {
        var map = new Dictionary<string, List<FormatterEntry>>(StringComparer.Ordinal);
        foreach (var entry in Builtins)
        {
            foreach (var ext in entry.Extensions)
            {
                if (!map.TryGetValue(ext, out var list))
                {
                    map[ext] = list = [];
                }
                list.Add(entry);
            }
        }
        return map.ToFrozenDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// Resolve o formatter efetivo de <paramref name="ext"/>:
    /// <paramref name="bindingCommand"/> (um comando por repo — ganha de
    /// tudo) → <paramref name="customCommand"/> (Format:Commands:{ext})
    /// → primeiro builtin cujo binário responde ao probe.
    /// </summary>
    public static async Task<ResolvedFormatter> ResolveAsync(
        string ext,
        string? bindingCommand,
        string? customCommand,
        Func<string, CancellationToken, Task<bool>> binaryAvailable,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(bindingCommand))
        {
            return new ResolvedFormatter(
                new FormatterEntry("binding", [ext], bindingCommand, "", Builtin: false),
                "custom-binding");
        }
        if (!string.IsNullOrWhiteSpace(customCommand))
        {
            return new ResolvedFormatter(
                new FormatterEntry($"custom:{ext}", [ext], customCommand, "", Builtin: false),
                "custom-user");
        }
        if (ByExt.TryGetValue(ext, out var candidates))
        {
            foreach (var entry in candidates)
            {
                if (await binaryAvailable(entry.Binary, ct))
                {
                    return new ResolvedFormatter(entry, "builtin");
                }
            }
        }
        return new ResolvedFormatter(null, "none");
    }

    /// <summary>Agrupa paths por extensão (com ponto, lower; sem ext → ".").</summary>
    public static IReadOnlyDictionary<string, List<string>> GroupByExtension(
        IReadOnlyList<string> paths)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in paths)
        {
            var ext = Path.GetExtension(p);
            if (string.IsNullOrEmpty(ext))
            {
                ext = ".";
            }
            ext = ext.ToLowerInvariant();
            if (!groups.TryGetValue(ext, out var list))
            {
                groups[ext] = list = [];
            }
            list.Add(p);
        }
        return groups;
    }
}
