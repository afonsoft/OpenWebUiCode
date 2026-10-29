# SPEC-20261002-notifications-webhooks

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `notifications-webhooks` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261002-notifications-webhooks` |
| Ticket | Issue #58 |
| Status | `Completed` |

## 1. User Story

**As a** admin do Open WebUI
**I want** configurar webhooks de notificação (novo usuário pendente, aprovação, erros de automação)
**So that** recebo alertas fora da aplicação, como no upstream `notifications.py`.

**Problem context:**
Upstream tem 7 endpoints de notifications (webhook URL por usuário/admin + teste). Nosso port não tem nenhum disparo externo.

## 2. Scope

**In scope:**
- `NotificationWebhook` (user-scoped + admin-scoped): url, events habilitados, enabled.
- Eventos: `user.pending`, `user.approved`, `automation.failed`, `channel.mention` (se trivial).
- `POST /api/v1/notifications/webhook` config por usuário; `.../admin` para global (admin-only); `POST .../test` dispara payload de teste.
- Dispatcher `NotificationService` com fila best-effort (não bloqueia request; log de falha).
- Assinatura: header `X-Webhook-Event` + `X-Webhook-Signature` HMAC com segredo por webhook.

**Out of scope:**
- Retry com backoff persistente (best-effort + log na v1); templates customizáveis; digest.

## 3. Technical Context

**Where:** `Domain` (NotificationWebhook), `Infrastructure` (NotificationService com HttpClient), `Api` (`NotificationEndpoints` + chamadas no fluxo de signup/approval/automations), `Client` (campo webhook URL em Settings).

**Files to read:**
- `src/OpenWebUI.Api/Endpoints/{UserEndpoints,AutomationEndpoints}.cs`
- `src/OpenWebUI.Infrastructure/Services/AutomationService.cs`
- `src/OpenWebUI.Infrastructure/Services/ToolExecutor.cs` (padrão HttpClient externo)

**Files to create/modify:**
```text
src/OpenWebUI.Domain/Entities/NotificationWebhook.cs
src/OpenWebUI.Application/Contracts/NotificationContracts.cs
src/OpenWebUI.Infrastructure/Services/NotificationService.cs
src/OpenWebUI.Api/Endpoints/NotificationEndpoints.cs
tests/OpenWebUI.Api.Tests/NotificationEndpointsTests.cs
```

## 4. Requirements

### RF-001: Config
- **Description:** webhook por usuário (`{url, events[]}`) e global admin; máscara na leitura; segredo HMAC gerado no create.
- **Input → Output:** `{url, events}` → webhook `{id, url_masked, events}`

### RF-002: Dispatch
- **Description:** Eventos publicam POST JSON `{event, data, ts}` com headers de assinatura; falha → log warning, nunca quebra request.
- **Input → Output:** evento interno → POST externo best-effort

### RF-003: Teste
- **Description:** `POST .../test` envia `{event:"test"}` e retorna status HTTP do destino.
- **Input → Output:** — → `{status_code, ok}`

**Invariants:** URLs validadas (http/https; bloquear RFC1918? `[A DEFINIR]` — upstream não bloqueia); secrets assinam mas nunca retornam.

## 5. API Contract

`GET|POST|DELETE /api/v1/notifications/webhook` · `POST /api/v1/notifications/webhook/test` · variantes `/admin/*`
**Auth:** Bearer · admin scope admin-only

## 6. Critérios de Aceite

- [x] Webhook recebe payload assinado no evento configurado.
- [x] Falha do destino não afeta o fluxo originário.
- [x] Test endpoint reflete status real do destino.
- [x] Testes NUnit com HttpListener.

## 7. Notas

SSRF: avaliar blocklist de IPs internos (link-local, loopback) — upstream permite, mas recomendo validação com opt-out por config.

## 8. Delivered

- `NotificationWebhook` (OwnerId null = global/admin) + migration `NotificationWebhooks`.
- `NotificationService`: dispatch best-effort (fire-and-forget, timeout 15s, log de falha), HMAC-SHA256 `X-Webhook-Signature` + `X-Webhook-Event`; segredo nunca retornado.
- Endpoints `GET|POST|DELETE /api/v1/notifications/webhook`, `POST /webhook/test` e variantes `/admin/*` (admin-only).
- Eventos: `user.pending` (signup pendente), `user.approved` (update/role saindo de pending), `automation.failed` (run falha no scheduler). `channel.mention` ficou fora — upstream só menciona modelos hoje.
- Cliente: campo webhook (URL + eventos + enabled + testar/remover) na aba Conta e seção global na aba Admin do Settings, localizado pt-BR/en-US.
- Testes: `NotificationEndpointsTests` (5 casos com HttpListener real).
- Nota de implementação: handlers registrados como lambdas com 2+ parâmetros — lambda de 1 parâmetro (`HttpContext`) casa com o overload `RequestDelegate` que descarta o `IResult` (analyzer ASP0016), quebrando silenciosamente os status codes.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/73 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/58
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/48
