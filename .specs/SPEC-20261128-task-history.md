# SPEC-20261128-task-history — histórico de listas de tarefas por run/chat/workdir

**Status**: Approved — plano de melhorias opencode/oh-my-pi aprovado 2026-11-28

## Motivação

Hoje `todo_write` publica o snapshot da lista apenas como evento SSE
`tasks` transitório (`ChatRunExecutor` → `ChatWorkspacePanel`). Nada é
persistido: ao iniciar nova run, a lista anterior some da aba "Tarefas",
e fechar/reabrir o chat perde o plano.

opencode persiste todos por sessão (`TodoTable`, posição ordenada);
oh-my-pi idem com fases. Queremos paridade **com histórico**: a aba
"Tarefas" do workspace mostra todas as listas, agrupadas por run.

## Design

### Persistência

Nova entidade `ChatTaskList` (Domain `Entities.cs`):

- `Id` (guid), `ChatId` (FK navegação), `RunId` (string, da `ChatRun`
  que publicou), `UserId`, `WorkdirId` (string?, do request/run),
  `Seq` (long — contagem de snapshots da run), `ItemsJson`
  (`[{id,content,status}]` — snapshot completo, mesmo shape do evento),
  `CreatedAt` (epoch).
- Migration EF: `AddChatTaskLists`; índice `(ChatId, RunId, Seq)` e
  `(WorkdirId, CreatedAt)`.
- `ChatRunExecutor` (onde publica `event: tasks`): persiste o snapshot
  antes do broadcast — bookkeeping nunca derruba a run (try/catch igual
  ao bookkeeping de media/changes).

### Endpoints (`ChatRunEndpoints` ou novo grupo)

- `GET /api/v1/chats/{chatId}/task-lists` → lista de runs com sua
  última lista: `[{runId, chatTitle?, status, createdAt, updatedAt,
  items:[…]}]`, mais recente primeiro. Escopo: dono do chat.
- `GET /api/v1/workdirs/{workdirId}/task-lists` → idem, todas as runs
  de todos os chats daquele workdir (workspace panel por workdir).

Auth: `RequireAuthorization`, isolamento por `UserId` igual aos demais
endpoints de chat.

### Cliente

- `ChatStreamService`/`ChatView`: além do snapshot em memória, a aba
  Tarefas consulta o endpoint ao montar (e ao trocar de chat/workdir).
- `ChatWorkspacePanel` aba `tasks`:
  - Topo: lista da run ativa (como hoje, atualiza via SSE `tasks`).
  - Abaixo, seção "Histórico": grupos colapsáveis por run — header com
    nº da run/data/status (ex.: "Run #3 · 28/11 · 4/6 concluídas") e os
    itens (mesmo markup atual). Runs sem lista não aparecem.
- i18n: chaves `chat.panel_tasks_history`, `chat.panel_tasks_current`,
  `chat.panel_tasks_run_header` com `{{done}}`/`{{total}}` — flat,
  nos 8 locales (pt-BR, en-US, es-ES, + 5 demais).

### Continuidade entre runs (padrão opencode `Todo.get`)

- No prelude da run (`ChatRunExecutor`), injetar no contexto da tool o
  último snapshot do chat (se houver): o resultado do primeiro
  `todo_write` parte da lista anterior em vez de vazia — exposto ao
  modelo via contexto (nota "lista anterior" no system prompt contributor
  ou no primeiro tool result). Implementação: `BuiltinToolContext` ganha
  `PreviousTasksJson`; se a run nova ainda não chamou `todo_write` e o
  modelo pedir a lista, o primeiro result devolve a lista anterior com
  aviso "continuando do plano anterior".

## Testes (NUnit, `OpenWebUI.Api.Tests`)

- `todo_write` persiste snapshot com RunId/Seq correto; segundo write na
  mesma run cria novo registro (histórico, não update).
- Endpoint por chat: retorna só runs do chat e do usuário; última lista
  por run; ordem desc.
- Endpoint por workdir: agrega chats do workdir.
- Continuidade: run 2 do mesmo chat recebe lista final da run 1.

## Critérios de aceite

1. Fechar e reabrir o chat mostra a última lista da última run.
2. Aba Tarefas lista runs anteriores do mesmo workdir com contagem
   done/total e itens colapsáveis.
3. Nova run no mesmo chat continua da lista anterior.
4. `dotnet build` 0 warnings; drift guards verdes; i18n paridade.
