# SPEC-20261001-automations-calendar

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `automations-calendar` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261001-automations` |
| Ticket | `[automations-calendar] Issue #17` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** agendar prompts recorrentes e eventos em um calendário
**So that** a app execute tarefas programadas como no upstream (Automations + Calendar).

**Problem context:**
Sem automations/calendar (`docs/MIGRACAO-DOTNET.md:73,89` — ⬜). Nenhum job agendado nem UI de calendário.

## 2. Scope

**In scope:**
- Entidade `Automation`: prompt, modelo, schedule (cron-like simplificado: daily/weekly/intervalo), habilitado.
- Executor em background (`BackgroundService`) que dispara runs e cria chats com o resultado.
- Página `/automations` (lista, criar/editar, toggle, histórico de runs).
- Página `/calendar` com visão mensal das execuções agendadas e runs passados.

**Out of scope:**
- Pipelines/fluxos multi-etapa (fase 2).
- Notificações externas (email/webhook).
- Timezones por usuário (usa UTC ou local do servidor — `[A DEFINIR]`; recomendado: local do usuário).

## 3. Technical Context

**Where the change happens:**
`Domain` (Automation, AutomationRun), `Infrastructure` (BackgroundService scheduler), `Api` (`/api/v1/automations`), `Client` (páginas Automations + Calendar).

**Files to read before implementing:**
- `src/OpenWebUI.Api/Endpoints/{ChatEndpoints,WorkspaceEndpoints}.cs`, `src/OpenWebUI.Infrastructure/` (padrão de serviços)

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities/Automation*.cs
src/OpenWebUI.Infrastructure/Services/AutomationScheduler.cs
src/OpenWebUI.Api/Endpoints/AutomationEndpoints.cs
src/OpenWebUI.Client/Pages/{Automations,Calendar}.razor, Components/Sidebar.razor
tests/OpenWebUI.Api.Tests/AutomationEndpointsTests.cs
```

## 4. Requirements

### RF-001: CRUD de automations
- **Description:** Criar/editar/deletar/toggle automations com prompt + modelo + schedule.
- **Rules:** schedule validado; dono vê só os seus.
- **Input → Output:** `{prompt,model,schedule}` → automation

### RF-002: Execução agendada
- **Description:** Scheduler dispara runs no horário, chama completions e grava resultado como chat.
- **Rules:** falha de provider registra run como failed com erro; não bloqueia próximas execuções.
- **Input → Output:** tick do schedule → run + chat criado

### RF-003: Calendário
- **Description:** Visão mensal com dias marcados por execuções passadas/futuras.
- **Rules:** clique no dia lista runs; navegação entre meses.

## 5. API Contract

**Endpoint:** `GET|POST|PUT|DELETE /api/v1/automations`, `GET /api/v1/automations/{id}/runs`
**Auth:** `Bearer`

**Response:** `200` lista · `404` · `403` outro dono

## 6. Critérios de Aceite

- [ ] Automation "a cada minuto" (intervalo mínimo) dispara run registrado.
- [ ] Run failed registra erro sem parar o scheduler.
- [ ] Calendário marca dias com runs.
- [ ] Testes cobrem CRUD e cálculo de próxima execução.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/40 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/26
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/14
