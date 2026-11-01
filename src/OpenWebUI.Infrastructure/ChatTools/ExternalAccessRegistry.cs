using System.Collections.Concurrent;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Registro de paths externos autorizados por run
/// (SPEC-20261018-permissions-v2 RF-003): quando a regra
/// <c>external_directory</c> do usuário é <c>allow</c> — ou o dono
/// aprova o prompt — o gate registra o path absoluto resolvido e a tool
/// de arquivo consulta este registro em vez de bater no jail do
/// workspace. Chaveado por run: uma aprovação "uma vez" não vaza para a
/// próxima run; <see cref="Clear"/> é chamado quando a run termina.
/// </summary>
public sealed class ExternalAccessRegistry
{
    private readonly ConcurrentDictionary<string, byte> _allowed =
        new(StringComparer.Ordinal);

    private static string Key(string? runId, string path) => $"{runId}|{path}";

    /// <summary>Libera o path absoluto para a run.</summary>
    public void Allow(string? runId, string path) =>
        _allowed[Key(runId, path)] = 0;

    /// <summary>Se o path absoluto foi liberado para a run.</summary>
    public bool IsAllowed(string? runId, string path) =>
        runId is not null && _allowed.ContainsKey(Key(runId, path));

    /// <summary>Remove todas as liberações da run (fim da run).</summary>
    public void Clear(string runId)
    {
        foreach (var key in _allowed.Keys)
        {
            if (key.StartsWith(runId + "|", StringComparison.Ordinal))
            {
                _allowed.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Resolve um path de arg de tool que não coube no jail do workspace:
    /// retorna o caminho absoluto quando <paramref name="candidate"/> é um
    /// path existente/fora do workdir já liberado para a run, senão
    /// <c>null</c>. Usado pelas tools de arquivo como escape autorizado.
    /// </summary>
    public string? ResolveAllowed(
        string? runId, string workspace, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }
        var trimmed = candidate.Trim();
        string full;
        try
        {
            full = Path.IsPathRooted(trimmed)
                ? Path.GetFullPath(trimmed)
                : Path.GetFullPath(Path.Join(workspace, trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
        return IsAllowed(runId, full) ? full : null;
    }
}
