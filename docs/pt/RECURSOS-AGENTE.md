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

## 13. Combos de modelos (failover)

`GET`/`POST`/`PUT`/`DELETE /api/v1/combos[/{id}]` (+ `/{id}/status`)

- Um combo é uma **lista ordenada de modelos** resolvida por uma de três
  estratégias: `priority` (sempre o primeiro saudável), `round-robin` ou
  `priority-round-robin`. Quando o modelo atual falha, o combo tenta os
  próximos; um modelo que falha entra em **cooldown** por modelo (máx. 5min).
- Limites de taxa compartilhados por combo: `requestsPerMinute` e
  `tokensPerMinute` numa janela deslizante de 60s.
- Escopo por usuário por padrão; admins podem criar **combos globais** (sem
  dono — visíveis a todos). Gerenciado em **Configurações → Combos**.

![Configurações → Combos](../screenshots/settings-combos.png)

## 14. Personas de agente

`GET`/`POST`/`PUT`/`DELETE /api/v1/agent-personas[/{id}]` (+ `/{id}/message`,
`/{id}/audit`)

- Uma persona é um perfil de agente nomeado: `systemPrompt`, `modelId`,
  `guardrailLevel` (`basic`/`strict`) e uma allowlist de tools opcional.
- Personas são acionáveis via dispatch de canais/hooks e por
  `delegate_task(persona: "…")`; cada dispatch fica registrado no log de
  auditoria da persona (`GET /{id}/audit`).
- Gerenciadas em **Configurações → Agentes**, junto ao catálogo de agentes e
  ao registro de agentes externos.

![Configurações → Agentes](../screenshots/settings-agents.png)

## 15. Agentes externos e o escopo global `~/.agents`

- **Registro de agentes externos** — `GET /api/v1/external-agents/detect`
  varre o host procurando CLIs de agente (Claude Code, Codex CLI, OpenCode,
  Devin CLI, Gemini CLI). CLIs detectados podem ser habilitados por usuário e
  integrados a runs delegadas.
- **Catálogo de agentes** — `builtin:agent_catalog` lista os agentes do
  workspace descobertos em arquivos `*.agent.md` e roles builtin
  (`code-reviewer`, `researcher`).
- **Escopo global** — skills, slash commands e `mcp.json` são descobertos em
  `~/.agents` (mais pastas `.claude`/`.agents` do projeto e arquivos `*.md`
  de agente) e mesclados ao catálogo do repo/workspace.

## 16. Forms multi-pergunta do `ask_user`

- Uma chamada `ask_user` pode carregar **várias perguntas**, cada uma com
  `options`, seleção `multiple` e opção de `reject` — renderizadas num form
  único.
- Presets de autonomia respondem pergunta a pergunta (opção recomendada,
  senão a primeira); cada resposta automática continua emitindo
  `decision_auto`.

## 17. Durabilidade, resiliência e throughput de runs

- **Eventos duráveis de run** — eventos persistidos; **replay de checkpoint**
  reconstrói o estado do stream e **heartbeat ownership** permite que uma API
  reiniciada adote runs órfãs em vez de abandoná-las (`run-recovery`).
- **Resiliência de provider** — retry com backoff em falhas transientes,
  continuação automática em `finish_reason=length` e recuperação de
  context-overflow (encolher e retentar) dentro do pipeline da run.
- **Tool calls em paralelo** — tools marcadas `SafeInParallel` executam em
  concorrência numa mesma rodada do provider; tools inseguras seguem seriais.
- **Passos por agente + uso/custo** — runs expõem feed de passos por agente,
  wrap-up do último passo e metadata de uso/custo (`run.usage`/`run.cost`).
- **Saída de tool limitada** — resultados grandes são clipados e transbordam
  para um arquivo gerenciado, recuperável via `builtin:big_output`, sem
  inundar a janela de contexto.
- **Failover de busca web** — o modo `auto` alterna entre providers
  configurados com cooldown por provider.
- **Expansão de commands** — slash commands do repo/workspace são expandidos
  server-side com overrides por run.

## 18. Workspace e refinamentos de UX

- **IDE estilo VS Code** (`/ide`) — views Explorer/Busca/SCM/Outline/Zen sobre
  a worktree vinculada ao chat, abas de editor e painéis laterais
  Chat/PRs/Testes.

![Workbench da IDE](../screenshots/ide-workbench.png)

- **Aba Mudanças do workspace** — mostra o **diff da run**, incluindo
  alterações commitadas pelo agente na branch da worktree (não só a árvore de
  trabalho).
- **View de fonte markdown** — mensagens do assistente alternam entre
  markdown renderizado e fonte crua, com copiar.
- **Secrets de agente no env** — secrets chave→valor configurados em nível
  global de admin e por usuário injetados nos jobs `shell_exec` e no terminal
  do workspace; o token do GitHub da integração entra por padrão.
- **Loop visual do browser** — `browser_navigate`/`browser_observe`/
  `browser_act`/`browser_screenshot` (Playwright) com OCR local para loops de
  raciocínio visual.
- **Redesign das Configurações** — o modal usa seções categorizadas (Combos,
  Agentes, Manutenção, …) com barra de salvar unificada.

![Modal de Configurações](../screenshots/settings-modal.png)

## 19. Manutenção (retenção de chats)

- **Configurações → Manutenção** — retenção de chats/logs por usuário (dias),
  tamanho do banco, última/próxima execução agendada, além de prévia **dry
  run** e **executar agora**.
- A passada agendada remove chats/memórias expirados (destilando fatos
  duráveis antes) e faz vacuum do banco — em cima do sweep global do
  `RetentionService` da seção 9.

![Configurações → Manutenção](../screenshots/settings-maintenance.png)

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
| Automações | `GET`/`POST`/`PUT`/`DELETE /api/v1/automations[/{id}]` (+ `/run`, `/run-now`) |
| Combos | `GET`/`POST`/`PUT`/`DELETE /api/v1/combos[/{id}]` (+ `/{id}/status`) |
| Personas | `GET`/`POST`/`PUT`/`DELETE /api/v1/agent-personas[/{id}]` (+ `/{id}/message`, `/{id}/audit`) |
| Agentes externos | `GET /api/v1/external-agents/detect` |
| Memórias | via tools `builtin:memory_*` |
