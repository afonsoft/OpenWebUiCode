# SPEC-20261008-a11y-motion-status

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `a11y-motion-status` |
| Type | `Bugfix` |
| Stack | `.NET` (Blazor WASM + Tailwind CSS v4) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261008-a11y-motion-status` |
| Ticket | [#187](https://github.com/afonsoft/OpenWebUiCode/issues/187) (Epic [#182](https://github.com/afonsoft/OpenWebUiCode/issues/182)) |
| Status | `Completed` |

## 1. User Story

**As a** user with vestibular sensitivity or a screen reader
**I want** animations to honor `prefers-reduced-motion`, status not conveyed by color alone, and realtime updates announced
**So that** the UI doesn't make me sick and I don't miss state changes.

**Problem context:**
Findings D1, A5–A7. Zero `prefers-reduced-motion` handling in source while `animate-spin` (status spinners), `animate-pulse` (`VoiceButton:9`, `CallOverlay:14-17`), `transition-all` (`Sidebar:20`), `.typing` blink and the animated `thinking.gif` (`MessageBubble:10`) run unconditionally. Job/MCP/session/day status is colored-dot-only (`ChatWorkspacePanel:187,227`, `TerminalView:33`, `Calendar:89,95,99,103`). Channel messages lack the live region the chat has (`role="log"`). Decorative emoji (📌📎📄📁◈) get announced.

## 2. Scope

**In scope:**
- Global `@media (prefers-reduced-motion: reduce)` kill-switch for animations/transitions/blink; replace `transition-all` with explicit properties.
- `thinking.gif` → CSS/static indicator (or `<video muted loop>` if motion essential).
- Status dots → dot + text/`title`/`aria-label` (or legend); Calendar day dots get textual equivalents.
- `ChannelPage` message list `role="log" aria-live="polite"` + typing indicator live region.
- `aria-hidden="true"` on decorative emoji/glyph spans; explicit `aria-label` on emoji-only buttons (ChannelPage reacts).
- Regenerate `tailwind.css`.

**Out of scope:**
- Focus/keyboard/menu/dialog fixes → other SPECs.
- BarChart data tables (chart already has `role="img"` + label; tabular data = separate enhancement).

## 3. Technical Context

**Where the change happens:**
`src/OpenWebUI.Client/tailwind.input.css`, `Components/{MessageBubble,VoiceButton,CallOverlay,Sidebar,ChatWorkspacePanel,TerminalView}.razor`, `Pages/{ChannelPage,Calendar,Workspace,WorkspaceItemEditor,FolderPage,SharedChat}.razor`, `wwwroot/assets/thinking.gif` usage.

**Files to read before implementing:**
- `design-review-20261008.md` findings D1, A5, A6, A7
- `.typing` / `.spinner` definitions in `tailwind.input.css`; `animate-*`/`transition` usage sites (grep list)

**Files to create or modify:**
```text
src/OpenWebUI.Client/tailwind.input.css
src/OpenWebUI.Client/wwwroot/css/tailwind.css        # regenerated
src/OpenWebUI.Client/Components/MessageBubble.razor  # thinking.gif replacement
src/OpenWebUI.Client/Components/VoiceButton.razor
src/OpenWebUI.Client/Components/CallOverlay.razor
src/OpenWebUI.Client/Components/Sidebar.razor        # transition-all → explicit
src/OpenWebUI.Client/Components/ChatWorkspacePanel.razor
src/OpenWebUI.Client/Components/TerminalView.razor
src/OpenWebUI.Client/Pages/ChannelPage.razor         # role=log + typing live + emoji
src/OpenWebUI.Client/Pages/Calendar.razor            # day-dot text
src/OpenWebUI.Client/Pages/{Workspace,WorkspaceItemEditor,FolderPage,SharedChat}.razor  # decorative emoji
```

## 4. Requirements

### RF-001: Reduced-motion kill-switch
- **Description:** Add a `@media (prefers-reduced-motion: reduce)` block neutralizing animations/transitions/blink (`.typing`, `animate-spin`, `animate-pulse`, CSS transitions) app-wide; `Sidebar.razor:20` `transition-all` → explicit properties.
- **Rules:** functional state still communicated (spinner→static "…"/text); no layout breakage.
- **Input → Output:** unconditional motion → honored OS preference.

### RF-002: Replace `thinking.gif`
- **Description:** `MessageBubble.razor:10` animated GIF → static icon or CSS dot-pulse (itself reduced-motion aware); remove the asset if unused.
- **Input → Output:** GIF → reduced-motion-safe indicator.

### RF-003: Status not by color alone
- **Description:** `ChatWorkspacePanel:187,227`, `TerminalView:33`, `Calendar:89,95,99,103` colored dots get paired text/`title`/`aria-label` (e.g. "running", "error", "scheduled", "today"); add a compact legend where the meaning isn't self-evident (Calendar).
- **Input → Output:** color-only dots → dot+text/label.

### RF-004: Channel live region
- **Description:** `ChannelPage.razor:58` messages container gets `role="log" aria-live="polite" aria-relevant="additions"` (parity with `ChatView:115`); typing indicator `:131-136` gets `role="status"`/polite.
- **Input → Output:** silent realtime updates → announced.

### RF-005: Decorative glyphs hidden
- **Description:** `aria-hidden="true"` on decorative emoji spans (`ChannelPage:50`, `Workspace:70,111`, `FolderPage:10`, `SharedChat:34`, `WorkspaceItemEditor:136`); explicit `aria-label` on `ChannelPage:94-95` emoji react buttons.
- **Input → Output:** emoji noise → hidden/named.

## 5. API Contract

N/A — client-only.

## 6. Acceptance Criteria

- [ ] **Given** OS `prefers-reduced-motion: reduce` **when** any view renders **then** no looping animation runs (spinners static, no pulse/blink, transitions instant).
- [ ] **Given** reduced-motion off **when** streaming **then** spin/pulse still indicates activity.
- [ ] **Given** jobs/MCP/calendar/terminal views **when** read by SR or viewed by color-blind user **then** status is understandable without color.
- [ ] **Given** a channel receiving a realtime message or typing event **when** using a SR **then** it's announced (like chat does).
- [ ] **Given** decorative emoji **when** traversed by SR **then** skipped.

| Scenario | Input | Expected |
| --- | --- | --- |
| reduce-motion + send message | stream starts | static indicator, no spin |
| failed job dot | SR | "failed"/"error" text, not just red |
| emoji react buttons | focus | named action, not bare emoji |

## 7. Task Plan

- [ ] **T1 — Discovery:** list all `animate-*`/`transition`/`thinking.gif`/status-dot/live-region sites.
- [ ] **T2 — Motion:** RF-001 + RF-002; regen css.
- [ ] **T3 — Status:** RF-003.
- [ ] **T4 — Live + glyphs:** RF-004, RF-005.
- [ ] **T5 — Verification:** reduced-motion emulation pass, SR spot-checks, build+test; fill DoD.
- [ ] **T6 — Done + PR.**

**7.1 Validation strategy:** Bugfix — suite green; manual `prefers-reduced-motion` emulation evidence; Lighthouse/axe color-use spot-check.

## 8. Organization Guardrails

- **Branches:** `feature/devin-20261008-a11y-motion-status`; never `main`.
- **CSS:** regen `tailwind.css`, commit.
- **i18n:** new status text via `L[...]`.
- **Scope:** motion/status/live-region only.

## 9. Definition of Done

- [ ] RF-001…RF-005 implemented.
- [ ] Reduced-motion emulation shows no forced animation.
- [ ] All status dots have non-color equivalents; channel parity with chat live regions.
- [ ] Build + tests green; `tailwind.css` regenerated.

## Open Questions / Pending Ambiguity

- Whether `thinking.gif` removal affects brand/parity with upstream (recommend CSS indicator — matches modern upstream style).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/195 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/187
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/182
