# Open-WebUI+Code — .NET (Edição Agente de Código)

Migração do [Open WebUI](https://github.com/open-webui/open-webui) para
**.NET 10 · C# 14 · Blazor WebAssembly** (SvelteKit + FastAPI → ASP.NET Core + Blazor),
fiel ao layout e à configuração do original — **estendida numa plataforma completa de
agente de codificação** no espírito do [opencode](https://github.com/sst/opencode):
conversas vinculadas a repositório rodando em worktrees git dedicadas, sub-agentes
em paralelo, steer no meio da run, memória durável, rotinas agendadas, IDE no
estilo VS Code e pipeline de revisão em worktree.

[![CI Build & Test](https://github.com/afonsoft/open-webui/actions/workflows/ci-build-test.yml/badge.svg)](https://github.com/afonsoft/open-webui/actions/workflows/ci-build-test.yml)
[![Code Quality](https://github.com/afonsoft/open-webui/actions/workflows/code-quality.yml/badge.svg)](https://github.com/afonsoft/open-webui/actions/workflows/code-quality.yml)
[![Security Scan](https://github.com/afonsoft/open-webui/actions/workflows/security-scan.yml/badge.svg)](https://github.com/afonsoft/open-webui/actions/workflows/security-scan.yml)
[![Accessibility Audit](https://github.com/afonsoft/open-webui/actions/workflows/a11y-audit.yml/badge.svg)](https://github.com/afonsoft/open-webui/actions/workflows/a11y-audit.yml)
[![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=afonsoft_open-webui&metric=alert_status)](https://sonarcloud.io/project/overview?id=afonsoft_open-webui)

> **English version:** [README.md](README.md)

## Open WebUI (original) vs opencode vs oh-my-pi vs este fork

O roadmap de agente foi benchmarked contra o [opencode](https://github.com/sst/opencode)
(incl. o fork [alltomatos/opencode](https://github.com/alltomatos/opencode) —
veja a [análise completa](docs/analise-fork-alltomatos-opencode.md)) e o
[oh-my-pi](https://github.com/can1357/oh-my-pi), cujo desenho de árvore de
sessões, advisor e ferramentas de contexto guiou várias features abaixo.

| Capacidade | Open WebUI (FastAPI/Svelte) | opencode | oh-my-pi | **Este fork (.NET/Blazor)** |
|---|---|---|---|---|
| Chat + streaming, multi-provider | ✅ Ollama/OpenAI/RAG/pipelines | ✅ qualquer LLM via gateway | ✅ multi-provider (TUI) | ✅ paridade + OmniRoute-friendly |
| Repo/workspace por sessão | ❌ workspace único | ✅ por projeto | ✅ por projeto | ✅ **worktree git por chat** — escopo estrito, global nunca vaza |
| Sub-agentes / sessões paralelas | ❌ | ✅ task tool + sessions | ✅ task agents `.md` declarativos | ✅ `delegate_task` async (procedural **e** definições `*.agent.md`), depth ≤3, isolado em worktree |
| Revisão antes do merge | ❌ | ✅ orquestrador revisa diffs | ❌ | ✅ `worktree_diff`/`worktree_merge` + auto-merge opt-in |
| Steer/queue no meio da run | ❌ | ✅ steer/queue durável | ❌ | ✅ `POST …/runs/{id}/steer` (modos steer + queue) |
| Memória durável do agente | ❌ | ✅ tools de memória + auto-sync | ✅ tools de memória | ✅ `memory_save`/`memory_search` + auto-destilação 6h em memórias `auto:*` |
| Rotinas / lembretes | ❌ | ✅ routine tool + scheduler | ❌ | ✅ tools `routine`/`reminder` + agenda `once` |
| Tool de lista de tarefas | ❌ | ✅ todo write | ✅ | ✅ `todo_write` com ids estáveis |
| Compactação de contexto | ❌ | ✅ auto-resumo | ✅ auto + sob demanda, com poda | ✅ resumo via LLM + poda mecânica + digest estruturado + recuperação `history://` |
| Árvore pai/filho de runs | ❌ | ✅ árvore de sessions | ✅ árvore de sessions | ✅ árvore na sidebar + badge "delegado" + marcadores no pai |
| Console de runs paralelas | ❌ | ✅ | ✅ | ✅ seção "Runs ativas" na sidebar (`GET /chats/runs`) |
| Run sobrevive fechar o browser | parcial | ✅ | ✅ | ✅ runs server-side + resume por `Last-Event-ID` |
| **Ramificação de sessão** (fork de qualquer turno) | ❌ | ❌ | ✅ entries com `parentId` + `/fork` | ✅ árvore `ParentId`/`LeafMessageId` + "ramificar daqui" + resumo do ramo |
| **Papéis de modelo + fallback** | ❌ modelo único | parcial (modelo pequeno p/ títulos) | ✅ `modelRoles` + `retry.fallbackChains` | ✅ papéis `tasks`/`memory`/`advisor` + mapa de capacidades (visão) + fallback por papel |
| **Tiers de aprovação de tools** | aprovar-tudo binário | ✅ modelo de permissão | ✅ tier + política por tool + override do usuário | ✅ tiers `Read`/`Write`/`Exec`, default fail-closed, overrides por tool |
| **Advisor / watchdog** | ❌ | ❌ | ✅ modelo de segunda opinião read-only | ✅ papel advisor inspeciona runs + injeta orientação, sem poder de aprovação |
| **Contexto dirigido pelo agente** | ❌ | parcial | ✅ `checkpoint`/`rewind`, `context_notes`, `new_context` | ✅ mesmo trio: checkpoint/rewind, notebook por ramo, rollover sob demanda |
| **Config em camadas + proveniência** | env + UI admin | parcial | ✅ env>runtime>overlay>project>global | ✅ camadas global/admin/user com rastreio de origem |
| **Agentes declarativos** | ❌ | ✅ AgentUI | ✅ `.md` + frontmatter | ✅ `*.agent.md` (tools, model, spawns) descobertos no repo, via `delegate_task` |
| **Modo plan → saída estruturada** | ❌ | ✅ modos plan/build | ❌ | ✅ modo `plan` + slash `/plan`: análise do requisito → markdown **SPEC SDD** (metadata, user story, RF-###, critérios BDD, task plan); `plan_exit` valida e persiste em `.openwebui/plans/` |
| **Anexos de mídia no chat** | ✅ uploads | parcial | ❌ | ✅ thumbnails de imagem/vídeo no composer, render inline + clique para ampliar, análise por modelo de visão |

## Screenshots

| Sub-agentes delegados em paralelo | Árvore de sessões + marcadores |
|---|---|
| ![delegate_task criando duas runs filhas](docs/screenshots/delegate-parallel.png) | ![marcadores no pai + árvore](docs/screenshots/parent-notify.png) |

| Console de runs ativas | Sub-sessão com link pro pai + aprovação |
|---|---|
| ![runs ativas na sidebar](docs/screenshots/runs-parallel.png) | ![card de aprovação no chat filho](docs/screenshots/subsession-approval.png) |

| Binding de repo por chat | Rotinas e lembretes |
|---|---|
| ![seletor de repositório do workspace](docs/screenshots/repo-binding-picker.png) | ![página de automações](docs/screenshots/automations.png) |

| Chat (claro/escuro) | Painel Workspace | Terminal PTY |
|---|---|---|
| ![chat](docs/screenshots/chat-light.png) | ![workspace](docs/screenshots/workspace-panel.png) | ![terminal](docs/screenshots/terminal.png) |

## Recursos de agente (novos — camada aditiva sobre a paridade upstream)

- **`delegate_task`** — cria chat filho + run server-side: `wait:false` retorna
  `childRunId` na hora, `wait:true` (padrão) bloqueia até 300s; parâmetros `repo`
  e `persona` (uma Skill do workspace como system prompt); profundidade ≤ 3;
  cada sub-agente herda o repo do pai mas trabalha na sua **worktree exclusiva**.
- **Worktrees de repo por chat** — vincular um chat a `owner/repo` cria uma
  worktree exclusiva (`workspaces/{user}/worktrees/{owner}__{repo}/{chatId}`,
  branch `chat/{chatId}`) sobre um checkout base compartilhado; a resolução é
  estrita por chat — conversa nova nunca herda o binding global. Desvincular,
  arquivar ou deletar libera a worktree; o checkout base sai de disco quando
  ninguém mais referencia, e um base divergente se recupera sozinho (HEAD
  divergente preservado em `backup/*`).
- **Três modos de agente** — seletor no composer `chat` / `plan` / `build`:
  `chat` é sem repo (tools de workspace negadas), `build` exige repo vinculado
  (`PATCH` 400 sem binding; desvincular derruba `build`→`chat`; `plan_exit` só
  promove a `build` com binding). O gate de workspace cobre `delegate_task`,
  `lsp_*` e `skill`; o hook de formatação honra o `FormatCommand` do binding.
- **Decisões autônomas** — presets `smart`/`auto`/`always-allow` respondem
  sozinhos a prompts `ask_user` que trazem opções (opção recomendada, senão a
  primeira) sem pausar a run; cada escolha automática emite evento auditável
  `decision_auto`.
- **Workspace mobile** — alvos de toque e chips que colapsam mantêm o painel
  workspace, o seletor de repo e a IDE alcançáveis em telas pequenas.
- **`run_result` / `worktree_diff` / `worktree_merge`** — colhe resultados assíncronos,
  revisa o diff do filho e faz merge explícito (ou `auto_merge` opt-in).
- **Steer e queue** — `POST /api/v1/chats/{id}/runs/{runId}/steer` injeta mensagem
  do usuário numa run viva (`steer` = próxima fronteira de turno, `queue` = quando ociosa).
- **Memória do agente** — `memory_save`/`memory_search` (escopo projeto + global),
  bloco `<agent_memory>` injetado automaticamente; scheduler de 6h destila fatos
  duráveis em memórias `auto:*` com watermark e opt-out por usuário.
- **Rotinas e lembretes** — tool `routine` (list/create/update/delete/run_now,
  `once`/`interval`/`daily`/`weekly`) e `reminder` (dispara notificação
  `automation.reminder`) sobre o engine de Automações embutido.
- **Compactação de contexto** — quando o histórico cresce, o pipeline resume os
  turnos antigos pelo modelo configurado e mantém um cutoff em kv por chat.
- **Modo plan → SPEC SDD** — o modo `plan` (ou o slash builtin `/plan`) analisa
  o requisito em read-only e emite uma SPEC SDD completa em markdown: metadata,
  user story, escopo, requisitos `RF-###`, critérios BDD, task plan, DoD e
  perguntas abertas. O `plan_exit` valida a estrutura (planos incompletos são
  recusados com as seções faltantes), persiste em `.openwebui/plans/` e só
  promove a `build` após aprovação do usuário.
- **Papéis de modelo + cadeias de fallback** — modelos dedicados para `tasks`
  (títulos, follow-ups, tags, compactação), `memory` (destilação) e `advisor`;
  cada papel resolve por mapa de capacidades (ex.: visão) com cadeia de
  fallback ordenada, configurável por camada.
- **Tiers finos de aprovação de tools** — cada tool declara `Read`/`Write`/
  `Exec` (fail-closed `Exec`); overrides por tool do usuário podem relaxar,
  nunca enfraquecer um deny da tool.
- **Run advisor (watchdog)** — um modelo secundário read-only revisa o
  transcript vivo, inspeciona o workspace com tools próprias de leitura e
  injeta orientação no meio da run — sem poder de aprovação ou mutação.
- **Contexto dirigido pelo agente** — par de tools `checkpoint`/`rewind`
  (marca → explora → colapsa), `context_notes` (notebook por ramo que sobrevive
  ao rollover) e `new_context` (compactação sob demanda); poda mecânica elide
  tool results superados e URIs `history://` recuperam turnos pré-compactação.
- **Ramificação de chat** — mensagens formam árvore (`ParentId`/`LeafMessageId`);
  "ramificar daqui" forka qualquer turno num chat irmão, com resumo do ramo
  abandonado.
- **Agentes declarativos** — arquivos `*.agent.md` (frontmatter: tools, model,
  spawns) descobertos no repo definem sub-agentes nomeados, invocáveis por
  `delegate_task(agent: "…")`.
- **Configuração em camadas** — settings efetivos resolvem por
  global → admin → usuário com proveniência por chave.
- **Pipeline de memória** — consolidação agendada destila resultados de sessões
  nas camadas de memória durável reinjetadas em runs novas.
- **Anexos de mídia no chat** — thumbnails de imagem/vídeo dentro do composer,
  tamanho inline adequado após o envio, clique para ampliar; imagens anexadas
  são sempre analisadas junto ao prompt por um modelo com visão.
- **Retenção** — expurgo diário (runs de automação >90d, notificações >60d, steers
  de runs terminais >30d) + prune de worktrees órfãs + WAL checkpoint + VACUUM semanal.
- **Hierarquia de chats** — `ParentChatId`/`ParentRunId`, `GET /chats/{id}/children`,
  árvore aninhada na sidebar, marcadores de conclusão postados no chat pai.

Detalhes por funcionalidade (com screenshots): [docs/pt/RECURSOS-AGENTE.md](docs/pt/RECURSOS-AGENTE.md) · [docs/en/AGENT-FEATURES.md](docs/en/AGENT-FEATURES.md)

## Recursos clássicos (paridade upstream)

- **Chat** com streaming SSE (Ollama / OpenAI-compat), anexos, 👍/👎,
  edição, regeneração, título/follow-ups/tags gerados por LLM
- **Runs desacopladas** — a resposta roda no servidor; fechar/recarregar a aba não
  interrompe; ao reabrir, o cliente reanexa com replay por `Last-Event-ID`
- **Notificações** — toast in-app, Notification API e Web Push (VAPID)
- **Streaming de tools + gate de aprovação** — `tool_call`/`tool_result` no SSE,
  presets de aprovação por conversa (readonly/aprovar/sempre/**auto por risco**
  LOW-MED-HIGH), negar com instrução pro modelo corrigir a rota
- **Tools builtin** — `shell_exec` + jobs em background, `file_*` (list/read/grep/
  glob/write/edit com diff), `code_interpreter`, `fetch_url`, `web_search`,
  `generate_image`/`generate_video`, `browser_screenshot`, `ask_user`,
  `n8n_list_workflows`/`n8n_trigger`, `todo_write`, `skill`, `lsp_*`, `plan_exit`
- **Painel Workspace** — Tasks, Changes (diff git), Jobs, MCPs, Terminal (PTY) e
  Info da run; redimensionador arrastável; tela cheia em `/ide`
- **Modos Chat/Plan/Build** + modos de permissão; **checkpoints e revert**;
  **pause/resume** de runs; slash commands vindos das skills do repo
- **RAG, Knowledge, Arena, Canais/Notas/Calendário, Admin (usuários, conexões,
  servidores MCP, integrações), i18n em 8 locales, shell PWA offline**

## Início rápido

```bash
dotnet restore OpenWebUI.slnx
dotnet build OpenWebUI.slnx --configuration Release
dotnet run --project src/OpenWebUI.Api   # http://localhost:8080
```

Docker (`:3032 → :8080`):

```bash
docker compose up -d --build
# ou a imagem publicada
docker run -p 3032:8080 ghcr.io/afonsoft/open-webui:latest
```

Guia completo de deploy: [docs/pt/DEPLOY-DOCKER.md](docs/pt/DEPLOY-DOCKER.md) ·
[docs/en/DEPLOY-DOCKER.md](docs/en/DEPLOY-DOCKER.md)

## Testes

```bash
dotnet test OpenWebUI.slnx   # 1700+ testes de API + testes de cliente (NUnit)
```

Gate de cobertura via Coverlet + ratchet (`.ci/coverage-baseline.txt` só sobe).

## Qualidade e CI

| Workflow | O que cobre |
|---|---|
| `ci-build-test.yml` | Build Release, todos os testes + gate de cobertura, payload WASM, freshness do tailwind, build Docker, ratchet do baseline |
| `a11y-audit.yml` | axe-core (Playwright) mobile+desktop |
| `code-quality.yml` | Qodana + [SonarCloud](https://sonarcloud.io/project/overview?id=afonsoft_open-webui) |
| `security-scan.yml` | CodeQL (C#+JS), Trivy, GitGuardian, Snyk |
| `release.yml` | Tag `vX.Y.Z` → imagem GHCR + Docker Hub |

## Documentação

- [docs/pt/RECURSOS-AGENTE.md](docs/pt/RECURSOS-AGENTE.md) — detalhes do modo agente (PT-BR)
- [docs/en/AGENT-FEATURES.md](docs/en/AGENT-FEATURES.md) — agent platform details (EN)
- [docs/MIGRACAO-DOTNET.md](docs/MIGRACAO-DOTNET.md) — mapa de paridade upstream → .NET
- [docs/architecture/architecture.md](docs/architecture/architecture.md) — arquitetura (Mermaid)
- [docs/analise-fork-alltomatos-opencode.md](docs/analise-fork-alltomatos-opencode.md) — análise do fork do opencode que guiou o roadmap de agente
- `.specs/` — SPECs em andamento; `docs/specs/` — SPECs entregues
- [CHANGELOG.md](CHANGELOG.md)

## Star History

[![Star History Chart](https://api.star-history.com/chart?repos=afonsoft/open-webui&type=date&legend=bottom-right)](https://www.star-history.com/?repos=afonsoft%2Fopen-webui&type=date&legend=bottom-right)

## Licença

BSD-3-Clause — ver [LICENSE](LICENSE).
