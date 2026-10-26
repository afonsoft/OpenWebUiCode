namespace OpenWebUI.Application.Contracts;

/// <summary>Contratos da References API (SPEC-20261018-references).</summary>
/// <param name="Id">Identificador interno da referência.</param>
/// <param name="Alias">Alias <c>[a-z0-9-]+</c> — usado em <c>ref://alias/path</c> e <c>@alias</c>.</param>
/// <param name="Source">Origem: path de diretório local ou URL git.</param>
/// <param name="Branch">Branch/tag do clone git (null = default da origem).</param>
/// <param name="Description">Texto injetado no contexto quando anexada.</param>
/// <param name="Hidden">Oculta do menu <c>@</c> — ainda resolve direto.</param>
/// <param name="LastFetchedAt">Último clone/fetch bem-sucedido (epoch s).</param>
/// <param name="UpdatedAt">Última edição do registro (epoch s).</param>
public sealed record ReferenceItemResponse(
    string Id, string Alias, string Source, string? Branch,
    string? Description, bool Hidden, long LastFetchedAt, long UpdatedAt);

/// <summary>Resposta do tree — listing relativo como o agente veria.</summary>
/// <param name="Alias">Alias resolvido.</param>
/// <param name="Warning">Aviso não-fatal (ex.: fetch falhou, cache stale).</param>
/// <param name="Paths">Caminhos relativos ordenados (cap 2000).</param>
/// <param name="Truncated">True quando o listing bateu o cap.</param>
public sealed record ReferenceTreeResponse(
    string Alias, string? Warning,
    IReadOnlyList<string> Paths, bool Truncated);
