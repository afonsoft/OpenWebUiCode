# SPEC-20261008-a11y-form-semantics

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `a11y-form-semantics` |
| Type | `Bugfix` |
| Stack | `.NET` (Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261008-a11y-form-semantics` |
| Ticket | [#185](https://github.com/afonsoft/OpenWebUiCode/issues/185) (Epic [#182](https://github.com/afonsoft/OpenWebUiCode/issues/182)) |
| Status | `Completed` |

## 1. User Story

**As a** screen-reader user (and any password-manager user on `/auth`)
**I want** every form control to expose an accessible name and every visible label to be programmatically associated
**So that** fields announce their purpose on focus and autofill/sign-in works.

**Problem context:**
Design review findings B1–B3. The `.field > <label> + .input` pattern never associates the label (`for`/`id` or wrapping) — ~40 fields in `SettingsModal`, ~100+ in `Admin`, all of `WorkspaceItemEditor`, `Automations`, `NoteEditor`, `Calendar`, `Auth`. Many controls have no name at all: chat composer textarea, attachment `×`, `✕`/`🗑`/`▲▼` icon buttons, ShareDialog selects, batch checkboxes, QuestionPrompt/PermissionPrompt inputs, dozens of Admin placeholder-only fields. `Auth` has no `<form>` and no `autocomplete` — password managers degrade.

## 2. Scope

**In scope:**
- Associate every `<label>` with its control (`for`/`id` or wrap), app-wide.
- Give accessible names (`aria-label` localized, or `<label>`) to all unnamed inputs/buttons listed in B2.
- `Auth`: wrap in `<form @onsubmit>`, add `autocomplete` (`email`, `current-password`/`new-password`, `name`), `type` correctness; Enter submits from any field.
- `ChannelPage` send buttons + textareas; `ChatView` composer + attachment `×`.
- `ShareDialog` selects; `Workspace` checkbox/▲▼; `TerminalView` kill; `SettingsModal` `×`/`✕`; Admin glyph buttons (✎🗑⏻◌) + placeholder-only fields; `QuestionPromptCard`/`PermissionPromptCard` inputs.

**Out of scope:**
- Focus-ring styling → `SPEC-20261008-a11y-keyboard-focus`.
- Menu semantics → `SPEC-20261008-a11y-keyboard-focus` (RF-005).
- i18n missing keys/hardcoded strings → `SPEC-20261008-i18n-parity` (new aria-labels DO use `L[...]`).
- Touch-target sizing → `SPEC-20261008-ui-polish`.

## 3. Technical Context

**Where the change happens:**
`src/OpenWebUI.Client` — all `.razor` files with form controls: `Pages/{Auth,Admin,Automations,Calendar,ChannelPage,NoteEditor,Workspace,WorkspaceItemEditor}.razor`, `Components/{ChatView,SettingsModal,ShareDialog,Sidebar,TerminalView,QuestionPromptCard,PermissionPromptCard}.razor`.

**Files to read before implementing:**
- `design-review-20261008.md` findings B1–B3
- `tailwind.input.css` (`.field`, `.input`, `.check`, `.field-note`)
- `wwwroot/i18n/{en-US,pt-BR}.json` (reuse keys where they exist — e.g. `common.close`, `common.delete`)

**Files to create or modify:**
```text
src/OpenWebUI.Client/Pages/Auth.razor
src/OpenWebUI.Client/Pages/Admin.razor
src/OpenWebUI.Client/Pages/Automations.razor
src/OpenWebUI.Client/Pages/Calendar.razor
src/OpenWebUI.Client/Pages/ChannelPage.razor
src/OpenWebUI.Client/Pages/NoteEditor.razor
src/OpenWebUI.Client/Pages/Workspace.razor
src/OpenWebUI.Client/Pages/WorkspaceItemEditor.razor
src/OpenWebUI.Client/Components/ChatView.razor
src/OpenWebUI.Client/Components/SettingsModal.razor
src/OpenWebUI.Client/Components/ShareDialog.razor
src/OpenWebUI.Client/Components/Sidebar.razor
src/OpenWebUI.Client/Components/TerminalView.razor
src/OpenWebUI.Client/Components/QuestionPromptCard.razor
src/OpenWebUI.Client/Components/PermissionPromptCard.razor
src/OpenWebUI.Client/wwwroot/i18n/*.json        # any new label keys
```

## 4. Requirements

### RF-001: Associate all labels
- **Description:** Every `<label>` adjacent to a control gets `for` + control `id`, or wraps the control. Applies to the `.field` pattern everywhere and `Auth.razor:51,57,62`.
- **Rules:** ids deterministic-unique (`id="field-{name}"`); no duplicate ids within a page; `.check` wrapped labels already associate — leave.
- **Input → Output:** unassociated labels → programmatically associated.

### RF-002: Accessible names on unnamed controls
- **Description:** Add `aria-label` (localized via `L[...]`) or `<label>` to: `ChatView:321` composer, `:302` attachment `×`; `ChannelPage:146,189` textareas, `:150,:192` send buttons, `:166` `✕`, `:94-95` emoji reacts; `SettingsModal:170,:220,:234`; `ShareDialog:41,50,67,80`; `Workspace:112,:123-125,:130-132`; `TerminalView:35`; `QuestionPromptCard:38,52`; `PermissionPromptCard:39`; Admin glyph buttons + placeholder-only inputs/selects; `Sidebar` search/share inputs.
- **Rules:** prefer existing i18n keys; create `a11y.*` keys when absent (en-US + pt-BR).
- **Input → Output:** unnamed controls → announced names.

### RF-003: Real `<form>` + autocomplete on Auth
- **Description:** `Auth.razor:47-79` wrapped in `<form @onsubmit="SubmitAsync" @onsubmit:preventDefault>`; inputs get `autocomplete` (`name`, `email`, `current-password`/`new-password` per `_signUp`); submit button `type="submit"`; Enter works from every field; remove per-field `OnKeyDown` submit hack.
- **Input → Output:** div form → semantic form with autofill.

### RF-004: `aria-expanded` on toggle buttons
- **Description:** `Workspace.razor:123-125` ▲▼, `ChatWorkspacePanel` change-row expand buttons (`:98,:144`), `Calendar` day cells where expanded detail exists — expose `aria-expanded` + `aria-controls` or `aria-label` naming the region.
- **Input → Output:** toggles → announced state.

## 5. API Contract

N/A — client-only.

## 6. Acceptance Criteria

- [ ] **Given** SR/virtual cursor on Settings, Admin, Workspace editor, Automations, NoteEditor, Calendar, Auth **when** focusing any field **then** its label is announced.
- [ ] **Given** the chat composer **when** focused **then** an accessible name (e.g. "Message"/"Mensagem") is announced; attachment `×` announces "Remove {filename}".
- [ ] **Given** channel send buttons **when** focused **then** "Send"/"Reply" is announced.
- [ ] **Given** `/auth` **when** using a password manager **then** email+password autofill works; Enter in any field submits.
- [ ] **Given** an expander (knowledge detail, change row, ShareDialog) **when** toggled **then** expanded state is announced.

| Scenario | Input | Expected |
| --- | --- | --- |
| Field without visible label | icon button | `aria-label` present |
| Select in ShareDialog | focus | announces purpose |
| Emoji react button | focus | meaningful name, not raw codepoint-only |
| Duplicate ids after fix | DOM | none within a page |

## 7. Task Plan

- [ ] **T1 — Discovery:** enumerate every label/control pair missing association + every unnamed control (list from report B1–B2 verified in DOM).
- [ ] **T2 — Label association:** RF-001 across `.field` usages and Auth.
- [ ] **T3 — Names:** RF-002 + RF-004; add `a11y.*` keys to en-US/pt-BR.
- [ ] **T4 — Auth form:** RF-003.
- [ ] **T5 — Verification:** build+test green; spot-check with browser a11y tree / Lighthouse "form elements have labels" audit clean; fill DoD.
- [ ] **T6 — Done + PR:** `Status = Done` + PR.

**7.1 Validation strategy:** Bugfix — suite green; Lighthouse/axe `label` audit passes on audited routes; no visual regressions (label association must not change layout).

## 8. Organization Guardrails

- **Branches:** `feature/devin-20261008-a11y-form-semantics`; never `main`.
- **Workflows:** `.github/workflows/` untouched.
- **i18n:** every new string via `L[...]`; keys added to `en-US.json` + `pt-BR.json` (and other locales or tracked under i18n-parity).
- **CSS:** regen `tailwind.css` only if classes changed.
- **Scope:** semantics only — no visual/functional redesign.

## 9. Definition of Done

- [ ] RF-001…RF-004 implemented.
- [ ] Lighthouse/axe shows no unlabeled controls on reviewed routes.
- [ ] Password-manager autofill verified on `/auth`.
- [ ] Build + tests green.
- [ ] Guardrails respected.

## Open Questions / Pending Ambiguity

- Whether to introduce a small `Field`/`LabeledInput` component to prevent regression (recommended: yes, low-cost, enforces association).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/193 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/185
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/182
