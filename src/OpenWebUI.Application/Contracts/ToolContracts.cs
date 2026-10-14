namespace OpenWebUI.Application.Contracts;

/// <summary>Tool registrada na listagem/edição.</summary>
/// <param name="Id">Identificador.</param>
/// <param name="Name">Nome.</param>
/// <param name="Description">Descrição.</param>
/// <param name="SpecJson">Spec da função no formato OpenAI.</param>
/// <param name="Code">Fonte Python da tool (class Tools), quando definida.</param>
/// <param name="Enabled">Se está habilitada.</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
/// <param name="Source">Origem: <c>user</c> (workspace) ou <c>mcp</c> (virtual, gerida na aba MCP do Admin).</param>
public sealed record ToolResponse(
    string Id,
    string Name,
    string? Description,
    string SpecJson,
    string? Code,
    string? Url,
    bool Enabled,
    long CreatedAt,
    string Source = "user",
    bool RequiresApproval = false);

/// <summary>Criação/atualização de tool.</summary>
/// <param name="Name">Nome.</param>
/// <param name="Description">Descrição.</param>
/// <param name="SpecJson">Spec da função {"type":"function","function":{...}}.</param>
/// <param name="Url">Endpoint HTTP POST de execução (obrigatório na criação quando Code ausente; omitir para manter).</param>
/// <param name="Code">Fonte Python executada em subprocess (convenção <c>class Tools</c> do upstream). Alternativa ao Url.</param>
/// <param name="Enabled">Habilitada.</param>
public sealed record ToolUpsertRequest(
    string Name,
    string? Description,
    string SpecJson,
    string? Url,
    string? Code = null,
    bool Enabled = true,
    bool RequiresApproval = false);

/// <summary>Linha da política de aprovação por tool do usuário
/// (SPEC-20261013-approval-tiers): nome da função, tier declarado e
/// override vigente (<c>allow|deny|prompt</c>, null = segue o preset).</summary>
public sealed record ToolApprovalEntry(
    string Name, string Tier, string? Override);

/// <summary>Resposta do catálogo de aprovações por tool.</summary>
public sealed record ToolApprovalsResponse(List<ToolApprovalEntry> Tools);

/// <summary>Patch de overrides: tool → <c>allow|deny|prompt</c>;
/// null/"" remove o override da tool.</summary>
public sealed record UpdateToolApprovalsRequest(
    Dictionary<string, string?> Overrides);
