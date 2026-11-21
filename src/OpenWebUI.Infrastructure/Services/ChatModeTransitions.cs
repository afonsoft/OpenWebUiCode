using OpenWebUI.Domain;

namespace OpenWebUI.Infrastructure.Services;

/// <summary>
/// Transições de modo do agente (SPEC-20261122-mode-transitions): ponto
/// único das regras aplicadas sempre que o chat entra em
/// <c>plan</c>/<c>build</c> — via PATCH, <c>plan_exit</c>,
/// <c>mode_switch</c> ou slash digitado.
/// </summary>
public static class ChatModeTransitions
{
    /// <summary>Preset aplicado na entrada de plan|build quando o dono
    /// nunca escolheu um preset manualmente (RF-005): os comandos de
    /// análise do plan e a execução do build ganham o gate "smart" em vez
    /// de pedir aprovação a cada chamada.</summary>
    public const string EntryPreset = "smart";

    /// <summary>kv que marca a escolha manual de preset pelo dono.</summary>
    public static string PinnedKey(string chatId) => $"chat:{chatId}:preset.pinned";

    /// <summary>Presets conservadores substituídos por
    /// <see cref="EntryPreset"/> na entrada de plan|build.</summary>
    public static bool IsConservativePreset(string preset) =>
        preset is "allow-readonly" or "approve-mutations";

    /// <summary>
    /// RF-005: ao entrar em <paramref name="target"/> plan|build, sobe o
    /// preset para <c>smart</c> quando o atual é conservador E o dono
    /// nunca fixou um preset. Devolve true se o chat foi alterado.
    /// </summary>
    public static async Task<bool> ApplyEntryPresetAsync(
        ConfigService config, Chat chat, string target, CancellationToken ct)
    {
        if (target is not ("plan" or "build")
            || !IsConservativePreset(chat.ApprovalPreset)
            || await config.GetAsync(PinnedKey(chat.Id), "", ct) == "1")
        {
            return false;
        }
        chat.ApprovalPreset = EntryPreset;
        return true;
    }

    /// <summary>Marca que o dono escolheu o preset manualmente — nunca
    /// mais sobrescrito pelas transições de modo (RF-005).</summary>
    public static Task PinPresetAsync(
        ConfigService config, string chatId, CancellationToken ct) =>
        config.SetAsync(PinnedKey(chatId), "1", ct);
}
