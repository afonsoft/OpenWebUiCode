# SPEC-20261008-a11y-nav-landmarks

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `a11y-nav-landmarks` |
| Type | `Bugfix` |
| Stack | `.NET` (Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261008-a11y-nav-landmarks` |
| Ticket | [#184](https://github.com/afonsoft/OpenWebUiCode/issues/184) (Epic [#182](https://github.com/afonsoft/OpenWebUiCode/issues/182)) |
| Status | `Completed` |

## 1. User Story

**As a** screen-reader / keyboard / browser user
**I want** route navigation to use real links and each page to expose proper landmarks and a heading
**So that** links behave like links (new tab, URL preview, SR "link" role), `FocusOnNavigate` can move focus, and the page structure is navigable.

**Problem context:**
Findings C1–C3. Route navigation is implemented as `<button @onclick="Nav.NavigateTo(...)">` in the sidebar (new-chat, chat items), workspace section tabs (real routes `/workspace/*`), edit/back buttons, and `FolderPage` reinvents links via `role="link" tabindex="0"`. No `href` means no open-in-new-tab, no URL preview, weaker SR/SEO semantics. Pages `/`, `/c/*`, `/channels/*` have no `h1` — `FocusOnNavigate Selector="h1"` silently no-ops there. The sidebar (primary navigation) is `<div>`s, not `<nav>`. `index.html` hardcodes `lang="en"` vs. pt-BR default.

## 2. Scope

**In scope:**
- Replace navigation-buttons with `<a href>`/`NavLink` where the action is route navigation (sidebar chat items + new chat, workspace tabs, edit/back buttons, notes/automation links, folder rows).
- Add `h1` (visible or visually-hidden) to `/`, `/c/*` (chat title or app name), `/channels/*` (channel name → real heading).
- Wrap sidebar nav in `<nav aria-label>`; keep `<main>`/`Admin` nav as-is (already correct).
- `index.html` `lang` default aligned with `DefaultLanguage` (pt-BR).
- Keep drag/pin/menu buttons as buttons (they're actions, not navigation).

**Out of scope:**
- Menu/dialog keyboard behavior → keyboard-focus SPEC.
- Accessible names for those buttons → form-semantics SPEC (this SPEC changes element type only where it's a route).
- URL/route-structure redesign; SEO beyond semantics.

## 3. Technical Context

**Where the change happens:**
`src/OpenWebUI.Client` — `Components/{Sidebar,ChatView}.razor`, `Pages/{Workspace,Notes,Automations,AutomationDetail,NoteEditor,WorkspaceItemEditor,FolderPage,ChannelPage,SharedChat}.razor`, `wwwroot/index.html`, `App.razor` (FocusOnNavigate stays `h1`).

**Files to read before implementing:**
- `design-review-20261008.md` findings C1–C3
- `MainLayout.razor` (skip-link → `#main-content`), `Admin.razor:32` (`<nav aria-label>` reference)
- `_Imports.razor` (`NavLink` available)

**Files to create or modify:**
```text
src/OpenWebUI.Client/Components/Sidebar.razor
src/OpenWebUI.Client/Components/ChatView.razor      # h1 for chat routes
src/OpenWebUI.Client/Pages/ChannelPage.razor        # channel name → h1
src/OpenWebUI.Client/Pages/Workspace.razor          # tabs + edit → links
src/OpenWebUI.Client/Pages/Notes.razor
src/OpenWebUI.Client/Pages/Automations.razor
src/OpenWebUI.Client/Pages/AutomationDetail.razor
src/OpenWebUI.Client/Pages/NoteEditor.razor
src/OpenWebUI.Client/Pages/WorkspaceItemEditor.razor
src/OpenWebUI.Client/Pages/FolderPage.razor         # role=link row → <a>
src/OpenWebUI.Client/wwwroot/index.html             # lang default
src/OpenWebUI.Client/tailwind.input.css             # .sr-only utility if absent + regenerated css
```

## 4. Requirements

### RF-001: Real links for route navigation
- **Description:** Any element whose sole action is `Nav.NavigateTo(<route>)` becomes `<a href="<route>">` (Blazor intercepts → SPA nav preserved) or `NavLink` when active-state styling is needed (`Workspace` tabs).
- **Rules:** keep current classes/visuals; middle-click/⌘-click must open new tab; no `NavigateTo` left for pure navigation (exceptions allowed only where auth-guard logic runs first — document them).
- **Input → Output:** nav buttons → anchors with href.

### RF-002: Page `h1` everywhere
- **Description:** `/` & `/c/*` render a visually-hidden `h1` (chat title or "Open WebUI"/new chat); `/channels/*` promotes the channel-name div (`ChannelPage.razor:18-20`) to `h1`; verify every other route already has one (`Admin:13`, `Workspace:17`, `Calendar:9`, `Automations:10`, `Archived:10`, `Notes:11`, `FolderPage:10`, `AutomationDetail:10`, `NoteEditor:15`, `TerminalPage:8`, `SharedChat:25` ✓).
- **Input → Output:** routes without `h1` → exactly one `h1`.

### RF-003: `nav` landmark on sidebar
- **Description:** Sidebar chat/folder navigation region wrapped in `<nav aria-label="…">` (localized); rail icons region too if navigational.
- **Input → Output:** divs → `<nav>`.

### RF-004: `html lang` matches default locale
- **Description:** `index.html:2` `lang="en"` → `lang="pt-BR"` (matches `LocalizationService.DefaultLanguage`); `setLang` keeps overriding at runtime.
- **Input → Output:** static `en` → `pt-BR`.

### RF-005: `FolderPage` row as anchor
- **Description:** `FolderPage.razor:31-36` `role="link" tabindex="0"` + Enter-handler div becomes a plain `<a href="/c/{id}">` (native focus + Enter free).
- **Input → Output:** simulated link → real link.

## 5. API Contract

N/A — client-only.

## 6. Acceptance Criteria

- [ ] **Given** a chat item / workspace tab / edit link **when** inspected **then** it is an `<a>` with correct `href`; ⌘/middle-click opens a new tab.
- [ ] **Given** any route (`/`, `/c/x`, `/channels/x`, `/admin`, `/workspace/*`…) **when** SPA-navigated to **then** focus moves to the page `h1` (FocusOnNavigate works).
- [ ] **Given** SR landmark list **when** opened **then** `navigation` (sidebar) + `main` appear.
- [ ] **Given** `document.documentElement.lang` **when** read pre/post locale switch **then** starts `pt-BR` and follows `L.Language`.

| Scenario | Input | Expected |
| --- | --- | --- |
| Sidebar chat item | ⌘-click | opens `/c/{id}` new tab |
| Folder chat row | Enter/click | navigates; is real `<a>` |
| `/channels/x` load | SR | `h1` = channel name, focus moved |
| Non-navigation buttons | audit | remain `<button>` |

## 7. Task Plan

- [ ] **T1 — Discovery:** enumerate every `NavigateTo` bound to markup; classify navigation vs. action.
- [ ] **T2 — Anchors:** RF-001 + RF-005 conversions preserving styles/active state.
- [ ] **T3 — Headings & landmarks:** RF-002, RF-003 (add `.sr-only` utility if missing; regen css).
- [ ] **T4 — lang:** RF-004.
- [ ] **T5 — Verification:** ⌘-click checks, FocusOnNavigate check per route, landmark audit; build+test; fill DoD.
- [ ] **T6 — Done + PR.**

**7.1 Validation strategy:** Bugfix — suite green; manual link/landmark evidence; Lighthouse `link-name`/`landmark` audits clean.

## 8. Organization Guardrails

- **Branches:** `feature/devin-20261008-a11y-nav-landmarks`; never `main`.
- **Workflows:** untouched.
- **i18n:** `aria-label`s/headings via `L[...]`.
- **Scope:** semantics only — visuals unchanged.
- **CSS:** regenerate `tailwind.css` if `.sr-only` (or other class) added.

## 9. Definition of Done

- [ ] RF-001…RF-005 implemented.
- [ ] Every route has exactly one `h1`; FocusOnNavigate verified.
- [ ] Sidebar is a `nav` landmark; anchors behave as links (new-tab verified).
- [ ] Build + tests green; no visual regression.

## Open Questions / Pending Ambiguity

- Chat `h1` content on `/` (empty state) — recommend "Open WebUI" (static) since there's no chat title yet.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/192 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/184
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/182
