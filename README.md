# Open-WebUI+Code — .NET (Coding Agent Edition)

A migration of [Open WebUI](https://github.com/open-webui/open-webui) to
**.NET 10 · C# 14 · Blazor WebAssembly** (SvelteKit + FastAPI → ASP.NET Core + Blazor),
faithful to the original layout and configuration — **extended into a full coding-agent
platform** in the spirit of [opencode](https://github.com/sst/opencode): repo-bound
chats running on dedicated git worktrees, sub-agents in parallel, mid-run steering,
durable memory, scheduled routines, a VS Code-style IDE and a worktree review
pipeline.

[![CI Build & Test](https://github.com/afonsoft/open-webui/actions/workflows/ci-build-test.yml/badge.svg)](https://github.com/afonsoft/open-webui/actions/workflows/ci-build-test.yml)
[![Code Quality](https://github.com/afonsoft/open-webui/actions/workflows/code-quality.yml/badge.svg)](https://github.com/afonsoft/open-webui/actions/workflows/code-quality.yml)
[![Security Scan](https://github.com/afonsoft/open-webui/actions/workflows/security-scan.yml/badge.svg)](https://github.com/afonsoft/open-webui/actions/workflows/security-scan.yml)
[![Accessibility Audit](https://github.com/afonsoft/open-webui/actions/workflows/a11y-audit.yml/badge.svg)](https://github.com/afonsoft/open-webui/actions/workflows/a11y-audit.yml)
[![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=afonsoft_open-webui&metric=alert_status)](https://sonarcloud.io/project/overview?id=afonsoft_open-webui)

> **Versão em português:** [README.pt-BR.md](README.pt-BR.md)

## Open WebUI (original) vs opencode vs oh-my-pi vs this fork

The agent roadmap was benchmarked against [opencode](https://github.com/sst/opencode)
(incl. the [alltomatos/opencode](https://github.com/alltomatos/opencode) fork —
see [the full analysis](docs/analise-fork-alltomatos-opencode.md)) and
[oh-my-pi](https://github.com/can1357/oh-my-pi), whose session-tree, advisor and
context-tooling design shaped several features below.

| Capability | Open WebUI (FastAPI/Svelte) | opencode | oh-my-pi | **This fork (.NET/Blazor)** |
|---|---|---|---|---|
| Chat + streaming, multi-provider | ✅ Ollama/OpenAI/RAG/pipelines | ✅ any LLM via gateway | ✅ multi-provider (TUI) | ✅ parity + OmniRoute-friendly |
| Repo/workspace bound to a session | ❌ single workspace | ✅ per-project | ✅ per-project | ✅ **per-chat git worktree** — strict scope, global never leaks |
| Sub-agents / parallel sessions | ❌ | ✅ task tool + sessions | ✅ declarative `.md` task agents | ✅ `delegate_task` async (procedural **and** `*.agent.md` definitions), depth ≤3, worktree-isolated |
| Review before merge | ❌ | ✅ orchestrator reviews diffs | ❌ | ✅ `worktree_diff`/`worktree_merge` + auto-merge opt-in |
| Steer/queue mid-run input | ❌ | ✅ durable steer/queue | ❌ | ✅ `POST …/runs/{id}/steer` (steer + queue modes) |
| Durable agent memory | ❌ | ✅ memory tools + auto-sync | ✅ memory tools | ✅ `memory_save`/`memory_search` + 6h auto-distill into `auto:*` memories |
| Routines / reminders | ❌ | ✅ routine tool + scheduler | ❌ | ✅ `routine`/`reminder` tools + `once` schedules |
| Task list tool | ❌ | ✅ todo write | ✅ | ✅ `todo_write` with stable ids |
| Context compaction | ❌ | ✅ auto-summarize | ✅ auto + on-demand, pruning | ✅ LLM summary + mechanical pruning + structured digest + `history://` recovery |
| Parent/child run tree UI | ❌ | ✅ session tree | ✅ session tree | ✅ sidebar tree + "delegado" badge + parent markers |
| Parallel runs console | ❌ | ✅ | ✅ | ✅ sidebar "Active runs" (`GET /chats/runs`) |
| Runs survive browser close | partial | ✅ | ✅ | ✅ server-side runs + `Last-Event-ID` resume |
| **Session branching** (fork any turn) | ❌ | ❌ | ✅ `parentId` entries + `/fork` | ✅ `ParentId`/`LeafMessageId` tree + "branch from here" + branch summary |
| **Model roles + fallback chains** | ❌ single model | partial (small model for titles) | ✅ `modelRoles` + `retry.fallbackChains` | ✅ `tasks`/`memory`/`advisor` roles + capability map (vision) + per-role fallback |
| **Tool approval tiers** | binary approve-all | ✅ permission model | ✅ tier + per-tool policy + user override | ✅ `Read`/`Write`/`Exec` tiers, fail-closed default, per-tool overrides |
| **Advisor / watchdog** | ❌ | ❌ | ✅ read-only second-opinion model | ✅ advisor role inspects runs + injects guidance, no approval power |
| **Agent-directed context** | ❌ | partial | ✅ `checkpoint`/`rewind`, `context_notes`, `new_context` | ✅ same trio: checkpoint/rewind pairs, per-branch notebook, on-request rollover |
| **Layered config + provenance** | env + admin UI | partial | ✅ env>runtime>overlay>project>global | ✅ global/admin/user layers with source tracking |
| **Declarative agent definitions** | ❌ | ✅ AgentUI | ✅ `.md` + frontmatter | ✅ `*.agent.md` (tools, model, spawns) discovered per repo, spawnable via `delegate_task` |
| **Plan mode → structured output** | ❌ | ✅ plan/build modes | ❌ | ✅ `plan` mode + `/plan` slash: requirement analysis → **SPEC SDD** markdown (metadata, user story, RF-###, BDD criteria, task plan); `plan_exit` validates and persists to `.openwebui/plans/` |
| **Media attachments in chat** | ✅ uploads | partial | ❌ | ✅ image/video thumbnails in composer, inline render + click-to-enlarge, vision-model analysis of attached images |

## Screenshots

| Delegated sub-agents in parallel | Session tree + completion markers |
|---|---|
| ![delegate_task spawning two child runs](docs/screenshots/delegate-parallel.png) | ![parent markers + subtask tree](docs/screenshots/parent-notify.png) |

| Active-runs console | Sub-session with parent link + approval |
|---|---|
| ![sidebar active runs](docs/screenshots/runs-parallel.png) | ![child chat approval card](docs/screenshots/subsession-approval.png) |

| Per-chat repo binding | Routines & reminders |
|---|---|
| ![workspace repo picker](docs/screenshots/repo-binding-picker.png) | ![automations page](docs/screenshots/automations.png) |

| Chat (light/dark) | Workspace panel | Terminal PTY |
|---|---|---|
| ![chat](docs/screenshots/chat-light.png) | ![workspace](docs/screenshots/workspace-panel.png) | ![terminal](docs/screenshots/terminal.png) |

## Agent features (new — additive layer over upstream parity)

- **`delegate_task`** — spawns a child chat + server-side run: `wait:false` returns
  `childRunId` immediately, `wait:true` (default) blocks up to 300s; `repo` and
  `persona` (a workspace Skill as system prompt) params; depth ≤ 3; each sub-agent
  inherits the parent repo but works in its **own chat-scoped worktree**.
- **Per-chat repo worktrees** — binding a chat to `owner/repo` creates an exclusive
  worktree (`workspaces/{user}/worktrees/{owner}__{repo}/{chatId}`, branch
  `chat/{chatId}`) over a shared base checkout; resolution is strict per chat —
  a new conversation never inherits your global binding. Unbind/archive/delete
  releases the worktree; the base checkout leaves disk when unreferenced, and a
  diverged base self-heals (diverged HEAD preserved as `backup/*`).
- **Three agent modes** — composer selector `chat` / `plan` / `build`: `chat` is
  repo-free (workspace tools denied), `build` requires a bound repo (`PATCH` 400
  otherwise; unbind drops `build`→`chat`; `plan_exit` only promotes with binding).
  Workspace gate covers `delegate_task`, `lsp_*` and `skill`; the format hook
  honors the chat binding's `FormatCommand`.
- **Autonomous decisions** — `smart`/`auto`/`always-allow` presets auto-answer
  `ask_user` prompts that carry options (recommended option, else first) without
  pausing the run; every auto choice emits an auditable `decision_auto` event.
- **Mobile workspace** — touch targets and collapsing chips keep the workspace
  panel, repo picker and IDE reachable on small screens.
- **`run_result` / `worktree_diff` / `worktree_merge`** — collect async results,
  review the child diff, merge explicitly (or `auto_merge` opt-in).
- **Steer & queue** — `POST /api/v1/chats/{id}/runs/{runId}/steer` injects a user
  message into a live run (`steer` = next turn boundary, `queue` = when idle).
- **Agent memory** — `memory_save`/`memory_search` (project + global scopes),
  auto-injected `<agent_memory>` block; 6h scheduler distills durable facts into
  `auto:*` memories with per-user watermark + opt-out.
- **Routines & reminders** — `routine` tool (list/create/update/delete/run_now,
  `once`/`interval`/`daily`/`weekly`) and `reminder` (fires an
  `automation.reminder` notification) over the built-in Automations engine.
- **Context compaction** — when history grows, the pipeline summarizes old turns
  through the configured model and keeps a cutoff kv per chat.
- **Plan mode → SPEC SDD** — `plan` mode (or the `/plan` slash builtin) analyses
  the requirement read-only, then emits a full SPEC SDD markdown: metadata,
  user story, scope, `RF-###` requirements, BDD acceptance criteria, task plan,
  DoD and open questions. `plan_exit` validates the structure (incomplete plans
  are refused with the missing sections), persists to `.openwebui/plans/` and
  only promotes to `build` on user approval.
- **Model roles + fallback chains** — dedicated models for `tasks` (titles,
  follow-ups, tags, compaction), `memory` (distillation) and `advisor`; each
  role resolves through a capability map (e.g. vision) with an ordered fallback
  chain, configurable per layer.
- **Fine-grained tool approval tiers** — every tool declares `Read`/`Write`/
  `Exec` (fail-closed `Exec`); per-tool user overrides can relax but never
  weaken a tool-level deny.
- **Run advisor (watchdog)** — a secondary, read-only model reviews the live
  transcript, can inspect the workspace with its own read tools and injects
  guidance mid-run — with no approval or mutation power.
- **Agent-directed context** — `checkpoint`/`rewind` tool pair (mark → explore
  → collapse), `context_notes` (per-branch notebook that survives rollover) and
  `new_context` (on-request compaction); mechanical pruning elides superseded
  tool results and `history://` URIs recover full pre-compaction turns.
- **Chat branching** — messages form a tree (`ParentId`/`LeafMessageId`);
  "branch from here" forks any turn into a sibling chat, with a branch summary
  for the abandoned path.
- **Declarative agents** — `*.agent.md` files (frontmatter: tools, model,
  spawns) discovered in the repo define named sub-agents, spawnable through
  `delegate_task(agent: "…")`.
- **Layered configuration** — effective settings resolve through
  global → admin → user layers with per-key provenance.
- **Memory pipeline** — scheduled consolidation distills session outcomes into
  the durable memory layers that are re-injected into new runs.
- **Chat media attachments** — image/video thumbnails inside the composer,
  acceptable inline size after send, click-to-enlarge; attached images are
  always analyzed together with the prompt by a vision-capable model.
- **Retention** — daily purge (automation runs >90d, notifications >60d, steers of
  terminal runs >30d) + worktree orphan prune + WAL checkpoint + weekly VACUUM.
- **Chat hierarchy** — `ParentChatId`/`ParentRunId`, `GET /chats/{id}/children`,
  nested sidebar tree, completion markers posted back to the parent chat.

Full details per feature (with screenshots): [docs/en/AGENT-FEATURES.md](docs/en/AGENT-FEATURES.md) · [docs/pt/RECURSOS-AGENTE.md](docs/pt/RECURSOS-AGENTE.md)

## Classic features (upstream parity)

- **Chat** with SSE streaming (Ollama / OpenAI-compat), attachments, 👍/👎,
  editing, regeneration, LLM-generated title/follow-ups/tags
- **Decoupled runs** — responses execute server-side; closing/reloading the tab
  does not interrupt; on reopen the client reattaches with `Last-Event-ID` replay
- **Notifications** — in-app toasts, Notification API and Web Push (VAPID)
- **Tool streaming + approval gate** — `tool_call`/`tool_result` on SSE, approval
  presets per chat (readonly/approve/always/**auto by risk** LOW-MED-HIGH), deny
  with instructions for the model to correct course
- **Builtin tools** — `shell_exec` + background jobs, `file_*` (list/read/grep/
  glob/write/edit with diff), `code_interpreter`, `fetch_url`, `web_search`,
  `generate_image`/`generate_video`, `browser_screenshot`, `ask_user`,
  `n8n_list_workflows`/`n8n_trigger`, `todo_write`, `skill`, `lsp_*`, `plan_exit`
- **Workspace panel** — Tasks, Changes (git diff), Jobs, MCPs, Terminal (PTY) and
  run Info; draggable resizer; `/ide` full-screen view
- **Chat/Plan/Build agent modes** + permission modes; **checkpoints & revert**;
  **pause/resume** runs; slash commands from repo skills
- **RAG, Knowledge, Arena, Channels/Notes/Calendar, Admin (users, connections,
  MCP servers, integrations), i18n in 8 locales, PWA offline shell**

## Quickstart

```bash
dotnet restore OpenWebUI.slnx
dotnet build OpenWebUI.slnx --configuration Release
dotnet run --project src/OpenWebUI.Api   # http://localhost:8080
```

Docker (`:3032 → :8080`):

```bash
docker compose up -d --build
# or the published image
docker run -p 3032:8080 ghcr.io/afonsoft/open-webui:latest
```

Full Docker guide: [docs/en/DEPLOY-DOCKER.md](docs/en/DEPLOY-DOCKER.md) ·
[docs/pt/DEPLOY-DOCKER.md](docs/pt/DEPLOY-DOCKER.md)

## Tests

```bash
dotnet test OpenWebUI.slnx   # 1700+ API tests + client tests (NUnit)
```

Coverage gate via Coverlet + ratchet (`.ci/coverage-baseline.txt` only goes up).

## Quality & CI

| Workflow | Covers |
|---|---|
| `ci-build-test.yml` | Release build, all tests + coverage gate, WASM payload, tailwind freshness, Docker build, baseline ratchet |
| `a11y-audit.yml` | axe-core (Playwright) mobile+desktop |
| `code-quality.yml` | Qodana + [SonarCloud](https://sonarcloud.io/project/overview?id=afonsoft_open-webui) |
| `security-scan.yml` | CodeQL (C#+JS), Trivy, GitGuardian, Snyk |
| `release.yml` | Tag `vX.Y.Z` → GHCR + Docker Hub image |

## Documentation

- [docs/en/AGENT-FEATURES.md](docs/en/AGENT-FEATURES.md) — agent platform details (EN)
- [docs/pt/RECURSOS-AGENTE.md](docs/pt/RECURSOS-AGENTE.md) — detalhes do modo agente (PT-BR)
- [docs/MIGRACAO-DOTNET.md](docs/MIGRACAO-DOTNET.md) — upstream → .NET parity map
- [docs/architecture/architecture.md](docs/architecture/architecture.md) — architecture (Mermaid)
- [docs/analise-fork-alltomatos-opencode.md](docs/analise-fork-alltomatos-opencode.md) — opencode-fork analysis that drove the agent roadmap
- `.specs/` — in-flight SPECs; `docs/specs/` — delivered SPECs
- [CHANGELOG.md](CHANGELOG.md)

## Star History

[![Star History Chart](https://api.star-history.com/chart?repos=afonsoft/open-webui&type=date&legend=bottom-right)](https://www.star-history.com/?repos=afonsoft%2Fopen-webui&type=date&legend=bottom-right)

## License

BSD-3-Clause — see [LICENSE](LICENSE).
