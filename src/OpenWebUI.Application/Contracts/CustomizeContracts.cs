namespace OpenWebUI.Application.Contracts;

/// <summary>
/// Contratos do hub Customize (SPEC-20261104-customize-hub): notas de
/// conhecimento org/pessoal e catálogo unificado de skills.
/// </summary>

/// <summary>Upsert de nota de conhecimento. <c>Global=true</c> só admin.</summary>
public sealed record KnowledgeNoteRequest(
    string Title,
    string? Trigger,
    string Content,
    bool Pinned,
    bool Global = false);

/// <summary>Nota de conhecimento com escopo efetivo para o chamador.</summary>
public sealed record KnowledgeNoteResponse(
    string Id,
    string Title,
    string? Trigger,
    string Content,
    bool Pinned,
    /// <summary><c>org</c> (global) ou <c>personal</c> (do usuário).</summary>
    string Scope,
    /// <summary>Nota global desligada pelo usuário (tombstone) → false.</summary>
    bool EnabledForMe,
    /// <summary>O chamador pode editar/apagar (dono ou admin em nota global).</summary>
    bool Editable,
    long UpdatedAt);

/// <summary>Toggle simples de habilitação.</summary>
public sealed record CustomizeEnabledRequest(bool Enabled);

/// <summary>Item do catálogo unificado de skills (RF-006).</summary>
public sealed record SkillCatalogItem(
    /// <summary>Id da linha DB; null em skills de repo (filesystem).</summary>
    string? Id,
    string Name,
    string? Description,
    /// <summary><c>global</c> | <c>personal</c> | <c>repo</c>.</summary>
    string Source,
    /// <summary>Estado absoluto (deny admin ou <c>IsActive</c> da linha).</summary>
    bool Enabled,
    /// <summary>Estado efetivo para o chamador (aplica tombstone/deny).</summary>
    bool EnabledForMe,
    /// <summary>Desligada pelo admin — usuário não religa.</summary>
    bool DisabledByAdmin,
    /// <summary>Pode ser editada via API (DB do próprio escopo permitido).</summary>
    bool Editable,
    /// <summary>Corpo injetável — preenchido em itens DB; repo via fetch dedicado.</summary>
    string? Content,
    /// <summary>Tools permitidas (CSV) — paridade Devin allowed-tools.</summary>
    string? AllowedTools = null);

/// <summary>Upsert de skill DB. <c>Global=true</c> só admin.</summary>
public sealed record SkillUpsertRequest(
    string Name,
    string? Description,
    string Content,
    bool Global = false,
    string? AllowedTools = null);

/// <summary>Denylist global de skills (admin) — por nome.</summary>
public sealed record SkillsDisabledRequest(List<string> Names);

/// <summary>Importação de SKILL.md de repositório git público (admin).</summary>
public sealed record SkillImportRequest(
    string RepoUrl,
    string? SubPath = null,
    List<string>? Names = null);

/// <summary>Item pulado na importação com o motivo.</summary>
public sealed record SkillImportSkip(string Name, string Reason);

/// <summary>Resultado do import: importadas/atualizadas e puladas.</summary>
public sealed record SkillImportResult(
    List<string> Imported,
    List<SkillImportSkip> Skipped);

/// <summary>Importação de plugin (plugin.json) de repo git público — admin.</summary>
public sealed record PluginImportRequest(string RepoUrl);

/// <summary>Item pulado na importação de plugin com o motivo.</summary>
public sealed record PluginImportSkip(string Name, string Reason);

/// <summary>Resultado do import de plugin: skills/commands, MCPs e avisos.</summary>
public sealed record PluginImportResult(
    List<string> ImportedSkills,
    List<string> ImportedMcps,
    List<PluginImportSkip> Skipped,
    List<string> Warnings);
