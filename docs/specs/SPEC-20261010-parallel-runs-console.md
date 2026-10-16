# SPEC-20261010-parallel-runs-console — Console consolidado de runs paralelas

Status: Completed

## Contexto

Com `delegate_task wait=false` (SPEC-20261010-async-delegate) o usuário pode ter várias runs — próprias e de sub-agents — executando em paralelo em chats diferentes. Hoje o status só é consultável via `run_result` dentro de um chat ou na árvore da sidebar (sem status de run). Falta a vista consolidada estilo "sessions" do opencode-web.

## Objetivo

Uma seção "Runs ativas" na sidebar listando as runs em execução (queued/running/paused) de todos os chats do usuário — status via indicador colorido, título do chat, marcador ↳ para runs filhas — com link direto pro chat.

## Escopo

- `GET /api/v1/chats/runs` — últimas 50 runs do usuário com `ChatTitle`, `ParentRunId`, `ParentChatId` (`ParallelRunResponse`). Isolado por usuário.
- Client: `ApiService.GetRunsConsoleAsync`; Sidebar carrega junto do batch de chats; seção só aparece quando há runs ativas; dot por status (running=pulse azul, queued=cinza, paused=âmbar).
- Fora de escopo: página dedicada de runs, cancel/ações inline, SSE de atualização (recarrega junto com a lista de chats).

## Acceptance Criteria

- Endpoint retorna runs de todos os chats com título + hierarquia, isolado por usuário.
- Sidebar mostra seção apenas com runs ativas; clicar navega pro chat.
- Teste de endpoint cobrindo multi-chat + isolamento; suíte completa 0 falhas; drift guards verdes.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/298 |
