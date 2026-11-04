# SPEC-20261103-semantic-code-search

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `semantic-code-search` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feat/semantic-code-search-20261103` |
| Ticket | — |
| Status | `Draft` |
| Parent | builtin-tools improvement sweep 2026-11-03 (agent tool primitives) |

## 1. User Story

**As a** agente investigando um codebase grande
**I want** `code_search` com busca semântica (embeddings) híbrida com a lexical existente
**So that** encontro código por intenção ("onde trato retry de provider?") mesmo sem saber o nome exato do símbolo — cobertura que `file_grep` (lexical) e `lsp_*` (estrutural) não dão sozinhos.

**Problem context:** o comparativo mostra `file_search`/`code_search` por embeddings como primitiva do Codex. Hoje temos `file_grep` (lexical literal/regex) e `lsp_workspace_symbols` (símbolos) — ambos exigem que o agente conheça o vocabulário do repo. A infra de embeddings já existe (`EmbeddingService`, `RagService`) — falta a ponte workspace→índice→tool.

## 2. Scope

**In scope:**
- Nova builtin `code_search` em `ChatTools/Tools/` consumindo `EmbeddingService` quando configurado.
- Índice incremental por workspace: hash/mtime de arquivo → chunks (função/bloco com file+linhas); persistência em memória da instância + invalidação por mtime (sem novo schema EF nesta fatia).
- Ranking híbrido: score = semântico (cosine) ∪ lexical (match literal via file_grep-like) com fusão simples (RRF) quando ambos disponíveis.
- Resultado: top-K chunks `{path, start_line, end_line, score, excerpt}`.
- Degrade: sem provider de embeddings configurado → lexical puro com flag `degraded:true` no resultado (honesto, nunca "semântico" falso).
- Respeita `.gitignore`/binários (mesma exclusão do file_grep).

**Out of scope:**
- Índice persistente cross-sessão (SQLite/vetor store dedicado) — fase 2.
- Reindex em background/watcher (reindex lazy por chamada na fatia atual).
- AST chunking por linguagem (chunking por heurística de blocos nesta fatia).

## 3. Technical Context

**Where the change happens:** novo `CodeSearchBuiltinTool.cs` + serviço `WorkspaceIndexService` em `Infrastructure/Services/`; `EmbeddingService`/`RagService` reutilizados para vetores; chunker simples por janela de linhas com overlap.

**Files to read before implementing:**
- `EmbeddingService.cs`, `RagService.cs` — API de embeddings existente, modelo usado, fallback
- `FileBuiltinTools.cs` (`file_grep` — exclusões gitignore/binário, caps)
- `IBuiltinChatTool.cs`, `BuiltinToolRegistry.cs`
- `ToolIndexService.cs` — padrão de índice em memória

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/WorkspaceIndexService.cs (novo)
src/OpenWebUI.Infrastructure/ChatTools/Tools/CodeSearchBuiltinTool.cs (novo)
tests/OpenWebUI.Api.Tests/** (novo)
```

## 4. Requirements

### RF-001: Índice incremental
- **Description:** chunks por arquivo com hash de conteúdo/mtime; reindex só de arquivos alterados; arquivos excluídos por .gitignore/binário/tamanho nunca entram.

### RF-002: Busca híbrida
- **Description:** com embeddings ativos → fusão semântica+lexical (RRF); só lexical → score lexical puro; top-K configurável (default 10, clamp 50).

### RF-003: Resultado navegável
- **Description:** cada hit traz `path`, `start_line`, `end_line`, `excerpt` (≤ N linhas) — formato diretamente consumível por `file_read` subsequente.

### RF-004: Degrade honesto
- **Description:** sem provider de embeddings → `degraded:true` + motivo; erro de embeddings → fallback lexical no mesmo call (nunca falha a busca).

## 5. API Contract

`code_search{query, top_k?, path_filter?}` → `{results:[{path,start_line,end_line,score,excerpt}], degraded?, mode:"hybrid|lexical|semantic"}`.

## 6. Acceptance Criteria

- [ ] Query conceitual encontra código sem nome exato (fixture com embeddings fake).
- [ ] Sem embeddings → lexical + `degraded:true`.
- [ ] Arquivo alterado reindexa só ele (assert por contador).
- [ ] .gitignore/binário excluídos.
- [ ] `dotnet test` verde (EmbeddingService mockado).

## 7. Task Plan

1. `WorkspaceIndexService`: walk + exclusões + chunker + cache em memória.
2. Scoring lexical + fusão RRF.
3. `code_search` builtin + degrade paths.
4. Testes com embeddings fake/mocks.

## 8. Effort Estimate

`~6-9h`.
