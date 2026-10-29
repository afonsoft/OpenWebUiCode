# SPEC-20261008-ui-polish

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `ui-polish` |
| Type | `Refactor` |
| Stack | `.NET` (Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261008-ui-polish` |
| Ticket | [#190](https://github.com/afonsoft/OpenWebUiCode/issues/190) (Epic [#182](https://github.com/afonsoft/OpenWebUiCode/issues/182)) |
| Status | `Completed` |

## 1. User Story

**As a** mobile/keyboard user
**I want** controls sized for touch, destructive actions confirmed by a consistent in-app dialog, and list UI predictable at scale
**So that** the app is easier to hit, safer, and consistent.

**Problem context:**
Findings A4, A8, G4–G6, H1–H3. `.icon-mini` = 20 px and `.msg-action` ≈ text-xs with no padding — sub-24 px touch targets carrying destructive actions (🗑). `window.confirm`/`prompt` native dialogs confirm deletes (unstyled, inconsistent vs. the app's modal pattern). Tab rows (`modal-tab`) lack `tablist` semantics. Lists render fully (no `Virtualize`). `blazor-error-ui` dismiss span and minor image-delivery items (dims/`fetchpriority`) remain if not covered elsewhere.

## 2. Scope

**In scope:**
- Touch targets: `.icon-mini`, `.msg-action` (and similar) → ≥24 px hit area (padding/`min-h`/`min-w`), keeping visual density.
- Shared `ConfirmDialog`/`PromptDialog` component replacing `window.confirm`/`window.prompt` call sites (`openwebui.confirm`/`prompt`), using the SettingsModal dialog pattern (role/aria-modal/trap/Escape).
- Tab semantics on `modal-tab` rows (Settings, Workspace, ChatWorkspacePanel): `role="tablist"/tab`, `aria-selected`, arrow-key nav.
- `<Virtualize>` evaluation/adoption for sidebar chat list + message list at scale (threshold ~200).
- Any leftover low items not covered by other SPECs (error-ui dismiss button if keyboard-focus SPEC doesn't take it; `transition-all` if motion SPEC doesn't take it).
- Regenerate `tailwind.css`.

**Out of scope:**
- Focus indicators/labels/navigation → prior SPECs.
- Perf boot-path work → `perf-boot`.
- Redesign of confirm flows (same texts, same behavior — in-app dialog only).

## 3. Technical Context

**Where the change happens:**
`src/OpenWebUI.Client/tailwind.input.css` (`.icon-mini` L255, `.msg-action` L259), `Components/ConfirmDialog.razor` (new), `wwwroot/js/app.js:35-40` (native prompt/confirm wrappers), call sites in `Pages/{Workspace,Automations,Archived,Notes,Admin}.razor`, tab rows in `Components/{SettingsModal,ChatWorkspacePanel}.razor` + `Pages/Workspace.razor`, lists in `Components/{Sidebar,ChatView}.razor`.

**Files to read before implementing:**
- `design-review-20261008.md` findings A4, G6, H1–H3
- `SettingsModal.razor` (dialog + trap reference), `app.js` confirm/prompt wrappers

**Files to create or modify:**
```text
src/OpenWebUI.Client/tailwind.input.css
src/OpenWebUI.Client/wwwroot/css/tailwind.css        # regenerated
src/OpenWebUI.Client/Components/ConfirmDialog.razor  # new
src/OpenWebUI.Client/wwwroot/js/app.js               # bridge or removal of native wrappers
src/OpenWebUI.Client/Pages/Workspace.razor
src/OpenWebUI.Client/Pages/Automations.razor
src/OpenWebUI.Client/Pages/Archived.razor
src/OpenWebUI.Client/Pages/Notes.razor
src/OpenWebUI.Client/Pages/Admin.razor               # if it uses confirm/prompt
src/OpenWebUI.Client/Components/SettingsModal.razor  # tabs
src/OpenWebUI.Client/Components/ChatWorkspacePanel.razor
src/OpenWebUI.Client/Components/Sidebar.razor        # Virtualize if adopted
src/OpenWebUI.Client/Components/ChatView.razor       # Virtualize if adopted
```

## 4. Requirements

### RF-001: ≥24 px touch targets
- **Description:** `.icon-mini` and `.msg-action` (and any other sub-24 px interactive class found) get `min-h/min-w` ≥24 px (prefer 32–44 px on coarse pointers via `hover:none` media or uniform padding) without changing visual footprint.
- **Input → Output:** sub-24 px targets → ≥24 px.

### RF-002: `ConfirmDialog` component
- **Description:** New `ConfirmDialog` (title, message, confirm/cancel, optional danger styling) with dialog semantics + focus trap + Escape; a `ConfirmService` (or injected helper) exposes `Task<bool> ConfirmAsync(text)`; `openwebui.confirm`/`prompt` call sites migrate (prompt → small input dialog or keep native if only used for "add file by name" — decision at impl).
- **Rules:** same confirmation copy (i18n keys); destructive buttons keep `.danger` styling; native dialogs no longer used for confirms.
- **Input → Output:** `window.confirm` → in-app accessible dialog.

### RF-003: Tab semantics
- **Description:** `modal-tab` rows get `role="tablist"`, tabs `role="tab"` + `aria-selected`, panels associated, Left/Right (or Up/Down on vertical) arrow navigation.
- **Input → Output:** button rows → tab widgets.

### RF-004: List virtualization at scale
- **Description:** Evaluate `<Virtualize>` for sidebar chat list and chat `_messages` (already imported); adopt where lists can exceed ~200 items; keep DOM-driven features (scroll-pinned stream, aria-live log) working.
- **Input → Output:** full-render lists → virtualized where justified.

### RF-005: Regenerate CSS
- **Description:** `tailwind.css` regenerated + committed.
- **Input → Output:** source change → artifact.

## 5. API Contract

N/A — client-only.

## 6. Acceptance Criteria

- [ ] **Given** mobile/touch **when** tapping `.icon-mini`/`.msg-action` controls **then** hit area ≥24 px (no mis-taps on adjacent controls).
- [ ] **Given** a delete action (notes/automations/archived/workspace/admin) **when** triggered **then** an in-app dialog opens (trapped focus, Escape cancels, localized); native dialogs gone.
- [ ] **Given** Settings/Workspace/panel tabs **when** using arrows **then** focus moves between tabs and `aria-selected` reflects state.
- [ ] **Given** a chat list >200 items **when** Virtualize adopted **then** scroll stays smooth and keyboard order preserved (or documented decision not to virtualize).

| Scenario | Input | Expected |
| --- | --- | --- |
| Confirm canceled | dialog | nothing deleted; focus restored |
| Confirm on danger action | dialog | `.danger` confirm button; executes |
| `prompt` replacement | add-file flow | works or justified exception |

## 7. Task Plan

- [ ] **T1 — Discovery:** enumerate sub-24 px controls + all confirm/prompt call sites + tab rows.
- [ ] **T2 — Touch targets:** RF-001 (+regen css).
- [ ] **T3 — ConfirmDialog:** RF-002 (component + service + migrate call sites).
- [ ] **T4 — Tabs:** RF-003.
- [ ] **T5 — Virtualize:** RF-004 (adopt or document decision).
- [ ] **T6 — Verification:** build+test, touch/dialog/tab manual checks; fill DoD; **T7 — Done + PR.**

**7.1 Validation strategy:** Refactor — suite green, no behavioral change; manual evidence: hit-area measurement, dialog a11y, tab keyboard nav.

## 8. Organization Guardrails

- **Branches:** `feature/devin-20261008-ui-polish`; never `main`.
- **CSS:** regenerate `tailwind.css`; don't hand-edit.
- **i18n:** dialog strings reuse existing `common.*`/delete keys; new ones via `L[...]`.
- **Scope:** polish items listed only — no new features.

## 9. Definition of Done

- [ ] RF-001…RF-005 implemented.
- [ ] All confirm/prompt call sites migrated or exceptions documented.
- [ ] Touch targets verified; tabs keyboard-navigable; Virtualize decision documented.
- [ ] Build + tests green.

## Open Questions / Pending Ambiguity

- `prompt` usage (`Workspace:337` add-knowledge-file-by-name): replace with input dialog vs. keep native — recommend small input dialog for consistency.
- Virtualize adoption vs. documented deferral — decide by list-size reality at impl time.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/198 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/190
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/182
