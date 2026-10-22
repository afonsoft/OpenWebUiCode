using Microsoft.Extensions.Configuration;
using OpenWebUI.Application.Interfaces;

namespace OpenWebUI.Infrastructure.Runs;

/// <summary>
/// Guard de tool call repetida (SPEC-20261021-harness-seams RF-A3 —
/// análogo ao <c>guard/repeat-tool-reminder</c> do deepseek-harness):
/// quando a mesma tool é chamada com args idênticos N vezes consecutivas
/// (default 3, config <c>interceptors.repeat_tool_guard.max_repeats</c>),
/// a N-ésima é vetada com instrução para o modelo mudar de abordagem.
/// Chamadas diferentes (tool ou args) resetam a sequência. Estado por
/// instância — o escopo DI é o da run.
/// </summary>
public sealed class RepeatToolGuard(IConfiguration configuration) : IRunInterceptor
{
    private string? _lastSignature;
    private int _streak;

    private int MaxRepeats { get; } =
        int.TryParse(configuration["interceptors.repeat_tool_guard.max_repeats"], out var n)
            ? Math.Max(2, n) : 3;

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public Task<ToolCallDecision> OnToolCallAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        var signature = ctx.ToolName + "\n" + ctx.ArgumentsJson;
        _streak = signature == _lastSignature ? _streak + 1 : 1;
        _lastSignature = signature;

        if (_streak >= MaxRepeats)
        {
            return Task.FromResult(ToolCallDecision.Veto(
                $"A tool '{ctx.ToolName}' foi chamada {_streak} vezes "
                + "consecutivas com argumentos idênticos sem progresso. "
                + "Mude de abordagem: reformule os argumentos, use outra "
                + "tool ou explique o bloqueio ao usuário em vez de "
                + "repetir a mesma chamada."));
        }

        return Task.FromResult(ToolCallDecision.Pass);
    }
}
