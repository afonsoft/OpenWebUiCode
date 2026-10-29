# SPEC-20261007-chat-notifications

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-notifications` (P2 da série harness-chat; depende de P1 `chat-detached-runs`) |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261007-chat-notifications` |
| Ticket | `GAP-chat-notifications` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** ser avisado quando uma resposta termina — toast na aba aberta e notificação do SO quando a aba está em background ou fechada
**So that** eu não precise ficar olhando a tela esperando respostas longas/loops de tools.

**Problem context:**
Com runs desacopladas (P1) a resposta pode terminar quando a aba está fechada — hoje não há nenhum aviso. O `ChatHub` SignalR já existe com grupos `chat-{chatId}` (usado por notes-collab), e o `service-worker.js` do PWA já está registrado — falta o caminho run→notificação→clique-volta-pro-chat. O harness faz isso com `IChatRunNotifier` (SignalR quando conectado + Web Push VAPID quando offline).

## 2. Scope

**In scope:**
- `INotificacaoRunChat` (port de `IChatRunNotifier`): eventos `completed`, `failed`, `stopped`, `interrupted` com `{chatId, runId, title, snippet}`.
- Caminho 1 — SignalR: dispatcher publica `run.completed` no grupo `chat-{chatId}`; `RealtimeService` do cliente despacha.
- Caminho 2 — Notification API (mesma aba, `document.hidden`): `ChatNotificationsService` mostra `Notification` com título do chat + trecho.
- Caminho 3 — Web Push: subscription por usuário (`POST/DELETE /api/v1/notifications/push`), `service-worker.js` ganha handler `push`/`notificationclick` que abre `/c/{chatId}`; envio VAPID server-side quando nenhum cliente ativo.
- Preferência por usuário: toggle em Settings → Notifications ("avisar quando resposta terminar") + toggle de Web Push.
- Permissão de notificação pedida sob gesto do usuário (toggle), não no boot.

**Out of scope:**
- Push para menções em canais/collab (generalizar depois).
- Push de tools pendentes de aprovação (P3 faz separado, mesma seam).
- iOS Safari Web Push (limitações documentadas — funciona só com PWA instalado).

## 3. Technical Context

**Where the change happens:**
`Domain` (`ChatPushSubscription`), `Infrastructure` (migration, `WebPushSender` — pacote `WebPush` ou JWT ES256 + fetch manual), `Api` (endpoints de subscription + `VAPID__*` config), `Client` (`ChatNotificationsService`, toggle em Settings, `service-worker.js`).

**Files to read before implementing:**
- `src/OpenWebUI.Api/Hubs/ChatHub.cs` (grupos `chat-{chatId}`)
- `src/OpenWebUI.Client/wwwroot/service-worker.js` (PWA atual)
- `src/OpenWebUI.Client/Services/{RealtimeService,ApiService}.cs`
- Referência: `repos/agent-harness` `WebPushChatRunNotifier.cs`, `WebPushSender.cs`, `push-sw.js`, seção notify do `taskboard.js`

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities.cs (+ChatPushSubscription)
src/OpenWebUI.Infrastructure/Notifications/{ChatRunNotifier,WebPushSender}.cs
src/OpenWebUI.Api/Endpoints/NotificationEndpoints.cs (+push subscribe/vapid-key)
src/OpenWebUI.Client/Services/ChatNotificationsService.cs
src/OpenWebUI.Client/wwwroot/service-worker.js (+push handlers)
src/OpenWebUI.Client/Components/SettingsModal.razor (+toggle)
tests/OpenWebUI.Api.Tests/PushEndpointsTests.cs
```

## 4. Requirements

### RF-001: Seam de notificação de run
- **Description:** dispatcher chama `INotificacaoRunChat.NotifyAsync(run)` ao finalizar (completed/failed/stopped/interrupted); implementação publica SignalR + decide push.
- **Rules:** nunca bloqueia nem falha a run; notificação é best-effort com log.
- **Input → Output:** run final → eventos para os caminhos ativos

### RF-002: SignalR `run.completed`
- **Description:** evento no grupo `chat-{chatId}` com `{runId, status, title, snippet}`; cliente com o chat aberto atualiza UI; cliente em outra tela dispara toast/Notification.
- **Rules:** grupo só recebe quem tem acesso ao chat (JoinChat já valida).
- **Input → Output:** run final → `run.completed` aos membros do grupo

### RF-003: Notification API quando oculto
- **Description:** `document.hidden && Notification.permission === "granted"` → `new Notification(title, {body: snippet, tag: runId})`; clique foca a aba e navega pro chat.
- **Rules:** nunca dispara com a aba visível; `tag` deduplica por run.
- **Input → Output:** evento + aba oculta → notificação do SO

### RF-004: Web Push para aba fechada
- **Description:** subscription `{endpoint, p256dh, auth}` por UserId; no fim da run, se não houver conexão SignalR ativa do usuário, envia push com `{chatId, title, snippet}`; SW exibe e `notificationclick` abre `/c/{chatId}`.
- **Rules:** subscription 404/410 → remove do banco; payload criptografado (aes128gcm); `VAPID_SUBJECT` configurável; sem VAPID keys → push desligado silenciosamente.
- **Input → Output:** run final sem cliente ativo → push → SW → notificação clicável

### RF-005: Preferências
- **Description:** toggles em Settings → Notifications: `notify_chat_done` (toast+Notification) e `notify_push` (Web Push). Persistidos por usuário.
- **Rules:** pedir permissão `Notification.requestPermission()` só ao ligar o toggle; permissão `denied` mostra hint.
- **Input → Output:** toggle → subscription criada/removida + preferência salva

**Business rules / invariants:**
- Push só contém título do chat + trecho curto — nunca conteúdo integral nem dados de outros usuários.
- Subscription é por usuário — endpoint de outro usuário nunca recebe push.
- Sem SignalR conectado E sem subscription → nenhuma notificação (silencioso).

## 5. API Contract

**Endpoint:** `GET /api/v1/notifications/push/vapid-key` · `POST|DELETE /api/v1/notifications/push` · `PATCH /api/v1/users/me/settings {notifyChatDone, notifyPush}`
**Auth:** `Bearer` JWT

**Response:** `200` chave pública/ok · `404` subscription inexistente

## 6. Critérios de Aceite

- [ ] Com aba aberta em outra tela, fim de run dispara toast.
- [ ] Com aba oculta, fim de run dispara notificação do SO; clique foca e navega pro chat.
- [ ] Com aba fechada + Web Push habilitado, notificação chega via SW e abre `/c/{chatId}`.
- [ ] Subscription morta (410) é removida sem falhar a run.
- [ ] Sem VAPID configurado tudo funciona menos o push.
- [ ] Testes: endpoints de subscription, decisão push-vs-signalr, payload sem dados alheios.

## 7. Notas

VAPID keys: gerar par e persistir em ConfigEntry ou env `VAPID__PUBLIC_KEY`/`VAPID__PRIVATE_KEY`. Biblioteca `WebPush` (NuGet) é a escolha simples; alternativa sem dependência é JWT ES256 + POST com `aes128gcm` manual (mais código). SW de push pode coexistir no `service-worker.js` atual do PWA.

## Reconciliation

_Reconciliado em 2026-10-08 (SPEC-20261008-spec-status-reconciliation, Issue #203)._

| RF | Veredito | Evidência |
| --- | --- | --- |
| RF-001 Seam de notificação | delivered | `Application/Interfaces/IChatRunNotifier.cs`; dispatcher chama `NotifyAsync` ao finalizar — best-effort, nunca falha a run (`Api/Notifications/`) |
| RF-002 SignalR `run.completed` | delivered | `Api/Notifications/SignalRChatRunNotifier.cs` — evento no grupo `chat-{chatId}` com `{runId,status,title,snippet}` |
| RF-003 Notification API | delivered | `Client/wwwroot/js/app.js:147-178` — `document.hidden && permission==='granted'` → `new Notification` com `tag` por run; clique foca aba |
| RF-004 Web Push | delivered | `WebPushChatRunNotifier.cs` + `Infrastructure/Services/WebPushSender.cs` + `VapidKeyService.cs` + `ChatPushSubscriptions` (404/410 remove, `:69`); SW `push`/`notificationclick` em `service-worker.js:64,90` abre `/c/{chatId}` |
| RF-005 Preferências | delivered | `SettingsModal.razor:185,193` toggles `notify_chat_done`/`notify_push`; `Client/Services/ChatNotificationsService.cs` — requestPermission só ao ligar; hint se `denied` |

