# SPEC-20261002-channels-v2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `channels-v2` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261002-channels-v2` |
| Ticket | Issue #55 |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** canais DM, threads de resposta, reações e controle de leitura
**So that** os canais tenham a profundidade social do upstream.

**Problem context:**
Slice realtime-channels entregou canais em grupo + SignalR (`message:new`, `typing`, `presence`, `@modelo`). Upstream `channels.py` (28 eps) ainda tem: DM channels, threads (`parent_id`), reactions, unread counts, pinned messages, access grants por canal.

## 2. Scope

**In scope:**
- Canal tipo `dm` — criado ao abrir conversa direta com outro usuário (não listável por outros).
- Threads: `ChannelMessage.ParentId`, reply inline, painel de thread; evento `message:thread` no Hub.
- Reactions: `ChannelMessageReaction` (user+emoji), toggle add/remove; evento realtime.
- Unread: `last_read_at` por (channel,user) → badge na sidebar; marcar como lido.
- Pinned messages: pin/unpin + listagem.
- Access grants por canal (read/write por user/group) alinhado ao modelo de groups já entregue.

**Out of scope:**
- Notificações push/badge do browser (depende de notifications webhook).
- Criptografia E2E — upstream não tem.

## 3. Technical Context

**Where:** `Domain` (ChannelMessage.ParentId, ChannelMessageReaction, ChannelMember.LastReadAt, ChannelAccessGrant), `Infrastructure` (migration + lógica), `Api` (`ChannelEndpoints` novos eps, `ChatHub` novos eventos), `Client` (sidebar badge, thread panel, reactions UI).

**Files to read:**
- `src/OpenWebUI.Domain/Entities/Channel*.cs`
- `src/OpenWebUI.Api/Endpoints/ChannelEndpoints.cs`, `Hubs/ChatHub.cs`
- `src/OpenWebUI.Client/Pages/ChannelPage.razor`, `Components/Sidebar.razor`

**Files to create/modify:**
```text
src/OpenWebUI.Domain/Entities/{ChannelMessage,ChannelMessageReaction,ChannelMember}.cs
src/OpenWebUI.Infrastructure/Data/Migrations/*_ChannelsV2.cs
src/OpenWebUI.Api/Endpoints/ChannelEndpoints.cs
src/OpenWebUI.Api/Hubs/ChatHub.cs
src/OpenWebUI.Client/{Pages/ChannelPage.razor,Components/Sidebar.razor,Components/ThreadPanel.razor}
tests/OpenWebUI.Api.Tests/ChannelsV2Tests.cs
```

## 4. Requirements

### RF-001: DM channels
- **Description:** `POST /channels/dm {user_id}` cria ou retorna DM existente (tipo `dm`, 2 membros fixos).
- **Rules:** não aparece para terceiros; não pode sair sem arquivar.
- **Input → Output:** `{user_id}` → channel

### RF-002: Threads
- **Description:** `POST /channels/{id}/messages` aceita `parent_id`; `GET .../messages/{id}/replies` lista thread.
- **Rules:** reply notifica evento `message:new` com `parent_id` preenchido; contagem `reply_count` no parent.
- **Input → Output:** `{content, parent_id}` → mensagem

### RF-003: Reactions
- **Description:** `POST/DELETE /channels/{id}/messages/{mid}/reactions/{emoji}` toggle; broadcast `reaction` via SignalR.
- **Input → Output:** emoji → reaction agregada `[{emoji, user_ids, count}]`

### RF-004: Unread
- **Description:** `POST /channels/{id}/read` atualiza `last_read_at`; lista de canais retorna `unread_count` por usuário.
- **Input → Output:** GET channels → `unread_count` por canal; sidebar exibe badge.

### RF-005: Pinned
- **Description:** `POST/DELETE /channels/{id}/messages/{mid}/pin`; `GET .../pinned` lista.
- **Rules:** apenas membros com write.

**Invariants:** DM nunca visível fora dos 2 membros; grants respeitados em todo endpoint.

## 5. API Contract

`POST /api/v1/channels/dm` · `GET /channels/{id}/messages/{mid}/replies` · `POST|DELETE .../reactions/{emoji}` · `POST /channels/{id}/read` · `POST|DELETE .../pin` · `GET .../pinned`
**Auth:** Bearer (membro do canal; grants de write onde aplicável)

## 6. Critérios de Aceite

- [ ] DM criado uma vez e invisível a terceiros (401/403/404 consistente).
- [ ] Reply aparece na thread e emite evento realtime.
- [ ] Reaction toggle idempotente e agregada corretamente.
- [ ] `unread_count` zera após `/read`.
- [ ] Testes NUnit para todos os novos endpoints + migração.

## 7. Notas

Checar upstream `channels.py` para shape exato de `access_grants` (mesmo modelo de `knowledge`/`notes` grants se existir upstream) antes do merge — reutilizar padrão `Group`-based já existente.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/70 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/55
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/48
