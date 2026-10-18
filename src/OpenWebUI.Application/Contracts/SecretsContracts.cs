namespace OpenWebUI.Application.Contracts;

/// <summary>Secret resolvido para listagem (SPEC-20261017-agent-secrets-env) — sem valor.</summary>
/// <param name="Name">Nome da variável de ambiente.</param>
/// <param name="Source"><c>user</c> (override do usuário) ou <c>global</c> (default do admin).</param>
/// <param name="CreatedAt">Epoch seconds da criação.</param>
/// <param name="UpdatedAt">Epoch seconds da última edição.</param>
public sealed record SecretEntryResponse(
    string Name, string Source, long CreatedAt, long UpdatedAt);

/// <summary>Body do PUT de secret — write-only (o valor nunca volta).</summary>
/// <param name="Value">Valor do secret (até 8KB).</param>
public sealed record SecretUpsertRequest(string? Value);

/// <summary>Resposta do upsert — confirma o nome, nunca o valor.</summary>
/// <param name="Name">Nome persistido.</param>
public sealed record SecretUpsertResponse(string Name);
