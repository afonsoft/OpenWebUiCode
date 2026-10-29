# SPEC-20261010-chat-activity-feed

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-activity-feed` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261010-chat-activity-feed` |
| Ticket | `[A DEFINIR — create-issues after approval]` |
| Status | `Completed` |
| Priority | `high` |
| Depends on | — (SPEC-20261007-chat-tool-streaming, SPEC-20261007-chat-agent-ux delivered) |

## 1. User Story

**As a** user watching the assistant work **I want** a simple, friendly feed of what it's doing — "Pensando…", "Pesquisando na web…", "Editando arquivo…" — instead of raw tool calls **So that** the chat feels fluid like Devin web: it keeps processing while telling me, in plain language, what it's doing.

**Problem context:** during a run, `ChatView` renders `ToolCallCard`s per `tool_call` SSE event — monospace tool name (`builtin_shell_exec`), sanitized JSON args preview, ok/error pills. That's a developer view. The ask: don't expose which tools are being called by default; while the model "thinks", show it *using* tools and doing things in simple lines, informing progress continuously, Devin-web style.

## 2. Scope

**In scope:**

- Live **activity feed** inside the assistant turn while a run streams: one friendly line per `tool_call` (icon + pt/en label + optional short target like a path or query), spinner while running → check on success, ✕ on error, amber mark on denial. No tool name or JSON visible by default.
- **Work log after completion:** once the run finishes, the feed collapses to a compact "N passos" summary row that expands back to the full lines — and each line still expands to the existing rich `ToolCallCard` details (args/result/shell output/images) for power users.
- **Friendly phase labels:** `RunPhaseEvent` phases get plain copy — `generating` → "Pensando…" before first delta / "Escrevendo resposta…" while streaming text, `running_tool` → the current activity line, `awaiting_approval` → "Aguardando sua aprovação". A thinking indicator shows immediately after send, before the first token.
- Historical rendering: persisted `ToolCallsJson`/`tool` messages render the same collapsed work log in old chats (derived client-side — no new persistence).
- i18n keys in all 8 locales; regenerated `tailwind.css`; mobile keeps the compact inline variant.

**Out of scope:**

- Server-side changes to the SSE contract (reuse `tool_call`/`tool_result`/`status` events as-is).
- Changing which tools run, approval flow, or the risk classifier.
- Token-streaming performance work (SSE deltas already stream; no re-batching).
- Plan/agent-mode badges — untouched.

## 3. Technical Context

**Where the change happens:** `ChatView.razor` swaps `_liveToolCalls` rendering for the feed; a new `Components/RunActivityFeed.razor` owns the list; a client-side mapper `ToolActivityMap` translates tool names → i18n keys + icons + optional target extraction from `ArgsPreview`; persisted tool messages reuse the feed collapsed.

**Files to read before implementing:**

- `CLAUDE.md` · `.claude/rules/global-rules.md`
- `src/OpenWebUI.Client/Components/ChatView.razor` (`_liveToolCalls`, `_statusLabel`, message loop)
- `src/OpenWebUI.Client/Components/ToolCallCard.razor` (rich detail body to keep)
- `src/OpenWebUI.Client/Services/ChatStreamService.cs` (`ChatStreamEvent` types)
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` (`PublishToolCallAsync`, `RunPhaseEvent` phases)
- `src/OpenWebUI.Client/wwwroot/i18n/*.json`
- `src/OpenWebUI.Domain` `Tool` names + `builtin_` prefix (memory: `builtin_{name}` in SpecJson)
- `tests/OpenWebUI.Client.Tests/*` (component test recipes, stub `IJSRuntime`)

**Files to create or modify:**

```text
src/OpenWebUI.Client/Components/RunActivityFeed.razor     # new
src/OpenWebUI.Client/Services/ToolActivityMap.cs          # new — name → key/icon/target
src/OpenWebUI.Client/Components/ChatView.razor            # live feed + collapsed work log
src/OpenWebUI.Client/Components/ToolCallCard.razor        # reused inside details expander
src/OpenWebUI.Client/wwwroot/i18n/*.json                  # 8 locales
tests/OpenWebUI.Client.Tests/*                          # mapper + feed rendering
```

## 4. Requirements

### RF-001: Friendly activity mapping

- **Description:** every `tool_call` name maps to a plain-language activity line via a category table.
- **Rules:** match on the tool name after stripping the `builtin_`/`builtin:` prefix, case-insensitive, keyword → category:
  `web_search|search_web|brave|google_search` → "Pesquisando na web";
  `fetch|open_url|browser|visit` → "Abrindo página";
  `read_file|file_read|view` → "Lendo arquivo";
  `write_file|edit|apply_patch|create_file` → "Editando arquivo";
  `shell_exec|run|bash|terminal|exec` → "Executando comando";
  `delegate|subagent|spawn|agent` → "Trabalhando em subtarefa";
  `memory_search|memory_save|memory` → "Consultando memória";
  `list_files|grep|search_files|find` → "Procurando no código";
  `image` → "Gerando imagem"; `video` → "Gerando vídeo";
  anything else → "Usando ferramenta".
  Optional short target: first meaningful arg (`path`, `query`, `url`, `command`) from `ArgsPreview`, truncated ~40 chars, rendered subtly — never raw JSON.
- **Input → Output:** `tool_call{name:"builtin_web_search", argsPreview:"{query: 'release notes .NET 10'}"}` → line "Pesquisando na web — release notes .NET 10".

### RF-002: Live feed states

- **Description:** each line reflects the call lifecycle from SSE events.
- **Rules:** `tool_call` → running line (animated spinner); matching `tool_result` → check (ok), red ✕ (`ok=false`), amber mark (`denied`); lines append in arrival order; feed lives inside the current assistant turn area, above/alongside streaming text.
- **Input → Output:** 3 sequential calls → 3 lines; 2 checked, 1 spinning while it runs.

### RF-003: Collapsed work log after run

- **Description:** when the run reaches a terminal status, the feed collapses to a summary row; persisted messages render it collapsed by default.
- **Rules:** summary = "N passos" + worst outcome tint (error/denied shown); expand shows the same lines; per-line "details" reveals the existing `ToolCallCard` body; state resets on new run.
- **Input → Output:** completed run with 4 calls → row "4 passos ▸" expandable.

### RF-004: Phase + thinking indicators

- **Description:** the status chip/inline label uses friendly copy and a thinking state appears before the first token.
- **Rules:** immediately after send → "Pensando…"; `generating` with deltas flowing → "Escrevendo resposta…"; `running_tool` → current activity text in the chip too; `awaiting_approval` → "Aguardando sua aprovação" (approval UI unchanged); zero-activity runs keep the single status label only.
- **Input → Output:** send → "Pensando…" → feed lines → "Escrevendo resposta…" → done.

### RF-005: i18n + a11y

- **Description:** all new strings localized; feed is screen-reader friendly.
- **Rules:** flat keys (`chat.activity.*`, `chat.worklog.*`) in all 8 locales; feed container `role="log"`/`aria-live="polite"` (throttled — announce only line additions, not every delta); toggles keyboard-operable; mobile compact text.

## 5. API Contract

No new endpoints — consumes the existing run SSE (`tool_call`, `tool_result`, `status`, `approval_asked`) and persisted `ToolCallsJson`.

## 6. Acceptance Criteria

- [ ] **Given** a run with tool calls **when** they execute **then** the user sees friendly lines ("Pesquisando na web…"), not tool names/JSON.
- [ ] **Given** a call still running **when** its `tool_result` arrives **then** the spinner becomes a check (or ✕/denied mark).
- [ ] **Given** the run finished **when** the feed settles **then** it collapses to "N passos" expandable; details still reachable per line.
- [ ] **Given** an old chat with tool messages **when** rendered **then** it shows the same collapsed work log.
- [ ] **Given** send → first token delay **when** streaming starts **then** "Pensando…" shows immediately, switching to "Escrevendo resposta…" once text flows.
- [ ] **Given** an unknown tool name **when** called **then** the fallback line "Usando ferramenta" appears.
- [ ] **Given** pt-BR locale **when** the feed renders **then** all labels localize; same for the other 7 locales (keys exist).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Rapid sequential calls | 5 calls in 2s | lines append, no flicker/scroll-jump |
| Denied call | user denies | line shows denial mark, feed continues |
| Call with no args | `builtin_memory_search {}` | label only, no dangling dash |
| Long path/query | >40 chars | truncated with ellipsis |
| Run fails mid-tool | run error | spinning line resolves to error mark |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** read section-3 files; inventory emitted tool names (ChatTools + builtin names) to fill the category table.
- [ ] **T2 — Mapper:** `ToolActivityMap` name→(i18n key, icon, target) + unit tests.
- [ ] **T3 — Feed component:** `RunActivityFeed` (live lines, collapsed summary, per-line details hosting `ToolCallCard`); wire into `ChatView` for live events + persisted tool messages.
- [ ] **T4 — Phases:** friendly phase labels + thinking indicator; status chip text.
- [ ] **T5 — i18n + guards:** keys ×8, tailwind regen, a11y/css guards, client tests.
- [ ] **T6 — Done + PR:** SPEC `Status = Done`, PR `feature/devin-20261010-chat-activity-feed`, merge after CI green.

**7.1 Validation strategy**

- `dotnet test tests/OpenWebUI.Client.Tests` — mapper table, feed render states, collapse/expand, persisted-message derivation.
- `python3 tools/check-i18n-parity.py`, `check-form-a11y.py`, `check-css-classes.py` — clean.
- `dotnet build OpenWebUI.slnx` + `dotnet test tests/OpenWebUI.Api.Tests` — no regression.

## 8. Organization Guardrails

- Dedicated branch → PR → merge; `main` untouched.
- `.github/workflows/` untouched.
- No raw args/JSON surfaced by default; scrubbed previews stay inside details expander.
- Keep `ToolCallCard` features (image/video render, shell output) reachable — feed wraps, doesn't delete.

## 9. Definition of Done

- [ ] During a run the user sees friendly activity lines, not tool names.
- [ ] Feed collapses to a work-log summary after completion (live + historical).
- [ ] "Pensando…"/"Escrevendo resposta…" states show around streaming.
- [ ] Details remain one click away per step.
- [ ] All tests green; i18n parity; tailwind regenerated.
- [ ] SPEC `Status = Done`; PR merged after CI green.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/324 |
