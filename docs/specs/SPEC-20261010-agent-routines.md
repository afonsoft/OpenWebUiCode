# SPEC-20261010-agent-routines — Routine/reminder tools

Status: Completed

## Contexto

O fork já tem a infra de automações completa — entidade `Automation`
(interval/daily/weekly), `AutomationService.RunNowAsync` (cria um chat com a
resposta do modelo), `AutomationScheduler` (tick de 15s, reagenda sempre) e
endpoints REST. Falta: (a) um kind de execução única para lembretes e (b)
tools builtin para o agente criar/gerenciar rotinas a partir de uma run —
equivalente às tools `routine`/`reminder` do opencode.

## Objetivo

O agente agenda execuções recorrentes de prompt (rotina) e lembretes
one-shot (reminder) sem sair da conversa.

## Escopo

1. **ScheduleKind `once`** — `AutomationSchedule.ComputeNextRun` retorna o
   `NextRunAt` armazenado quando futuro, `null` quando passado (dispara uma
   vez e nunca reagenda). `AutomationUpsertRequest` ganha `RunAt` (epoch) e
   `InMinutes`; `AutomationEndpoints` valida `once` com `RunAt` futuro ou
   `InMinutes ≥ 1` e `Apply` resolve `NextRunAt` na criação/edição.
2. **`builtin:routine`** — ações `list|create|update|delete|enable|disable|run_now`
   sobre `db.Automations` do usuário. `model` opcional no create: default =
   modelo da run corrente (`context.RunId` → `ChatRun.Model`). Sem aprovação
   (recursos próprios do usuário, idempotente via id).
3. **`builtin:reminder`** — atalho one-shot: `message` + `in_minutes`
   (default 60) + `model` opcional → cria `Automation` com `ScheduleKind=once`.
4. **Notificação de reminder** — `RunNowAsync` dispara
   `automation.reminder` no feed quando uma automação `once` conclui `ok`
   (o lembrete precisa aparecer ao usuário, não só virar chat silencioso).

## Fora de escopo

Rotinas com ação `shell`/`mcp_tool`/`skill` do opencode (a ação aqui é sempre
prompt→modelo); edição de rotina na UI (já existe tela de automações que
lista as criadas pela tool).

## Acceptance Criteria

- `once` com `in_minutes` agenda `NextRunAt ≈ now+m`; após disparar,
  `ComputeNextRun` → `null` (nunca repete).
- `builtin:routine create` persiste automação com modelo da run quando
  `model` omitido; `list`/`enable`/`disable`/`delete`/`run_now` funcionam
  e respeitam `UserId`.
- `builtin:reminder` cria automação `once` com prompt da mensagem.
- Endpoint `POST /api/v1/automations` aceita `once` + `inMinutes`.
- Testes novos cobrindo criação/once/update/delete + scheduler unitário.
- Suíte Release completa 0 falhas.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/306 |
