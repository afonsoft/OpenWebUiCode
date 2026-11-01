using System.Collections.Concurrent;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Resolve nomes de executáveis para path absoluto via <c>PATH</c> (com cache),
/// evitando PATH-hijack (Sonar S4036) e exigência de caminho absoluto no
/// <see cref="System.Diagnostics.ProcessStartInfo.FileName"/>.
/// </summary>
internal static class ExecutableResolver
{
    private static readonly ConcurrentDictionary<string, string> s_cache = new(StringComparer.Ordinal);

    /// <summary>Resolve <paramref name="name"/> para path absoluto; lança se ausente no PATH.</summary>
    public static string Resolve(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (Path.IsPathRooted(name))
        {
            return File.Exists(name)
                ? name
                : throw new InvalidOperationException($"Executável '{name}' não encontrado.");
        }

        return s_cache.GetOrAdd(name, static n => FindOnPath(n)
            ?? throw new InvalidOperationException(
                $"Executável '{n}' não encontrado no PATH."));
    }

    /// <summary>Varre os diretórios do <c>PATH</c> atrás do executável
    /// (no Windows, tenta <c>.exe/.cmd/.bat</c> quando o nome não tem
    /// extensão). Null quando ausente.</summary>
    private static string? FindOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }
        var candidates = OperatingSystem.IsWindows() && !Path.HasExtension(name)
            ? new[] { name + ".exe", name + ".cmd", name + ".bat", name }
            : new[] { name };
        foreach (var dir in pathEnv.Split(
            Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                var full = Path.Join(dir, candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }
        return null;
    }
}
