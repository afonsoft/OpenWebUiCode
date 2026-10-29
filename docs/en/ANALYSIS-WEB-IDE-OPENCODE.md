# Analysis: migrating opencode features → Open WebUI (.NET) — the path to a Web IDE

> Date: 2026-10-09 · Sources: `afonsoft/OpenWebUiCode` (main @ `5579e4e`), `afonsoft/opencode` (fork of `anomalyco/opencode`, branch `dev` @ `ecc4916`)
> Status: **analysis** — nothing here is approved implementation; draft SPECs in `.specs/` await the gate.

## 1. Goal and method

Assess what's missing for .NET Open WebUI to become an **agent-powered web IDE** — comparing the repo's AS-IS against what opencode offers (reference for coding-agent UX/architecture), and mapping what can be migrated/adapted to our stack (.NET 10 + Blazor WASM + EF Core SQLite, host execution).

"Did we implement opencode's features?" — **partially**. The `chat-agent-*` series (SPECs `docs/specs/SPEC-20261007-chat-agent-*.md`, delivered) already ported the agent core: file/shell/todo tools, risk approvals, workspace panel, subagent delegation. What's missing is the **IDE surface** (explorer, editor, tests, LSP) and **repo-context mechanisms** (skills→slash commands, AGENTS.md injection, plan/build modes, checkpoints).

## 2. What we already have (AS-IS, evidence)

| Capability | Status | Evidence |
| --- | --- | --- |
| Per-user workspace (`data/workspaces/{uid}`) | ✅ | `Infrastructure/Services/WorkspaceRepoService.cs:24` |
| GitHub repo binding (clone + branch switch, per-user PAT) | ✅ | `GitHubEndpoints.cs:20-29`, `WorkspaceRepoService.OpenAsync:53`, `RepoBindingPanel.razor` UI (chat + admin) |
| Workdir-jailed file tools | ✅ | `ChatTools/Tools/FileBuiltinTools.cs` — `file_list`, `file_read` (paged), `file_grep`, `file_glob`, `file_write`, `file_edit` with persisted unified diff |
| `shell_exec` with static risk classifier | ✅ | `ShellExecBuiltinTool.cs` → `ChatJobService`; `CommandRiskClassifier.cs` (Safe/WorkspaceWrite/Dangerous, fail-closed) |
| Code execution (python3/node) | ✅ | `CodeInterpreterBuiltinTool.cs` |
| Background jobs (spawn/list/output/kill) | ✅ | `ChatJobService`, `ChatJobEndpoints.cs`, Jobs panel tab |
| Run todos | ✅ | `TodoWriteBuiltinTool.cs`, Tasks tab with manual toggle |
| Ask-user questions | ✅ | `AskUserBuiltinTool.cs` + `QuestionPromptCard.razor` |
| Subagent (`delegate_task`, isolated child run) | ✅ | `DelegateTaskBuiltinTool.cs` |
| Risk approvals + permission card | ✅ | `ToolCallRiskClassifier.cs`, `PermissionPromptCard.razor`, `auto`/`smart` presets |
| Workspace panel (tasks/changes/jobs/terminal/mcps/info) | ✅ | `ChatWorkspacePanel.razor` — Changes tab renders real git diff |
| Real PTY terminal over WebSocket | ✅ | `TerminalPtyEndpoints.cs`, `TerminalSessionManager`, `TerminalView.razor` (`/terminal` page + panel tab; `terminal.enabled` flag) |
| Run pause/resume | ✅ | `ChatRunEndpoints.cs` (`runs/{id}/pause|resume`), `ChatRunPauses.cs` |
| User prompt slash commands | ✅ partial | `/workspace/prompts` + `GET /command/{command}` + `/` suggestions in composer (`ChatView.razor:130-137,341`) |
| web_search, fetch_url, browser_screenshot, n8n, media | ✅ | `ChatTools/Tools/*` |
| User MCP servers | ✅ | `McpClientService`, `McpEndpoints`, MCPs panel tab |

## 3. What opencode has that's missing here (reference TO-BE)

| opencode capability | Where in opencode | Missing in open-webui |
| --- | --- | --- |
| **File explorer/editor in UI** (Solid app: tree, tabs, diff review) | `packages/app` | ❌ — no workdir file surface; `/workspace/files` is the RAG upload library |
| **Repo skills → commands/agent** — `SKILL.md` discovery in `{skill,skills}/**`, `.claude/skills`, `.agents/skills`, `.opencode/skill*` + `skill` tool injecting the body | `src/skill/discovery.ts`, `src/tool/skill.ts` | ❌ — workspace has a skills catalog (`/workspace/skills`) but **does not read the bound repo** or expose skills as tool/command |
| **Custom slash commands** — markdown with frontmatter (agent, model, subtask, `$1..$N` hints) in `.opencode/command`, `.claude/commands`, MCP prompts; sources command/mcp/skill | `src/command/index.ts`, `command/template/` | ⚠️ partial — user prompts exist, no repo discovery, no `$N` args, no agent/model binding |
| **Project rules in system prompt** — AGENTS.md/CLAUDE.md/opencode.json auto-injected | `src/session/system.ts`, `session/prompt.ts` | ❌ — system prompt comes only from Model.SystemPrompt (`ModelEndpoints.cs:270`); nothing reads workdir AGENTS.md |
| **plan/build agent modes** — restricted `plan` agent (read-only) → `plan_exit` asks and promotes to build | `src/agent/agent.ts:141-157`, `src/tool/plan.ts` | ❌ — no mode concept; every run uses the same toolset |
| **Checkpoints/snapshot + revert** — git-hash workdir snapshot per turn; `revert` restores; diffFull between snapshots | `src/snapshot/index.ts`, `session/revert.ts` | ❌ — Changes shows diff, but no snapshot/revert |
| **Per-agent/tool/pattern permission rulesets** (allow/ask/deny wildcards; "always" answer memorized) | `src/permission/` (`evaluate.ts` wildcard match) | ⚠️ partial — `ToolCallRiskClassifier` is global static; no per-agent rulesets, no session-persisted "always" |
| **`apply_patch`** (structured multi-file patch) + post-edit **format hook** | `src/tool/apply_patch.ts`, `src/format/` | ⚠️ — `file_edit` is search/replace; no multi-file patch or auto-formatter |
| **LSP** — documentSymbol/workspaceSymbol/references/hover/diagnostics as tools + for the editor | `src/lsp/`, `src/tool/lsp.ts` | ❌ |
| **Worktree isolation** (session runs in a separate `git worktree`) | `src/worktree/` | ❌ — single workdir per user |
| **`@file` mentions / explicit context** in composer | `packages/app` (mention picker) | ❌ — composer only has `/prompts` |
| **code-mode** (structured output via code) | `src/tool/code-mode.ts` | ❌ (optional) |
| Dedicated **test runner** | via `shell`/task (no dedicated `test` tool — flow is prompt + terminal) | ⚠️ same — but the IDE needs "run tests" UX (job template + output parsing) |
| Session fork/share/compaction/auto-title | `session/summary.ts`, `share/` | ⚠️ share exists as public chat link (`/s/{id}`); no fork/compaction |

## 4. Gap matrix (verdicts)

Legend: `CONFIRMADO` = real actionable difference · `DUPLICADO` = already covered · `REJEITADO` = not applicable.

| # | Gap | Verdict | Note |
| --- | --- | --- | --- |
| GAP-impl-ide-surface | Web IDE: explorer + editor + tabs + diff + integrated terminal | **CONFIRMED** | No editing surface exists; everything is a read-only run panel. The core "WEB IDE" block. |
| GAP-impl-workspace-file-api | Workdir file REST API (tree/read/write/mkdir/delete/rename) serving the UI | **CONFIRMED** | `WorkspaceFiles.ResolveInside` already jails; endpoints are missing (only agent tools use it). |
| GAP-impl-repo-skills | Read `SKILL.md`/commands from bound repo → slash commands + `skill` tool | **CONFIRMED** | Explicit user ask ("read repo skills and create slash commands"). |
| GAP-impl-project-instructions | Inject workdir `AGENTS.md`/`CLAUDE.md`/`README` into run system prompt | **CONFIRMED** | No reference in code (`grep AGENTS` → only model SystemPrompt). |
| GAP-impl-agent-modes | plan/build modes + `plan_exit` tool + per-mode ruleset | **CONFIRMED** | Big coding-UX lever; builds on existing permission infra. |
| GAP-impl-checkpoints | Git snapshot per turn + `revert`/`restore` + inter-checkpoint diff | **CONFIRMED** | Real "undo" — practical requirement for free-form agent edits. |
| GAP-impl-at-mentions | `@file` composer mentions injecting content into context | **CONFIRMED** | Cheap: reuse the `/` suggestion flow. |
| GAP-impl-test-runner | Test UX: repo-detected command + results tab/log in panel | **CONFIRMED** | Today it runs opaquely via `shell_exec`; an IDE needs "Run tests" with parsed output. |
| GAP-impl-lsp | LSP (diagnostics, hover, symbols, go-to-def) | **CONFIRMED** (later phase) | Highest cost; per-language server spawn + JSON-RPC. Prerequisite for editor squiggles. |
| GAP-impl-worktree | Per-run/chat worktree isolation | **CONFIRMED** (optional) | Only needed for parallel runs on the same repo. |
| GAP-impl-apply-patch | `apply_patch` multi-file + format hook | **CONFIRMED** (low priority) | `file_edit` covers the common case; patch improves large-edit robustness. |
| GAP-arch-permission-rulesets | Persisted per-agent+pattern allow/ask/deny rulesets | **DUPLICATED-partial** | Static classifier exists; evolving to rulesets fits the modes spec as an RF — no separate spec. |
| GAP-impl-code-mode | `code-mode` tool | **REJECTED** | Marginal; StructuredOutput already exists in the pipeline. Revisit if asked. |
| GAP-impl-share-sync | opencode-style share/sync server | **REJECTED** | open-webui already has public chat sharing (`/s/{id}`); multi-device sync isn't IDE scope. |
| GAP-impl-tui-acp | TUI, ACP protocol, desktop bridge | **REJECTED** | Out of web scope. |
| GAP-sec-sandbox | Execution sandbox (container/VM per workspace) | **CONFIRMED as pending decision** `[TBD]` | Prior SPEC chose host-exec + approvals; for a public/multi-user IDE this becomes the main risk. Not a feature gap — an architecture decision to record. |

## 5. Migration proposal — suggested slices

Ordered by value/cost. Each slice = 1 draft SPEC (`.specs/`) = 1 branch/PR (repo convention).

| Slice | Content | Depends on | Effort | Impact |
| --- | --- | --- | --- | --- |
| **S1 — Workspace File API** | Endpoints `GET /workspace/repo/tree`, `GET/PUT /workspace/repo/file`, `POST mkdir/delete/rename` — all jailed via `WorkspaceFiles.ResolveInside`; pagination, byte cap, binary guard (reuses `file_*` rules). Auth: workspace owner. | — | S | Unblocks S2/S3 |
| **S2 — IDE surface (`/ide`)** | Dedicated page: lazy file tree, **CodeMirror 6** editor via JS interop (light, no worker build — Monaco is ~3MB + worker setup), tabs, git status (reuses `WorkspaceGitService`), embedded terminal (`TerminalView`), run/jobs panel, chat split view. Read-only when no repo bound. | S1 | L | The visible "IDE" |
| **S3 — Repo skills & slash commands** | `SkillDiscoveryService` scans workdir: `**/SKILL.md` under `{skill,skills}/`, `.claude/skills/`, `.agents/skills/`, `.devin/skills/`, `.opencode/skill*/`; frontmatter `name`/`description` → `/{name}` commands in the composer (merged with user prompts; source tagged `skill`/`command`/`prompt`); skill body injected as run instruction + `$1..$N` support; new `skill {name}` tool; cached scan invalidated on file-change/checkout. | S1 | M | Explicit ask |
| **S4 — Project instructions in context** | System prompt assembly concatenates (in order): `AGENTS.md`, `CLAUDE.md`, `.cursor/rules/*.md`(?), summarized `README`. Bounded (e.g. 32KB, repo→user precedence). | S1 | S | Agent quality |
| **S5 — Agent modes (plan/build)** | `mode` on Chat/chat-preset; `plan` disables write/shell-write via ruleset; `plan_exit` tool (writes plan to `.openwebui/plans/<run>.md` in workdir + `ask_user` confirm); UI: composer toggle + mode chip. Extends `ToolCallRiskClassifier` → per-mode `PermissionRuleset`. | — | M | Real agent workflow |
| **S6 — Checkpoints & revert** | Per-turn workdir snapshot via git shadow index (opencode pattern: hidden ref commits / `git stash create`); `checkpoint` SSE event; per-turn Revert button + Changes diff between checkpoints. | S1 | M-L | Trust for free edits |
| **S7 — `@file` mentions + test runner UX** | `@` in composer → repo path autocomplete (tree API) injecting a bounded excerpt into run context; "Run tests" detects command by manifest (package.json→`npm test`, *.slnx→`dotnet test`, pyproject→`pytest`) → ChatJob with output parser → ✓/✗ badge + log tab. | S1, S2 | M | Completes code→test loop |
| **S8 — LSP (phase 2)** | `LspService` spawns a language server per workspace (omnisharp/csharp-ls, typescript-language-server, pylsp) over stdio JSON-RPC; `lsp_diagnostics/hover/definition/references` tools; editor consumes diagnostics/hover. | S2 | L | Editor intelligence |
| **S9 — Per-run worktree + formatter hooks** (optional) | Ephemeral worktree per run + merge via Changes; post-write hook (`dotnet format`, `prettier`) configurable. | S5/S6 | M | Safe parallelism |

### What NOT to migrate
- **opencode's TUI/ACP/desktop/share-sync/enterprise multi-tenancy** — out of scope.
- **Literal TS port** — the reference is architectural/contractual (tools, events, modes); implementation is native .NET on the existing `ChatRun`/`ChatJob`/SSE pipeline.
- **`code-mode`, session fork, auto-compaction** — deferred; compaction can come later with real context limits.

## 6. Risks and `[TBD]` decisions

1. **Sandbox (biggest risk):** a web IDE where the agent runs shell on the host with user credentials → decide container-per-workspace (Docker) vs keep host+approvals. Recommend an ADR. It doesn't block S1–S7 (approvals exist) but blocks real multi-user use.
2. **Editor:** CodeMirror 6 (recommended — small, no build worker) vs Monaco (VS Code parity, heavy). `[TBD]` — default CM6.
3. **Skill paths inside the repo:** recursive `**/SKILL.md` or known dirs only? opencode does both (`SKILL_PATTERN` + external dirs). Default: both, capped at 50 skills.
4. **Test execution can be destructive** (tests write files): goes through the existing classifier; "Run tests" runs as classified `shell_exec` → user sees the command before approving.
5. **WASM + large file editing:** paged tree/read; byte-capped editor (reuse `MaxFileBytes`) + virtualization.

## 7. Generated draft SPECs

| SPEC | Slice |
| --- | --- |
| `.specs/SPEC-20261009-workspace-file-api.md` | S1 |
| `.specs/SPEC-20261009-web-ide-surface.md` | S2 |
| `.specs/SPEC-20261009-repo-skills-slash-commands.md` | S3 + S4 |
| `.specs/SPEC-20261009-agent-modes-plan-build.md` | S5 |
| `.specs/SPEC-20261009-checkpoints-revert.md` | S6 |
| `.specs/SPEC-20261009-ide-mentions-tests.md` | S7 |
| `.specs/SPEC-20261009-lsp-diagnostics.md` | S8 |
| `.specs/SPEC-20261009-worktree-format-hooks.md` | S9 |

Suggested dependency order: **S1 → S2 → (S3+S4, S5) → S6 → S7 → S8/S9**.

---
*Consolidated report also at `.claude/memory/gap-analysis-20261009.md` (resume state).*
