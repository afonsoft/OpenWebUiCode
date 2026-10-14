using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenWebUI.Application.Contracts;
using OpenWebUI.Domain;
using OpenWebUI.Infrastructure.ChatTools;
using OpenWebUI.Infrastructure.Data;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Revisor de run (SPEC-20261013-run-advisor): um modelo secundário
/// (papel <c>advisor</c>, fallback = modelo da run) lê o transcript tail +
/// atividade recente de tools e devolve UM conselho curto — ou nada.
/// Pode inspecionar o workspace com um subset read-only
/// (<see cref="IsAdvisorTool"/>): sem mutação, sem aprovação, sem secrets.
///
/// A execução é isolada do pipeline: o executor chama
/// <see cref="AdviseAsync"/> em task própria com timeout curto — falha,
/// provider offline ou modelo ausente resultam em <c>null</c> e a run
/// segue sem advisor (RF-004).
/// </summary>
public sealed class RunAdvisorService(
    AppDbContext db,
    BuiltinToolRegistry builtins,
    ModelRoleResolver roles,
    ConfigService config,
    Func<ChatCompletionRequest, CancellationToken, Task<ProviderCompletion>> completeWithTools,
    Func<IReadOnlyList<Tool>, string, string, BuiltinToolContext?, CancellationToken, Task<ToolExecutionOutcome>> executeTool)
{
    /// <summary>Teto de conselhos publicados por run (RF-001).</summary>
    public const int MaxAdvicesPerRun = 10;

    /// <summary>Cooldown entre revisões do advisor (RF-001).</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    /// <summary>Teto de chamadas de tool por revisão (RF-002).</summary>
    private const int MaxToolCalls = 3;

    /// <summary>Teto de rodadas do mini-loop interno.</summary>
    private const int MaxRounds = 3;

    /// <summary>Tamanho máximo do conselho publicado/injetado (RF-003).</summary>
    internal const int AdviceChars = 500;

    /// <summary>Palavras do transcript tail enviadas ao advisor (RF-001).</summary>
    private const int TailWords = 4000;

    /// <summary>Chars de uma mensagem individual no tail (tool results longos são resumidos).</summary>
    private const int MessageChars = 1200;

    private static readonly Lazy<string> s_systemPrompt = new(LoadPrompt);

    /// <summary>
    /// Gate global + flag por chat (RF-005): <c>advisor.enabled=false</c>
    /// desliga o recurso na instância inteira; <see cref="Chat.AdvisorEnabled"/>
    /// é o opt-in por conversa (default admin em <c>advisor.default</c>).
    /// </summary>
    public async Task<bool> IsEnabledAsync(string chatId, CancellationToken ct)
    {
        // advisor.enabled=false desliga globalmente; ausente/null permite
        // (o opt-in é por chat via AdvisorEnabled — RF-005).
        if (await config.GetAsync<bool?>("advisor.enabled", null, ct) == false)
        {
            return false;
        }
        return await db.Chats.AsNoTracking()
            .Where(c => c.Id == chatId)
            .Select(c => c.AdvisorEnabled)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Produz o conselho de um gatilho. Retorna null quando o advisor não
    /// tem nada a dizer (<c>OK</c>/vazio), quando o recurso está desligado
    /// ou quando a chamada falha — o caller nunca precisa tratar erro.
    /// </summary>
    /// <param name="run">Run em execução (POCO — não é tracked neste escopo).</param>
    /// <param name="trigger"><c>user_message</c> | <c>tool_burst</c> | <c>pause</c>.</param>
    /// <param name="recentTools">Linhas curtas "nome → preview" das últimas tool calls.</param>
    /// <param name="builtinContext">Contexto do workspace da run; null desabilita tools do advisor.</param>
    /// <param name="ct">Cancelamento (timeout do caller).</param>
    public async Task<string?> AdviseAsync(
        ChatRun run, string trigger, IReadOnlyList<string> recentTools,
        BuiltinToolContext? builtinContext, CancellationToken ct)
    {
        if (!await IsEnabledAsync(run.ChatId, ct))
        {
            return null;
        }

        var tail = await LoadTranscriptTailAsync(run.ChatId, ct);
        if (tail.Length == 0)
        {
            return null;
        }

        var model = await roles.ResolveAsync("advisor", run.UserId, run.Model, ct);
        var user = new StringBuilder()
            .Append("Trigger: ").Append(trigger).Append("\n\n")
            .Append("## Transcript tail\n").Append(tail)
            .Append("\n\n## Recent tool activity\n")
            .Append(recentTools.Count == 0 ? "(none)" : string.Join('\n', recentTools))
            .Append("\n\nReview and advise (or OK).")
            .ToString();

        var messages = new List<ChatCompletionMessage>
        {
            new("system", s_systemPrompt.Value),
            new("user", user),
        };

        // Subset read-only (RF-002): só entra no spec se a run tem workspace
        // (builtinContext). Tools fora do allowlist são negadas na execução.
        var advisorTools = builtinContext is null
            ? []
            : builtins.All
                .Where(t => IsAdvisorTool(t.Name))
                .Select(builtins.ToSyntheticTool)
                .ToList();
        var specs = advisorTools
            .Select(t => JsonSerializer.Deserialize<JsonElement>(t.SpecJson))
            .ToList();

        var toolCalls = 0;
        for (var round = 0; round < MaxRounds; round++)
        {
            var request = new ChatCompletionRequest(
                model, messages, Stream: false,
                Tools: advisorTools.Count > 0 ? specs : null);
            var step = await completeWithTools(request, ct);
            if (step.ToolCalls.Count == 0)
            {
                return TruncateAdvice(step.Content);
            }

            messages.Add(new ChatCompletionMessage(
                "assistant", step.Content, ToolCallsJson: step.ToolCallsJson));
            foreach (var call in step.ToolCalls)
            {
                if (toolCalls >= MaxToolCalls)
                {
                    break;
                }
                toolCalls++;
                var outcome = IsAdvisorTool(BareName(call.Name))
                    ? await executeTool(
                        advisorTools, call.Name, call.ArgumentsJson, builtinContext, ct)
                    : new ToolExecutionOutcome(
                        $"Erro: a tool '{call.Name}' não está disponível ao advisor.");
                messages.Add(new ChatCompletionMessage(
                    "tool", outcome.Text, ToolCallId: call.Id));
            }
        }
        return null;
    }

    /// <summary>
    /// Subset read-only permitido ao advisor (RF-002): leitura de arquivos
    /// e consultas LSP. Nada de escrita, execução, rede ou secrets.
    /// </summary>
    public static bool IsAdvisorTool(string name) =>
        name is "file_read" or "file_grep" or "file_glob"
        || name.StartsWith("lsp_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Normaliza o conselho: <c>OK</c>/vazio → null (nada a dizer);
    /// demais textos capados a <see cref="AdviceChars"/> (RF-003).
    /// </summary>
    internal static string? TruncateAdvice(string? content)
    {
        var text = content?.Trim();
        if (string.IsNullOrEmpty(text)
            || text.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return text.Length <= AdviceChars ? text : text[..AdviceChars];
    }

    /// <summary>
    /// Nome cru da tool chamada — providers podem emitir
    /// <c>builtin_file_read</c>/<c>builtin:file_read</c> em vez de
    /// <c>file_read</c>; o allowlist opera sobre o nome sem prefixo.
    /// </summary>
    internal static string BareName(string callName) =>
        callName.StartsWith(BuiltinToolRegistry.SpecPrefix, StringComparison.OrdinalIgnoreCase)
            ? callName[BuiltinToolRegistry.SpecPrefix.Length..]
            : callName.StartsWith(BuiltinToolRegistry.IdPrefix, StringComparison.OrdinalIgnoreCase)
                ? callName[BuiltinToolRegistry.IdPrefix.Length..]
                : callName;

    /// <summary>
    /// Tail do transcript persistido (RF-001): últimas mensagens do chat
    /// formatadas <c>[role] texto</c> com cap de ~<see cref="TailWords"/>
    /// palavras, mais antigas descartadas — tool calls em andamento chegam
    /// separadas via <c>recentTools</c>.
    /// </summary>
    private async Task<string> LoadTranscriptTailAsync(string chatId, CancellationToken ct)
    {
        var messages = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ChatId == chatId)
            .OrderByDescending(m => m.Position)
            .Take(24)
            .Select(m => new { m.Role, m.Content })
            .ToListAsync(ct);
        if (messages.Count == 0)
        {
            return string.Empty;
        }

        messages.Reverse();
        var sb = new StringBuilder();
        var words = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
            var content = m.Content.Length <= MessageChars
                ? m.Content
                : m.Content[..MessageChars] + " …";
            var line = $"[{m.Role}] {content}";
            words += line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (words > TailWords)
            {
                break;
            }
            sb.Insert(0, line + '\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Prompt do advisor como asset (.md embedded) — nunca inline.</summary>
    private static string LoadPrompt()
    {
        var asm = typeof(RunAdvisorService).Assembly;
        using var stream = asm.GetManifestResourceStream(
            "OpenWebUI.Infrastructure.prompts.advisor-system.md");
        if (stream is null)
        {
            return "Review the transcript tail and emit one short advice, or OK.";
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
