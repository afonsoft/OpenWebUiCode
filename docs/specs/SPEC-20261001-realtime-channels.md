# SPEC-20261001-realtime-channels

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `realtime-channels` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261001-realtime-channels` |
| Ticket | `GAP-implementation-realtime-channels — Issue a criar` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** canais de chat em grupo com atualização em tempo real entre participantes
**So that** equipes conversem junto ao modelo como no upstream (Channels + Socket.io).

**Problem context:**
Não existe infra realtime nem Channels (`docs/MIGRACAO-DOTNET.md:63,69` — ⬜). Chat é 1:1 usuário↔modelo; presença, "digitando...", mensagens de outros usuários não existem.

## 2. Scope

**In scope:**
- SignalR hub `/ws` para eventos de chat (nova mensagem, edição, "typing", presença).
- Entidade `Channel` (nome, descrição, membros, mensagens próprias — separadas de `Chat`).
- Rotas `/channels/{id}` no Blazor com lista de canais na sidebar (seção "Channels").
- Mensagens de canal: texto, menção a modelos (`@modelo` invoca completions no canal), indicador de quem enviou.
- Eventos realtime: nova mensagem, typing, usuário online.

**Out of scope:**
- Threads de canal, reações, threads aninhadas (upstream tem; fase 2).
- Canais DM/privados granulares por grupo (depende de Groups/RBAC — SPEC `auth-sso-rbac`).
- Escala multi-instância (backplane Redis).

## 3. Technical Context

**Where the change happens:**
`Domain` (Channel, ChannelMessage, ChannelMember), `Infrastructure` (EF + hub SignalR), `Api` (`/api/v1/channels`, `MapHub`), `Client` (página Channel, sidebar section, `HubConnection`).

**Files to read before implementing:**
- `CLAUDE.md` · `.claude/rules/dotnet.md` · `.claude/rules/blazor.md`
- `src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs`, `src/OpenWebUI.Client/Components/{Sidebar,ChatView,MessageBubble}.razor`
- `src/OpenWebUI.Client/Services/ApiService.cs`

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities/Channel*.cs
src/OpenWebUI.Infrastructure/Hubs/ChatHub.cs (+ EF entities)
src/OpenWebUI.Api/Endpoints/ChannelEndpoints.cs, Program.cs (MapHub)
src/OpenWebUI.Client/Pages/ChannelPage.razor, Components/Sidebar.razor, Services/RealtimeService.cs
tests/OpenWebUI.Api.Tests/ChannelEndpointsTests.cs
```

## 4. Requirements

### RF-001: CRUD de canais
- **Description:** Criar/listar/arquivar canais com membros; endpoints `/api/v1/channels`.
- **Rules:** criador é admin do canal; membros veem só canais onde participam.
- **Input → Output:** `{name, memberIds[]}` → canal criado

### RF-002: Mensagens de canal
- **Description:** Enviar/listar mensagens de texto em canal; histórico paginado.
- **Rules:** mensagem carrega autor (nome + avatar); ordenação cronológica.
- **Input → Output:** `{content}` → mensagem persistida e difundida

### RF-003: Realtime via SignalR
- **Description:** Hub entrega `message:new`, `user:typing`, `presence` aos membros conectados; cliente Blazor conecta com token JWT.
- **Rules:** reconexão automática; queda do hub não quebra REST.
- **Input → Output:** evento → UI atualiza sem reload

### RF-004: Menção a modelo
- **Description:** `@modelo` em mensagem de canal invoca completions e posta a resposta como mensagem do modelo.
- **Rules:** mesma pipeline de providers do chat; erro vira mensagem de erro visível.
- **Input → Output:** `@modelo pergunta` → resposta streaming postada no canal

**Business rules / invariants:**
- Canal vazio de membros não pode existir (criador sempre membro).
- Mensagens de canal nunca aparecem na lista de chats 1:1.

## 5. API Contract

**Endpoint:** `GET|POST /api/v1/channels`, `GET|POST /api/v1/channels/{id}/messages`, `Hub /ws`
**Auth:** `Bearer` JWT (query token no handshake SignalR)

**Response:** `200` coleção de canais/mensagens · `403` não-membro · `404` canal inexistente

## 6. Critérios de Aceite

- [ ] Dois usuários em abas distintas veem mensagens um do outro sem refresh.
- [ ] `@modelo` no canal produz resposta do provider no canal.
- [ ] Indicador de typing aparece para os demais membros.
- [ ] Sidebar lista canais; rota `/channels/{id}` renderiza histórico.
- [ ] Testes cobrem CRUD de canal e autorização de membros.

## 7. Notas

`[A DEFINIR]` se DM entre usuários entra já ou fica para RBAC (SPEC `auth-sso-rbac`) — recomendado: depois.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/31 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/17
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/14
