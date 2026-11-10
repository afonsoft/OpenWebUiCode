# SPEC-20261110-run-stream-polling-fallback

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `run-stream-polling-fallback` |
| Type | `Bugfix/Resilience` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/run-stream-polling-fallback` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do chat atrás de proxy corporativo
**I want** ver o progresso da run (deltas, tool calls, tasks, fases) mesmo quando o attach SSE morre
**So that** a UI não fica muda até a run terminar — hoje o conteúdo só aparece de uma vez no fim (ou no F5).

**Problem context:** o attach `GET /api/v1/chats/{id}/runs/{runId}/stream` recebe 504/hang atrás de proxies que derrubam ou bufferizam SSE — o cliente re-anexa em loop sem nunca ver o 1º evento. `HandleEmptyAttachAsync` já consultava `GET /runs/{id}` mas só para status + aprovação pendente — deltas e tool events nunca chegavam à tela.

## 2. Decisão de transporte

WebSocket/SignalR passa pelos mesmos proxies hostis (o próprio SignalR faz fallback WS→SSE→long-polling). O transporte que sempre atravessa é **GET/POST curto**: polling JSON do mesmo backlog seq-numerado que alimenta o SSE — sem infra nova, sem mudança de protocolo.

## 3. Scope

- `ChatRunBroadcaster.GetEventsSince(runId, since)`: snapshot do backlog + flag `Closed`.
- `GET /api/v1/chats/{id}/runs/{runId}/events?lastSeq=N` → `RunEventsResponse` `{status, closed, lastSeq, partialContent, error, events:[{seq,payload}], pendingApproval, pendingQuestion}`.
- Cliente `ChatStreamService`: `GetRunEventsAsync` (404→Gone, demais falhas→Transient) + `ParseEventBlock` (bloco SSE cru → `ChatStreamEvent` pelo parser existente).
- `ChatView`:
  - TTFB do attach: 45s → `FirstEventStallMs` (15s) — o pad SSE sai na hora em conexão saudável.
  - Empty-attach já faz um ciclo de polling (eventos + pendências + PartialContent).
  - `PollFallbackAttaches` (3) vazios seguidos → modo polling dedicado a cada `PollIntervalMs` (1,5s) até terminal; `MaxPollFailures` (20) de rede → volta aos attaches.
  - `PartialContent` cobre gap do backlog (restart/overflow) quando à frente do acumulado.
  - Chip de status marca o modo (`· chat.status_polling`).

## 4. Critérios de aceite

- Com o SSE morto pelo proxy, deltas/tool cards/tasks/fases aparecem a cada ciclo de polling.
- Aprovação/pergunta pendente aparece via polling (já coberto em SPEC-20261030).
- Run terminal (completed/failed/stopped/interrupted/closed) encerra o polling; `error` exibido.
- Run saudável via SSE não é afetada (attach → eventos → terminal, sem polling).
- 404 no /events encerra o consumo; falhas transitórias não derrubam o loop.

## 5. Fora de escopo

- SignalR/WebSocket como transporte do stream (mesma vulnerabilidade a proxy; infra já existe para notificações).
- Long-polling server-side (hold aberto) — ciclo curto de GET é mais compatível.

## Delivered

Mergeado em `main` — commit `b242be8 (merge 080a592)`. Issue de tracking: #719 (fusion chain).
