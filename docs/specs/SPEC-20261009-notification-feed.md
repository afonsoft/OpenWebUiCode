# SPEC-20261009-notification-feed

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `notification-feed` (série devin-webapp, item D3) |
| Type | `Feature` |
| Stack | `.NET 10 / Blazor WASM` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-{YYYYMMDD}-notification-feed` |
| Ticket | `GAP-devin-D3-notification-feed` |
| Status | `Completed` |
| Priority | `medium` |
| Depends on | — |

## 1. User Story

**As a** usuário
**I want** um sino com feed in-app das minhas notificações (runs concluídas, aprovações, erros)
**So that** eu veja o que aconteceu sem depender de push do navegador — como o feed do Devin.

**Problem context:** `NotificationEndpoints` + WebPush existem, mas não há feed in-app; eventos passam sem registro navegável.

## 2. Scope

**In scope:**
- Entidade `Notification` por usuário (title, body, link, readAt, kind, createdAt) + endpoints `GET /api/v1/notifications` (paged), `POST .../{id}/read`, `POST .../read-all`. Reusar `NotificationService`/`WebPushSender` para gravar junto do push.
- UI: sino no header com badge de não-lidas; dropdown/página com lista, mark-read, deep-link para o chat/run.
- i18n 8 locales.

**Out of scope:**
- Preferências novas (as do WebPush já existem); e-mail; retention policy (default: manter 90 dias via cleanup se já houver job de limpeza — senão sem cleanup na v1).

## 3. Requirements

### RF-001
Todo push WebPush gravado também como `Notification` (mesma construção de título/corpo).

### RF-002
Badge reflete não-lidas; `read`/`read-all` atualizam na hora.

### RF-003
Notificações novas chegam por poll (30s) — reuso de polling existente se houver.

## 4. Tests

Api.Tests: write-on-push, paged list, mark read/unread, isolamento por usuário. Client.Tests: badge, dropdown, deep-link.

## 5. Rollout

Sem flag; migration nova (`Notifications`).

## 6. Risks

- Crescimento da tabela → cleanup por TTL se o app já tiver job de manutenção.

## Reconciliation

_Entregue em 2026-10-09 (SPEC-20261009-notification-feed, Issue #253)._

| RF | Veredito | Evidência |
| --- | --- | --- |
| RF-001 Push → `Notification` | delivered | `Api/Notifications/WebPushChatRunNotifier.cs` — grava linha com mesma construção de título/trecho/link antes dos early-returns (feed independe de subscription) |
| RF-002 Badge + read/read-all | delivered | `Endpoints/NotificationEndpoints.cs` (`GET /api/v1/notifications`, `POST /{id}/read`, `POST /read-all`) + `Components/NotificationBell.razor` |
| RF-003 Poll 30s | delivered | `NotificationBell.razor` — `PeriodicTimer(30s)` sem SignalR |
| Tests | delivered | `Api.Tests/NotificationFeedTests.cs` (7) + `Client.Tests/NotificationBellTests.cs` (5) |

Entidade `Notification` + migration `AddNotifications`; sino montado na
sidebar (expandida/rail) e na barra mobile (`MainLayout`).
