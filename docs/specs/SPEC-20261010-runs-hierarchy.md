# SPEC-20261010-runs-hierarchy — Hierarquia pai↔filho + árvore de runs/chats

Status: Completed
Owner: Afonso

## Contexto
`delegate_task` criava um Chat filho independente ("delegado: …") com sua própria `ChatRun` — sem nenhum link pai↔filho persistido. Impossível navegar do pai ao filho, listar delegações ou renderizar árvore de sub-sessions (estilo opencode-web). Cobre a decisão de produto D6.

## Implementado
- **Schema**: `Chat.ParentChatId` e `ChatRun.ParentRunId` (nullable, índices) — migration `ChatHierarchy`.
- **delegate_task**: grava `ParentChatId`/`ParentRunId` no filho.
- **API**: `GET /api/v1/chats/{id}/children` (owner-only) → `[{id, title, lastRunStatus, createdAt}]`; `ChatSummaryResponse` ganha `parentChatId` + `childrenCount` (group-count, sem N+1); `ChatResponse` ganha `parentChatId` + `parentTitle`; `ChatRunResponse` expõe `parentRunId`.
- **UI sidebar**: chats com `ParentChatId` saem do topo e renderizam indentados sob o pai (grupo colapsável via caret, `aria-expanded`); órfãos (pai deletado) caem na raiz com badge "delegado". O Virtualize foi substituído por foreach no grupo de chats (agrupamento quebra virtualização).
- **UI chat**: chip "↑ {parentTitle}" no header linkando `/c/{parentChatId}`.
- **i18n**: `sidebar.children`, `sidebar.delegated`, `sidebar.delegated_badge`, `chat.parent` — flat nos 8 locales.

## Testes
- `ChatEndpointsTests`: children vazio, 404 inexistente, 404 cross-user.
- `ToolStreamingTests.Delegate_CriaRunFilhaEmChatProprioEAggregaResultado`: estendido — `ParentChatId`/`ParentTitle` no detalhe do filho, `ParentRunId` na run, `GET children` com `LastRunStatus`, `childrenCount`/`parentChatId` nos summaries.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/293 |
