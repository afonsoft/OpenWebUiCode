# SPEC-20261103-anthropic-thinking-config

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `anthropic-thinking-config` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feat/anthropic-thinking-config-20261103` |
| Ticket | `GH#659` |
| Status | `Done` |
| Parent | provider improvement sweep 2026-11-03 (Gemini/Anthropic) |

## 1. User Story

**As a** usuário com conexão Anthropic (Claude)
**I want** habilitar extended thinking por modelo/variant com budget configurável
**So that** Claude raciocina no canal de thinking (já renderizado pela UI) sem eu precisar hand-craft `extra_body` por conexão.

**Problem context:** a stream já separa `thinking_delta`/`thinking` blocks para o canal de reasoning (`ProviderService.cs` L2063, L2197, L2366 — `SPEC-20261015-chat-thinking-stream`) e `signature_delta` é descartado corretamente. Mas **ativar** extended thinking exige `ExtraBody` manual na variant (`SPEC-20261018-model-variants`) — não há knob first-class (`thinking: {type:"enabled", budget_tokens:N}`), nem validação de mínimo 1024 tokens/budget < max_tokens, nem beta header `interleaved-thinking-2025-05-14` opt-in.

## 2. Scope

**In scope:**
- Campo `ThinkingBudget`/`ThinkingEnabled` na variant/model config (camada OpenWebUI) → `payload.thinking = {type:"enabled", budget_tokens:N}` no `BuildAnthropicPayload`.
- Validação: budget ≥1024 e < `max_tokens` (elevar `max_tokens` automaticamente quando necessário, cap no máximo do modelo).
- Header beta `anthropic-beta: interleaved-thinking-2025-05-14` opt-in por conexão (flag na `ProviderConnection` ou ExtraHeaders).
- `redacted_thinking`/`thinking` blocks na **transcript**: replay de contexto envia blocos thinking de volta (Messages API exige tool_use+thinking assinados preservados no histórico) — verificar se o histórico atual re-emite `thinking` blocks ou os descarta (descartar pode invalidar tool_use em turns com thinking obrigatório).

**Out of scope:**
- UI visual de "thinking budget" (usa config de variant existente).
- Gemini `thinkingConfig` — provider separado (ver SPEC `gemini-native-tools` para tools; thinkingConfig pode ir junto se trivial).

## 3. Technical Context

**Where the change happens:** `ProviderService.BuildAnthropicPayload` (~L1722), `ApplyAnthropicParams` (~L1975, já mapeia `stop_sequences`/`max_tokens`), `NewAnthropicRequest` (headers, ~L1693); variant config em `ModelVariantService`/`ISystemPromptContributor` (`ExtraBody`, `CacheFormat`).

**Files to read before implementing:**
- `ProviderService.cs` — BuildAnthropicPayload, ApplyAnthropicParams, NewAnthropicRequest, stream thinking blocks
- `docs/specs/SPEC-20261018-model-variants.md`, `docs/specs/SPEC-20261015-chat-thinking-stream.md`
- Como o histórico é serializado hoje (`BuildAnthropicMessages` ~L1774 — thinking blocks preservados ou descartados?)

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/ProviderService.cs
src/OpenWebUI.Application/** (contrato variant/connection se campo novo)
src/OpenWebUI.Infrastructure/Services/ModelVariantService.cs (se knob em variant)
tests/OpenWebUI.Api.Tests/**
```

## 4. Requirements

### RF-001: Knob first-class de thinking
- **Description:** `thinking_budget` na variant (ou model overlay) → `thinking:{type:"enabled",budget_tokens:N}`; ausente = sem thinking (default da API).

### RF-002: Validação e cap
- **Description:** budget <1024 → clamp 1024; `max_tokens` < budget+1 → elevar para budget+1024 (cap por modelo do `model-capabilities.json`); erro claro se exceder cap do modelo.

### RF-003: Beta interleaved opt-in
- **Description:** flag por conexão/variant adiciona `anthropic-beta: interleaved-thinking-2025-05-14` (não default — muda billing/behavior).

### RF-004: Replay de thinking no histórico
- **Description:** turns passados com thinking re-emitem blocos `thinking`/`redacted_thinking` (com `signature` quando disponível) antes dos `tool_use` correspondentes; se a mensagem armazenada não guarda signature, estender o persist (canal reasoning já existe) ou documentar a degradação.

## 5. API Contract

Payload `thinking` só presente quando habilitado — sem mudança para modelos sem budget.

## 6. Acceptance Criteria

- [ ] Variant com `thinking_budget=4096` produz `thinking:{enabled,4096}` no payload e reasoning streamado para o canal.
- [ ] Budget <1024 → 1024; budget+max > cap do modelo → clamp com log.
- [ ] Multi-turn com tool_use + thinking não é rejeitado pela API (replay correto).
- [ ] `dotnet test` verde; fixtures de payload/stream atualizadas.

## 7. Task Plan

1. Auditar replay do histórico Anthropic (thinking preservado?) — define RF-004.
2. Campo + payload + validação.
3. Beta header opt-in.
4. Replay de thinking blocks.
5. Testes + checklist.

## 8. Effort Estimate

`~4-6h` (RF-004 é o ponto de risco — depende do formato persistido).

## Delivered

PR #666 (merged `5682a89`): `thinking_budget` via Params (model/variant/
connection) → `thinking:{enabled,budget}` na Messages API; clamps
(≥1024, max_tokens elevado p/ budget+1024, cap 64k); beta headers via
`connection.Headers` provados no wire (RF-003 zero-código). RF-004
documentado: thinking não persiste no histórico — replay omite blocos
(persistência = mudança de schema separada).
