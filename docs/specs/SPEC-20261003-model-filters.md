# SPEC-20261003-model-filters

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `model-filters` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261003-model-filters` |
| Ticket | Issue #87 |
| Status | `Completed` |

## 1. User Story

**As a** usuário
**I want** filtros de modelo (filtros upstream: inlet/outlet sobre mensagens)
**So that** o router `models`/`functions` suporta transformações de payload por modelo.

**Problem context:**
Upstream tem `filters` (functions tipo filter com inlet/outlet manipulando body). No .NET não executamos código arbitrário (decisão plugin-ecosystem) — o equivalente é o **filter declarativo por modelo**: regras de transformação (prepend/append system prompt, overrides de params, cap de max_tokens, redação de PII simples por regex) aplicadas no pipeline de completions.

## 2. Scope

**In scope:**
- `ModelEntry.FiltersJson` ou reutilizar MetaJson `{"filters":[{type,config}]}`: tipos `system_inject`, `params_override`, `regex_redact`, `max_tokens_cap`.
- Pipeline: ChatCompletions aplica filtros do modelo antes do provider (inlet) e opcionalmente pós-processa a resposta (outlet regex).
- UI: editor de modelo ganha seção "Filtros" (JSON editável + presets).

**Out of scope:**
- Filters como código Python (incompatível com a decisão no-code-exec do plugin-ecosystem).

## 3. Technical Context

**Files to read:**
- `src/OpenWebUI.Api/Endpoints/ApiEndpoints.cs` (pipeline completions)
- `src/OpenWebUI.Api/Endpoints/ModelEndpoints.cs` (MetaJson já existe)

## 4. Requirements

### RF-001: Filtros inlet
- **Rules:** ordem estável; falha de parse → ignora filtro (log); regex inválido rejeitado no save (400).

### RF-002: Outlet
- **Rules:** aplica sobre o texto final antes do SSE terminar.

## 5. API Contract

Sem endpoint novo — MetaJson do modelo + pipeline interno.

## 6. Critérios de Aceite

- [ ] Modelo com `system_inject` gera request com prompt extra (assert mock).
- [ ] `regex_redact` remove padrão do conteúdo enviado.
- [ ] JSON inválido → 400 com detalhe.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/97
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/87
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/79

## Reconciliation

_Reconciliado em 2026-10-08 (SPEC-20261008-spec-status-reconciliation, Issue #203)._

| RF | Veredito | Evidência |
| --- | --- | --- |
| RF-001 Filtros inlet | delivered | `Infrastructure/Services/ModelFilterService.cs:164` `ApplyInlet` — ordem estável; validação rejeita regex inválida com erro (`:131-139`); parse de filtro com falha é ignorado com log; testado em `ModelFiltersTests`/`ModelFilterEdgeTests` |
| RF-002 Outlet | delivered | `regex_redact` aplicado sobre o texto final antes do SSE terminar (`ModelFilterService.cs:16,199`); pipeline em `Api/Completions/ChatPipeline.cs` |

