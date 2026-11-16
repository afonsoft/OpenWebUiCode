using Microsoft.Extensions.Configuration;
using OpenWebUI.Application.Interfaces;

namespace OpenWebUI.Infrastructure.Runs;

/// <summary>
/// Guard de tool call repetida (SPEC-20261021-harness-seams RF-A3 —
/// análogo ao <c>guard/repeat-tool-reminder</c> do deepseek-harness):
/// quando a mesma tool é chamada com args idênticos N vezes consecutivas
/// (default 3, config <c>interceptors.repeat_tool_guard.max_repeats</c>),
/// a N-ésima é vetada com instrução para o modelo mudar de abordagem.
/// Um segundo detector cobre o loop por assinatura (P2 da análise de
/// autonomia): a mesma tool M vezes consecutivas mesmo com args
/// diferentes (default 2× max_repeats, config
/// <c>interceptors.repeat_tool_guard.max_same_tool</c>) — o veto usa um
/// limite maior porque retries com args reformulados são legítimos.
/// Chamadas de outra tool resetam as duas sequências. Estado por
/// instância — o escopo DI é o da run.
/// </summary>
public sealed class RepeatToolGuard(IConfiguration configuration) : IRunInterceptor
{
    private string? _lastSignature;
    private string? _lastTool;
    private int _streak;
    private int _toolStreak;

    private int MaxRepeats { get; } =
        int.TryParse(configuration["interceptors.repeat_tool_guard.max_repeats"], out var n)
            ? Math.Max(2, n) : 3;

    private int MaxSameTool { get; } =
        int.TryParse(configuration["interceptors.repeat_tool_guard.max_same_tool"], out var n)
            ? Math.Max(2, n) : 6;

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public Task<ToolCallDecision> OnToolCallAsync(
        ToolCallContext ctx, CancellationToken ct)
    {
        var signature = ctx.ToolName + "\n" + ctx.ArgumentsJson;
        _streak = signature == _lastSignature ? _streak + 1 : 1;
        _lastSignature = signature;
        _toolStreak = ctx.ToolName == _lastTool ? _toolStreak + 1 : 1;
        _lastTool = ctx.ToolName;

        if (_streak >= MaxRepeats)
        {
            return Task.FromResult(ToolCallDecision.Veto(
                $"A tool '{ctx.ToolName}' foi chamada {_streak} vezes "
                + "consecutivas com argumentos idênticos sem progresso. "
                + "Mude de abordagem: reformule os argumentos, use outra "
                + "tool ou explique o bloqueio ao usuário em vez de "
                + "repetir a mesma chamada."));
        }

        if (_toolStreak >= MaxSameTool)
        {
            return Task.FromResult(ToolCallDecision.Veto(
                $"A tool '{ctx.ToolName}' foi chamada {_toolStreak} vezes "
                + "consecutivas sem progresso, mesmo variando os "
                + "argumentos. Mude de abordagem: use outra tool ou "
                + "explique o bloqueio ao usuário em vez de insistir "
                + "na mesma tool."));
        }

        return Task.FromResult(ToolCallDecision.Pass);
    }
}
