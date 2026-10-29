# SPEC-20261002-access-grants-calendars

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `access-grants-calendars` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261002-access-grants` |
| Ticket | Issue #52 |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** compartilhar knowledge/notes/calendários com usuários e grupos com controle read/write
**So that** a colaboração tenha o modelo de permissões do upstream.

**Problem context:**
Upstream usa `access_grants` em knowledge, notes, channels, models e calendars (`principal_type: user|group`, `principal_id` ou `*`, `permission: read|write`). Hoje essas entidades são owner-only ou group-flag, e `/calendar` só mostra runs de automations — o upstream tem calendários reais (`calendar.py` 13 eps: calendars CRUD, events CRUD/search, grants).

## 2. Scope

**In scope:**
- Coluna `AccessGrants` JSON em `Knowledge`, `Note`, `Channel`, `Model` (quando não existir): `[{principal_type, principal_id, permission}]`; `*` = todos os usuários verificados.
- Filtro de listagem: owner + grants diretos + grants via membership de grupo + `*`; write exige `write`.
- `POST /{entity}/{id}/access/update` admin/owner ajusta grants.
- Calendário real: entidades `Calendar` + `CalendarEvent` (`title`, `start`, `end`, `color`, `notes`), endpoints `GET|POST /api/v1/calendars`, `/events`, `/events/search`; página `/calendar` passa a mostrar events + runs de automations (camadas alternáveis).
- UI de sharing (people/group picker) reutilizável nas entidades.

**Out of scope:**
- Convites por email; links públicos de calendar (ICS feed); sync externo (CalDAV).

## 3. Technical Context

**Where:** `Domain` (`AccessGrant` shared + campos), `Application` (`IAccessControlService`), `Infrastructure` (resolução grants→groups), `Api` (filtros nos endpoints existentes + `CalendarEndpoints` reais), `Client` (ShareDialog + calendar events).

**Files to read:**
- `src/OpenWebUI.Domain/Entities/{Knowledge,Note,Channel,Group}.cs`
- `src/OpenWebUI.Api/Endpoints/{KnowledgeEndpoints,ChannelEndpoints,AutomationEndpoints}.cs`
- `src/OpenWebUI.Client/Pages/Calendar.razor`

**Files to create/modify:**
```text
src/OpenWebUI.Domain/Entities/{AccessGrant,Calendar,CalendarEvent}.cs
src/OpenWebUI.Application/Interfaces/IAccessControlService.cs
src/OpenWebUI.Infrastructure/Services/AccessControlService.cs
src/OpenWebUI.Api/Endpoints/CalendarEndpoints.cs
src/OpenWebUI.Client/Components/ShareDialog.razor + Pages/Calendar.razor
tests/OpenWebUI.Api.Tests/AccessGrantsTests.cs
```

## 4. Requirements

### RF-001: Modelo de grants
- **Description:** `AccessGrant` por entidade; resolução: owner sempre total; `*` read se `permission=read`; group resolve via membership; write exige `write`.
- **Input → Output:** entidade + user → `none|read|write`

### RF-002: Aplicação
- **Description:** GET list/detail filtra por grants em knowledge, notes, channels, models; update exige write; grants exigem owner.
- **Rules:** usuário sem grant → `404` (não vazar existência).

### RF-003: Calendars reais
- **Description:** CRUD de calendários e eventos com grants; `/events?from&to` range; search por título.
- **Input → Output:** event → `{id,title,start,end,...}`

### RF-004: UI
- **Description:** ShareDialog (add user/group, read/write) + `/calendar` com toggle events/automation-runs e criação de evento.

**Invariants:** grants nunca escalam permissão do owner; `principal_id='*'` só com permissão do usuário para compartilhar publicamente (flag `sharing.public_*`).

## 5. API Contract

`POST /api/v1/{knowledge|notes|channels|models}/{id}/access/update` · `GET|POST /api/v1/calendars` · `GET|POST|DELETE /api/v1/calendars/events[/{id}]` · `GET /api/v1/calendars/events/search`
**Auth:** Bearer; mutações conforme grant/owner

## 6. Critérios de Aceite

- [ ] Usuário com grant read lê sem escrever; write edita; sem grant → 404.
- [ ] Grant `*` respeita flag de sharing público.
- [ ] Calendário cria/edita eventos e range filtra corretamente.
- [ ] Testes NUnit de matriz de permissão.

## 7. Notas

Verificar shape exato de `access_grants` no upstream (`models/groups.py` + routers) antes de finalizar contrato — padronizar.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/67 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/52
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/48
