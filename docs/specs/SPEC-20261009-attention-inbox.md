# SPEC-20261009-attention-inbox

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `attention-inbox` (série devin-webapp, item D2) |
| Type | `Feature` |
| Stack | `.NET 10 / Blazor WASM` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-{YYYYMMDD}-attention-inbox` |
| Ticket | `GAP-devin-D2-attention-inbox` |
| Status | `Completed` |
| Priority | `medium` |
| Depends on | — |

## 1. User Story

**As a** usuário com várias runs em andamento
**I want** um filtro/badge "aguardando você" na sidebar listando chats com run bloqueada em aprovação ou pergunta
**So that** eu saiba onde o agente está esperando minha ação — como a inbox do Devin destaca sessões que precisam de input.

**Problem context:** runs pausam em `PermissionPromptCard`/`QuestionPromptCard` dentro do chat; nada sinaliza isso fora da conversa aberta.

## 2. Scope

**In scope:**
- `GET /api/v1/chats?attention=1` (ou campo `attention` no list): chats do usuário com run em estado `awaiting_input` (pergunta/aprovação pendente).
- Sidebar: filtro "Aguardando" + badge count; item marcado no list.
- Atualização quando a aprovação/pergunta resolve (poll leve do list ou broadcast existente).
- i18n 8 locales.

**Out of scope:**
- Push/notificação (D3 cobre feed); snooze.

## 3. Requirements

### RF-001
Estado `awaiting_input` derivado da run pendente mais recente do chat (join leve; índice/enum já existente ou coluna `State` na run — verificar `ChatRun`).

### RF-002
Badge na sidebar só aparece com count > 0; clicar filtra a lista.

## 4. Tests

Api.Tests: run com aprovação pendente → chat aparece em `attention=1`; resolve → some. Client.Tests: badge e filtro.

## 5. Rollout

Sem flag.

## 6. Risks

- Query cara em chats grandes → avaliar projeção/`AsNoTracking` + cache curto.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/261 |
