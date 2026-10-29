# SPEC-20261003-knowledge-v2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `knowledge-v2` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261003-knowledge-v2` |
| Ticket | Issue #81 |
| Status | `Completed` |

## 1. User Story

**As a** usuário com bases de conhecimento
**I want** vincular `file_id` existente a itens, reindexar coleção e operações em lote
**So that** o router `knowledge` upstream (35 eps) atinge paridade operacional.

**Problem context:**
Hoje: coleções + itens + embeddings + access grants. Falta: anexar arquivo já enviado (`/files` → knowledge), reindexar embeddings após mudança de modelo, e batch delete/update.

## 2. Scope

**In scope:**
- `POST /api/v1/knowledge/{id}/file/add` com `file_id` (extrai texto do arquivo persistido via `IFileStorage`, indexa).
- `POST /api/v1/knowledge/{id}/reindex`: re-embed todos os itens com o provider atual.
- Batch: `POST /api/v1/knowledge/batch/delete` (ids[]) respeitando permissão.
- UI: botões "adicionar arquivo", "reindexar" na aba Knowledge.

**Out of scope:**
- Reindex progressivo com progresso em tempo real (fazer síncrono + toast).

## 3. Technical Context

**Files to read:**
- `src/OpenWebUI.Api/Endpoints/KnowledgeEndpoints.cs`
- `src/OpenWebUI.Infrastructure/Services/{RagService,EmbeddingService}.cs`
- `src/OpenWebUI.Api/Endpoints/FileEndpoints.cs` (storage)

## 4. Requirements

### RF-001: file_id → knowledge item
- **Input → Output:** `{file_id}` → item criado com texto extraído + embeddings; 404 se arquivo não existe/não é do usuário.

### RF-002: Reindex
- **Rules:** recria embeddings dos itens da coleção; falha de provider → 502 parcial com contagem.

### RF-003: Batch ops
- **Rules:** só dono/admin; resposta `{deleted: n}`.

## 5. API Contract

`POST /api/v1/knowledge/{id}/file/add` · `POST /api/v1/knowledge/{id}/reindex` · `POST /api/v1/knowledge/batch/delete`

## 6. Critérios de Aceite

- [ ] Arquivo enviado em `/files` anexa à coleção e vira retrievable.
- [ ] Reindex atualiza embeddings (assert via mock).
- [ ] Batch delete respeita grants/ownership.
- [ ] Testes NUnit dos 3 endpoints + erro paths.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/95
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/81
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/79
