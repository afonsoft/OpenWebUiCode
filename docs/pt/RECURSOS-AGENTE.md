# Recursos de agente — guia detalhado

Tudo abaixo é **camada aditiva** sobre a paridade com o upstream: nenhuma rota
nem o core de gateway foi reestruturado. Cada item lista a superfície
(tool/endpoint/UI), o comportamento e onde aparece na tela.

## 1. `delegate_task` — sub-agentes e sessões paralelas

Cria um **chat** filho mais uma **run** server-side.

```jsonc
// argumentos da tool
{
  "prompt": "Escreva hello-api.txt contendo exatamente: api",
  "wait": false,              // false → retorna { childRunId, childChatId } na hora
  "repo": "owner/outro-repo", // opcional — vincula o filho a outro repo
  "persona": "reviewer",      // opcional — Skill do workspace como system prompt
  "auto_merge": false         // opcional — merge da worktree ao terminar
}
```

- `wait:false` → assíncrono: o resultado é colhido depois via `builtin:run_result`.
- Profundidade limitada a **3** (`MaxDepth`), medida pela cadeia `ParentRunId`.
- O filho herda repo/branch do pai mas ganha a **própria worktree de chat**
  (`workspaces/{user}/worktrees/{owner}__{repo}/{childChatId}`, materializada
  sob demanda) — workers paralelos nunca escrevem no mesmo checkout.
- O filho herda o **binding de repo por chat** do pai, a menos que `repo` sobrescreva.

![delegate_task criando duas filhas em paralelo](../screenshots/delegate-parallel.png)

## 2. Hierarquia de sessões/runs

- Schema: `Chat.ParentChatId`, `ChatRun.ParentRunId`.
- API: `GET /api/v1/chats/{id}/children` → chats filhos + `lastRunStatus`.
- Sidebar: expansor "Subtarefas" aninha os filhos sob o pai com badge `delegado`;
  o header do filho linka de volta pro **Chat pai**.

![árvore de sessões expandida](../screenshots/chat-hierarchy.png)

Quando uma run filha chega a status terminal, o dispatcher anexa um marcador no
chat **pai** — `Subtarefa concluída (completed): … — run {id}. Resultado
completo via builtin:run_result.` — e a próxima rodada do pai já vê o desfecho
em contexto.

![marcadores de conclusão no pai](../screenshots/parent-notify.png)

## 3. Revisão antes do merge (pipeline de worktree)

Como cada sub-run vive na sua worktree, a revisão é explícita:

- `builtin:worktree_diff` — diff da worktree do filho contra a branch base.
- `builtin:worktree_merge` — merge da branch da worktree de volta (e limpa a
  worktree depois).
- `delegate_task` com `auto_merge:true` pula a revisão e mergeia no sucesso.

Worktrees órfãs são podadas no boot e diariamente (retenção, abaixo).

## 4. Steer e queue no meio da run

`POST /api/v1/chats/{chatId}/runs/{runId}/steer`

```jsonc
{ "message": "atualiza o README também", "mode": "steer" } // ou "queue"
```

- `steer` — injetado na próxima fronteira de turno da run viva.
- `queue` — só promovido quando a run ficaria ociosa.
- Linhas duráveis (`ChatRunSteers`); steers de runs terminais são expurgados
  após 30 dias pela retenção.

## 5. Console de runs paralelas

`GET /api/v1/chats/runs` devolve as 50 runs mais recentes entre chats
(`ParallelRunResponse` com `ParentRunId`/`ParentChatId`). A sidebar mostra a
seção **"Runs ativas"** para itens queued/running/paused — um clique abre o
chat dono.

![seção de runs ativas](../screenshots/runs-parallel.png)

## 6. Memória do agente (durável + auto-sync)

- Tools: `builtin:memory_save` (título, conteúdo, escopo `project|global`) e
  `builtin:memory_search` (consulta → memórias ranqueadas).
- Injeção: o pipeline prefixa um bloco `<agent_memory>` com as memórias
  relevantes no system prompt.
- **Auto-sync** (`MemorySyncService`, tick de 6h): por usuário, os chats
  atualizados desde o watermark kv `u:{uid}:memsync.last` são destilados pelo
  modelo configurado em memórias `auto:*` (mantém as 25 mais novas). Opt-out
  com `u:{uid}:memsync.enabled=false`; o watermark só avança quando o provider
  responde — falha = retry no próximo tick.

## 7. Rotinas e lembretes

Sobre o engine de Automações (entidade + scheduler de 15s + CRUD REST em
`/api/v1/automations`, página **Automações** na sidebar):

- `builtin:routine` — `list|create|update|delete|enable|disable|run_now` com
  `schedule` = `once | interval | daily | weekly` (`once` aceita `in_minutes`
  ou `run_at` epoch).
- `builtin:reminder` — mensagem única → automação `once`; ao disparar também
  envia notificação `automation.reminder` pro feed do usuário.

![página de automações](../screenshots/automations.png)

## 8. Compactação de contexto

Chats longos são compactados dentro do pipeline: os turnos antigos são
resumidos pelo modelo configurado e um cutoff por chat (`chat:{id}:compact.*`)
faz a próxima run reprocessar `[resumo] + cauda` em vez do histórico inteiro.

## 9. Retenção de dados

`RetentionService` (tick de 24h) mantém o SQLite enxuto:

- `AutomationRuns` com mais de **90 dias**, `Notifications` com mais de
  **60 dias**, `ChatRunSteers` com mais de **30 dias** **e** cuja run terminou.
- Prune de worktrees órfãs (mesmas regras do boot), WAL
  `checkpoint(TRUNCATE)` e `VACUUM` semanal guardado por kv `sys:vacuum.last`.
- Nunca toca em chats, mensagens, arquivos ou memórias.

## 10. Worktrees de repo por chat

- kv `chat:{id}:workspace.repo` resolve **estrito por chat** — `chat` ou
  `none`. Conversa nova nunca herda o binding global do usuário
  (`Source` informado na resposta); sem `chatId`, a resolução cai no
  binding global (`user` | `none`).
- Vincular um chat a `owner/repo` cria uma **worktree git exclusiva** em
  `workspaces/{user}/worktrees/{owner}__{repo}/{chatId}` na branch
  `chat/{chatId}`, a partir do checkout base compartilhado em
  `repos/{owner}__{repo}`.
- `GET`/`PUT /api/v1/chats/{id}/workspace-repo` (owner-only). `PUT` com
  repo vazio/nulo **desvincula**: a worktree é removida, o chat sai de
  `build` (→ `chat`) e o checkout base sai de disco quando nenhuma
  conversa nem o binding global referenciam o repo.
- Arquivar preserva o kv do binding e recria a worktree sob demanda no
  primeiro uso após desarquivar; deletar libera tudo.
- Se o checkout base divergir do `origin` (commits locais velhos), o
  próximo bind se recupera sozinho: o HEAD divergente vira
  `backup/{branch}-{ts}` e o base é resetado para o ref buscado — tree
  suja continua reportando o erro honesto do pull.
- Todas as superfícies de workspace — tools da run, `/ide`, árvore de
  arquivos, barra git, test-run, skills/commands — aceitam `?chatId=`.
- `delegate_task` herda repo/branch do pai numa worktree própria do filho
  (ou `repo` sobrescreve).

![seletor de repositório](../screenshots/repo-binding-picker.png)

## 11. Três modos de agente (chat / plan / build)

- O composer expõe um seletor de modo persistido por chat (`PATCH
  /api/v1/chats/{id}` com `mode`).
- **`chat`** (padrão) — conversa sem repo: tools de workspace
  (`file_*`, `shell_exec`, `delegate_task`, `lsp_*`, `skill`, …) são
  negadas e nem anunciadas ao modelo.
- **`plan`** — planejamento somente-leitura: só rodam os builtins
  read/list/grep/glob, web/fetch, `ask_user`, `todo_write` e `plan_exit`
  (tools externas HTTP/MCP negadas); `plan_exit` só é anunciado nesse modo.
- **`build`** — acesso completo ao workspace mediado pelo preset de
  aprovação, **exige repo vinculado**: `PATCH` devolve `400` sem binding;
  `plan_exit` só promove a `build` quando há binding; desvincular derruba
  um `build` persistido de volta a `chat`.
- Seleção pendente de repo: escolher um repo numa conversa nova fica
  pendente até o primeiro envio e materializa como **binding de chat** —
  nunca escreve o binding global do usuário.
- `TestCommand`/`FormatCommand` customizados são herdados do binding de
  chat anterior (ou do global) no primeiro bind da conversa.

## 12. Escolha autônoma de decisão

- Presets de aprovação `smart` / `auto` / `always-allow` respondem sozinhos
  a prompts `ask_user` que trazem opções — a opção recomendada (marcada),
  senão a primeira — sem pausar a run.
- Toda escolha automática emite evento SSE auditável `decision_auto`
  renderizado no feed de atividade da run.

## Referência rápida de API

| Superfície | Rota |
|---|---|
| Enfileirar run | `POST /api/v1/chats/{id}/messages` |
| Steer/queue | `POST /api/v1/chats/{id}/runs/{runId}/steer` |
| Resume do stream | `GET /api/v1/chats/{id}/runs/{runId}/stream?lastSeq=` |
| Diff da run | `GET /api/v1/chats/{id}/runs/{runId}/diff` |
| Filhos | `GET /api/v1/chats/{id}/children` |
| Runs ativas | `GET /api/v1/chats/runs` |
| Binding de repo | `GET`/`PUT /api/v1/chats/{id}/workspace-repo` |
| Automações | `GET`/`POST`/`PUT`/`DELETE /api/v1/automations[/{id}]` (+ `/run`) |
| Memórias | via tools `builtin:memory_*` |
