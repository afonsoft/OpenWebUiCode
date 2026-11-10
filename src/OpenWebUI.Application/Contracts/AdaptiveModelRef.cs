namespace OpenWebUI.Application.Contracts;

/// <summary>
/// Referência de modelo <c>adaptive:{m1,m2,…,m4}</c> (SPEC-20261110
/// adaptive-model-pool): pool ordenado de 2–4 candidatos com fallback por
/// falha de provedor <b>e memória entre runs</b> — a run tenta sempre o
/// preferido (M1) e, quando ele acumula falhas consecutivas em várias
/// tarefas, o início passa a ser o último modelo que funcionou
/// (<i>sticky</i>), com sonda periódica de recuperação do preferido.
/// </summary>
/// <remarks>
/// Mesmo padrão dos modelos virtuais <c>fusion:</c>/<c>chain:</c>/
/// <c>combo:</c>/<c>pipeline:</c>: o id composto viaja no campo
/// <c>Model</c> e persiste por chat em <c>ModelsJson</c>/<c>webui.model</c>.
/// Diferente de <c>chain:</c> (cadeia muda por run, sem estado), o
/// <c>adaptive:</c> tem estado por chat nos kvs
/// <c>c:{chat}:adaptive.{sticky,fails,probe}</c> — o executor decide o
/// índice de início a partir da reputação acumulada.
/// </remarks>
public static class AdaptiveModelRef
{
    /// <summary>Prefixo que marca a referência adaptive.</summary>
    public const string Prefix = "adaptive:";

    /// <summary>Separador das posições do pool.</summary>
    public const char Separator = ',';

    /// <summary>Tamanho mínimo do pool — 1 candidato não tem o que adaptar.</summary>
    public const int MinMembers = 2;

    /// <summary>Tamanho máximo do pool (4 slots no seletor).</summary>
    public const int MaxMembers = 4;

    /// <summary>Prefixo <c>adaptive:</c> presente?</summary>
    public static bool IsAdaptive(string? model) =>
        model is not null
        && model.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Monta a referência <c>adaptive:{m1,m2,…}</c>.</summary>
    public static string Format(IReadOnlyList<string> pool) =>
        $"{Prefix}{string.Join(Separator, pool)}";

    /// <summary>
    /// Extrai o pool ordenado com dedup case-insensitive preservando a
    /// primeira posição. <c>false</c> quando não é adaptive, quando o
    /// corpo está vazio, quando algum slot está vazio ou quando o pool
    /// deduplicado sai dos limites <see cref="MinMembers"/>–
    /// <see cref="MaxMembers"/>.
    /// </summary>
    public static bool TryParse(string? model, out IReadOnlyList<string> pool)
    {
        pool = [];
        if (!IsAdaptive(model))
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parsed = new List<string>(MaxMembers);
        foreach (var part in model![Prefix.Length..]
                     .Split(Separator, StringSplitOptions.None))
        {
            var id = part.Trim();
            if (id.Length == 0)
            {
                return false;
            }
            if (seen.Add(id))
            {
                parsed.Add(id);
            }
        }
        if (parsed.Count is < MinMembers or > MaxMembers)
        {
            return false;
        }
        pool = parsed;
        return true;
    }

    /// <summary>
    /// Modelo efetivo para chamadas que não suportam o pool (tasks
    /// auxiliares, playground, metadata): primeiro membro quando adaptive,
    /// o próprio id nos demais casos.
    /// </summary>
    public static string FirstOrSelf(string model) =>
        TryParse(model, out var pool) ? pool[0] : model;
}
