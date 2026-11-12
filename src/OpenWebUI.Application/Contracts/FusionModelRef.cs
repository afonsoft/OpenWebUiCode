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
/// <para>
/// SPEC-20261110 (fusion-fallback-chain): cada papel aceita uma cadeia
/// ordenada separada por vírgula — <c>fusion:{L1,L2}&gt;{S1,S2}</c>. A
/// cadeia efetiva da run é <c>[L1,L2,S1,S2]</c>: em falha de provedor a
/// run avança para o próximo candidato na ordem declarada.
/// </para>
/// </remarks>
public static class FusionModelRef
{
    /// <summary>Prefixo que marca a referência fusion.</summary>
    public const string Prefix = "fusion:";

    /// <summary>Separador leader→sidekick dentro da referência.</summary>
    public const char Separator = '>';

    /// <summary>Separador de opções de fallback dentro de cada papel.</summary>
    public const char ChainSeparator = ',';

    /// <summary>Prefixo <c>fusion:</c> presente?</summary>
    public static bool IsFusion(string? model) =>
        model is not null
        && model.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Monta a referência composta <c>fusion:{leader}&gt;{sidekick}</c>.</summary>
    public static string Format(string leader, string sidekick) =>
        $"{Prefix}{leader}{Separator}{sidekick}";

    /// <summary>
    /// Monta a referência com cadeias — <c>fusion:{L1,L2}&gt;{S1,S2}</c>.
    /// </summary>
    public static string Format(
        IReadOnlyList<string> leaders, IReadOnlyList<string> sidekicks) =>
        $"{Prefix}{string.Join(ChainSeparator, leaders)}{Separator}{string.Join(ChainSeparator, sidekicks)}";

    /// <summary>
    /// Extrai o primeiro par da referência. <c>false</c> quando não é
    /// fusion, quando falta o separador ou quando algum lado está vazio.
    /// </summary>
    public static bool TryParse(
        string? model, out string leader, out string sidekick)
    {
        leader = sidekick = string.Empty;
        if (!TryParseChain(model, out var leaders, out var sidekicks))
        {
            return false;
        }
        leader = leaders[0];
        sidekick = sidekicks[0];
        return true;
    }

    /// <summary>
    /// Extrai as cadeias ordenadas de leaders e sidekicks
    /// (<c>fusion:{L1,L2}&gt;{S1,S2}</c>, SPEC-20261110). Cada lado aceita
    /// de 1 a N opções separadas por <see cref="ChainSeparator"/>; ids
    /// duplicados na cadeia são mantidos uma única vez (primeira
    /// posição). <c>false</c> quando não é fusion, quando falta o
    /// separador ou quando algum lado/posição está vazio.
    /// </summary>
    public static bool TryParseChain(
        string? model,
        out IReadOnlyList<string> leaders,
        out IReadOnlyList<string> sidekicks)
    {
        leaders = sidekicks = [];
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

        var parsedLeaders = ParseChain(body[..sep]);
        var parsedSidekicks = ParseChain(body[(sep + 1)..]);
        if (parsedLeaders is null || parsedSidekicks is null)
        {
            return false;
        }

        leaders = parsedLeaders;
        sidekicks = parsedSidekicks;
        return true;
    }

    /// <summary>
    /// Cadeia efetiva ordenada da run — leaders primeiro, depois
    /// sidekicks, com dedup preservando a primeira posição.
    /// </summary>
    public static IReadOnlyList<string> EffectiveChain(
        IReadOnlyList<string> leaders, IReadOnlyList<string> sidekicks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chain = new List<string>(leaders.Count + sidekicks.Count);
        chain.AddRange(leaders.Concat(sidekicks).Where(seen.Add));
        return chain;
    }

    /// <summary>Split por vírgula → ids trimmed; <c>null</c> em slot vazio.</summary>
    private static List<string>? ParseChain(string group)
    {
        var chain = new List<string>();
        foreach (var part in group.Split(ChainSeparator, StringSplitOptions.None))
        {
            var id = part.Trim();
            if (id.Length == 0)
            {
                return null;
            }
            chain.Add(id);
        }
        return chain;
    }

    /// <summary>
    /// Modelo efetivo para chamadas que não suportam orquestração
    /// (tasks auxiliares, playground): primeiro leader quando fusion,
    /// o próprio id nos demais casos.
    /// </summary>
    public static string LeaderOrSelf(string model) =>
        TryParse(model, out var leader, out _) ? leader : model;
}

/// <summary>
/// Referência de modelo <c>chain:{m1,m2,…}</c> (SPEC-20261110
/// fusion-fallback-chain): cadeia ordenada de fallback por falha de
/// provedor <b>sem</b> semântica de orquestração — sem prompt fusion,
/// sem kv de par. Gerada pelo executor para os filhos de uma run
/// fusion (kv <c>fusion.sidekick</c>) e aceita direto no campo
/// <c>Model</c> como utilidade.
/// </summary>
public static class ChainModelRef
{
    /// <summary>Prefixo que marca a referência chain.</summary>
    public const string Prefix = "chain:";

    /// <summary>Separador das posições da cadeia.</summary>
    public const char Separator = ',';

    /// <summary>Prefixo <c>chain:</c> presente?</summary>
    public static bool IsChain(string? model) =>
        model is not null
        && model.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Monta a referência <c>chain:{m1,m2,…}</c>.</summary>
    public static string Format(IReadOnlyList<string> chain) =>
        $"{Prefix}{string.Join(Separator, chain)}";

    /// <summary>
    /// Extrai a cadeia ordenada. <c>false</c> quando não é chain ou
    /// quando alguma posição está vazia.
    /// </summary>
    public static bool TryParse(string? model, out IReadOnlyList<string> chain)
    {
        chain = [];
        if (!IsChain(model))
        {
            return false;
        }
        var parsed = new List<string>();
        foreach (var part in model![Prefix.Length..]
                     .Split(Separator, StringSplitOptions.None))
        {
            var id = part.Trim();
            if (id.Length == 0)
            {
                chain = [];
                return false;
            }
            parsed.Add(id);
        }
        if (parsed.Count == 0)
        {
            return false;
        }
        chain = parsed;
        return true;
    }
}
