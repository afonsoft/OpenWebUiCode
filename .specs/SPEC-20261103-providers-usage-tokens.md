# SPEC-20261103-providers-usage-tokens

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `providers-usage-tokens` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feat/providers-usage-tokens-20261103` |
| Ticket | `GH#658` |
| Status | `Approved` |
| Parent | provider improvement sweep 2026-11-03 (Gemini/Anthropic) |

## 1. User Story

**As a** usuário com conexões Anthropic/Google
**I want** tokens contabilizados nas runs (steps-usage, budgets, analytics) como já acontece com OpenAI/Ollama
**So that** custo por step e budgets param de reportar uso zero/estimado nesses providers.

**Problem context:** `ProviderService.ParseUsage` (`ProviderService.cs:~664`) só mapeia os shapes OpenAI (`usage.prompt_tokens/completion_tokens`) e Ollama (`prompt_eval_count/eval_count`). Anthropic devolve `usage.{input_tokens,output_tokens,cache_creation_input_tokens,cache_read_input_tokens}` (non-stream) e `message_delta.usage` (stream); Gemini devolve `usageMetadata.{promptTokenCount,candidatesTokenCount,thoughtsTokenCount,cachedContentTokenCount}` — nenhum dos dois é parseado hoje → `ProviderUsage` fica nulo nesses providers, quebrando steps-usage (`SPEC-20261016-agent-steps-usage`) e run-cost-budgets (`SPEC-20261023`).

## 2. Scope

**In scope:**
- Parse de usage Anthropic: non-stream `usage` + stream `message_start.usage` / `message_delta.usage` (campos `input_tokens`, `output_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`).
- Parse de usage Gemini: `usageMetadata` no response e no chunk de stream (`promptTokenCount`, `candidatesTokenCount`, `thoughtsTokenCount`, `cachedContentTokenCount`, `totalTokenCount`).
- Extensão de `ProviderUsage` (ou sub-record) para carregar `cached`/`thoughts` tokens sem quebrar consumidores atuais (prompt/completion/reasoning).
- Testes de fixture (SSE + JSON) para os dois providers.

**Out of scope:**
- Repricing/billing UI (consome `ProviderUsage` — ganha automaticamente).
- Usage para tools do modelo (ex.: code execution token count) — campo existe mas fica para iter. 2.

## 3. Technical Context

**Where the change happens:** `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` — `ParseUsage` + loops de stream Anthropic (`StreamAnthropicAsync`/`StreamAnthropicToolsAsync` ~L2000-2400) e Google (`StreamGoogle*` ~L2800-3050); `ProviderUsage` record ~L3761; consumidores em `ChatRunExecutor` (steps-usage) e `BudgetPolicyService`.

**Files to read before implementing:**
- `ProviderService.cs` — `ParseUsage`, `AnthropicStreamCtx`, Google stream loop, `ProviderUsage`/`ProviderCompletionResult` records
- `docs/specs/SPEC-20261016-agent-steps-usage.md`, `docs/specs/SPEC-20261023-run-cost-budgets.md` — consumidores
- Referências: Anthropic Messages API `usage` object; Gemini `usageMetadata`

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/ProviderService.cs
src/OpenWebUI.Application/** (se ProviderUsage mora nos contracts)
tests/OpenWebUI.Api.Tests/** (fixtures + asserts)
```

## 4. Requirements

### RF-001: Usage Anthropic (stream + non-stream)
- **Description:** mapear `input_tokens`→prompt, `output_tokens`→completion; capturar `cache_creation_input_tokens`/`cache_read_input_tokens` em campo `CachedTokens`/`CacheWriteTokens` novo; stream: `message_start.usage` (input inicial) + `message_delta.usage` (output acumulado) — último valor vence.

### RF-002: Usage Gemini
- **Description:** `promptTokenCount`→prompt, `candidatesTokenCount`→completion, `thoughtsTokenCount`→reasoning, `cachedContentTokenCount`→cached; stream lê `usageMetadata` do último chunk; non-stream do root.

### RF-003: Modelo de usage estendido, backward-compatible
- **Description:** `ProviderUsage` ganha campos opcionais (`CachedTokens`, `ThoughtTokens`/`ReasoningTokens` se já não existir mapeado) — serialização aditiva, sem quebrar consumidores atuais (steps-usage UI, budgets).

### RF-004: Fixtures de contrato
- **Description:** golden files SSE Anthropic (`message_start`/`content_block_delta`/`message_delta` com usage) e Gemini (chunks com `usageMetadata`) + JSON non-stream — teste prova o valor parseado end-to-end no `ProviderCompletionResult.Usage`.

## 5. API Contract

`ProviderUsage` serializado aditivo (novos campos nullable/0 default) — wire compatível com UI atual.

## 6. Acceptance Criteria

- [ ] Run Anthropic reporta prompt/completion/cache tokens por step.
- [ ] Run Gemini reporta prompt/candidates/thoughts/cached por step.
- [ ] Steps-usage e budgets consomem os valores sem zero-fill.
- [ ] Testes de fixture verdes para ambos os providers (stream + non-stream).

## 7. Task Plan

1. Estender `ProviderUsage` + `ParseUsage` (dispatch por provider).
2. Wire no stream Anthropic (`message_delta`) e non-stream.
3. Wire no stream/response Gemini (`usageMetadata`).
4. Fixtures + testes.
5. `dotnet test` + gate `.ci/`.

## 8. Effort Estimate

`~4-6h`.
