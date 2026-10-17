# SPEC-20261016-websearch-failover

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `websearch-failover` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261016-websearch-failover` |
| Ticket | [#406](https://github.com/afonsoft/open-webui/issues/406) · Epic [#398](https://github.com/afonsoft/open-webui/issues/398) |
| Status | `Completed` |
| Fonte | análise opencode v2.0.31 (docs `/websearch` — provider `random` + retry em outro provider no 429 com cooldown `Retry-After|60s`) |

## 1. User Story

**As a** usuário com web_search no agente
**I want** que rate-limit/falha de um provider de busca tente automaticamente outro configurado
**So that** uma cota estourada do Tavily não devolva erro seco quando Brave/Exa também estão configurados.

**Problem context:**
`WebSearchService` despacha para um único provider configurado (`duckduckgo|tavily|brave|google_pse|searxng|exa|kagi|jina|perplexity`) — já temos mais providers que o opencode, mas sem failover: 429/5xx/timeout propaga erro à tool. O opencode mantém cooldown por provider (`Retry-After` ou 60s) e tenta outro disponível.

## 2. Scope

**In scope:**
- RF-001: `websearch.provider=auto` (novo valor; hoje exige um fixo) → ordem de tentativa: configurado primeiro, depois providers com credencial presente (ordem estável: tavily→brave→exa→google_pse→kagi→duckduckgo como último sem-key).
- RF-002: cooldown por provider: 429 → `Retry-After` respeitado (cap 5min) ou 60s default; timeout/5xx → 30s de cooldown; provider em cooldown é pulado na seleção.
- RF-003: `builtin:web_search` registra provider usado no resultado (`[via tavily]` suffix) — visível/debugável.
- RF-004: se todos em cooldown → erro agregado `all-providers-cooling` com próximo retry disponível.

**Out of scope:**
- Balanceamento/round-robin para reduzir custo (failover só sob falha).
- Cache de resultados de busca.

## 3. Technical Context

- `src/OpenWebUI.Infrastructure/Services/WebSearchService.cs` — dispatch por `cfg.Provider`.
- `ConfigService` — `websearch.provider`, chaves por provider.
- Estado de cooldown: `MemoryCache`/`ConcurrentDictionary` in-proc (single-instance ok).

## 4. Requirements

### RF-001: Seleção automática
- **Input → Output:** query + config → provider efetivo; `auto` exige ≥1 provider com credencial (duckduckgo conta como sem-key sempre disponível, porém com baixa qualidade — vai por último).

### RF-002: Cooldown
- **Description:** `CooldownUntil[provider]` atualizado em falha 429/5xx/timeout; seleção ignora providers cooling.

### RF-003: Rastreio
- **Description:** resultado e log da run indicam provider efetivo e se houve failover (`tavily→brave`).

### RF-004: Esgotamento
- **Description:** todos cooling → `BuiltinToolResult` de erro com `retryAfterSeconds` até o cooldown mais próximo.

## 5. API Contract

Sem endpoints novos. Config: `websearch.provider=auto`.

## 6. Acceptance Criteria

- [x] **Given** `auto` com tavily+brave configurados e tavily 429 **when** `web_search` executa **then** brave responde e o resultado indica failover.
- [x] **Given** tavily em cooldown **when** nova busca **then** nem tenta tavily.
- [x] **Given** todos cooling **when** busca **then** erro `all-providers-cooling` com próximo retry.
- [x] **Given** provider fixo (não auto) falha **when** busca **then** comportamento atual (erro direto — auto é opt-in).

## 7. Task Plan

- [x] **T1 —** Detecção de 429/`Retry-After`/timeout nas implementações + cooldown map.
- [x] **T2 —** Seleção `auto` + loop de tentativas com skip.
- [x] **T3 —** Marcação de provider/failover no output.
- [x] **T4 —** Testes (fake HttpMessageHandler 429→ok, cooldown, esgotamento); build; DoD; PR.

**7.1 Validation strategy:** unit tests da seleção/cooldown + integration tool; ≥80%.

## 8. Organization Guardrails

- Branch `feature/devin-20261016-websearch-failover`.
- Chaves nunca em log; provider no log só pelo id.
- `auto` é opt-in — default mantém provider configurado (retrocompatível).

## 9. Definition of Done

- [x] RF-001..RF-004 implementados e testados.
- [x] Critérios da seção 6 verdes.
- [x] build/test verdes.

## Delivered

| Item | Link |
| --- | --- |
| PR/commit | #424 · 4c457fc54 |
| Issue | https://github.com/afonsoft/open-webui/issues/406 |
