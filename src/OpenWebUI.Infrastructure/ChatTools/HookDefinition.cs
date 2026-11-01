namespace OpenWebUI.Infrastructure.ChatTools;

/// <summary>
/// Definição de hook de ciclo de vida (SPEC-20261031-lifecycle-hooks):
/// um comando ou prompt avaliado num evento
/// (<c>PreToolUse</c>, <c>PostToolUse</c>, <c>UserPromptSubmit</c>,
/// <c>Stop</c>, <c>SessionStart</c>, <c>SessionEnd</c>), com matcher
/// regex opcional sobre o nome da tool.
/// </summary>
public sealed record HookDefinition(
    /// <summary>Evento do ciclo de vida (nome canônico).</summary>
    string Event,
    /// <summary>Regex case-insensitive sobre tool_name; null = todas.</summary>
    string? Matcher,
    /// <summary><c>command</c> (processo) ou <c>prompt</c> (LLM).</summary>
    string Type,
    /// <summary>Comando shell ou instrução do prompt.</summary>
    string Command,
    /// <summary>Timeout em segundos (default 60).</summary>
    int TimeoutSeconds,
    /// <summary>Arquivo de origem (auditoria).</summary>
    string Source);

/// <summary>Decisão devolvida por um hook.</summary>
public enum HookDecision
{
    /// <summary>Segue o fluxo normal.</summary>
    Approve,
    /// <summary>Bloqueia: PreToolUse devolve reason como resultado da
    /// tool; UserPromptSubmit rejeita a mensagem; Stop continua a run.</summary>
    Block,
    /// <summary>Roteia para o gate de aprovação.</summary>
    Ask,
}

/// <summary>Resultado agregado da execução dos hooks de um evento.</summary>
public sealed record HookOutcome(
    HookDecision Decision,
    string? Reason,
    /// <summary>Nome do hook que decidiu (para "Blocked by hook X").</summary>
    string? DecidedBy,
    /// <summary>JSON reescrito dos args da tool (só PreToolUse).</summary>
    string? UpdatedInput,
    /// <summary>Contextos injetados (mensagens system efêmeras).</summary>
    IReadOnlyList<string> AdditionalContexts)
{
    /// <summary>Aprovação vazia — segue o fluxo.</summary>
    public static readonly HookOutcome ApproveNone =
        new(HookDecision.Approve, null, null, null, []);
};
