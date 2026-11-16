# SPEC-20261115-automation-observability

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `automation-observability` |
| Type | `Fullstack` |
| Stack | `.NET 10 / EF Core / Blazor WASM` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/Devin-20261115-automation-observability` |
| Depends on | SPEC-20261115-scheduler-robustness |
| Status | `Approved` |

---

## 1. User Story

> Como usuário, quero ver a saúde das minhas automações (taxa de sucesso,
> últimas falhas, execuções perdidas) e quero que retries do emissor de
> webhook não criem runs duplicadas — para confiar no agendamento e evitar
> trabalho repetido.

---

## 2. Escopo

### Dentro do escopo

- Endpoint `GET /api/v1/automations/{id}/stats`: total de runs, ok/failed,
  success-rate, última execução, último erro, falhas consecutivas atuais.
- Badge de saúde na lista de automações da UI (verde/amarelo/vermelho por
  success-rate e consecutive-failures) + painel de últimos runs.
- Deduplicação de webhook de entrada: `AutomationHookDelivery` registra
  `IdempotencyKey` (header `Idempotency-Key` ou hash SHA-256 do payload);
  re-entrega dentro de 24h retorna 200 com `deduplicated=true` sem criar
  run.
- Índice em `AutomationRun(AutomationId, StartedAt)` para stats baratos.

### Fora do escopo

- Alertas externos (email/webhook de saída) sobre saúde — já coberto
  parcialmente por `automation.failed`/`automation.disabled`.
- Dashboard global de todas as automações do admin.

---

## 3. Requisitos Funcionais

### RF-001 — Stats endpoint
Resposta: `{ total, ok, failed, successRate, lastRunAt, lastError,
consecutiveFailures, runs[] (últimas 10, desc) }`.

### RF-002 — Badge UI
Lista de automações mostra ícone de saúde: verde (≥90% ok nas últimas 20),
amarelo (70–90% ou 1–2 falhas seguidas), vermelho (<70% ou ≥3 seguidas ou
desabilitada por circuit breaker). Tooltip com `lastError`. i18n 8 locales.

### RF-003 — Dedup de webhook
`FireAsync`: lê `Idempotency-Key` (se presente) ou calcula
`SHA256(payload)`; lookup em `AutomationHookDeliveries` por
`(HookId, IdempotencyKey)` criado nas últimas 24h → resposta
`{ deduplicated: true }` sem run. Nova coluna `IdempotencyKey` + índice.

### RF-004 — Índice de runs
Migration: `IX_AutomationRuns_AutomationId_StartedAt`.

---

## 4. Requisitos Não-Funcionais

- Stats calculados por query agregada (sem carregar todas as runs).
- Dedup não bloqueia payloads legítimos distintos (hash do corpo cru).
- i18n paridade nos 8 locales para novos labels.

---

## 5. Critérios de Aceite

1. Stats refletem runs reais (ok/failed/últimas 10).
2. Badge muda de cor conforme thresholds; tooltip mostra erro.
3. Mesmo `Idempotency-Key` em 24h → `deduplicated`, nenhuma run nova;
   após 24h ou payload diferente → run criada.
4. Testes verdes + drift guards OK; migration limpa.

---

## 6. Test Plan

`tests/OpenWebUI.Api.Tests/AutomationObservabilityTests.cs`:
- `Stats_ReturnsAggregatesAndLastRuns`
- `WebhookDedup_SameKey_SkipsRun` / `DifferentPayload_NewRun`
- `Badge_Thresholds` (unit-level dos helpers de classificação)
