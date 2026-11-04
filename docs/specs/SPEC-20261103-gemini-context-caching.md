# SPEC-20261103-gemini-context-caching

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `gemini-context-caching` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feat/gemini-context-caching-20261103` |
| Ticket | `GH#663` |
| Status | `Done` |
| Parent | provider improvement sweep 2026-11-03 (Gemini/Anthropic) |

## 1. User Story

**As a** admin com conexão Google AI Studio
**I want** contexto longo (system prompt + docs) cacheado via `cachedContents` do Gemini
**So that** runs repetidas paguem `cachedContentTokenCount` (mais barato) em vez de reprocessar o prompt inteiro.

**Problem context:** Anthropic já tem `cache_control` ephemeral (`SPEC-20261018` RF-003, `ApplyAnthropicCacheControl`); Gemini não tem equivalente — a API exige cache **explícito** (`POST /v1beta/cachedContents` → `name` referenciado no `generateContent` como `cachedContent`), com TTL e mínimo de tokens (~1024-32k conforme modelo). Sem isso, system prompts longos (compactor context, persona, tools) são cobrados integralmente a cada request.

## 2. Scope

**In scope:**
- Serviço `GeminiContextCacheService`: `create` (`model`, `contents`/`systemInstruction`, `ttl`/`expireTime`), `get`, `delete` de `cachedContents` na base da conexão.
- Estratégia de cache key: hash do (modelo + system + docs estáticos) → reusa `name` existente com refresh de TTL (`PATCH`), senão cria.
- `generateContent`/`streamGenerateContent` enviam `cachedContent: "cachedContents/<id>"` quando hit ≥ threshold configurável (default ~4k tokens estimados).
- Lifecycle: TTL default 60min, GC de caches expirados por nome armazenado (não persiste novo schema se possível — cache em memória + re-fetch por `GET cachedContents`); opt-in por conexão (`context_caching: true`).
- `cachedContentTokenCount` do `usageMetadata` já reportado via SPEC `providers-usage-tokens` (dependência).

**Out of scope:**
- Cache implícito (a API já aplica automaticamente quando elegível — sem ação).
- Anthropic — já tem `cache_control` ephemeral.
- Persistência EF de metadados de cache (preferir in-memory + list remota).

## 3. Technical Context

**Where the change happens:** novo serviço em `src/OpenWebUI.Infrastructure/Services/` (padrão `ProviderService`); wire em `BuildGooglePayload`/`NewGoogleRequest` (~L2457-2663) e `CompleteGoogle*`/`StreamGoogle*` (~L2827+); config da conexão (`ProviderConnection` — campo ou settings bag).

**Files to read before implementing:**
- `ProviderService.cs` — request builders Google, `TrimSlash`, auth (`key` query param vs header)
- `ProviderConnections`/`ConfigContracts` — onde o flag por conexão mora
- `SPEC-20261018-model-variants` RF-003 (Anthropic ephemeral — analogia de UX)
- Gemini `cachedContents` API (v1beta: create/get/patch/delete)

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/GeminiContextCacheService.cs (novo)
src/OpenWebUI.Infrastructure/Services/ProviderService.cs
src/OpenWebUI.Application/** (flag da conexão se necessário)
tests/OpenWebUI.Api.Tests/**
docs/en|pt/**
```

## 4. Requirements

### RF-001: CRUD de cachedContents
- **Description:** create/get/patch(ttl)/delete na base URL da conexão com a mesma auth do provider (`x-goog-api-key`/query `key` conforme implementação atual).

### RF-002: Cache-aware request path
- **Description:** opt-in por conexão; hash estável de (modelo + system + conteúdo cacheável acima do threshold) → `cachedContent` no request; miss → cria e usa; expiração → recria.

### RF-003: Threshold e degrad graceful
- **Description:** < threshold estimado → sem cache (evita overhead); erro 4xx do cachedContents → log + request sem cache (nunca falha a run por causa do cache).

### RF-004: Cleanup
- **Description:** dispose de caches criados pela instância no shutdown (best-effort) e refresh de TTL em reuso; sem vazamento de nomes.

## 5. API Contract

Request aditivo: `cachedContent` opcional; sem mudança quando flag off.

## 6. Acceptance Criteria

- [ ] Conexão com `context_caching` on + system > threshold: segunda run reusa `cachedContents/<name>` e `usageMetadata.cachedContentTokenCount` > 0.
- [ ] Falha simulada em cachedContents → run segue sem cache (degrade, log warning).
- [ ] Flag off → wire idêntico ao atual.
- [ ] `dotnet test` verde (mock HTTP do cache CRUD).

## 7. Task Plan

1. `GeminiContextCacheService` (CRUD + hash + TTL).
2. Wire no payload/request Google + flag da conexão.
3. Threshold + degrade path.
4. Testes com HttpClient mockado.
5. Docs en/pt da conexão Google.

## 8. Effort Estimate

`~6-8h`.

## Delivered

PR (merged): `GeminiContextCacheService` singleton (create/patch-ttl/
delete, auth x-goog-api-key, hash SHA256 de base+model+system, TTL 60min,
grace 5min) — RF-001; `TryApplyContextCacheAsync` nos 4 paths Google
(buffered/tools/stream/tools-stream) com flag `context_caching` e
threshold `context_caching_min_tokens` (default 4096, estimativa 4ch/tok)
em `ProviderConnection.Settings` — RF-002; systemInstruction sai do
payload quando cachedContent vai — RF-002; erro/4xx → warning + request
inline (RF-003); `IAsyncDisposable` deleta caches criados (RF-004).
Validado ao vivo (gemini-3.8-flash): create→generate retornou
`cachedContentTokenCount:2102`, PATCH estendeu expireTime, DELETE ok.
Escopo da fatia: cacheia systemInstruction (parte estática principal);
conteúdo user/multimodal fica para fatia futura.
