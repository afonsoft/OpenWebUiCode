# SPEC-20261003-ollama-management

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `ollama-management` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261003-ollama-management` |
| Ticket | Issue #82 |
| Status | `Completed` |

## 1. User Story

**As a** admin/usuário
**I want** gerenciar modelos Ollama (pull/delete/copy/show blobs) e passthrough de audio/images
**So that** o router passthrough cobre o gerenciamento de modelos como o upstream.

**Problem context:**
Slice passthrough-routers entregou `/ollama/api/{tags,version,show,chat,generate,embed}` + `/openai/{models,chat/completions,embeddings}`. Falta: `pull` (streaming de progresso), `create`, `delete`, `copy`, `blobs/{digest}` e passthrough de `/audio/speech` e imagens.

## 2. Scope

**In scope:**
- `POST /ollama/api/pull` (NDJSON progresso repassado ao cliente), `DELETE /ollama/api/delete`, `POST /ollama/api/copy`, `HEAD|POST|GET /ollama/api/blobs/{digest}`.
- Passthrough `/openai/audio/speech`, `/openai/audio/transcriptions`, `/openai/images/generations` (roteiam para conexões OpenAI configuradas).
- UI admin mínima para pull/delete (aba Models ou Settings → Conexões).

**Out of scope:**
- Upload de blob com resumable upload; modelfile builder visual.

## 3. Technical Context

**Files to read:**
- `src/OpenWebUI.Api/Endpoints/PassthroughEndpoints.cs`
- `src/OpenWebUI.Infrastructure/Services/ProviderProxyService.cs`

## 4. Requirements

### RF-001: Model lifecycle Ollama
- **Rules:** proxy fiel (status + body); `pull` deve streamar progresso; erro do upstream → repasse do status (nunca 500 genérico).

### RF-002: Blobs
- **Rules:** `sha256:{hex}` validado; upload binário até limite configurável (default 512MB).

### RF-003: Passthrough audio/images
- **Rules:** `OPENAI_API_KEY(S)` mascaradas; indexado por connection como o resto do router.

## 5. API Contract

Espelho upstream: `/ollama/api/{pull,create,delete,copy}`, `/ollama/api/blobs/{digest}`, `/openai/{audio/speech,audio/transcriptions,images/generations}`.

## 6. Critérios de Aceite

- [ ] Pull com progresso visível (SSE/NDJSON repassado).
- [ ] Blobs upload/download roundtrip.
- [ ] Testes com HttpListener mock: pull stream, delete, blob roundtrip, 502 em host morto.

## Delivered

- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/82
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/79
- PR: https://github.com/afonsoft/OpenWebUiCode/pull/103
