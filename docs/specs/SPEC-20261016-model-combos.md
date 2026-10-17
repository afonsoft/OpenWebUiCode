# SPEC-20261016-model-combos

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `model-combos` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261016-model-combos` |
| Ticket | [#410](https://github.com/afonsoft/open-webui/issues/410) · Epic [#408](https://github.com/afonsoft/open-webui/issues/408) |
| Status | `Completed` |
| Fonte | análise fork alltomatos/opencode — `opencode/src/combo/index.ts` (failover strategies + rate limit + report loop) |

## 1. User Story

**As a** usuário do open-webui
**I want** agrupar vários modelos num "combo" nomeado com failover automático e rate limit compartilhado
**So that** uma run sobrevive a quota/indisponibilidade de um provider sem intervenção, e automações apontam para o combo em vez de um modelo fixo.

**Problem context:** hoje `request.Model` resolve exatamente um `ModelEntry`; um 429/credit-exhausted mata a run (provider-resilience retenta o MESMO modelo). O fork tem combos com estratégias `priority|round-robin|priority-round-robin`, cooldown por falha reportada e limite agregado de req/tokens por minuto.

## 2. Scope

**In scope:**
- RF-001: Entidade `ModelCombo` `{Id, Name, UserId?, Strategy, RequestsPerMinute?, TokensPerMinute?, Enabled}` + `ModelComboItem` `{ComboId, ModelId, Position}`.
- RF-002: `ComboService.ResolveAsync(comboId)` → próximo modelo respeitando estratégia + cooldown de falhas + janela de rate limit; `ReportAsync(id, model, ok, tokens)`.
- RF-003: Referência `combo:{id}` aceita onde `Model` é aceito (chat run, automação, agent `model:`): o executor resolve para o modelo efetivo; falha transitória do efetivo → report + próximo candidato (integra com o retry da SPEC provider-resilience como camada externa).
- RF-004: Cooldown por (combo,modelo): falha reportada → modelo pulado por 60s (ou `Retry-After` capturado, cap 5min); esgotados → erro `combo-exhausted`.
- RF-005: API CRUD `/api/v1/combos` + `GET /combos/{id}/status` (cooldowns/janela atuais); UI picker aceita combos.
- RF-006: SSE/run meta marca `combo:{id}` + modelo efetivo resolvido por step (transparência).

**Out of scope:**
- `generateDraft` (LLM sugere combo por descrição) — fase 2.
- Balanceamento por custo/latência medida — fase 2.

## 3. Technical Context

- `src/OpenWebUI.Domain/Entities.cs` — `ModelEntry`; `ChatCompletionRequest.Model`.
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs` + `Runs/ChatRunExecutor.cs` — resolução de modelo.
- `ProviderService` — chamadas ao provider (ponto do report).
- Migration EF (`ModelCombos`, `ModelComboItems`).

## 4. Requirements

### RF-002: Estratégias
- `priority`: primeiro disponível na ordem `Position`.
- `round-robin`: rotaciona entre disponíveis (contador por combo, persiste em kv).
- `priority-round-robin`: round-robin dentro do mesmo grupo de prioridade (Position igual), depois próximo grupo.

### RF-004: Rate limit compartilhado
- Janela deslizante de 60s por combo: `RequestsPerMinute`/`TokensPerMinute` excedidos → resolve pula o combo inteiro (erro `combo-rate-limit`).

## 5. API Contract

**`POST /api/v1/combos`** `{name, strategy, items:[{modelId}], requestsPerMinute?}` → 201.
**`GET /api/v1/combos/{id}/status`** → `{effectiveModel, cooldowns:[{modelId,until}], window:{requests,tokens}}`.
**Errors:** `400` strategy inválida/model inexistente; `404` combo; `409` nome duplicado (por usuário).

## 6. Acceptance Criteria

- [x] **Given** combo [m1,m2] priority, m1 retorna 429 **when** run **then** report(m1,fail) e run segue em m2 com meta marcando ambos.
- [x] **Given** round-robin 3 modelos **when** 3 resolves **then** distribui 1/1/1.
- [x] **Given** todos em cooldown **when** resolve **then** `combo-exhausted` com `retryAt`.
- [x] **Given** `requestsPerMinute=1` e 2 runs seguidas **when** resolve **then** segunda → `combo-rate-limit`.
- [x] **Given** modelo efetivo falha no meio da run **when** report **then** próxima rodada usa outro modelo e SSE mostra `combo`+efetivo.

## 7. Task Plan

- [x] **T1 —** Entidades + migration + ComboService (resolve/report/cooldown/janela).
- [x] **T2 —** Resolução `combo:{id}` no pipeline/executor + report de falhas + meta.
- [x] **T3 —** Endpoints CRUD + status.
- [x] **T4 —** Picker do client (combo como opção de modelo).
- [x] **T5 —** Testes: estratégias, cooldown, rate limit, resolução na run.

**7.1 Validation strategy:** unit tests com relógio injetável; integration com provider fake falhando.

## 8. Organization Guardrails

- Combo de usuário é privado; `UserId=null` só admin cria (mesmo padrão de tools/MCP globais).
- `report(ok=false)` nunca por cancelamento do usuário — não queima cooldown.
- Não logar API keys na resolução.

## 9. Definition of Done

- [x] RF-001..RF-006 verdes; build+test limpos; spec-status ok.

## Delivered

| Item | Link |
| --- | --- |
| PR/commit | #417 (cadeia #421) · 7a2bb4265 |
| Issue | https://github.com/afonsoft/open-webui/issues/410 |
