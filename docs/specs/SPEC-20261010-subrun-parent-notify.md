# SPEC-20261010-subrun-parent-notify — Marcador de sub-session concluída no chat pai

Status: Completed

## Contexto
Com o `delegate_task` assíncrono (`wait:false`), o pai despacha sub-sessions e segue seu turno. Quando a run filha termina, o usuário recebe a notificação global `run.completed` (SignalR/WebPush), mas nada aparece **dentro do chat pai** — quem abre o transcript não vê que a sub-session terminou nem onde foi parar o resultado.

## Objetivo
Run filha (`ParentRunId != null`) ao atingir status terminal (completed/failed/stopped/interrupted) anexa uma mensagem marcador `assistant` no **chat pai**, resolvido via `ParentRunId → ChatRun.ChatId`.

## Escopo
1. `ChatRunDispatcher.NotifyRunFinishedAsync` chama `NotifyParentChatAsync` antes do loop de `IChatRunNotifier` — cobre todos os caminhos terminais (sucesso do executor e `FailRunBestEffortAsync`).
2. Marcador: mensagem `assistant` no chat pai — `> Subtarefa concluída ({status}): {título do chat filho} — run {runId}. Resultado completo via builtin:run_result.` Entra no histórico e no contexto das próximas runs do pai.
3. Best-effort: falha no marcador nunca derruba o término da run (try/catch + log warning).

## Fora de escopo
Streaming em tempo real do progresso do filho no pai; marcador interativo/link clicável; retry de marcador.

## Acceptance Criteria
- Run com `ParentRunId` terminando (qualquer status terminal) anexa marcador no chat do run pai.
- Run sem `ParentRunId` não gera marcador.
- Falha ao escrever o marcador não afeta o status da run.
- Suíte Release completa 0 falhas.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/299 |
