# Comparative analysis — Open WebUI (.NET) vs Devin web app

**Date:** 2026-10-09 · **Author:** Devin · **Epic:** E16 (Web IDE) — companion analysis
**Goal:** compare Devin's web app (`app.devin.ai`) with our fork and map what is worth migrating — same format as `ANALYSIS-WEB-IDE-OPENCODE.md`. Devin is closed-source: the "Devin" column is feature-level (public UI + docs.devin.ai); the "ours" column is evidence-backed by file.

## 1. What the Devin web app has

| Area | Devin feature |
|---|---|
| Session inbox | Session list with search, status (running/waiting/done), tags, billing tags, bulk archive |
| Session timeline | Messages + expandable tool calls, per-step diffs, attachments, structured output, playback |
| Desktop/VM | Live "Desktop" tab of the agent's VM; port preview (an app on the VM becomes browsable) |
| Devin Review | Automated PR review: severity-tagged findings on the PR page + merge-box status |
| PRs/CI | A session surfaces the PRs it created plus check state |
| Secrets | Per-user/org secret vault (write-only, masked) consumable by sessions |
| Automations | Triggers (schedule, Slack, GitHub) → sessions; reusable playbooks |
| Boards | Ticket kanban consumed by sessions |
| Usage | Consumption dashboard (ACUs) per user/tag |
| Notifications | In-app feed + push |

## 2. What we already have (evidence)

| Area | Our state | Evidence |
|---|---|---|
| Chat list/search | Sidebar with search, folders, archived page | `Sidebar.razor:83`, `Pages/Archived.razor` |
| Timeline | Messages + `ToolCallCard` + permission/question cards + diffs | `ChatView.razor`, `ToolCallCard.razor` |
| Terminal | Real PTY + jobs + dedicated page | `TerminalPtyEndpoints.cs`, `TerminalPage.razor` |
| Web IDE | `/ide`: explorer + editor + diff + terminal + jobs | `Pages/Ide.razor` (S1+S2 merged) |
| Automations | Entity + endpoints + `/automations` page | `AutomationEndpoints.cs`, `Pages/Automations.razor` |
| Notifications | WebPush + per-user preferences | `NotificationEndpoints.cs`, SettingsModal notifications tab |
| Analytics | Admin analytics endpoints | `AnalyticsEndpoints.cs` |
| Sharing | Public chat links | `Pages/SharedChat.razor` |
| Repo/GitHub | Token, binding, clone, branch, git status/diff | `GitHubEndpoints.cs`, `WorkspaceRepoService.cs` |
| Subagent | `delegate_task` spawns a child run | `DelegateTaskBuiltinTool.cs` |

## 3. Items evaluated

| # | Item (Devin) | Status | Comment |
|---|---|---|---|
| D1 | **PR + CI status panel in chat/repo** | ✅ confirmed | GitHub already integrated; only the surface is missing. Effort M. |
| D2 | **"Waiting on you" inbox** | ✅ confirmed | Filter chats whose run is blocked on approval/question — the state already exists. Effort M. |
| D3 | **In-app notification feed** | ✅ confirmed | WebPush+prefs exist, no in-app feed; endpoint already there. Effort M. |
| D4 | **Port preview** (terminal app in an iframe/URL) | ✅ confirmed | Proxy `/preview/{port}` → `localhost:{port}` on the API host; iframe in the IDE. Effort L, high value for the web IDE. |
| D5 | **Per-user secrets vault (write-only UI)** | 🔶 candidate | User API keys (`sk-*`) exist; a generic secret vault for tools/runs is new. Effort M. |
| D6 | **Child-run tree (delegate_task)** | 🔶 candidate | `delegate_task` exists; the timeline doesn't show the child run as a navigable entity. Effort M. |
| D7 | **"My usage" — per-user analytics** | 🔶 candidate | `AnalyticsEndpoints` is admin-facing; personal usage card is a small gap. Effort S. |
| D8 | Desktop/VM live view | ❌ rejected | No per-session remote VM; the terminal runs on the API host. No architectural equivalent. |
| D9 | Environment blueprints/snapshots | ❌ rejected | Devin-infra concept; a self-hosted app doesn't provision per-session environments. |
| D10 | Boards kanban | 🔶 big candidate | New product surface; evaluate in its own Epic if requested. |
| D11 | Billing/ACU | ❌ rejected | No compute-billing model. |
| D12 | Automated Devin Review | 🔶 big candidate | Equivalent to a review product of our own (skills already cover QA/review); revisit after S5–S9. |

## 4. Draft SPECs generated (suggested order)

| Spec | Title | Size | Depends on |
|---|---|---|---|
| `SPEC-20261009-pr-ci-panel` | D1 — PR + checks panel in workspace/IDE | M | S1 |
| `SPEC-20261009-attention-inbox` | D2 — "waiting on you" filter + sidebar badge | M | — |
| `SPEC-20261009-notification-feed` | D3 — in-app feed (bell) over NotificationService | M | — |
| `SPEC-20261009-port-preview` | D4 — `/preview/{port}` proxy + IDE iframe | L | S1/S2 |

D5–D7 stay documented as candidates (no SPEC) until prioritized; D10/D12 need a product decision.

## 5. Where they fit in the E16 queue

Approved order: S1 ✅ → S2 ✅ → **S3+S4 ✅ (PR #248)** → S5 (in progress) → S6 → S7 → S8/S9.
Recommendation: the D-specs land **after S7** (checkpoints and @mentions change the chat/IDE surfaces they consume). D1 and D4 have the highest value for the "web IDE" goal; D2/D3 improve the daily usage loop.

## 6. Addendum 2026-11-04 — docs.devin.ai deep-read (Customize / Skills / Memory / Insights / DeepWiki)

Second pass driven by the product guides (skills, memory, session-insights, knowledge-onboarding, deepwiki-mcp). New findings:

| # | Item (Devin) | Status | Comment |
|---|---|---|---|
| D13 | **Customize page** (Personal/Org scopes: Skills, Knowledge, Memory) | ✅ confirmed → SPECs | Our equivalents exist scattered (skills DB+repo, MCP admin+user, no knowledge-notes). `SPEC-20261104-customize-hub` unifies at `/customize`. |
| D14 | **`!` skills trigger in composer** | ✅ confirmed → SPEC | `@` files+refs already shipped; `!` dedicated skills dropdown is greenfield. `SPEC-20261104-chat-bang-trigger`. |
| D15 | **Session Insights** (size XS–XL, category, issue timeline, LLM improved-prompt/action-items, knowledge usage) | ✅ confirmed → SPEC | Data layer complete (`RunUsageLedger`, `/usage/*`, `/analytics`); UI is the gap. `SPEC-20261104-session-insights-v2`. |
| D16 | **Devin Knowledge = deprecated** | ⚠️ noteworthy | Upstream migrates Knowledge → Skills-in-Plugins. We still ship Devin-style notes (context injection) but keep the concept thin — heavy lifting stays in Skills. |
| D17 | **DeepWiki MCP** (`https://mcp.deepwiki.com/mcp`, tools: `read_wiki_structure`/`read_wiki_contents`/`ask_question`) | ✅ adopted | `.mcp.json` added at repo root; free/remote/no-auth. Repo `afonsoft/OpenWebUiCode` needs indexing at deepwiki.com (public repos only). |
| D18 | Devin Memory (MEMORY.md git drive + Dreaming) | 🔶 candidate | We have `AgentMemory` (builtin memory_save/search, global/repo scope) — close enough; Dreaming-style consolidation is a possible follow-up SPEC. |
