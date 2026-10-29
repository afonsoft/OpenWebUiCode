# SPEC-20261008-a11y-keyboard-focus

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `a11y-keyboard-focus` |
| Type | `Bugfix` |
| Stack | `.NET` (Blazor WASM + Tailwind CSS v4) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261008-a11y-keyboard-focus` |
| Ticket | [#186](https://github.com/afonsoft/OpenWebUiCode/issues/186) (Epic [#182](https://github.com/afonsoft/OpenWebUiCode/issues/182)) |
| Status | `Completed` |

## 1. User Story

**As a** keyboard-only / switch-device / screen-reader user
**I want** a visible focus indicator on every interactive element, and all actions reachable without a mouse
**So that** I can navigate and operate the whole app without a pointer.

**Problem context:**
Design review `design-review-20261008.md` (findings A1–A3, B4/B5 focus parts). `tailwind.input.css` defines focus styling only for `.input` (L195) and `.skip-link` (L142). Every other control — `.msg-action`, `.icon-btn`, `.icon-mini`, `.modal-tab`, `.auth-submit`, sidebar/menu/ad-hoc buttons — has zero `:focus-visible` styling, and several inputs add raw `outline-none` with no replacement. Worse, key actions are hover-gated: message actions stay **focusable while invisible** (`opacity-0`), and the sidebar `⋯` and channel action rows use `invisible` — fully removed from the tab order, so rename/delete/pin/react are unreachable by keyboard. Two spots nest a clickable `<span>` inside a `<button>` (invalid HTML, unfocusable inner control).

## 2. Scope

**In scope:**
- Global `:focus-visible` indicator in `tailwind.input.css` + remove orphaned `outline-none`.
- Make hover-gated actions reachable/visible on focus (group-focus-within/focus-visible) and on `(hover:none)` touch.
- Fix nested-interactive violations in `Sidebar` (folder delete) and `TerminalView` (session kill).
- Add Escape close + focus return to custom menus (ModelSelector, tools/mode menus, sidebar menus) and `role`/`aria-haspopup`/`aria-expanded`.
- Give `CallOverlay` and the mobile workspace drawer full dialog behavior (role, focus trap via `openwebui.trapFocus`, Escape).
- Regenerate `wwwroot/css/tailwind.css` (repo rule).

**Out of scope:**
- Form labels/accessible names → `SPEC-20261008-a11y-form-semantics`.
- Touch-target sizing → `SPEC-20261008-ui-polish`.
- `role="tablist"` semantics → `SPEC-20261008-ui-polish`.
- Any visual redesign beyond focus/visibility states.

## 3. Technical Context

**Where the change happens:**
`src/OpenWebUI.Client` — `tailwind.input.css` (focus layer), `Components/{Sidebar,ChatView,MessageBubble,ModelSelector,CallOverlay,TerminalView,SettingsModal}.razor`, `Pages/{Auth,ChannelPage}.razor`, `wwwroot/js/app.js` (`trapFocus`/`releaseFocus` already exist — reuse).

**Files to read before implementing:**
- `design-review-20261008.md` (findings A1–A3, B4, B5)
- `CLAUDE.md` · `.claude/rules/`
- `src/OpenWebUI.Client/tailwind.input.css` (only focus rules today: `.skip-link:focus-visible` L142, `.input` L195)
- `src/OpenWebUI.Client/wwwroot/js/app.js:73-102` (`trapFocus`/`releaseFocus`)
- `SettingsModal.razor:10-12,622-624` — reference dialog pattern to copy

**Files to create or modify:**
```text
src/OpenWebUI.Client/tailwind.input.css
src/OpenWebUI.Client/wwwroot/css/tailwind.css        # regenerated, committed
src/OpenWebUI.Client/Components/Sidebar.razor
src/OpenWebUI.Client/Components/ChatView.razor
src/OpenWebUI.Client/Components/MessageBubble.razor
src/OpenWebUI.Client/Components/ModelSelector.razor
src/OpenWebUI.Client/Components/CallOverlay.razor
src/OpenWebUI.Client/Components/TerminalView.razor
src/OpenWebUI.Client/Components/ChatWorkspacePanel.razor (drawer host lives in ChatView)
src/OpenWebUI.Client/Pages/Auth.razor
src/OpenWebUI.Client/Pages/ChannelPage.razor
src/OpenWebUI.Client/wwwroot/index.html             # error-ui dismiss → <button>
src/OpenWebUI.Client/wwwroot/js/app.js              # only if a shared menu-key helper is needed
```

## 4. Requirements

### RF-001: Global focus indicator
- **Description:** Every natively focusable element and custom interactive class MUST show a clearly visible focus indicator via `:focus-visible` (ring/outline ≥2 px, ≥3:1 contrast vs. background, both themes).
- **Rules:** implement once in `tailwind.input.css` (e.g. `:focus-visible` base rule + per-class `@apply` where needed); `.input` keeps its ring; no element may keep `outline-none`/`focus:outline-none` without a visible replacement.
- **Input → Output:** `tailwind.input.css` focus rules → regenerated `tailwind.css` with focus styles on all interactive classes.

### RF-002: Remove orphaned `outline-none`
- **Description:** `outline-none` on `Sidebar.razor:66,83,323`, `Auth.razor:16,52,58,63`, `ChannelPage.razor:146,189`, `ChatView.razor:321` must gain a visible focus style or use `.input` styling; the `ChatView` composer keeps/improves `focus-within` container indicator.
- **Input → Output:** listed elements → visible focus state.

### RF-003: Hover-gated actions reachable
- **Description:** `MessageBubble.razor:48` action bar, `Sidebar` chat `⋯` (`invisible group-hover:visible`), `ChannelPage.razor:91` action row must become visible on `group-focus-within`/`focus-visible` and remain reachable on touch (`hover:none` → always visible or an always-visible affordance).
- **Rules:** no focusable element may be invisible (`visibility:hidden`/`opacity-0`) at any state.
- **Input → Output:** hover-gated controls → visible on keyboard focus and touch.

### RF-004: No nested interactives
- **Description:** Remove `<span @onclick>` inside `<button>` in `Sidebar` (folder delete) and `TerminalView.razor:35-36` (kill `✕`); inner action becomes a sibling `<button>` with accessible name (name via form-semantics SPEC or inline here if trivial).
- **Input → Output:** invalid nested markup → sibling buttons, both keyboard-operable.

### RF-005: Menu keyboard contract
- **Description:** Custom menus (ModelSelector `:16-41`, ChatView tools `:336-415`, mode `:475-505`, Sidebar `⋯`) must: trigger exposes `aria-haspopup` + `aria-expanded`; menu uses `role="menu"`/`listbox`; Escape closes and returns focus to the trigger; ArrowUp/ArrowDown navigate items where role=menu.
- **Input → Output:** menus → Escape/arrow operable, state announced.

### RF-006: Full dialog behavior on overlays
- **Description:** `CallOverlay.razor:8` gets `role="dialog" aria-modal="true"` + `trapFocus` + Escape-close; `ChatView.razor:567-578` mobile drawer gets `trapFocus` + Escape-close (already has role/aria-modal).
- **Input → Output:** overlays → focus trapped, Escape closes, focus restored.

### RF-007: Error-UI dismiss is a button
- **Description:** `index.html:49` `<span class="dismiss">` becomes `<button>` with localized `aria-label`.
- **Input → Output:** span → button.

## 5. API Contract

N/A — client-only.

## 6. Acceptance Criteria

- [ ] **Given** a fresh tab **when** the user tabs through sidebar → chat → composer → message actions **then** every stop shows a visible focus indicator (light + dark themes).
- [ ] **Given** keyboard-only use **when** focus lands on a chat row / message bubble / channel message **then** its hidden actions appear and are operable.
- [ ] **Given** `(hover: none)` touch device **when** viewing a chat list item or message **then** its actions are reachable without hover.
- [ ] **Given** an open ModelSelector/tools/mode menu **when** pressing Escape **then** menu closes and focus returns to the trigger; `aria-expanded` toggles.
- [ ] **Given** CallOverlay or the mobile drawer is open **when** pressing Tab repeatedly **then** focus cycles inside; Escape closes.
- [ ] **Given** a folder row or terminal session tab **when** tabbing **then** row action and delete/kill are two separate stops, both buttons.
- [ ] **Given** `#blazor-error-ui` visible **when** tabbing **then** Details and Dismiss are both reachable buttons.

| Scenario | Input | Expected |
| --- | --- | --- |
| Tab into message bubble | keyboard | actions revealed + focusable |
| `visibility:hidden` menu trigger | keyboard | never a tab stop while hidden |
| Shift+Tab out of modal first field | keyboard | wraps to last focusable |
| Escape with nested menus open | keyboard | closes top layer only |

## 7. Task Plan

- [ ] **T1 — Discovery:** read design-review findings A1–A3/B4/B5 + reference files; inventory every `outline-none`, `group-hover`, `invisible`, nested-interactive site.
- [ ] **T2 — Focus layer:** add global `:focus-visible` styling; fix RF-002 sites; regenerate `tailwind.css`.
- [ ] **T3 — Hover-gated + nested:** RF-003, RF-004 in MessageBubble/Sidebar/ChannelPage/TerminalView.
- [ ] **T4 — Menus & dialogs:** RF-005, RF-006, RF-007.
- [ ] **T5 — Verification:** keyboard walkthrough of all AC; `dotnet build` client; Lighthouse a11y spot check; fill DoD.
- [ ] **T6 — Done + PR:** `Status = Done` + PR on `feature/devin-20261008-a11y-keyboard-focus`.

**7.1 Validation strategy:** Refactor/Bugfix — full suite green, no coverage regression; manual keyboard-through-app evidence (tab order log or recording); Tailwind artifact regenerated and committed.

## 8. Organization Guardrails

- **Branches:** never commit to `main`; use `feature/devin-20261008-a11y-keyboard-focus`.
- **Workflows:** do not modify `.github/workflows/`.
- **CSS:** never hand-edit `wwwroot/css/tailwind.css` — regenerate via `tailwindcss -i ... -o ... --minify` and commit the output.
- **i18n:** any new visible string goes through `L[...]` in both `en-US.json` and `pt-BR.json` (other locales may follow E1).
- **Security:** no secrets/PII in commit.
- **Scope:** accessibility behavior only — no visual redesign, no feature changes.
- **Upstream parity:** stay visually consistent with upstream patterns.

## 9. Definition of Done

- [ ] All RF-001…RF-007 implemented.
- [ ] All AC verified manually (keyboard) on light + dark themes.
- [ ] `dotnet build` + `dotnet test` green; no coverage regression.
- [ ] `tailwind.css` regenerated and committed.
- [ ] No new strings hardcoded outside i18n.
- [ ] Guardrails respected.

## Open Questions / Pending Ambiguity

- Exact focus-ring color token (recommend reusing `.input` ring tokens for consistency).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/194 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/186
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/182
