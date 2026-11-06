namespace OpenWebUI.Application.Contracts;

/// <summary>
/// Referência de modelo <c>fusion:{leader}&gt;{sidekick}</c> — o modo
/// Adaptive/Fusion do seletor de modelos (SPEC-20261106). Mesmo padrão dos
/// modelos virtuais <c>arena:{model}</c>/<c>combo:{id}</c>/<c>pipeline:{id}</c>:
/// o id composto viaja no campo <c>Model</c> das requisições e persiste por
/// chat em <c>ModelsJson</c>/<c>webui.model</c>.
/// </summary>
/// <remarks>
/// O <b>Leader</b> dirige a run (análise, plano, revisão); o <b>Sidekick</b>
/// é o modelo default dos sub-agentes criados via
/// <c>builtin:delegate_task</c> — o executor grava o par no kv
/// <c>r:{run}:fusion.{leader,sidekick}</c> e reescreve
/// <c>request.Model</c> para o leader antes do tool loop.
/// </remarks>
public static class FusionModelRef
{
    /// <summary>Prefixo que marca a referência fusion.</summary>
    public const string Prefix = "fusion:";

    /// <summary>Separador leader→sidekick dentro da referência.</summary>
    public const char Separator = '>';

    /// <summary>Prefixo <c>fusion:</c> presente?</summary>
    public static bool IsFusion(string? model) =>
        model is not null
        && model.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Monta a referência composta <c>fusion:{leader}&gt;{sidekick}</c>.</summary>
    public static string Format(string leader, string sidekick) =>
        $"{Prefix}{leader}{Separator}{sidekick}";

    /// <summary>
    /// Extrai o par da referência. <c>false</c> quando não é fusion, quando
    /// falta o separador ou quando algum lado está vazio.
    /// </summary>
    public static bool TryParse(
        string? model, out string leader, out string sidekick)
    {
        leader = sidekick = string.Empty;
        if (!IsFusion(model))
        {
            return false;
        }

        var body = model![Prefix.Length..];
        var sep = body.IndexOf(Separator);
        if (sep <= 0 || sep == body.Length - 1)
        {
            return false;
        }

        leader = body[..sep].Trim();
        sidekick = body[(sep + 1)..].Trim();
        return leader.Length > 0 && sidekick.Length > 0;
    }

    /// <summary>
    /// Modelo efetivo para chamadas que não suportam orquestração
    /// (tasks auxiliares, playground): leader quando fusion, o próprio id
    /// nos demais casos.
    /// </summary>
    public static string LeaderOrSelf(string model) =>
        TryParse(model, out var leader, out _) ? leader : model;
}
