# SPEC-20261103-anthropic-messages-v2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `anthropic-messages-v2` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feat/anthropic-messages-v2-20261103` |
| Ticket | `GH#TBD` |
| Status | `Approved` |
| Parent | provider improvement sweep 2026-11-03 (Gemini/Anthropic) |

## 1. User Story

**As a** usuário com conexão Anthropic
**I want** PDF/documentos como blocos nativos, citações visíveis como fontes e resiliência a `overloaded`
**So that** a conversa aproveita os recursos da Messages API que hoje passam despercebidos ou quebram.

**Problem context:** hoje `BuildAnthropicMessages` mapeia texto/tool_use/tool_result/imagem base64 — não há blocos `document` (PDF), nem parse de `citations` nas respostas, `metadata.user_id` não vai, e a classificação de erros trata 429/529 como genérico (Claude devolve `overloaded_error` 529 sob pico e `rate_limit_error` 429 — `ProviderRetryPolicy` deve dar retry com backoff respeitando `retry-after`).

## 2. Scope

**In scope:**
- Bloco `document` (`type:"document", source:{type:"base64"|"url", media_type:"application/pdf"}`) para anexos PDF em `BuildAnthropicMessages`.
- `citations` nos content blocks → `sources`/`citations` da mensagem de chat (shape já usado para web_search/RAG — mapear `cited_text`, `title`, `url`/`document_title`, `start/end` indexes).
- `metadata.user_id` = user id/hash estável do OpenWebUI.
- Retry classification: `anthropic-error` SSE event + HTTP 429/529 → retryable em `ProviderRetryPolicy` (honrar `retry-after`).
- Beta header configurável genérico (`anthropic-beta` lista `;`-separada por conexão) — cobre `pdfs-2024-09-25`, `token-efficient-tools-2025-02-19` etc.

**Out of scope:**
- Files API do Anthropic (upload prévio de docs) — iter. futura.
- Vision para imagens >5MB (resize client-side) — edge documentável.

## 3. Technical Context

**Where the change happens:** `ProviderService.cs` — `BuildAnthropicMessages`/`MapAnthropicMessage` (~L1774-1920, imagens já entram como `image` base64 ~L1902), stream/complete parse de content blocks (adicionar `citations`), `NewAnthropicRequest` (headers), `ProviderRetryPolicy.cs`, seeding `config` (`connections` ganham campo `beta`/`extra_headers`?).

**Files to read before implementing:**
- `ProviderService.cs` — mapeamento de mensagens/imagens, parse de blocks, headers
- `ProviderRetryPolicy.cs` — classificação de erro atual
- Como `sources` são renderizados na UI (web_search/RAG precedente)
- `ChatCompletionMessage`/`ChatImageData` — shape de anexos (existe `Files`/docs além de `Images`?)

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/ProviderService.cs
src/OpenWebUI.Infrastructure/Services/ProviderRetryPolicy.cs
src/OpenWebUI.Application/** (se metadata/user_id em contrato)
tests/OpenWebUI.Api.Tests/**
```

## 4. Requirements

### RF-001: PDF como `document` block
- **Description:** anexos `application/pdf` em mensagens user viram `document` blocks (base64 ≤32MB ou URL); outros docs ficam como texto extraído (status quo).

### RF-002: Citations → sources
- **Description:** `citations` de blocks text viram entradas na lista de fontes da mensagem (mesmo shape de web_search) com quote `cited_text` + title/url; ausência de citations = sem sources (sem fake).

### RF-003: `metadata.user_id`
- **Description:** sempre enviado com o id/hash do usuário logado (abuse tracking exigido pela Anthropic para alguns recursos).

### RF-004: Retry-aware errors
- **Description:** 429/`rate_limit_error` e 529/`overloaded_error` → retryable com backoff exponencial respeitando `retry-after`; `anthropic-error` SSE event vira falha classificada (não "resposta vazia").

### RF-005: Beta headers configuráveis
- **Description:** campo por conexão `beta` (csv) → `anthropic-beta: a,b`; merge com o `interleaved-thinking` do SPEC irmão quando ambos ativos.

## 5. API Contract

Payloads aditivos; `sources` seguem o shape já consumido pela UI de citations.

## 6. Acceptance Criteria

- [ ] PDF anexado chega como `document` e a resposta o cita.
- [ ] Resposta com `citations` renderiza fontes clicáveis.
- [ ] 529 simulado → retried com backoff; 429 honra `retry-after`.
- [ ] `metadata.user_id` presente no payload (fixture).
- [ ] `dotnet test` verde.

## 7. Task Plan

1. PDF block + attachments mapping (verificar shape `Files` dos anexos).
2. Citations → sources (stream + non-stream).
3. metadata.user_id.
4. Retry classification 429/529/`anthropic-error`.
5. Beta headers + testes.

## 8. Effort Estimate

`~5-7h`.
