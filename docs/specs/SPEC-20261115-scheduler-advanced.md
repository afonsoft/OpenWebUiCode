# SPEC-20261115-scheduler-advanced

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `scheduler-advanced` |
| Type | `Backend` |
| Stack | `.NET 10 / EF Core / SQLite / Cronos` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/Devin-20261115-scheduler-advanced` |
| Depends on | SPEC-20261115-scheduler-robustness |
| Status | `Completed` |

---

## 1. User Story

> Como usuário, quero agendar automações no meu fuso horário e/ou via
> expressão cron, e quero que a automação execute uma **run de chat real**
> (com tools, worktree, budget e recovery) — não apenas um prompt simples —
> para que tarefas agendadas tenham a mesma autonomia de uma conversa normal.

---

## 2. Escopo

### Dentro do escopo

- `Automation.TimeZoneId` (IANA, default `UTC`): `daily`/`weekly`/`cron`
  computam `NextRunAt` no fuso do usuário.
- `ScheduleKind: "cron"` com campo `CronExpression`; parsing via pacote
  **Cronos** (somente parser, sem storage — sem Quartz).
- Nova `ActionKind: "run"`: cria `ChatRun` real (status `queued`) no chat da
  automação e enfileira no `ChatRunDispatcher` com `ApprovalPreset`
  configurável (`allow-readonly` default; `approve-mutations` opcional).
  Herda tools, worktree, budget gate, recovery e advisor.
- Contrato de API: campos `timeZoneId`, `cronExpression`, `approvalPreset`
  no request/response de automações; validação server-side de timezone e
  cron.
- UI `Automations.razor` (ou equivalente): campos de fuso, cron e modo de
  aprovação quando `kind=run`.

### Fora do escopo

- Histórico visual de execuções da automação na UI (SPEC-C).
- Agendamento multi-nó/cluster.
- `prompt`/`skill` continuam via `CompleteIntoChatAsync` (sem tools) —
  `run` é a via autônoma; migração de ações existentes fica como upgrade
  manual via UI.

---

## 3. Requisitos Funcionais

### RF-001 — Timezone
`ComputeNextRun` recebe o `TimeZoneInfo` resolvido de `TimeZoneId`
(fallback UTC em valor inválido). `daily`/`weekly`/`cron` calculam o
próximo instante local e convertem para epoch UTC. `interval` ignora fuso.

### RF-002 — Cron
`CronExpression` validada no create/update (erro 400 em expressão
inválida). `Cronos.CronExpression.Parse(...).GetNextOccurrence(nowTz)`
produz o próximo instante; `NextRunAt` armazena epoch UTC.

### RF-003 — Action `run`
`AutomationService.RunRunAsync`: cria `ChatRun` (queued, `Source="automation"`)
no `automation.ChatId` (ou cria chat nomeado pela automação), enfileira via
`IChatRunDispatcher.Enqueue`. `Automation.ApprovalPreset` define o preset
do chat antes do dispatch. O `AutomationRun` registra `ChatId` e `RunId`.

### RF-004 — UI
Campos novos aparecem condicionais ao `ScheduleKind`/`ActionKind`; i18n
nos 8 locales; validação client-side de cron (texto livre com hint do
formato).

---

## 4. Requisitos Não-Funcionais

- Dependência nova: `Cronos` (MIT, ~1 arquivo, battle-tested) — verificar
  breaking changes nulas.
- Migration EF: `TimeZoneId`, `CronExpression`, `ApprovalPreset`,
  `AutomationRun.RunId`.
- Retroatividade: automações existentes = `TimeZoneId "UTC"`, comportamento
  inalterado.

---

## 5. Critérios de Aceite

1. `daily 09:00 America/Sao_Paulo` dispara 12:00 UTC (fora do horário de
   verão) — teste com fuso fake via `TimeZoneInfo.FindSystemTimeZoneById`.
2. `cron "*/15 * * * *"` agenda a cada 15min; expressão inválida → 400.
3. `kind=run` cria `ChatRun` queued no dispatcher e executa o prompt com
   tools no worktree do chat (verificado via run events).
4. `approve-mutations` pausa mutações para aprovação do dono;
   `allow-readonly` segue autônomo.
5. Testes verdes + drift guards OK; migration limpa.

---

## 6. Test Plan

`tests/OpenWebUI.Api.Tests/AutomationScheduleAdvancedTests.cs`:
- `Daily_SaoPaulo_ConvertsToUtc`
- `Cron_Valid_SchedulesNext` / `Cron_Invalid_Returns400`
- `RunAction_CreatesQueuedChatRun`
- `RunAction_ApprovalPreset_AppliedToChat`
- `Interval_IgnoresTimeZone`
