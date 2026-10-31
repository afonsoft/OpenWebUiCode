# Devin CLI — Análise por Engenharia Reversa (REA)

Relatório de evidências extraídas do binário do Devin CLI para orientar a
evolução do agente do OpenWebUiCode. Produzido em 2026-10-31.

## 0. Proveniência

| Campo | Valor |
|-------|-------|
| Artefato | `~/.local/share/devin/cli/_versions/3000.11.234/bin/devin` (→ `current`) |
| Formato | ELF 64-bit, ARM aarch64, statically linked, **stripped** |
| Tamanho | 195.302.432 bytes |
| SHA-256 | `e8bf5931b36843ddc11ec8b818afc8f913299d66a78468c10e46d41c596753ef` |
| Toolchain | Rust (1 rustc commit inferido; sem `.dep-v0` cargo-auditable) |
| Build root | `/usr/local/cargo/registry/src/pkg.cognition.build-3983e96abc3e6656/` |
| REA evidence | `ev_50e2734e3c76b23d088951327b14a335ead2281b6109973ef72a2a93db36c7c0` (inspect_rust_binary) |
| Método | `strings`+`objcopy` sobre `.rodata` (44 MB), `--help`, docs em `share/devin/docs` |

Limitações: sem provider de decompilação configurado (Ghidra/Hopper/IDA/Kuna
ausentes) — a análise é baseada em strings embutidas (Rust armazena literais em
`.rodata` como fatias ptr+len não-NUL), panics com paths de fonte, ajuda do CLI
e documentação empacotada. Nenhuma execução foi observada.

## 1. Mapa de crates (via panic paths)

| Crate | Arquivos vistos | Papel inferido |
|-------|-----------------|----------------|
| `chisel` | 62 | binário `devin` (acp_repl, auth, auto_setup, internal_cmd, forward) |
| `chisel-agent` | 66 | agente ACP: acp_server, acp_tools, megaplan, session_*, revert, local_fusion, skills_loading, model_defaults, memory_guard, output_truncation, goal, deferred_fields, mcp_prompts, custom_subagents, conversation_history, browser_preview |
| `chisel-commands` | 16 | subcomandos (doctor, checks, worker, rules, skills, cloud, connect, manual_migrations) |
| `chisel-core` | 13 | model/provider, prompts.rs, context_usage, profiles, ref_tags, translator, turn_stats, mcp_mentions, flags |
| `chisel-api` | 14 | auth (pkce, credentials), devin_api, telemetry (sentry, skill_spool, unleash), team_settings, distribution, git_info, bug_report |
| `chisel-server` | 4 | `acp`, `agents/review`, `agents/summarizer` |
| `chisel-ui` | 18 | REPL TUI (spinner, tool_content, user_question_panel, subagent_panel, terminal_markdown, model_selector, side_chat_panel, readbox, paste_burst, confetti) |
| `chisel-otel` | 7 | telemetria OTEL |
| `chisel-cloud-bridge` | 6 | handoff flow, drs client, org, setup_git, share |
| `chisel-acp-relay`/`client` | 9 | relay stdio↔WebSocket ACP cloud |
| `toolbox` | 49 | **tools**: exec (pty, raw_exec, session/shell managers, shell_edits tracker, login_shell_env, native_terminal_host, powershell_parser, background_emitter), read/write/edit/apply_patch, grep/glob, todo, user_question, notebook_*, web_search, webfetch, view_image, request_scope, kill_shell, get_output→exec_output, write_to_process, fast_context, instant_context, exit_plan_mode, write_plan, persistent_subagent, readonly_path_extractor, mcp/* (registry, service, call_tool, list_*, read_resource, prompts, oauth, sse_transport, stderr_log) |
| `toolbox-core` | 12 | registry, plans, composite, typed_tool, local_overflow; tools: context, format, schema, **tool_search**, **unseen_messages**, subagent |
| `affogato` | 16 | **runtime do agente**: control_loop, effects/{handler,inference,tasks,tools}, tool_pipeline/scheduler, manager, runner, subagent, stall_watch, cache_keepalive, event_types, cog, image, client |
| `agent-ext` | 37 | **compactor** (async_file_compactor, file_compactor, extend_context, summarize, summary_shape, edited_files, handoff_history, todo_list, user_message_records/{edits,input}), hooks/{evaluator,event_handler,extension,hands}, rules, skills (event_handler, inheritable, legacy_tool, tool, types), smart_permission/classifier, lints/diagnostics_handler, plugins, btw, revise, title, discovery, tool_arg_leak_recovery, tool_result_footer |
| `barista` | 5 | event_sender, harness, model_registry, resume, tool_ui |
| `local-agent` | 10 | agent_setup, permissions, sandbox_mode, tool_registry, web_search, plugins/{hook_sink,mcp_sink,lazy,refresher} |
| `local-tools-client` | 8 | api, fingerprint, gateway, grep, permissions, process, service, session |
| `plugin`/`plugin-host` | 28 | plugins: manifest, lockfile, discover, governance, policy, install, store, catalog/fingerprint, mcp_sources, transfer, bundle_fetcher, ingest, actions, cloud |
| `config-importers` | 32 | importa config de claude, cognition, copilot, cursor, opencode, windsurf, zed, standard (mcp/rules/hooks/skills), jsonc, mcp_writer, repo_config, resource |
| `inference` | 4 | backend, request, retry, stream |
| `sandbox-runtime` | 4 | manager, linux, http_proxy, socks_proxy |
| `browser-preview` | 6 | csp, inject, manager, proxy, server, service |
| `message-forest` | 3 | hydrator, journal — **conversa como árvore/journal** (branching+revert) |
| `user-config` | 4 | user, project, trusted_workspaces |
| `scrollback`/`term`/`wezterm-*` | 24 | terminal (vendored wezterm surface/escape-parser) |

Vendored/observado: tree-sitter grammars (dezenas de linguagens), HTML5 parser
próprio para webfetch (estados Initial→AfterBody), git plumbing (`cat-file
--batch`, `check-ignore`), dioxus-core (UI desktop), unleash-yggdrasil (feature
flags), zopfli (compressão de assets), aws-lc-rs.

## 2. Superfície do CLI (`devin --help`)

- Comandos: `auth, mcp, models, doctor, rules, skills, plugins, cloud, desktop,
  list/ls, rm, ssh, forward, update, version, migrate, sandbox, setup,
  uninstall, acp`.
- `devin acp`: servidor ACP stdio; `--agent-type summarizer|review`;
  `--refusal-fallback <models>` (cadeia de fallback p/ recusa de policy);
  `--cloud` (relay ACP → WebSocket da nuvem).
- **Permission modes**: `auto` (read-only auto), `accept-edits` (+edições no
  workspace), `smart` (+ juiz LLM rápido por ação), `bypass`/`dangerous`
  (tudo). Com `--sandbox`: `autonomous` (único disponível; capabilities —
  prompts só para rede/escopo novo).
- Sandbox: macOS seatbelt / Linux **bwrap+seccomp**; escrita só no workspace +
  escopos `Write(...)`; leitura livre exceto `Deny(Read(...))`; filtro de
  domínios de rede; `sandbox.excluded` rules rodam fora.
- Flags: `--permission-mode`, `--sandbox`, `--cloud`, `--model`, `--verbosity
  0-3`, `-p/--print`, `--export`, `-c/--continue`, `-r/--resume`,
  `--respect-workspace-trust`, `--config`, `--prompt-file`.

## 3. Modos e comandos de sessão

- **Agent modes**: `normal` (code), `plan`, `ask`, `side_chat` (`/btw`).
- Slash: `/help /exit /quit /clear /new /mode /model /billing /resume /ls
  /continue /rm-session /workspace /add-dir /undo-add-dir /hooks /login
  /logout /update /upgrade /bug /compact /cloud /open /handoff /archive
  /plan /ask /bypass /yolo /normal /accept-edits /smart` + skills como
  `/skill-name`.
- Plan mode: só leitura + `write_plan` (arquivo em `~/.devin/plans/`); MCP só
  tools read-only; **workflow de 5 fases** — Understand → Design →
  Verification Planning → Present Plan (template `## Summary/Implementation
  Steps/Files to Modify/Verification/Risks`) → `exit_plan_mode`.

## 4. Hooks (documentado + binário)

- **8 eventos**: `PreToolUse, PostToolUse, PermissionRequest, UserPromptSubmit,
  Stop, PostCompaction, SessionStart, SessionEnd` (no binário: `pre_tool,
  post_tool, permission_request, user_prompt, pre_stop, post_compaction,
  session_start, session_end`).
- Tipos: `command` (JSON no stdin, JSON/exit-code no stdout) e `prompt`
  (avaliador LLM: `true/false|{"pass"}|{"decision":"approve|block|ask",
  "add_context"}`).
- Saídas: `decision: approve|block`, `reason`,
  `hookSpecificOutput.additionalContext` (injeta contexto),
  `hookSpecificOutput.updatedInput` (reescreve args da tool em PreToolUse).
- Exit codes: `0` ok, `2` block, demais = erro não-bloqueante. Timeout = erro.
- `matcher` regex sobre `tool_name`; correlação `session_id` + `prompt_id`;
  env `DEVIN_PROJECT_DIR`.
- Locais: `.devin/hooks.v1.json` (arquivo inteiro), chave `hooks` em
  `.devin/config.json`, `config.local.json`, `~/.config/devin/config.json`, e
  compat `.claude/settings*.json` + `~/.claude.json`.
- `tool_arg_leak_recovery` — repara sintaxe XML de tool-call vazada nos args.
- `tool_result_footer` — injeta footer pós-tool-result ("First privately list
  what you need next; then request every item that doesn't depend on
  another's result in this one response").

## 5. Permissões

- Presets do `--help`; modo **smart** usa o prompt classificador abaixo com um
  modelo rápido; categorias always-ask (installs, git mutável, rm, sudo, cloud
  destrutivo, arquivos sensíveis).
- Org/Team Settings (deny/ask) sempre vencem bypass.
- `request_scope` pede Read/Write por diretório quando sandbox bloqueia;
  `command` permite opt-out pontual do sandbox.

## 6. Subagentes

- Profiles: `subagent_explore` (read-only + web_search; modelo do **router de
  subagent**, não o do pai), `subagent_general` (full access; **mesmo modelo
  do pai**), custom em `agents/<name>.md|agents/<name>/AGENT.md` com
  frontmatter `name/description/model/allowed-tools/max-nesting`.
- Foreground: usuário aprova tools; background: herda permissões já dadas,
  não-aprovadas são auto-negadas; `Ctrl+B` move fg→bg; painel `f` bg→fg.
- Resume de subagent (sempre em fg); interrupt *estaciona* (não mata);
  `max-nesting` por profile (default: filhos não criam filhos).
- `subagents_enabled:false` remove as tools; `agent.subagent_model` override.
- **Sidekick** (`persistent_subagent`): worker persistente do "lead" —
  ver §8.

## 7. Sessão e contexto

- `message-forest`: conversa como árvore versionada (journal+hydrator) —
  revert/revisão de nós.
- Persistência: `session_db` (sqlite — `prompt_history` vista em SQL),
  `session_store/export`, summaries em
  `~/.local/share/devin/summaries/<id>.md`, transcripts.
- **Compactor** (`agent-ext/compactor`): resumo estruturado (`## Overview`,
  `## Key Details & Breadcrumbs` com citações a message-ids/termos,
  `## Current State`); proíbe recitar rules verbatim; salva histórico
  completo em arquivo; `AsyncFileCompactor` compacta nós em background com
  eviction por capacidade; rastreia `edited_files`, `todo_list`,
  `handoff_history`, `user_message_records`; "NOTES ALREADY WRITTEN for the
  user's earlier messages" — notas incrementais por mensagem.
- `extend_context` — tool que o modelo chama p/ pedir extensão de janela
  (grant/limit/at_ceiling).
- `unseen_messages` — mensagens do usuário chegadas durante a run (steering).
- `cache_keepalive` — pings p/ manter prompt-cache quente.
- `stall_watch` — watchdog de stall no control loop.
- `output_truncation` — overflow de tool-result escrito em arquivo
  (`<truncation_notice>` com path).
- Title generator: modelo gera título da sessão (`title/generated`).

## 8. Orquestração lead/sidekick (prompts completos extraídos)

Dois prompts grandes definem uma orquestração de 2 agentes (não subagentes
efêmeros): **lead** (fala com o usuário, planeja, revisa) e **sidekick**
(worker persistente com memória entre handoffs). Pontos portáveis:

- Lead: delega implementação/verificação/setup por padrão; **retém** julgamento,
  planejamento, queries a sistemas compartilhados (o texto exato da query é do
  lead), trabalho "correctness-critical" (análise de dados/evals), UI renderizada
  complexa, conversa com o usuário e revisão de diff antes de "land".
- Anti-duplicação: resultados já derivados entram no handoff como insumos
  congelados; sidekick não re-deriva; briefs carregam **código, não prosa**
  (snippet exato + comandos verbatim + done-criteria).
- Dispatch bloqueante por padrão; `block:false` só com trabalho paralelo real;
  um brief consolidado > vários retrabalhos; blocker também é handoff.
- Mensagem do usuário em voo é do lead — decide steering/correção do handoff.
- Sidekick: plano do lead é autoridade; ambiguidades menores decide e anota;
  "verify once" (um passe consolidado no fim, comando mais estreito que cubra a
  mudança; não re-rodar check que passou sem mudança relevante); escala após
  2-3 tentativas ou blocker de ambiente/credencial com evidência; relatório
  deve "stand alone" (feito, evidência com paths, desvios, pendências).
- "Known failure patterns": premature implementation briefing; promover
  hipótese delegada a conclusão; verificar cedo demais/vezes demais; re-rodar
  checks por reassurance; handoff de ask ainda não resolvido.

## 9. Prompts auxiliares extraídos

- **Security classifier** (smart mode): JSON `{"decision":"allow|deny|ask",
  "reason"}`; seções com nonce — `<project_rules-{n}>`, `<conversation_context-
  {n}>`, `<already_permitted-{n}>`, `<action_to_classify-{n}>` — marcadas como
  dados não-confiáveis; allow só se "extremely confident"; compound commands
  exigem todas as partes seguras; vigia `GIT_PAGER=`-style env injection,
  redirects, flags que executam programas.
- **Hook evaluator**: JSON `{"decision":"approve|block|ask","reason",
  "add_context"}` ou `true/false`/`{"pass"}` para filtros.
- **Summarizer**: shape §7; **Trace summarizer**: resume execução p/ arquivo.
- **Title generator**: título curto da 1ª mensagem / lista de mensagens.
- **Review subagent**: correctness/style/security/performance/completeness,
  achados por severidade.
- **instant_context**: agente coletor com `restricted_exec` (subcomandos `rg`,
  `readfile`, `tree` schema-enforced, N cmds/turn, M turns), saída estrita em
  XML `<ANSWER><file path><range>a-b/></ANSWER>` — contexto inicial cirúrgico.
- **ACP/Devin Desktop/sandbox**: variações do system prompt por hospedeiro.

## 10. System prompt principal (template)

Estrutura: identidade → Modes (Code/Plan) → Style (objetividade, tom,
proatividade, ambiguidade, `<ref_file>/<ref_snippet>`) → Tool usage policy
(parallel calls, truncation→arquivo) → Programming (convenções, deps ≥7 dias,
estilo, debugging, workflow test-first, git commits/PRs) → Task Management
(todo_write, nunca dropar tasks em silêncio, hooks como input do usuário) →
Completing/Verification/Saving learned info/Error recovery → System
Guidance/IDE Metadata (tags injetadas) → Tool Tips → Safety → Destructive
Operations → MCP (list_tools antes de call_tool) → Available MCP Servers.

## 11. Skills, rules e plugins

- SKILL.md com `name/description/allowed-tools/permissions(allow,deny,ask)/
  triggers(user,model)/subagent|agent/model`; `allowed-tools` auto-aprova
  inline; subagent usa o profile do subagente.
- Rules: always-on context blobs (`devin rules list/show/paths`); triggered
  rules podem ser omitidas por token budget ("triggered but could not be
  injected due to token limits").
- Plugins: bundles de skills+rules+MCP+hooks com manifest, lockfile,
  governance (required/optional/forbidden), `plugins
  install/list/info/update/remove/prune` com GC por escopo.
- `config-importers`: migra MCP/rules/hooks/skills de claude, cursor,
  windsurf, opencode, zed, cognition, copilot, standard.

## 12. MCP

- Protocolo `2025-11-25` (+fallback `2024-11-05`): tools, resources, prompts,
  elicitation (`CreateMessageElicitation`, `InputRequiredResult`,
  `ElicitationSchema`), roots, subscriptions, progress, logging, icons.
- OAuth interativo (well-known probe → PKCE), session recovery (re-initialize
  + replay), `Mcp-Session-Id`, SSE+streamable HTTP, `mcp__<server>__<tool>`
  naming, registry servers, policy block, stderr_log.
- `mcp_mentions` — @-menções de recursos MCP no prompt.
- Regra no system prompt: **`mcp_list_tools` antes de `mcp_call_tool`**.
- OAuth em `~/.local/share/devin/mcp/oauth`.

## 13. Features menores observadas

`goal.rs` (objetivo da sessão), `revise.rs` (editar mensagem), `btw` (side
question forkada), `coding_agent_detector` (detecta configs de outros
agentes), `coding_agent` team_settings (policy enterprise), `worker`
(headless worker: api/bootstrap/cache/runner/update), `auto_setup`, `drs`
(declarative repo setup), `local_fusion` (fusão local/cloud), `handoff`
(local↔cloud flow), `share` (compartilhar sessão), `revert` (reverter nós da
árvore de mensagens), `edit` em plan só `.md`/diretórios de plano,
`powershell_parser`, `readonly_path_extractor`, `native_terminal_host`,
`login_shell_env`, `shell_edits/tracker` (detecta edições feitas via shell
p/ reconciliar estado), `paste_burst`, `side_chat_panel`, `confetti`.

## 14. Gap analysis → SPECs

Mapeamento dos achados para o estado atual do OpenWebUiCode (já possui:
~30 builtin tools, delegate_task com worktree/team/resume/ext-CLI,
`.agent.md` com frontmatter, PermissionRuleset + risk classifiers, plan mode
com plan_exit, ContextCompactionService, TerminalSessionManager PTY, LSP,
browser tools, automation **webhooks** (inbound), ACP/MCP projetos):

| # | Achado Devin | Status local | SPEC |
|---|--------------|--------------|------|
| 1 | Lifecycle hooks (8 eventos, command+prompt, updatedInput, additionalContext, hooks.v1.json) | só webhooks inbound | `SPEC-20261031-lifecycle-hooks` |
| 2 | Smart permission por juiz LLM (nonce tags, allow/deny/ask, already_permitted) | classificadores determinísticos | `SPEC-20261031-smart-permission-llm` |
| 3 | Lead/sidekick persistente + protocolo de handoff + profiles explore/general | delegate_task efêmero + .agent.md | `SPEC-20261031-sidekick-orchestration` |
| 4 | tool_search / deferred tool loading | todas as tools no request | `SPEC-20261031-tool-search-deferred` |
| 5 | Plan workflow 5 fases + write_plan + plans dir | plan_exit + ruleset (parcial) | `SPEC-20261031-plan-mode-v2` |
| 6 | Sandbox bwrap+seccomp + Autonomous + domain filter | inexistente | `SPEC-20261031-sandboxed-exec` |
| 7 | Pacote de prompts (delegation, verify-once, report contract, instant_context, unseen_messages, tool_result_footer, extend_context, stall_watch) | parcial disperso | `SPEC-20261031-agent-prompt-pack` |
| 8 | Compactor v2 (notas/msg, edited_files, handoff_history, async) | ContextCompactionService existe | backlog (fold no #7/#3) |
| 9 | Plugins/manifest/lockfile/governance | skills+rules via FS | backlog futuro |
| 10 | config-importers multi-ferramenta | skill scan cobre dirs | backlog futuro |
| 11 | message-forest (árvore/journal + revert) | chats lineares + branching? | backlog futuro |
| 12 | refusal-fallback de modelo | inexistente | backlog futuro |
