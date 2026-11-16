# SPEC-20261115-scheduler-robustness

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `scheduler-robustness` |
| Type | `Backend` |
| Stack | `.NET 10 / EF Core / SQLite` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/Devin-20261115-scheduler-robustness` |
| Status | `Completed` |

---

## 1. User Story

> Como operador do Open WebUI, quero que automações agendadas e runs de chat
> sejam despachadas de forma robusta — sem bloqueio serial, sem sobreposição,
> com política de execuções perdidas e retry de falhas transitórias — para que
> o sistema continue previsível sob carga e após restarts.

**Decisão de plataforma**: manter `BackgroundService`/`IHostedService` nativo.
A tabela `Automations` já é o job store durável; Quartz.NET duplicaria o modelo
(~11 tabelas próprias ou RAM store volátil) sem ganho para deployment
single-instance. Parsing de cron (SPEC-B) será via `Cronos`, não Quartz.

---

## 2. Escopo

### Dentro do escopo

- Dispatch paralelo de automações `due` no `AutomationScheduler` (cap 4,
  `SemaphoreSlim`), com lock por `Automation.Id` (overlap guard).
- Política de misfire por automação (`MisfirePolicy`: `skip` | `run-once`).
- Retry com backoff exponencial para falhas transitórias de `mcp`/`webhook`
  (máx. 3 tentativas) + circuit breaker: N falhas consecutivas (default 5)
  desabilitam a automação e notificam o dono.
- Fila do `ChatRunDispatcher` bounded (`ChatRuns:QueueCapacity`, default 256)
  com backpressure (enqueue falha → endpoint responde 429) e fairness
  round-robin por `UserId` entre workers.
- Jitter ±5% no `NextRunAt` recalculado (anti thundering-herd).
- Extração de `PeriodicBackgroundService` (tick configurável + jitter +
  catch que não derruba) e adoção por `AutomationScheduler` e
  `RetentionScheduler` (demais migrados em follow-up).

### Fora do escopo

- Quartz.NET / Hangfire (ver decisão acima).
- Ação de automação que dispara ChatRun com tools (SPEC-B).
- Deduplicação de webhooks de entrada (SPEC-C).
- Migração dos demais schedulers ad-hoc para o base class (follow-up).

---

## 3. Requisitos Funcionais

### RF-001 — Dispatch paralelo com overlap guard
O tick deve: (a) carregar `due`; (b) pular automação cujo último
`AutomationRun` ainda está aberto (sem `FinishedAt`) — registrando skip;
(c) executar as restantes em paralelo com `Task.WhenAll` + `SemaphoreSlim`
limitado a `Automation:MaxConcurrent` (default 4); (d) um failure não afeta
as demais.

### RF-002 — Misfire policy
Nova coluna `Automation.MisfirePolicy` (`"skip"` default, `"run-once"`).
Ao carregar `due`, automações com `run-once` e `NextRunAt` atrasado
> `IntervalMinutes` (ou >1 dia para daily/weekly) executam uma única vez e
reagendam a partir de agora. `skip` executa normalmente (comportamento atual)
mas o run registra `Missed=true` quando o atraso > 2× intervalo.

### RF-003 — Retry + circuit breaker
`RunWebhookAsync`/`RunMcpAsync`: falhas transitórias (5xx, timeout, HTTP
error) retentam até 3 vezes com backoff 1s→4s (×2, jitter). Falha final
incrementa `Automation.ConsecutiveFailures`; ao atingir
`Automation:CircuitBreakerThreshold` (default 5) → `Enabled=false`,
`ConsecutiveFailures=0` reset ao próximo sucesso, notificação
`automation.disabled` ao dono. Nova coluna `ConsecutiveFailures`.

### RF-004 — Fila bounded + fairness
`ChatRuns:QueueCapacity` (default 256): `TryWrite` falha → run marcada
`failed` com erro "queue full" e endpoint retorna 429. Dispatcher passa a
canal de `(RunId, UserId)`; workers fazem round-robin entre usuários com
itens pendentes (estrutura `ConcurrentDictionary<UserId, ConcurrentQueue>`)
evitando starvation.

### RF-005 — Jitter
`ComputeNextRun` aplica jitter uniforme ±5% sobre o intervalo calculado
(apenas kinds `interval`/`daily`/`weekly`; `once` não tem jitter).

### RF-006 — `PeriodicBackgroundService`
Base class em `Infrastructure`: `Tick` (TimeSpan), `JitterPct`,
`ExecuteTickAsync(scope, ct)` abstrato; `ExecuteAsync` faz
`catch (Exception)` por tick (não derruba) + `Task.Delay` com jitter.
`AutomationScheduler` e `RetentionScheduler` herdam dela.

---

## 4. Requisitos Não-Funcionais

- Migration EF para `MisfirePolicy` + `ConsecutiveFailures` (defaults
  retrocompatíveis: `skip`, `0`).
- Concorrência testável: dispatch paralelo determinístico sob NUnit
  (injeção de factory, sem sleeps longos — backoff real ≤ 4s ou
  `Task.Delay` injetável).
- Sem mudança de API pública além do 429 em enqueue saturado e do novo
  campo `misfirePolicy` no contrato de automação.

---

## 5. Critérios de Aceite

1. 3 automações `due` executam em paralelo (tempo total ≈ max, não soma).
2. Automação com run aberto é pulada no próximo tick.
3. Webhook que falha 2× e depois responde 200 → run ok após retry.
4. 5 falhas consecutivas → `Enabled=false` + notificação ao dono.
5. Fila cheia → endpoint retorna 429; runs de usuários distintos são
   intercaladas pelos workers.
6. `dotnet test` verde + drift guards OK; migration aplicada limpa.

---

## 6. Test Plan

`tests/OpenWebUI.Api.Tests/AutomationSchedulerTests.cs`:
- `ParallelDispatch_DueAutomations_RunConcurrently`
- `Overlap_RunInFlight_SkipsNextTick`
- `Misfire_RunOnce_ExecutesOnceThenReschedules`
- `Retry_TransientFailure_SucceedsOnThirdAttempt`
- `CircuitBreaker_FiveFailures_DisablesAndNotifies`
- `Queue_Full_Returns429` / `Fairness_TwoUsers_Interleaved`
- `Jitter_WithinPlusMinus5Pct`
