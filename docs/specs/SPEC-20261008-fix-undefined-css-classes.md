# SPEC-20261008-fix-undefined-css-classes

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `fix-undefined-css-classes` |
| Type | `Bugfix` |
| Stack | `.NET` (Blazor WASM + Tailwind CSS v4) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261008-fix-undefined-css-classes` |
| Ticket | [#183](https://github.com/afonsoft/OpenWebUiCode/issues/183) (Epic [#182](https://github.com/afonsoft/OpenWebUiCode/issues/182)) |
| Status | `Completed` |

## 1. User Story

**As a** user on Settings/Admin/Calendar/Share screens
**I want** buttons and toggles to render with their intended styling
**So that** the UI doesn't show unstyled native controls or broken affordances.

**Problem context:**
Finding F. Five classes are used in markup but never defined in `tailwind.input.css` (nor the generated `tailwind.css`), so they render as unstyled browser defaults:
- `.button-sm` — 11× (`SettingsModal.razor:223,237,264`; `Admin.razor` ×8: :181,:398,:410,:434,:448,:508,:980,:1035,:1107,:1175 + more)
- `.primary-btn` — 2× (`ShareDialog.razor:84`, `Calendar.razor:45`)
- `.card-meta` — `Admin.razor:181`
- `.toggle` — `Admin.razor:1268,1288` (SAML/SCIM switches)
- `.hint` — `SettingsModal.razor:197,203` (likely meant `.field-note`)

## 2. Scope

**In scope:**
- Define (or replace with existing classes/Tailwind utilities) `.button-sm`, `.primary-btn`, `.card-meta`, `.toggle`, `.hint` — matching surrounding design tokens.
- `.toggle` should be a real switch-styled control for SAML/SCIM enable flags.
- Regenerate `wwwroot/css/tailwind.css` and commit (repo rule).
- Add a guard so the class drift doesn't recur (lint or doc note).

**Out of scope:**
- Accessibility of those controls (names/focus) → G1/G2 SPECs (may land in parallel; this SPEC covers styling only).
- Visual redesign of the affected controls — match existing `.msg-action`/`.auth-submit` family.

## 3. Technical Context

**Where the change happens:**
`src/OpenWebUI.Client/tailwind.input.css` (component layer) + the listed `.razor` usages. `.field-note` (defined) is the reference for `.hint`; `.msg-action`/`.auth-submit` for button sizing/tokens.

**Files to read before implementing:**
- `design-review-20261008.md` finding F
- `tailwind.input.css` full component layer (~L130-443)
- Each usage site for intended role (buttons vs. switch vs. meta text)

**Files to create or modify:**
```text
src/OpenWebUI.Client/tailwind.input.css
src/OpenWebUI.Client/wwwroot/css/tailwind.css        # regenerated
src/OpenWebUI.Client/Components/SettingsModal.razor  # .hint → .field-note (if rename chosen)
src/OpenWebUI.Client/Pages/Admin.razor               # only if replacing classes instead of defining
src/OpenWebUI.Client/Components/ShareDialog.razor    # only if replacing
src/OpenWebUI.Client/Pages/Calendar.razor            # only if replacing
```

## 4. Requirements

### RF-001: Resolve every undefined class
- **Description:** For each of the five classes, either (a) add a definition to `tailwind.input.css` consistent with the design system, or (b) replace usages with existing classes — decision per class.
- **Rules:** `.button-sm`/`.primary-btn` styled like the app's small/primary buttons (gray-900/dark white primary family); `.toggle` rendered as a switch (or replaced by `.check` checkbox pattern if a switch isn't intended); `.card-meta` muted meta text; `.hint` → reuse `.field-note` unless semantics differ.
- **Input → Output:** undefined classes → defined or replaced; rendered UI styled.

### RF-002: Regenerate Tailwind artifact
- **Description:** `wwwroot/css/tailwind.css` regenerated via `tailwindcss -i src/OpenWebUI.Client/tailwind.input.css -o ... --minify` and committed.
- **Input → Output:** source CSS change → committed generated artifact.

### RF-003: Drift guard
- **Description:** Add a CI/verify step or documented check comparing class names used in `.razor` against defined component classes (the audit used a simple grep diff).
- **Input → Output:** repeatable check → early detection of undefined classes.

## 5. API Contract

N/A — client-only.

## 6. Acceptance Criteria

- [ ] **Given** Settings → Connections/admin tabs and ShareDialog **when** rendered **then** all "add/delete/test" buttons show app styling (not native).
- [ ] **Given** Admin SAML/SCIM sections **when** rendered **then** `.toggle` controls render as intended switches (or are converted to the `.check` pattern).
- [ ] **Given** `grep -o 'class="..."'` audit **when** re-run **then** zero used-but-undefined classes remain.

| Scenario | Input | Expected |
| --- | --- | --- |
| `.button-sm` in SettingsModal | render | styled small button |
| `.primary-btn` in ShareDialog/Calendar | render | primary-styled button |
| Dark theme | render | new classes have `dark:` parity |

## 7. Task Plan

- [ ] **T1 — Discovery:** confirm each class's intended role at its usage sites (screenshot/DOM).
- [ ] **T2 — Define or replace:** RF-001 per-class decision; update `tailwind.input.css`/`*.razor`.
- [ ] **T3 — Regen + guard:** RF-002, RF-003.
- [ ] **T4 — Verification:** visual check of each site (light/dark); build+test; fill DoD.
- [ ] **T5 — Done + PR.**

**7.1 Validation strategy:** Bugfix — suite green; visual confirmation of previously-unstyled controls; zero undefined-class matches on audit grep.

## 8. Organization Guardrails

- **Branches:** `feature/devin-20261008-fix-undefined-css-classes`; never `main`.
- **CSS:** never hand-edit generated `tailwind.css`.
- **i18n/dark:** new classes get `dark:` variants per family convention.
- **Scope:** styling only.

## 9. Definition of Done

- [ ] All five classes resolved (defined or replaced).
- [ ] `tailwind.css` regenerated + committed.
- [ ] Drift guard in place (script or documented check).
- [ ] Build + tests green; affected screens visually verified.

## Open Questions / Pending Ambiguity

- `.toggle`: real switch component vs. `.check` checkbox — recommend switch (matches the SAML/SCIM intent + upstream style).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/191 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/183
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/182
