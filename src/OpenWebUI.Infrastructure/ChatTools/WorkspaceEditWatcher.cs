using System.Collections.Concurrent;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Vigia edições <em>externas</em> no workdir durante uma run
/// (SPEC-20261124-devin-parity-batch2 RF-002): quando o usuário toca um
/// arquivo por fora das tools, o próximo round recebe uma nota
/// <c>&lt;user_edits&gt;</c> com os paths — o modelo deixa de trabalhar
/// sobre cópia mental velha. Escritas das próprias tools são filtradas
/// via <see cref="MarkWrite"/>; ruído comum de build é ignorado.
/// </summary>
public sealed class WorkspaceEditWatcher : IDisposable
{
    /// <summary>Máximo de paths por drain.</summary>
    public const int DrainCap = 20;

    /// <summary>Janela em que um MarkWrite suprime o evento do watcher.</summary>
    private static readonly TimeSpan MarkWriteWindow = TimeSpan.FromMinutes(2);

    private static readonly string[] NoiseDirs =
        ["/.git/", "/bin/", "/obj/", "/node_modules/", "/.chat-jobs/",
         "/.chat-tmp/", "/.vs/", "/.idea/"];

    private static readonly string[] NoiseSuffixes = [".tmp", "~", ".swp", ".log"];

    private readonly string _workdir;
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, byte> _changed = new();
    private readonly ConcurrentDictionary<string, DateTime> _toolWrites = new();

    public WorkspaceEditWatcher(string workdir)
    {
        _workdir = Path.GetFullPath(workdir);
        _watcher = new FileSystemWatcher(_workdir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnEvent;
        _watcher.Created += OnEvent;
        _watcher.Renamed += (_, e) => Track(e.FullPath);
        _watcher.Deleted += OnEvent;
        // Falha de buffer/OS nunca pode derrubar a run.
        _watcher.Error += (_, _) => { };
    }

    private void OnEvent(object _, FileSystemEventArgs e) => Track(e.FullPath);

    /// <summary>Registra um path alterado (chamado pelo FSW; internal
    /// para os testes injetarem eventos sem depender de timing do SO).</summary>
    internal void Track(string fullPath)
    {
        if (!IsNoise(fullPath))
        {
            _changed[Path.GetFullPath(fullPath)] = 0;
        }
    }

    /// <summary>Marca path escrito por tool — suprime o evento do watcher.</summary>
    public void MarkWrite(string fullPath) =>
        _toolWrites[Path.GetFullPath(fullPath)] = DateTime.UtcNow;

    /// <summary>
    /// Devolve os paths relativos alterados desde o último drain
    /// (dedup, cap <see cref="DrainCap"/>), excluindo escritas de tools e
    /// ruído — e limpa o conjunto.
    /// </summary>
    public List<string> Drain()
    {
        var now = DateTime.UtcNow;
        var result = new List<string>();
        foreach (var path in _changed.Keys.ToArray())
        {
            _changed.TryRemove(path, out _);
            if (_toolWrites.TryGetValue(path, out var at)
                && now - at < MarkWriteWindow)
            {
                continue; // escrita nossa — não é edição do usuário.
            }
            var rel = Path.GetRelativePath(_workdir, path)
                .Replace('\\', '/');
            result.Add(rel);
            if (result.Count >= DrainCap)
            {
                break;
            }
        }
        // Limpa marks expirados para o dicionário não crescer sem fim.
        foreach (var (path, at) in _toolWrites.ToArray())
        {
            if (now - at >= MarkWriteWindow)
            {
                _toolWrites.TryRemove(path, out _);
            }
        }
        return result;
    }

    /// <summary>Path é ruído (build, VCS, logs internos) → ignorado.</summary>
    internal static bool IsNoise(string fullPath)
    {
        var normalized = "/" + fullPath.Replace('\\', '/').TrimStart('/');
        foreach (var dir in NoiseDirs)
        {
            if (normalized.Contains(dir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        foreach (var suffix in NoiseSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <inheritdoc />
    public void Dispose() => _watcher.Dispose();
}
