# SPEC-20261010-memory-autosync-retention — Memory auto-sync + retenção

Status: Completed

## Contexto

A memória do agente (SPEC-20261010-agent-memory) só registra o que o modelo
decide salvar na hora — nada garante que aprendizados de sessões antigas
sobrevivam. O opencode faz auto-sync periódico: resume sessões antigas via
LLM e grava fatos duráveis na memória. Em paralelo, run/eventos, automations
e worktrees em disco crescem sem limite — o opencode faz expurgo + VACUUM
automático; nós não temos nada.

## Objetivo

1. **Auto-sync de memória**: serviço em background que, a cada 6h por
   usuário, destila os chats atualizados desde o último sync em fatos
   duráveis e grava como `AgentMemory` (scope `global`, título
   `auto:{título do chat}`), reaproveitando o mesmo upsert por título.
2. **Retenção**: serviço em background diário que expurga dados transientes
   antigos e worktrees órfãs, e faz VACUUM/checkpoint semanal no SQLite.

## Escopo

1. `MemorySyncService` (BackgroundService, tick 6h):
   - Watermark por usuário: kv `u:{uid}:memsync.last` (ConfigService).
   - Opt-out: kv `u:{uid}:memsync.enabled` = "false" desliga (default ligado).
   - Por usuário: chats com `UpdatedAt > watermark`, até 3 por tick;
     últimas ≤20 mensagens (truncadas ~500 chars cada, ≤6000 total) →
     `providers.CompleteAsync` com o 1º modelo do chat; resposta (fatos, 1
     por linha) → upsert `AgentMemory` `auto:{chatTitle[:40]}`.
   - Falha de provider: não avança o watermark (retenta no próximo tick).
   - Poda: mantém só as 25 memórias `auto:*` mais recentes por usuário.
2. `RetentionService` (BackgroundService, tick 24h, atraso inicial 5min):
   - `AutomationRuns` com `FinishedAt` > 90 dias → delete.
   - `Notifications` com `CreatedAt` > 60 dias → delete.
   - `ChatRunSteers` de runs em status terminal com `Timestamp` > 30 dias →
     delete.
   - Worktrees: `{DATA_ROOT}/worktrees/{uid}/{runId}` cuja run terminou há
     >7 dias → `WorktreeService.RemoveAsync` (ou `TryDelete` quando a run
     não resolve mais o workdir principal).
   - `PRAGMA wal_checkpoint(TRUNCATE)` a cada tick; `VACUUM` quando
     `sys:vacuum.last` > 7 dias (try/catch — nunca derruba o serviço).
3. Ambos os serviços expõem método público `RunOnceAsync(ct)` invocável por
   testes (o loop do BackgroundService só o agenda).

## Fora de escopo

Retenção de chats/mensagens do usuário (dados primários — nunca auto-deleta);
UI de configuração (as chaves kv já podem ser gerenciadas pelos endpoints de
config existentes).

## Acceptance Criteria

- Sync grava `AgentMemory` `auto:*` por chat com conteúdo destilado pelo
  provider; segunda execução no mesmo watermark não reprocessa.
- Opt-out por kv desliga o sync do usuário.
- Poda mantém ≤25 memórias `auto:*`.
- Retenção remove AutomationRun/Notification/steer velhos e worktree de run
  terminada >7d; VACUUM só roda após 7 dias do último.
- Testes: sync com provider mock (grava memória), watermark, opt-out, poda;
  retenção por idade em cada entidade; worktree removida.
- Suíte Release completa 0 falhas.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/307 |
