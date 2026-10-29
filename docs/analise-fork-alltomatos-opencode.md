# Análise: alltomatos/opencode → melhorias aplicáveis ao nosso fork .NET

Data: 2026-10-10 · Fonte: https://github.com/alltomatos/opencode @ v1.21.78 (branch dev, 136 releases no changelog)
Foco: melhorias de **agente/core** para o afonsoft/OpenWebUiCode (.NET 10 + Blazor WASM)
Regra aplicada: paridade upstream — melhorias entram como **camada adicional** (tools/endpoints/serviços novos), nunca reestruturando gateway de providers nem superfície de rotas.

---

## 1. Inventário do fork (o que eles construíram por cima do opencode)

### Orquestração multi-agente — "Batuta" (epic E04, a peça central)
- Fluxo **Arquiteto → Orquestrador → Workers**:
  - *Arquiteto*: sessão que estuda o repo, escreve PRD + issues e define o pipeline em `docs/batuta-pipeline.md` (fases → skills). Arquivo compartilhado, editável pelo usuário, re-lido pelo orquestrador antes de cada despacho.
  - *Orquestrador*: sessão que recebe `handoff.md` (docs + lista de issues), delega cada issue a um worker e **revisa o diff antes de mergear** — nunca merge cego.
  - *Workers*: sub-agents internos (via `task` tool) ou **CLIs externos** (claude/codex) em PTY real.
- **Worktree por worker — obrigatório**: cada worker roda isolado num git worktree próprio (`Worktree.create`); nunca escreve no checkout principal. Elimina colisão entre workers paralelos.
- Detecção de fim de turno de CLI externo por **silêncio no PTY** (~1.5s idle, timeout 5min) — sem protocolo.
- Status vivo: `.batuta/{activityId}/pipeline.md` — checklist markdown atualizado a cada delegação/revisão/merge.
- Gate de worker externo: só aparece no combobox se a skill `batuta-cli` estiver instalada no CLI (detecção de CLIs via `fs.stat` no PATH, sem spawn).
- Modo orquestrador externo: um CLI de fora assume o papel via `POST /batuta/{id}/delegate` (HTTP síncrono).
- Skills fixas do orquestrador: grill-me, to-prd, to-issues, handoff, developer, tdd, qa-analyst etc.

### Agentes customizados + skills (AgentUI, E12)
- Usuário cria agentes conversacionais próprios (modelo/prompt/ferramentas) — incl. agentes "globais" usáveis em qualquer projeto.
- Skills embarcadas extraídas/instaladas automaticamente em máquina nova; skills acionadas aparecem no chat.
- Subagent especializado invocável por slug (`subagent_type`) — o orquestrador delega "por persona".

### Memória do agente (Breniac, E11)
- Tools `memory_save` (escopo `project`|`global`) e `memory_search`.
- Memória markdown persistente por projeto + global, **ligada por default**.
- **Auto-sync periódico** (~6h, configurável): job que resume sessões antigas para a memória via LLM antes do expurgo; retenção configurável + VACUUM automático do SQLite.

### Rotinas/lembretes (E16)
- Tool `routine` (list/get/…) para o agente gerenciar schedules; ações: `shell`, `mcp_tool`, `skill`, `agentui`.
- Engine cross-platform (cron/interval), sobrevive ao app fechado, timeout configurável, preservação de sessão.
- Tool `reminder` separada (lembretes por canal) + UI de agenda (grid/lista, cores por canal).

### Session engine (SessionV2)
- `session_input` **durável** (inbox persistida) — um prompt só vira mensagem de usuário quando o runner promove no boundary seguro.
- Modos de entrega: **steer** (injeta no próximo turno do provider; reseta allowance de turnos) vs **queue** (espera a run ficar idle).
- `SessionRunCoordinator`: junta resumes da mesma sessão, coalesce de wakes, sessões diferentes rodam em paralelo.
- Replay/recovery de eventos pós-crash separado de ownership de execução.

### Providers / resiliência
- **Combos de modelos**: prioridade + round-robin com **health monitor** ��� conta vai pra cooldown automático em 429/quota e rotação segue (`IntegrationRotation.markUnavailable`, benching).
- Providers nativos: OmniRoute (MCP auto-configurado ao conectar, edição sem desconectar), AgentRouter, Google Antigravity (IDE/CLI com OAuth multi-conta), Kiro, Kilo.
- Detecção Pro-vs-Free real via `loadCodeAssist`.

### Ferramentas do agente
- `computer` (computer-use), `browser`, `external-directory`, `question`, `plan` (enter/exit), `lsp`, `skill`, `task`, `todo`, `reminder`, `routine`, `memory_save/search`, `apply_patch`.
- System prompt global personalizável; modo Planejar troca agente de verdade (tools de escrita bloqueadas).

### Plataforma/canais
- Bots **WhatsApp** (webhook Tailscale interno ou Funnel público, filtros de remetente, caption em docs, Office) e **Telegram** — sessões de canal centralizadas no projeto dedicado `~/.opencode/projects/agentui` (não poluem repos).
- Pedidos de canal rodam em background (nunca travam o bot).
- Servidor remoto via túnel SSH/Tailscale, fila de tarefas persistente em VPS.
- Dashboard `/stats`: KPIs, tokens/s, consumo por sessão.
- Painel de manutenção: expurgo de sessões antigas, manutenção profunda com reinício, diagnóstico de saúde.
- MCP: OAuth, editar/remover, "Config raw", auto-reconnect (fixes p/ OmniRoute MCP).
- "Tarefas em segundo plano" + distinção UI "pensando" vs "executando".
- Sessões home: grade/lista com métricas e live working status.

---

## 2. Mapa de gaps nosso × deles (core do agente)

| Área | Deles | Nosso (.NET) | Gap |
|---|---|---|---|
| Sub-agents | `task` + `subagent_type` por skill/persona | `delegate_task` async, `run_result`, depth 3, repo target | 🟡 sub-agent não tem **persona/config própria** (modelo, prompt, toolset) — herda tudo do pai |
| Isolamento de worker | **worktree git por worker (obrigatório)** | checkout compartilhado por slug (`EnsureCheckoutAsync`) | 🔴 workers paralelos no mesmo repo colidem no mesmo dir |
| Merge/revisão | orquestrador revisa diff antes de merge | — | 🔴 sem flow review-before-merge |
| Pipeline/artefatos | `pipeline.md` + `handoff.md` editáveis | — | 🔴 sem convenção de artefatos de orquestração |
| Entrada mid-run | **steer/queue** em inbox durável | run desacoplada + resume SSE | 🔴 não dá pra injetar mensagem numa run viva |
| Contexto longo | compactação de sessão (upstream opencode tem) | histórico inteiro todo turno | 🔴 **runs longas estouram contexto** — gap de core |
| Memória | `memory_save/search` + auto-sync | — | 🔴 agente não lembra nada entre sessões |
| Agendamento | `routine`/`reminder` tools + engine | `job_list/output/kill` (só monitora) | 🟡 agente não cria agenda/lembrete |
| Failover | combos + cooldown 429 client-side | OmniRoute faz upstream | 🟢 OmniRoute já cobre (não precisa) |
| Workers externos | CLI em PTY + idle detection | `shell_exec` full-perms pode spawnar | 🟡 possível hoje mas sem gerência/idle-detect |
| Modos | Planejar/Build/bypass | ✅ plan/build + `plan_exit` + ApprovalPreset | 🟢 temos |
| Permissões | `ctx.ask` patterns/always | ApprovalPreset + gate | 🟢 temos |
| Checkpoint/undo | snapshot/revert | ✅ `CheckpointService` | 🟢 temos |
| Canais | whatsapp/telegram → projeto dedicado | — | ⚪ fora do foco coding-agent |
| Manutenção | expurgo + VACUUM + painel | — | 🟡 DB cresce sem política de retenção |
| Stats | dashboard tokens/KPI | — | 🟡 sem métricas de uso |
| MCP | OAuth + reconnect + raw config | `McpClientService` básico | 🟡 verificar reconnect |
| Runs paralelas | home grid + live status | ✅ console de runs (#298) | 🟢 temos |
| Notificação | bell/webpush + marker pai | ✅ `run.completed` + marker no pai | 🟢 temos |
| Tasks/todo | `todo` tool | ✅ `todo_write` com ids | 🟢 temos |
| Computer-use | `computer` tool | `browser_screenshot` | ⚪ gap opcional |
| System prompt | global customiz��vel | `DefaultSystemPrompt` | 🟡 custom por usuário? |

Legenda: 🔴 gap core pra trabalhar como agent · 🟡 melhoria relevante · 🟢 já coberto · ⚪ fora de escopo

---

## 3. SPECs sugeridas (prioridade — todas camada adicional, paridade gateway/rotas intocada)

### P0 — habilitadores de agente sério
1. **Context compaction** — pipeline de resumo quando histórico > threshold: mensagens antigas → resumo LLM persistido + replay só do resumo + tail recente. *Sem isso, todo o resto morre em run longa.* (`ChatPipeline` ganha etapa antes de montar `request.Messages`; mensagens compactadas ficam no DB, não deletadas.)
2. **Worktree por sub-agent** — `delegate_task` cria `git worktree add` isolado pro filho (dir `workspaces/{uid}/worktrees/{chatId}`); sub-agents no mesmo repo não colidem. Worktree descartada ou promovida no fim.
3. **Steer/queue** — `POST /chats/{id}/steer`: mensagem entra na inbox durável da run viva e é injetada no próximo boundary de turno do executor (vs. enqueue que espera idle). Modelo opencode-session-input.

### P1 — orquestração tipo Batuta
4. **Persona no sub-agent** — `delegate_task` aceita `agent`/`system_prompt`/`model`/`tools` override: sub-agent especializado (revisar, testar, documentar) em vez de clone do pai.
5. **Review-before-merge** — convenção `handoff.md`/`pipeline.md` no workspace + flow: filho escreve na worktree → `run_result` devolve diff → pai decide merge via `builtin:worktree_merge` (ou tool `git_worktree_*`).
6. **Workers externos gerenciados** — `delegate_task kind:"cli"` spawna `claude`/`codex` em PTY no worktree, idle-detection por silêncio, output vira resultado. (Nosso `shell_exec` já faz 80% — falta a sessão PTY persistente + wait-idle.)

### P2 — memória e agenda
7. **Memória do agente** — `builtin:memory_save` (`project`|`global`) + `memory_search`; markdown em `workspaces/{uid}/memory/` + global; injeção das top-N no system prompt da run.
8. **`builtin:routine`/`reminder`** — agente cria/edita schedules reutilizando a infra de Automations já existente.
9. **Auto-sync de memória + retenção** — job periódico resume sessões velhas pra memória + expurgo/VACUUM configurável (manutenção).

### P3 — observabilidade/robustez
10. **Skills acionadas visíveis no chat** — quando `skill` tool carrega uma skill, marker na mensagem (deles mostram isso).
11. **Stats de uso** — tokens por run/chat na página de runs console (já temos `ParallelRunResponse` — adicionar usage).
12. **MCP reconnect + OAuth** — hardening do `McpClientService`.

Fora do escopo agent/coding (anotado, não proposto): canais whatsapp/telegram, computer-use desktop, túnel SSH/VPS, Python vendored, dashboard de KPIs completo.
