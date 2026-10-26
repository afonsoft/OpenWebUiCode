using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.SystemPrompt;

/// <summary>
/// Contexto compartilhado dos contribuidores de system prompt
/// (SPEC-20261021-harness-seams RF-B1): carrega os inputs do request mais
/// estado mutável que contribuidores podem ajustar (modelo resolvido,
/// parâmetros mesclados, modelo custom encontrado). Factories memoizadas
/// expõem o binding/workdir do workspace sem repetir a consulta.
/// Vive em Infrastructure porque referencia entidades de Domain —
/// Application não referencia Domain neste codebase.
/// </summary>
public sealed class PromptContext
{
    /// <summary>Request de completion original.</summary>
    public required ChatCompletionRequest Request { get; init; }

    /// <summary>Dono da conversa (isolamento de dados).</summary>
    public required User User { get; init; }

    /// <summary>Chat da run, quando numa conversa persistida.</summary>
    public string? ChatId { get; init; }

    /// <summary>Modo efetivo do agente (<c>chat</c>|<c>plan</c>|<c>build</c>).</summary>
    public string? AgentMode { get; init; }

    /// <summary>Cancelamento do request.</summary>
    public required CancellationToken Ct { get; init; }

    /// <summary>
    /// Texto do último user message — base para RAG/referências/web search.
    /// </summary>
    public required string LastUserText { get; init; }

    /// <summary>
    /// Modelo efetivo — contribuidores podem redirecionar (modelo custom
    /// aponta para o base).
    /// </summary>
    public required string Model { get; set; }

    /// <summary>
    /// Parâmetros efetivos — contribuidores podem mesclar (params do
    /// modelo custom).
    /// </summary>
    public Dictionary<string, object>? Parameters { get; set; }

    /// <summary>Modelo custom resolvido (setado pelo contributor dono).</summary>
    public ModelEntry? CustomModel { get; set; }

    /// <summary>
    /// Aliases de referências anexadas via menções <c>ref:alias</c>
    /// (SPEC-20261018-references) — populado pelo MentionsContributor e
    /// propagado ao <c>BuiltinToolContext.AttachedRefs</c>.
    /// </summary>
    public HashSet<string> AttachedRefs { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Factory memoizada do binding do workspace (chat → usuário).</summary>
    public Func<CancellationToken, Task<WorkspaceRepoBinding?>>? BindingFactory { get; init; }

    /// <summary>Factory memoizada do workdir efetivo.</summary>
    public Func<CancellationToken, Task<string>>? WorkdirFactory { get; init; }

    private Task<WorkspaceRepoBinding?>? _bindingTask;
    private Task<string>? _workdirTask;

    /// <summary>Binding resolvido uma vez por request (memoizado).</summary>
    public Task<WorkspaceRepoBinding?> GetRepoBindingAsync() =>
        _bindingTask ??= BindingFactory?.Invoke(Ct) ?? Task.FromResult<WorkspaceRepoBinding?>(null);

    /// <summary>Workdir resolvido uma vez por request (memoizado).</summary>
    public Task<string> GetWorkdirAsync() =>
        _workdirTask ??= WorkdirFactory?.Invoke(Ct) ?? Task.FromResult(string.Empty);
}

/// <summary>
/// Contribuidor de seção do system prompt
/// (SPEC-20261021-harness-seams RF-B1): cada bloco do antigo
/// <c>systemParts</c> virou um contributor com <see cref="Order"/> fixo —
/// o composer itera por ordem crescente e concatena as seções na mesma
/// posição de antes. Registrado via DI
/// (<see cref="System.Collections.Generic.IEnumerable{T}"/>); exceção de
/// um contributor é tolerada pelo composer (seção omitida + LogWarning).
/// </summary>
public interface ISystemPromptContributor
{
    /// <summary>Posição na concatenação (menor primeiro).</summary>
    int Order { get; }

    /// <summary>
    /// Produz as seções de system prompt do contributor (0..N strings;
    /// entram na ordem retornada). Pode ajustar <see cref="PromptContext.Model"/>,
    /// <see cref="PromptContext.Parameters"/> e slots compartilhados.
    /// </summary>
    /// <param name="ctx">Contexto do request.</param>
    /// <returns>Seções a concatenar; lista vazia = contributor não se aplica.</returns>
    Task<IReadOnlyList<string>> ContributeAsync(PromptContext ctx);
}
