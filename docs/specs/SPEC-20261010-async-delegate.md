# SPEC-20261010-async-delegate — delegate_task assíncrono + run_result + profundidade

Status: Completed
Owner: Afonso

## Contexto
`delegate_task` bloqueava o turno do pai em poll (até 300s) e filhos nunca podiam delegar (recursão hardcoded depth 1). Para paralelismo estilo opencode-web: sub-agents em background, colheita sob demanda e cadeias de sub-delegação limitadas.

## Implementado
- **`delegate_task wait` param** (default `true`, compatível): `wait=false` enfileira a run filha e retorna imediatamente `{childChatId, childRunId, status:"queued"}` — o pai segue o turno e colhe depois. A run filha executa server-side (sobrevive fechar o browser).
- **`builtin:run_result`** (nova tool, read-only, sem aprovação): lê status/conteúdo/erro de uma run pelo `run_id`, owner-scoped (`UserId == context.UserId` → 404 lógico cross-user).
- **Profundidade configurável**: `MaxDepth = 3` derivada caminhando `ChatRun.ParentRunId` (sem coluna nova — cadeia já persistida pela SPEC-20261010-runs-hierarchy). Filho herda `delegate_task` enquanto `childDepth < MaxDepth`; no teto, a tool sai do `ToolIds` do filho.
- **`todo_write`**: ids estáveis já existiam (`id` opcional com fallback `t1,t2,…`) — nenhum delta.
- Hierarquia do filho (`ParentChatId`/`ParentRunId`) veio da SPEC-runs-hierarchy — mantida aqui também no path `wait=false`.

## Testes
- `BuiltinToolsTests`: `run_result` completed→conteúdo; cross-user→"não encontrada"; running→status; sem `run_id`→erro de parâmetro.
- Streaming E2E existente (`Delegate_CriaRunFilhaEmChatProprioEAggregaResultado`) segue cobrindo o path `wait=true` default.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/294 |
