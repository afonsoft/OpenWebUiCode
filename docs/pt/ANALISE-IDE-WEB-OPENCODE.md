# Análise: migrar funcionalidades do opencode → Open WebUI (.NET) — caminho para um IDE Web

> Data: 2026-10-09 · Fontes: `afonsoft/OpenWebUiCode` (main @ `5579e4e`), `afonsoft/opencode` (fork de `anomalyco/opencode`, branch `dev` @ `ecc4916`)
> Status: **análise** — nenhuma decisão aqui é implementação aprovada; os SPECs draft em `.specs/` aguardam o gate.

## 1. Objetivo e método

Avaliar o que falta para o Open WebUI .NET virar um **IDE web com agente** — comparando o AS-IS do repo com o que o opencode oferece (referência de UX/arquitetura de coding agent), e mapear o que pode ser migrado/adaptado para a nossa stack (.NET 10 + Blazor WASM + EF Core SQLite, execução no host).

A pergunta "implementou as funcionalidades do opencode?" — **parcialmente**. A série `chat-agent-*` (SPECs `docs/specs/SPEC-20261007-chat-agent-*.md`, entregue) já portou o núcleo do agente: tools de arquivo/shell/todo, approvals por risco, painel de workspace, subagente. O que falta é a **superfície de IDE** (explorer, editor, testes, LSP) e os **mecanismos de contexto do repo** (skills→slash commands, AGENTS.md, plan/build, checkpoints).

## 2. O que já temos (AS-IS, evidências)

| Capacidade | Estado | Evidência |
| --- | --- | --- |
| Workspace por usuário (`data/workspaces/{uid}`) | ✅ | `Infrastructure/Services/WorkspaceRepoService.cs:24` |
| Bind de repo GitHub (clone + troca de branch, PAT por usuário) | ✅ | `GitHubEndpoints.cs:20-29`, `WorkspaceRepoService.OpenAsync:53`, UI `RepoBindingPanel.razor` (chat + admin) |
| Tools de arquivo confinadas ao workdir | ✅ | `ChatTools/Tools/FileBuiltinTools.cs` — `file_list`, `file_read` (paginado), `file_grep`, `file_glob`, `file_write`, `file_edit` com diff unified persistido |
| `shell_exec` com classificador de risco estático | ✅ | `ShellExecBuiltinTool.cs` → `ChatJobService`; `CommandRiskClassifier.cs` (Safe/WorkspaceWrite/Dangerous, fail-closed) |
| Execução de código (python3/node) | ✅ | `CodeInterpreterBuiltinTool.cs` |
| Jobs em background (spawn/list/output/kill) | ✅ | `ChatJobService`, `ChatJobEndpoints.cs`, aba Jobs do painel |
| Todo da run | ✅ | `TodoWriteBuiltinTool.cs`, aba Tasks com toggle manual |
| Pergunta ao usuário | ✅ | `AskUserBuiltinTool.cs` + `QuestionPromptCard.razor` |
| Subagente (`delegate_task`, run filha isolada) | ✅ | `DelegateTaskBuiltinTool.cs` |
| Approvals por risco + card de permissão | ✅ | `ToolCallRiskClassifier.cs`, `PermissionPromptCard.razor`, preset `auto`/`smart` |
| Painel workspace (tasks/changes/jobs/terminal/mcps/info) | ✅ | `ChatWorkspacePanel.razor` — aba Changes com diff real do workdir git |
| Terminal PTY real via WebSocket | ✅ | `TerminalPtyEndpoints.cs`, `TerminalSessionManager`, `TerminalView.razor` (`/terminal` page + aba no painel; flag `terminal.enabled`) |
| Pause/resume de run | ✅ | `ChatRunEndpoints.cs` (`runs/{id}/pause|resume`), `ChatRunPauses.cs` |
| Slash commands de prompts do usuário | ✅ parcial | `/workspace/prompts` + `GET /command/{command}` + sugestões `/` no composer (`ChatView.razor:130-137,341`) |
| Web search, fetch_url, browser_screenshot, n8n, mídia | ✅ | `ChatTools/Tools/*` |
| MCP servers do usuário | ✅ | `McpClientService`, `McpEndpoints`, aba MCPs do painel |

## 3. O que o opencode tem que falta aqui (TO-BE de referência)

| Capacidade opencode | Onde no opencode | Falta no open-webui |
| --- | --- | --- |
| **Editor/explorer de arquivos na UI** (app Solid: file tree, tabs, diff review) | `packages/app` | ❌ — não existe visualização de arquivos do workdir; `/workspace/files` é a biblioteca de uploads do RAG |
| **Skills do repositório → comandos/agente** — discovery de `SKILL.md` em `{skill,skills}/**`, `.claude/skills`, `.agents/skills`, `.opencode/skill*` + tool `skill` que injeta o corpo | `src/skill/discovery.ts`, `src/tool/skill.ts` | ❌ — temos catálogo de skills no workspace UI (`/workspace/skills`) mas **não lê o repo vinculado** nem expõe como tool/comando |
| **Slash commands customizados** — markdown com frontmatter (agent, model, subtask, `$1..$N` hints) em `.opencode/command`, `.claude/commands`, MCP prompts; source: command/mcp/skill | `src/command/index.ts`, `command/template/` | ⚠️ parcial — prompts de usuário existem, mas sem descoberta no repo, sem args `$N`, sem binding de agente/modelo |
| **Regras do projeto no system prompt** — AGENTS.md/CLAUDE.md/opencode.json auto-injetados | `src/session/system.ts`, `session/prompt.ts` | ❌ — system prompt vem só do modelo/Model.SystemPrompt (`ModelEndpoints.cs:270`); nada lê AGENTS.md do workdir |
| **Modos de agente plan/build** — agente `plan` com permission ruleset restrito (read-only) → `plan_exit` pergunta e promove pra build | `src/agent/agent.ts:141-157`, `src/tool/plan.ts` | ❌ — sem noção de modo; todo run usa o mesmo conjunto |
| **Checkpoints/snapshot + revert** — snapshot git-hash do workdir por turno; `revert` restaura; diffFull entre snapshots | `src/snapshot/index.ts`, `session/revert.ts` | ❌ — Changes mostra diff, mas não há snapshot/revert |
| **Permission rulesets por agente/tool/pattern** (allow/ask/deny com wildcard; resposta "always" memoriza) | `src/permission/` (`evaluate.ts` wildcard matching) | ⚠️ parcial — `ToolCallRiskClassifier` é estático global; sem rulesets por agente, sem "always" persistido por sessão |
| **`apply_patch`** (patch multi-arquivo estruturado) + **format hook** pós-edição | `src/tool/apply_patch.ts`, `src/format/` | ⚠️ — `file_edit` é search/replace; sem patch multi-file nem formatter automático |
| **LSP** — documentSymbol/workspaceSymbol/references/hover/diagnostics como tools + para o editor | `src/lsp/`, `src/tool/lsp.ts` | ❌ |
| **Worktree isolation** (sessão em `git worktree` separado) | `src/worktree/` | ❌ — workdir único por usuário |
| **`@file` mentions / contexto explícito** no composer | `packages/app` (mention picker) | ❌ — composer só tem `/prompts` |
| **code-mode** (saída estruturada por código) | `src/tool/code-mode.ts` | ❌ (opcional) |
| **Runner de testes** dedicado | via `shell`/task (não há tool `test` dedicada — o fluxo é prompt + terminal) | ⚠️ igual — mas o IDE precisa de UX "rodar testes" (job template + parsing) |
| Session fork/share/compaction/title auto | `session/summary.ts`, `share/` | ⚠️ share existe só como link público de chat; sem fork/compaction |

## 4. Matriz de gaps (veredictos)

Legenda: `CONFIRMADO` = diferença real e acionável · `DUPLICADO` = já coberto · `REJEITADO` = não aplicável/coberto.

| # | Gap | Veredicto | Comentário |
| --- | --- | --- | --- |
| GAP-impl-ide-surface | IDE web: explorer + editor + abas + diff + terminal integrado | **CONFIRMADO** | Não existe nenhuma superfície de edição; tudo é painel read-only de run. É o bloco central do "IDE WEB". |
| GAP-impl-workspace-file-api | API REST de arquivos do workdir (tree/read/write/mkdir/delete/rename) servindo a UI | **CONFIRMADO** | `WorkspaceFiles.ResolveInside` já faz o jail; falta expor endpoints (hoje só tools de agente usam). |
| GAP-impl-repo-skills | Ler `SKILL.md`/commands do repo vinculado → slash commands + tool `skill` | **CONFIRMADO** | Pedido explícito do usuário ("ler as skills do repositório e criar o slash command da skill"). |
| GAP-impl-project-instructions | Injetar `AGENTS.md`/`CLAUDE.md`/`README` do workdir no system prompt da run | **CONFIRMADO** | Nenhuma referência no código (`grep AGENTS` → só SystemPrompt de modelo). |
| GAP-impl-agent-modes | Modos plan/build + tool `plan_exit` + ruleset por modo | **CONFIRMADO** | Alavanca grande de UX de coding; usa a infra de permissões existente. |
| GAP-impl-checkpoints | Snapshot git por turno + `revert`/`restore` + diff entre checkpoints | **CONFIRMADO** | Dá "undo" real — requisito prático para o agente editar código livremente. |
| GAP-impl-at-mentions | `@arquivo` no composer injetando conteúdo no contexto | **CONFIRMADO** | Barato: reaproveita o fluxo de sugestões `/`. |
| GAP-impl-test-runner | UX de testes: comando detectado por repo + aba/log de resultado no painel | **CONFIRMADO** | Hoje roda via `shell_exec` opaco; IDE precisa de "Run tests" com saída parseada. |
| GAP-impl-lsp | LSP (diagnostics, hover, símbolos, go-to-def) | **CONFIRMADO** (fase posterior) | Maior custo; spawn de language server por linguagem + JSON-RPC. Pré-requisito para squiggles no editor. |
| GAP-impl-worktree | Worktree por run/chat paralelo | **CONFIRMADO** (opcional) | Necessário só quando quisermos runs paralelos no mesmo repo. |
| GAP-impl-apply-patch | `apply_patch` multi-arquivo + format hook | **CONFIRMADO** (baixa prioridade) | `file_edit` já cobre o caso comum; patch melhora robustez de edição grande. |
| GAP-arch-permission-rulesets | Rulesets allow/ask/deny por agente+pattern persistidos | **DUPLICADO-parcial** | Classifier estático já existe; evoluir para ruleset pode entrar como RF do spec de modos, sem spec próprio. |
| GAP-impl-code-mode | `code-mode` tool | **REJEITADO** | Marginal; StructuredOutput já existe no pipeline. Revisitar se pedirem. |
| GAP-impl-share-sync | Compartilhamento/sync estilo opencode share server | **REJEITADO** | open-webui já tem compartilhamento público de chat (`/s/{id}`); sync multi-device não é escopo de IDE. |
| GAP-impl-tui-acp | TUI, ACP protocol, desktop bridge | **REJEITADO** | Fora do escopo web. |
| GAP-sec-sandbox | Sandbox de execução (container/VM por workspace) | **CONFIRMADO como decisão pendente** `[A DEFINIR]` | SPEC anterior decidiu host-exec com approvals; para IDE público/multiusuário, isso vira o principal risco. Não é gap de feature — é decisão de arquitetura a registrar. |

## 5. Proposta de migração — fatias sugeridas

Ordenadas por valor/custo. Cada fatia = 1 SPEC draft (`.specs/`) = 1 branch/PR (convenção do repo).

| Fatia | Conteúdo | Depende de | Esforço | Impacto |
| --- | --- | --- | --- | --- |
| **S1 — Workspace File API** | Endpoints `GET /workspace/repo/tree`, `GET/PUT /workspace/repo/file`, `POST mkdir/delete/rename` — todos jailed por `WorkspaceFiles.ResolveInside`; paginação, cap de bytes, binary-guard (reuso das regras do `file_*`). Autorização: dono do workspace. | — | P | Desbloqueia S2/S3 |
| **S2 — IDE surface (`/ide`)** | Página dedicada: file tree (lazy), editor **CodeMirror 6** via JS interop (leve, sem build pesado — Monaco é ~3MB e exige worker setup), abas, status git (reuso `WorkspaceGitService`), terminal embutido (`TerminalView`), painel de run/jobs, split view com chat. Read-only mode quando repo não vinculado. | S1 | G | O "IDE" visível |
| **S3 — Skills & slash commands do repo** | `SkillDiscoveryService` escaneia workdir: `**/SKILL.md` em `{skill,skills}/`, `.claude/skills/`, `.agents/skills/`, `.devin/skills/`, `.opencode/skill*/`; frontmatter `name`/`description` → comandos `/{name}` no composer (mescla com prompts de usuário; fonte etiquetada `skill`/`command`/`prompt`); body da skill injetado como instrução da run + suporte a `$1..$N`; nova tool `skill {name}`; scan com cache + invalidação em file-change/checkout. | S1 | M | Pedido explícito |
| **S4 — Instruções do projeto no contexto** | Assembly do system prompt concatena (na ordem): `AGENTS.md`, `CLAUDE.md`, `.cursor/rules/*.md`(?), `README` resumido, `opencode.json` ignorado. Bounded (ex.: 32KB, ordem de precedência repo→user). | S1 | P | Qualidade do agente |
| **S5 — Modos de agente (plan/build)** | `mode` no Chat/chat-preset; `plan` desabilita write/shell_write via ruleset; tool `plan_exit` (escreve plano em `.openwebui/plans/<run>.md` no workdir + `ask_user` para confirmar); UI: toggle no composer + chip de modo. Estende `ToolCallRiskClassifier` → `PermissionRuleset` por modo. | — | M | Workflow agente real |
| **S6 — Checkpoints & revert** | Snapshot do workdir por turno via `git` shadow index (padrão opencode: `git -C <dir> hash-object`/commit em ref oculto, ou `git stash create`); evento `checkpoint` no SSE; botão Reverter por turno + diff entre checkpoints na aba Changes. | S1 | M-G | Confiança p/ edição livre |
| **S7 — `@file` mentions + test runner UX** | `@` no composer → autocomplete de paths do repo (tree API) injetando trecho no contexto da run; botão "Run tests" detecta comando por manifesto (package.json→`npm test`, *.slnx→`dotnet test`, pyproject→`pytest`) → ChatJob com parser de saída → badge ✓/✗ + aba com log. | S1, S2 | M | Completa loop codar→testar |
| **S8 — LSP (fase 2)** | `LspService` spawna language server por workspace (omnisharp/csharp-ls, typescript-language-server, pylsp) via stdio JSON-RPC; tools `lsp_diagnostics/hover/definition/references`; editor consome diagnostics/hover. | S2 | G | Inteligência de editor |
| **S9 — Worktree por run + formatter hooks** (opcional) | Worktree efêmero por run + merge via Changes; hook pós-write (`dotnet format`, `prettier`) configurável. | S5/S6 | M | Paralelismo seguro |

### O que **não** migrar
- **TUI/ACP/desktop/share-sync/multi-tenant enterprise do opencode** — fora do escopo.
- **Port literal do código TS** — a referência é de arquitetura/contrato (ferramentas, eventos, modos); implementação é .NET nativa sobre `ChatRun`/`ChatJob`/SSE que já existem.
- **`code-mode`, session fork, compaction automática** — deferidos; compaction pode entrar depois com limites de contexto reais.

## 6. Riscos e decisões `[A DEFINIR]`

1. **Sandbox (maior risco):** um IDE web onde o agente executa shell no host com credenciais de usuário = precisa decidir container-per-workspace (Docker) vs continuar host+approvals. Recomendo registrar ADR. Isso não bloqueia S1–S7 (aprovações já existem), mas bloqueia multi-usuário real.
2. **Editor:** CodeMirror 6 (recomendado — pequeno, sem build worker) vs Monaco (paridade VS Code, pesado). `[A DEFINIR]` — default CM6.
3. **Path de skills dentro do repo:** descobrir recursivamente `**/SKILL.md` ou só diretórios conhecidos? opencode faz ambos (`SKILL_PATTERN` + externos). Default: ambos com cap de 50 skills.
4. **Execução de testes pode ser destrutiva** (testes escrevem arquivos): passa pelo classifier existente; "Run tests" roda como `shell_exec` classify → usuário vê o comando antes de aprovar.
5. **WASM + edição de arquivo grande:** tree/read paginados; editor com cap de bytes (reuso `MaxFileBytes`) + virtualização.

## 7. SPECs draft gerados

| SPEC | Fatia |
| --- | --- |
| `.specs/SPEC-20261009-workspace-file-api.md` | S1 |
| `.specs/SPEC-20261009-web-ide-surface.md` | S2 |
| `.specs/SPEC-20261009-repo-skills-slash-commands.md` | S3 + S4 |
| `.specs/SPEC-20261009-agent-modes-plan-build.md` | S5 |
| `.specs/SPEC-20261009-checkpoints-revert.md` | S6 |
| `.specs/SPEC-20261009-ide-mentions-tests.md` | S7 |
| `.specs/SPEC-20261009-lsp-diagnostics.md` | S8 |
| `.specs/SPEC-20261009-worktree-format-hooks.md` | S9 |

Ordenação de dependência sugerida: **S1 → S2 → (S3+S4, S5) → S6 → S7 → S8/S9**.

---
*Relatório consolidado também em `.claude/memory/gap-analysis-20261009.md` (estado de retomada).*
