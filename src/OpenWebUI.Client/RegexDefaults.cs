namespace OpenWebUI.Client;

/// <summary>Timeout padrão de Regex em código de produto (mitiga ReDoS — Sonar S6444).</summary>
internal static class RegexDefaults
{
    /// <summary>Limite de 1s por match — suficiente para padrões de produto, evita hang em input patológico.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
}
