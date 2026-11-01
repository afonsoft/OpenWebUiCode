using System.Collections.Concurrent;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.Services;

namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Estado por run do tool-search-deferred
/// (SPEC-20261031-tool-search-deferred): tools permitidas mas marcadas
/// <c>BuiltinTools:Deferred</c> saem do spec anunciado e ficam num mapa
/// por run — <c>builtin_tool_search</c> carrega o schema sob demanda e a
/// tool volta ao anúncio no round seguinte. Indexado por runId e por
/// chatId (o contributor do system prompt conhece só o chat).
/// </summary>
public sealed class DeferredToolState
{
    private sealed class Scope
    {
        public object Gate { get; } = new();
        public Dictionary<string, Tool> Deferred { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Tool> Loaded { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly ConcurrentDictionary<string, Scope> _scopes =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _chatToRun =
        new(StringComparer.Ordinal);

    private static string Key(Tool tool) =>
        ToolExecutor.FunctionName(tool) ?? tool.Name;

    /// <summary>Registra o conjunto deferido da run (idempotente).</summary>
    public void Set(string runId, string? chatId, IEnumerable<Tool> deferred)
    {
        var scope = new Scope();
        foreach (var tool in deferred)
        {
            scope.Deferred[Key(tool)] = tool;
        }
        if (scope.Deferred.Count == 0)
        {
            return;
        }
        _scopes[runId] = scope;
        if (!string.IsNullOrEmpty(chatId))
        {
            _chatToRun[chatId] = runId;
        }
    }

    private Scope? ScopeFor(string? key)
    {
        if (key is null)
        {
            return null;
        }
        if (_scopes.TryGetValue(key, out var scope))
        {
            return scope;
        }
        return _chatToRun.TryGetValue(key, out var runId)
            && _scopes.TryGetValue(runId, out scope)
                ? scope
                : null;
    }

    /// <summary>A run/chat tem tools deferidas ativas.</summary>
    public bool IsActive(string? key) => ScopeFor(key) is not null;

    /// <summary>Tools ainda deferidas (não anunciadas ao modelo).</summary>
    public IReadOnlyList<Tool> Deferred(string? key)
    {
        var scope = ScopeFor(key);
        if (scope is null)
        {
            return [];
        }
        lock (scope.Gate)
        {
            return scope.Deferred.Values.ToList();
        }
    }

    /// <summary>Tools já carregadas via tool_search (voltam ao anúncio).</summary>
    public IReadOnlyList<Tool> Loaded(string? key)
    {
        var scope = ScopeFor(key);
        if (scope is null)
        {
            return [];
        }
        lock (scope.Gate)
        {
            return scope.Loaded.Values.ToList();
        }
    }

    /// <summary>
    /// <paramref name="functionName"/> ainda está deferido nesta run —
    /// chamada direta deve falhar com "use tool_search".
    /// </summary>
    public bool IsDeferredOnly(string? key, string functionName)
    {
        var scope = ScopeFor(key);
        if (scope is null)
        {
            return false;
        }
        lock (scope.Gate)
        {
            return scope.Deferred.ContainsKey(functionName);
        }
    }

    /// <summary>Variação por objeto Tool (após FindTool).</summary>
    public bool IsDeferredOnly(string? key, Tool tool) =>
        IsDeferredOnly(key, Key(tool));

    /// <summary>
    /// Promove a tool deferida a carregada. Devolve a tool ou null quando o
    /// nome não estava deferido (nunca cria acesso a tool fora do permitido).
    /// </summary>
    public Tool? Load(string? key, string functionName)
    {
        var scope = ScopeFor(key);
        if (scope is null)
        {
            return null;
        }
        lock (scope.Gate)
        {
            if (!scope.Deferred.Remove(functionName, out var tool))
            {
                return null;
            }
            scope.Loaded[functionName] = tool;
            return tool;
        }
    }

    /// <summary>Libera o estado da run (fim do turno).</summary>
    public void Clear(string? runId, string? chatId = null)
    {
        if (runId is not null)
        {
            _scopes.TryRemove(runId, out _);
        }
        if (chatId is not null)
        {
            _chatToRun.TryRemove(chatId, out _);
        }
    }
}
