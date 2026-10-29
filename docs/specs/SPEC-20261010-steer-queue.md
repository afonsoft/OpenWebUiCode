# SPEC-20261010-steer-queue — Steer/queue em runs vivas

Status: Completed

## Contexto
Não há como injetar mensagem numa run em execução — o usuário espera o turno acabar. opencode SessionV2 tem inbox durável com modos steer (próximo turno) / queue (ao ficar ociosa).

## Objetivo
Endpoint que admite mensagem durável numa run viva; o tool loop a promove na fronteira de rodada (steer) ou ao ficar ocioso (queue), zerando o teto de rounds — a run continua em vez de terminar.

## Escopo
1. Entidade `ChatRunSteer` (Id, RunId, ChatId, Content, Mode steer|queue, Status pending|promoted, Timestamp) + migration `ChatRunSteer`.
2. `POST /api/v1/chats/{chatId}/runs/{runId}/steer` `{message, mode?}` — owner-only, run não-terminal, 202 Accepted.
3. `ToolLoopCallbacks.OnRoundBoundaryAsync(queuedOnly, ct)`: no topo de cada round promove `steer` pendentes; quando o modelo responde sem tool_calls, promove `queue` pendentes — injetadas no histórico, `round` reseta.
4. `ChatRunExecutor.PollSteerAsync`: marca promoted, anexa `ChatMessage` user no chat (entra no transcript e na próxima run), publica `event: steer` no SSE.
5. Falha na leitura da inbox (base sem a tabela) → run segue normal (best-effort).

## Fora de escopo
UI dedicada do steer (o evento SSE + mensagem user cobrem); client-side; múltiplos steers em lote (promove todos pendentes, um de cada vez por fronteira é equivalente).

## Acceptance Criteria
- `POST …/steer` em run viva grava linha pending (steer default; `mode=queue` respeitado) e retorna 202.
- Mensagem promovida vira `ChatMessage` user no chat e entra no histórico do próximo round; o loop continua em vez de encerrar.
- Run finalizada → 409; mensagem vazia → 400; run de outro usuário → 404.
- Tests: endpoint persiste inbox; finalizada → 409; vazia → 400; outro usuário → 404.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/302 |
