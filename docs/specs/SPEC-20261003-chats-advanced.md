# SPEC-20261003-chats-advanced

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chats-advanced` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261003-chats-advanced` |
| Ticket | Issue #80 |
| Status | `Completed` |

## 1. User Story

**As a** usuário/admin
**I want** versões de mensagens, eventos realtime de chat e lista admin de todos os chats
**So that** a paridade do router `chats` upstream (50 eps) se fecha.

**Problem context:**
Upstream: `/chats/{id}/messages/{mid}` guarda histórico de edições (versions), eventos de chat via websocket (`chat-events`), e admin lista todos os chats. Hoje: edição sobrescreve conteúdo, sem histórico; eventos realtime só em canais; admin não enxerga chats alheios.

## 2. Scope

**In scope:**
- `ChatMessage.Versions` (JSON ou tabela `ChatMessageVersions`): edição/regeneração preserva a versão anterior; endpoint `GET /api/v1/chats/{id}/messages/{mid}/versions` + navegação na UI (◀ ▶).
- `chat-events` realtime: canal SignalR por chat para `message:updated`, presença de leitura básica (opcional por config).
- `GET /api/v1/chats/all` (admin): lista paginada de todos os chats com owner; rota admin UI `/admin/chats` ou aba.

**Out of scope:**
- CRDT/edição colaborativa simultânea; diff visual inline.

## 3. Technical Context

**Where:** `Domain` (ChatMessage + Version), `Infrastructure` (migration), `Api` (ChatEndpoints, hub), `Client` (message actions + admin aba).

**Files to read:**
- `src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs`
- `src/OpenWebUI.Api/Hubs/ChatHub.cs`
- `src/OpenWebUI.Client/Components/ChatView.razor` (edit/regenerate)

## 4. Requirements

### RF-001: Versionamento de mensagens
- **Description:** toda edição/regeneração empurra a versão atual para histórico; GET versions retorna [{content, created_at}].
- **Rules:** limite razoável (ex.: 50 versões); deletar chat limpa versões.

### RF-002: Chat events
- **Description:** join group `chat:{id}` no hub; eventos `message:updated` propagam edições para outros viewers da sessão.

### RF-003: Admin all-chats
- **Description:** `GET /api/v1/chats/all?query=&page=` admin-only; UI admin com lista + link.

## 5. API Contract

`GET /api/v1/chats/{id}/messages/{mid}/versions` · `GET /api/v1/chats/all` · hub `chat:{id}` (`message:updated`).

## 6. Critérios de Aceite

- [ ] Editar mensagem → versão anterior recuperável via endpoint e UI.
- [ ] Admin lista chats de outros usuários com paginação.
- [ ] Testes: versions em edição/regeneração, admin list 403 para não-admin.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/94
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/80
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/79
