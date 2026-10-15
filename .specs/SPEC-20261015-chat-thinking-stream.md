# SPEC-20261015-chat-thinking-stream

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-thinking-stream` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/open-webui` |
| Branch | `feature/devin-20261015-chat-thinking-stream` |
| Ticket | `[A DEFINIR — create-issues after approval]` |
| Status | `In implementation` |
| Priority | `high` |
| Depends on | fix/run-json-guards-and-sse-dispose (merge); SPEC-20261010-chat-activity-feed (Done) |

## 1. User Story

**As a** user waiting for the assistant's answer **I want** to see what the model is doing while it "thinks" — reasoning tokens streaming live, plus friendly progress lines for the pre-work (context, search, tools) — **So that** the chat never looks frozen: I get continuous feedback from the moment I hit send until the final answer finishes, like ChatGPT's "Thinking" / Devin-web's live activity.

**Problem context:** today a run is silent in three stretches — (a) the pre-provider pipeline (compaction, RAG, web search, tool loading) emits **zero** events; (b) models that emit reasoning (`reasoning_content`, `thinking`) have those tokens **discarded** in the provider parsers; (c) the no-tools path emits **no** phase events at all — the UI sits on a static "Pensando…" until the first content token or the full blob arrives. The transport is fine: SSE with `seq` replay + 15s heartbeat + typed client events already exists (SPEC-20261007-chat-tool-streaming, SPEC-20261010-chat-activity-feed). The gap is event coverage and provider-field normalization — not the channel. SignalR was evaluated and rejected: SSE already delivers every semantic needed.

## 2. Scope

**In scope:**

- **Unified reasoning channel:** provider parsers normalize reasoning tokens into a single `delta.reasoning` field in the SSE chunk (OpenAI-compatible `reasoning_content`/`reasoning`, Anthropic `thinking_delta` blocks, Gemini `thought:true` parts, Ollama `message.thinking`); the executor forwards them on both paths (tool loop via extended delta callback, no-tools via pass-through line) and the client raises a new `ChatStreamEvent.Reasoning`.
- **Collapsible thinking UI:** a "Pensamento" section inside the assistant turn that auto-expands while reasoning streams, auto-collapses when answer text starts flowing, stays re-expandable after the run (muted/mono body, viewport cap + inner scroll).
- **Progress phases everywhere:** `event: status` `{phase,label}` emissions covering (a) pre-provider pipeline — `preparing_context`, `compacting`, `searching_web`, `consulting_memory`, `loading_tools`; (b) `connecting_model` right before the first provider call; (c) `generating`/`answering` on the no-tools path so phase copy reaches the activity chip everywhere.
- **Lifecycle hygiene:** reasoning/phase events get `seq` and replay on `lastSeq` resume like every other event; reasoning is **not** merged into persisted message content; client watchdog/heartbeats unchanged.

**Out of scope:**

- Persisting reasoning to the DB for historical chats (live-only in v1 — see open question Q2; persistence would need a schema migration and is a follow-up SPEC if wanted).
- Changing which tools run, the tool loop, approvals, or the risk classifier.
- Transport changes (no SignalR, no WebSockets — SSE stays).
- Replay of reasoning into *old* persisted messages (no data exists).
- Token usage/billing display for reasoning tokens.

## 3. Technical Context

**Where the change happens:** provider SSE parsers emit a second text channel; `ChatPipeline.RunToolLoopAsync` gains a `ProviderDelta { Content, Reasoning }` record (or parallel `OnReasoningAsync` callback — pick the smaller diff); `ChatRunExecutor` publishes reasoning chunks and emits `status` events around `MaybeCompactAsync`/`ApplyAsync`/`EnrichRequestAsync`/`LoadEnabledAsync`; `ChatStreamService` maps `delta.reasoning` → `Reasoning` event; `ChatView`/`RunActivityFeed` render the collapsible section.

**Files to read before implementing:**

- `CLAUDE.md` · `.claude/rules/global-rules.md`
- `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` — 4 stream parsers:
  - OpenAI-compatible ~L636-646 (`delta.content` only; drop `reasoning_content`/`reasoning` today)
  - Ollama ~L765 (`message.content`; newer API also emits `message.thinking`)
  - Anthropic ~L1166-1270 (`content_block_delta`; `thinking_delta`/`signature_delta` ignored)
  - Gemini ~L1510-1640 (`candidates[0].content.parts[]`; `thought:true` parts folded into text today — must be split out)
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` — tool path `OnDeltaAsync` (~L274-299, re-wraps into OpenAI-style chunk), no-tools pass-through (~L310-323), `PublishPhaseAsync` (~L361-366, `event: status` + `RunPhaseEvent{phase,label}`), `AccumulateDelta`
- `src/OpenWebUI.Infrastructure/Services/ChatPipeline.cs` — `ToolLoopCallbacks` signature
- `src/OpenWebUI.Client/Services/ChatStreamService.cs` — `ChatStreamEvent` union, delta parsing ~L520-532
- `src/OpenWebUI.Client/Components/ChatView.razor` + `RunActivityFeed.razor` — streaming buffer + feed
- `src/OpenWebUI.Client/wwwroot/i18n/*.json` — 8 locales
- `tests/OpenWebUI.Api.Tests/ToolStreamingTests.cs`, `tests/OpenWebUI.Client.Tests/ChatStreamServiceTests.cs`

**Files to create or modify:**

```text
src/OpenWebUI.Infrastructure/Services/ProviderService.cs   # reasoning normalization ×4 providers
src/OpenWebUI.Application/Contracts/...                    # ProviderDelta record (or extend callback)
src/OpenWebUI.Infrastructure/Services/ChatPipeline.cs      # reasoning callback through the tool loop
src/OpenWebUI.Api/Runs/ChatRunExecutor.cs                  # publish reasoning + progress phases
src/OpenWebUI.Client/Services/ChatStreamService.cs         # ChatStreamEvent.Reasoning
src/OpenWebUI.Client/Components/ChatView.razor             # accumulate + wire thinking section
src/OpenWebUI.Client/Components/ThinkingSection.razor      # new — collapsible reasoning body
src/OpenWebUI.Client/wwwroot/i18n/*.json                   # chat.thinking.*, chat.progress.* ×8
tests/OpenWebUI.Api.Tests/*                                # provider parser + executor event tests
tests/OpenWebUI.Client.Tests/*                             # stream mapping + section rendering
```

## 4. Requirements

### RF-001: Provider reasoning normalization

- **Description:** every provider stream parser extracts reasoning tokens and emits them on a channel separate from content.
- **Rules:** OpenAI-compatible reads `delta.reasoning_content`, `delta.reasoning` (string) and `delta.reasoning_details[]` (array of `{text}`); Anthropic reads `content_block_delta` with `delta.type=="thinking_delta"` (`signature_delta` ignored); Gemini reads `parts[]` entries with `thought==true` (normal parts stay content); Ollama reads `message.thinking`. Reasoning text is **never** appended to the content channel. Providers without reasoning emit nothing — no empty events.
- **Input → Output:** OmniRoute chunk `{"delta":{"reasoning_content":"preciso checar…","content":null}}` → one reasoning delta `"preciso checar…"` and no content delta.

### RF-002: Unified SSE contract for reasoning

- **Description:** reasoning travels inside the existing normalized `data:` chunk as `delta.reasoning` — additive, old clients ignore it.
- **Rules:** wire format `{"choices":[{"index":0,"delta":{"reasoning":"…"}}]}` (a chunk MAY carry `content`, `reasoning`, or both); tool-loop path re-wraps reasoning the same way `OnDeltaAsync` re-wraps content today, after `pauses.WaitIfPausedAsync` and outlet-rule processing applied to reasoning too (rules that hide secrets apply equally); every published event gets `seq` so replay/resume re-accumulates automatically.
- **Input → Output:** provider reasoning delta → `broadcaster.Publish("data: {…\"delta\":{\"reasoning\":\"…\"}}")` with next `seq`.

### RF-003: Client stream mapping

- **Description:** `ChatStreamService` surfaces reasoning as a typed event.
- **Rules:** new `ChatStreamEvent.Reasoning(string Text)`; parser reads `choices[0].delta.reasoning` (string) from both plain `data:` chunks and re-wrapped tool-loop chunks; empty/whitespace-only reasoning deltas are dropped; ordering between `Reasoning` and `Delta` events follows arrival.
- **Input → Output:** `data: {"choices":[{"delta":{"reasoning":"…"}}]}` → `Reasoning("…")`.

### RF-004: Collapsible thinking UI

- **Description:** the assistant turn shows a "Pensamento" section fed by `Reasoning` events.
- **Rules:** section header = icon + i18n label + live elapsed while open; body muted/monospace, `max-height` cap with inner scroll, auto-scrolls to bottom while expanded; **auto-expanded while reasoning streams, auto-collapses on the first `Delta` (content) event**; user toggle overrides auto behavior for the rest of that run; when no reasoning ever arrives nothing renders; on `run.failed` mid-reasoning the section keeps the partial text with the existing error affordance; state resets on new run.
- **Input → Output:** send → `Reasoning` chunks → expanded "Pensamento" streaming → first content `Delta` → section collapses to "Pensamento ▸", answer streams below.

### RF-005: Pre-provider + no-tools progress phases

- **Description:** `event: status` `{phase,label}` emissions cover the silent stretches.
- **Rules:** executor publishes `preparing_context` before `MaybeCompactAsync`/`ApplyAsync`, `compacting` when compaction actually ran, `searching_web`/`consulting_memory` around the `EnrichRequestAsync` enrichers that fired (label carries the query/scope when known), `loading_tools` before `LoadEnabledAsync`, `connecting_model` right before streaming starts; the no-tools path additionally publishes `generating` before the provider call and `answering` on first content delta; phases render through the existing activity-chip copy table — unknown phases fall back to the generic "Pensando…"; emissions are best-effort (never throw into the run).
- **Input → Output:** send → chip cycles "Preparando contexto…" → "Pesquisando na web…" → "Conectando ao modelo…" → "Pensando…" → "Escrevendo resposta…".

### RF-006: i18n + a11y + performance

- **Description:** all new strings localized; live regions throttled; bounded memory.
- **Rules:** flat keys `chat.thinking.*` and `chat.progress.*` in all 8 locales; thinking body is **not** `aria-live` (high-frequency) — the header is `aria-live="polite"` and announces only open/close state changes; toggle button keyboard-operable with `aria-expanded`; client-side reasoning buffer capped (~64KB displayed, older text elided with a "…" marker — full text stays in the event log for the session); mobile renders the same section full-width under the bubble.
- **Input → Output:** screen reader hears "Pensamento expandido/recolhido" once, not every token.

## 5. API Contract

Additive fields on existing SSE — no new endpoints, no breaking change:

```
event: status
data: {"phase":"searching_web","label":"release notes .NET 10"}

data: {"choices":[{"index":0,"delta":{"reasoning":"vou comparar as versões…"}}]}

data: {"choices":[{"index":0,"delta":{"content":"A resposta…"}}]}
```

- `RunPhaseEvent` record extended with new `phase` values only (client already tolerates unknown phases).
- Reasoning chunks replay on `lastSeq` resume with their `seq`, identical to content chunks.

## 6. Acceptance Criteria

- [ ] **Given** a model emitting `reasoning_content` (OmniRoute/DeepSeek-style) **when** the run streams **then** the "Pensamento" section fills live and the answer body stays clean.
- [ ] **Given** reasoning then content **when** the first content delta arrives **then** the section auto-collapses and stays re-expandable.
- [ ] **Given** a provider with no reasoning **when** the run streams **then** no thinking UI appears and nothing regresses.
- [ ] **Given** a no-tools run **when** it executes **then** the chip cycles through real phases instead of a static "Pensando…".
- [ ] **Given** a run with web search/RAG configured **when** it executes **then** "Pesquisando na web…"/"Consultando memória…" appear before "Conectando ao modelo…".
- [ ] **Given** a client reconnect mid-run (`Last-Event-ID`) **when** replay fires **then** reasoning text re-accumulates in order and the section reflects streamed-so-far state.
- [ ] **Given** a run that fails mid-reasoning **when** the error event lands **then** partial reasoning remains visible with the error affordance.
- [ ] **Given** persisted chat reload **when** an old message renders **then** no thinking section appears (no persisted data) and live behavior is unaffected.
- [ ] **Given** pt-BR + 7 locales **when** the UI renders **then** all new labels localize and `check-i18n-parity.py` is clean.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Interleaved reasoning+content chunk | one delta carrying both | reasoning appends to section, content to body — order preserved |
| Whitespace-only reasoning | `"reasoning":" \n"` | dropped, no flicker |
| Multi-round tool loop | reasoning on rounds 1 and 3 | single section accumulates both rounds |
| Reasoning >64KB | very long chain | display elides head with "…", stream keeps flowing |
| Gemini `thought:true` part | part flagged | routed to reasoning, not content |
| Anthropic `signature_delta` | signature event | ignored silently |
| Outlet rule matches reasoning | rule hides API key text | redaction applies to reasoning too |
| Steer/pause mid-reasoning | pause engaged | reasoning holds at `WaitIfPausedAsync` like content |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** read section-3 files; confirm `ToolLoopCallbacks` extension point; inventory actual reasoning field names the OmniRoute gateway emits (curl a stream) to lock RF-001 field list.
- [ ] **T2 — Provider layer:** `ProviderDelta`/`OnReasoningAsync` channel + normalize in the 4 parsers; unit tests per provider with fixture chunks.
- [ ] **T3 — Executor:** publish reasoning chunks (both paths) + the RF-005 phase emissions; API tests asserting SSE sequence.
- [ ] **T4 — Client:** `ChatStreamEvent.Reasoning` parse + tests; `ThinkingSection.razor` (collapse logic, elapsed, cap); wire into `ChatView`.
- [ ] **T5 — i18n + guards:** keys ×8, tailwind regen, `check-i18n-parity`/`check-form-a11y`/`check-css-classes` clean.
- [ ] **T6 — Done + PR:** SPEC `Status = Done`, PR `feature/devin-20261015-chat-thinking-stream`, merge after CI green.

**7.1 Validation strategy**

- `dotnet test tests/OpenWebUI.Api.Tests` — per-provider reasoning extraction (fixture chunks incl. double-encoded/absent), executor event ordering, phase emissions.
- `dotnet test tests/OpenWebUI.Client.Tests` — `Reasoning` mapping, auto-collapse on first `Delta`, toggle override, empty-reasoning no-render, buffer cap.
- `python3 tools/check-i18n-parity.py`, `check-form-a11y.py`, `check-css-classes.py` — clean.
- `dotnet build OpenWebUI.slnx` — 0 warnings.
- Manual: real OmniRoute run with a reasoning model end-to-end through the Docker stack.

## 8. Organization Guardrails

- Dedicated branch → PR → merge; `main` untouched.
- `.github/workflows/` untouched; SSE stays (no SignalR/WebSocket).
- Reasoning never enters persisted `content`; no schema migration in this SPEC.
- Redaction/outlet rules apply to reasoning identically to content.
- All `PublishPhaseAsync` calls wrapped best-effort — progress telemetry must never fail a run.

## 9. Definition of Done

- [ ] Reasoning-capable models stream a live, collapsible "Pensamento" section on both chat paths.
- [ ] Pre-provider pipeline and no-tools runs emit visible progress phases.
- [ ] Reconnect/replay restores reasoning state correctly.
- [ ] Answer content unchanged — no reasoning bleeds into persisted messages.
- [ ] All tests green; i18n parity; tailwind regenerated; a11y guard clean.
- [ ] SPEC `Status = Done`; PR merged after CI green.

## 10. Open Questions

- **Q1 — Anthropic `thinking` budget:** expose a per-model "reasoning effort" setting later, or out of scope entirely? (Recommendation: out of scope — request-payload config is a separate feature.)
- **Q2 — Persist reasoning?** v1 renders live only; historical chats show nothing. If persistence is wanted, follow-up SPEC adds a `ReasoningJson`-style column (migration required). (Recommendation: live-only now — matches the collapsed-after-answer UX.)
- **Q3 — Phase vocabulary:** the RF-005 list covers today's pipeline; new enrichers should register new phase names in the same i18n table — acceptable convention?
