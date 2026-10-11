namespace OpenWebUI.Application.Contracts;

/// <summary>Contratos da Workspace File API (SPEC-20261009-workspace-file-api, E16 S1).</summary>
/// <param name="Name">Nome da entrada (basename).</param>
/// <param name="Path">Caminho relativo ao workdir, separador '/'.</param>
/// <param name="Type">"file" ou "dir".</param>
/// <param name="Size">Tamanho em bytes (somente arquivos).</param>
/// <param name="Collapsed">Dir volumoso/gerado (.git, bin, obj, node_modules) — sem filhos por padrão.</param>
public sealed record WorkspaceFileEntryResponse(
    string Name, string Path, string Type, long? Size, bool Collapsed);

/// <summary>Resposta paginada da árvore do workdir.</summary>
/// <param name="Path">Prefixo da listagem (relativo).</param>
/// <param name="Entries">Entradas em DFS, dirs primeiro por nível.</param>
/// <param name="NextCursor">Cursor opaco para a próxima página (null = fim).</param>
/// <param name="Truncated">True quando há mais entradas além da página.</param>
public sealed record WorkspaceFileTreeResponse(
    string Path, IReadOnlyList<WorkspaceFileEntryResponse> Entries, string? NextCursor, bool Truncated);

/// <summary>Leitura paginada de arquivo texto.</summary>
/// <param name="Path">Caminho relativo.</param>
/// <param name="Content">Fatia de linhas pedida.</param>
/// <param name="TotalLines">Linhas totais do arquivo.</param>
/// <param name="Truncated">True quando há mais linhas além da fatia.</param>
/// <param name="ETag">Identidade de conteúdo (mtime+size) para If-Match/cache.</param>
public sealed record WorkspaceFileReadResponse(
    string Path, string Content, int TotalLines, bool Truncated, string ETag);

/// <summary>Escrita de arquivo texto (cria ou sobrescreve).</summary>
public sealed record WorkspaceFileWriteRequest(string? Path, string? Content);

/// <summary>Resposta de escrita com o novo etag.</summary>
public sealed record WorkspaceFileWriteResponse(
    bool Ok, string Path, string ETag, DateTimeOffset LastModified);

/// <summary>Criação de diretório (idempotente).</summary>
public sealed record WorkspaceFileMkdirRequest(string? Path);

/// <summary>Renomeação/movimentação de arquivo ou diretório.</summary>
public sealed record WorkspaceFileRenameRequest(string? From, string? To);

/// <summary>Remoção de arquivo ou diretório (recursiva).</summary>
public sealed record WorkspaceFileDeleteRequest(string? Path);

/// <summary>Match de busca global no workdir (SPEC-20261011-ide-search-scm, Fase C).</summary>
/// <param name="Path">Caminho relativo ao workdir.</param>
/// <param name="Line">Linha do match (0-based).</param>
/// <param name="Col">Coluna do match (0-based).</param>
/// <param name="Text">Linha inteira (trimmed, cap 300 chars).</param>
public sealed record WorkspaceSearchMatchItem(string Path, int Line, int Col, string Text);

/// <summary>Resultado da busca global.</summary>
/// <param name="Truncated">True quando hits excederam o cap (200).</param>
public sealed record WorkspaceSearchResponse(
    IReadOnlyList<WorkspaceSearchMatchItem> Matches, bool Truncated);

/// <summary>Commit SCM do painel (SPEC-20261011-ide-search-scm): <c>git add -A + commit</c>.</summary>
public sealed record WorkspaceCommitRequest(string? Message);

/// <summary>Resultado do commit: hash novo ou erro do git.</summary>
public sealed record WorkspaceCommitResponse(bool Ok, string? Commit, string? Error);

/// <summary>Item de skill do repositório (descoberta em <c>**/SKILL.md</c>).</summary>
/// <param name="Name">Nome do slash command (frontmatter <c>name</c> ou diretório-pai).</param>
/// <param name="Description">Descrição curta (frontmatter).</param>
/// <param name="Source">Origem: <c>skill</c>.</param>
/// <param name="Path">Caminho relativo ao workdir do arquivo SKILL.md.</param>
public sealed record RepoSkillItemResponse(
    string Name, string Description, string Source, string Path);

/// <summary>Skill do repositório com corpo markdown completo.</summary>
public sealed record RepoSkillDetailResponse(
    string Name, string Description, string Source, string Path, string Content);

/// <summary>Command markdown do repositório (<c>.opencode/command</c> etc.).</summary>
/// <param name="Agent">Agente-alvo declarado no frontmatter (informativo).</param>
/// <param name="Model">Modelo declarado no frontmatter (informativo).</param>
/// <param name="Subtask">Se o command declara execução em subtask (informativo).</param>
public sealed record RepoCommandItemResponse(
    string Name, string Description, string? Agent, string? Model,
    bool Subtask, string Source, string Path);

/// <summary>Command do repositório com corpo (template <c>$1..$N</c>/<c>$ARGUMENTS</c>).</summary>
public sealed record RepoCommandDetailResponse(
    string Name, string Description, string? Agent, string? Model,
    bool Subtask, string Source, string Path, string Content);

/// <summary>Item da listagem de checkpoints do workdir (S6 checkpoints-revert).</summary>
/// <param name="Hash">Identificador do checkpoint (commit na ref oculta ou id de manifesto).</param>
/// <param name="Turn">Turno da run que gerou o snapshot (0 = pré-run).</param>
/// <param name="CreatedAt">Criação (epoch seconds).</param>
public sealed record WorkspaceCheckpointItem(
    string Hash, int Turn, long CreatedAt);

/// <summary>Detalhe de um checkpoint: arquivos cobertos + diff de preview.</summary>
/// <param name="Files">Arquivos que divergem do workdir atual (conjunto do revert).</param>
/// <param name="Diff">Unified diff truncado (null no backend de manifesto).</param>
public sealed record WorkspaceCheckpointDetailResponse(
    string Hash, IReadOnlyList<string> Files, string? Diff);

/// <summary>Requisição de revert — <paramref name="Force"/> ignora a guarda de drift.</summary>
public sealed record WorkspaceCheckpointRevertRequest(bool? Force);

/// <summary>Resultado do revert: restaurados + conflitos (drift fora da trilha).</summary>
public sealed record WorkspaceCheckpointRevertResponse(
    IReadOnlyList<string> Reverted, IReadOnlyList<string> Conflicts);

// ---------- LSP do editor (SPEC-20261009-lsp-diagnostics, E16 S8) ----------

/// <summary>Sync do documento para o LSP (didOpen/didChange/didClose).</summary>
/// <param name="Path">Caminho relativo ao workdir.</param>
/// <param name="Kind"><c>open</c> | <c>change</c> | <c>close</c>.</param>
/// <param name="Text">Conteúdo completo (full-sync v1); null em close.</param>
public sealed record LspDocSyncRequest(string? Path, string? Kind, string? Text);

/// <summary>Status LSP de um arquivo (linguagem detectada + estado do servidor).</summary>
public sealed record LspStatusResponse(
    bool Enabled, string? Language, string State, string? Error);

/// <summary>Um diagnostic LSP no formato do editor (0-based).</summary>
public sealed record LspDiagnosticItem(
    string Path, int Line, int Col, int EndLine, int EndCol,
    int Severity, string? Code, string? Source, string Message);

/// <summary>Diagnostics do arquivo/workdir + flags de cap.</summary>
public sealed record LspDiagnosticsResponse(
    List<LspDiagnosticItem> Diagnostics, bool Truncated, int Total);

/// <summary>Resposta de hover do editor (null quando sem servidor/conteúdo).</summary>
public sealed record LspHoverResponse(string? Hover);
