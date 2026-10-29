# SPEC-20261008-i18n-parity

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `i18n-parity` |
| Type | `Bugfix` |
| Stack | `.NET` (Blazor WASM + JSON locales) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261008-i18n-parity` |
| Ticket | [#188](https://github.com/afonsoft/OpenWebUiCode/issues/188) (Epic [#182](https://github.com/afonsoft/OpenWebUiCode/issues/182)) |
| Status | `Completed` |

## 1. User Story

**As a** user on a non-English/non-Portuguese locale (de-DE, es-ES, fr-FR, it-IT, ja-JP, zh-CN)
**I want** the UI fully localized — no silent English fallbacks, no hardcoded Portuguese strings
**So that** the app speaks my language consistently.

**Problem context:**
Findings E1–E2. `en-US`/`pt-BR` carry 656 keys; the other six locales carry 598 — **58 keys missing** (mostly `admin.mcp_*`, `admin.perm_*`, `admin.config_*`) → English fallback in Admin. Hardcoded strings bypass `L[...]`: `BannerBar:21` `aria-label="dispensar"`, `Auth:59` `voce@exemplo.com`, `FolderPage:6` `"Pasta"`, `boot.js:180-184` boot error pt-BR, `WorkspaceItemEditor:222` preset text pt-BR, `CallOverlay:51`/`VoiceButton:35` default `Lang="pt-BR"` (should follow `L.Language`), and `dd/MM/yyyy HH:mm` date formats in `Automations`, `AutomationDetail`, `Archived`, `SharedChat`.

## 2. Scope

**In scope:**
- Add the 58 missing keys to de-DE/es-ES/fr-FR/it-IT/ja-JP/zh-CN (translated).
- Replace every hardcoded user-facing string with `L[...]`/`L.T` keys (add keys where absent).
- `VoiceButton`/`CallOverlay` `Lang` defaults → derive from `L.Language` (BCP-47) instead of literal `pt-BR`.
- Date/time formatting → culture-aware (`CultureInfo` per `L.Language`, or `ToString` with resolved culture).
- `boot.js` boot-failure copy: at minimum pt-BR default + note that full i18n isn't possible pre-boot (or bundle the 2–3 strings per locale if trivial).
- A key-parity guard (script/CI) so locales can't drift again.

**Out of scope:**
- RTL support (no RTL locales shipped).
- Locale additions/removals; server-side message localization.
- `index.html lang` → nav-landmarks SPEC (C3).

## 3. Technical Context

**Where the change happens:**
`src/OpenWebUI.Client/wwwroot/i18n/*.json` (8 locales + `locales.json`), `Services/LocalizationService.cs` (fallback chain pt-BR→en-US known-good), the hardcoded-string sites in `BannerBar`, `Auth`, `FolderPage`, `WorkspaceItemEditor`, `CallOverlay`, `VoiceButton`, `Automations`, `AutomationDetail`, `Archived`, `SharedChat`, `boot.js`.

**Files to read before implementing:**
- `design-review-20261008.md` findings E1–E2 (+ missing-key list)
- `Services/LocalizationService.cs` (`L.T` params, `Languages` list, fallback)
- Diff of `en-US.json` vs each locale (the 58 keys — regenerate list at impl time)

**Files to create or modify:**
```text
src/OpenWebUI.Client/wwwroot/i18n/de-DE.json
src/OpenWebUI.Client/wwwroot/i18n/es-ES.json
src/OpenWebUI.Client/wwwroot/i18n/fr-FR.json
src/OpenWebUI.Client/wwwroot/i18n/it-IT.json
src/OpenWebUI.Client/wwwroot/i18n/ja-JP.json
src/OpenWebUI.Client/wwwroot/i18n/zh-CN.json
src/OpenWebUI.Client/wwwroot/i18n/en-US.json     # new keys, if any
src/OpenWebUI.Client/wwwroot/i18n/pt-BR.json     # new keys, if any
src/OpenWebUI.Client/Components/BannerBar.razor
src/OpenWebUI.Client/Pages/Auth.razor
src/OpenWebUI.Client/Pages/FolderPage.razor
src/OpenWebUI.Client/Pages/WorkspaceItemEditor.razor
src/OpenWebUI.Client/Components/CallOverlay.razor
src/OpenWebUI.Client/Components/VoiceButton.razor
src/OpenWebUI.Client/Pages/{Automations,AutomationDetail,Archived,SharedChat}.razor
src/OpenWebUI.Client/wwwroot/js/boot.js           # boot-error copy, if localized
tools or scripts                                   # i18n parity check
```

## 4. Requirements

### RF-001: Locale key parity
- **Description:** All 6 locales gain the 58 missing keys, translated; key sets identical across locales.
- **Rules:** keep `admin.*`/`settings.*` naming; machine-assisted drafts acceptable but reviewed for glossary consistency (reuse existing terms within each file).
- **Input → Output:** 598-key locales → 656 keys each.

### RF-002: No hardcoded user-facing strings
- **Description:** Replace `dispensar` (BannerBar:21), `voce@exemplo.com` (Auth:59), `Pasta` (FolderPage:6), preset pt-BR text (WorkspaceItemEditor:222) with `L[...]` keys added to all locales.
- **Input → Output:** literals → `L[...]`.

### RF-003: Voice `Lang` follows UI locale
- **Description:** `VoiceButton`/`CallOverlay` `Lang` parameter defaults map `L.Language` → BCP-47 (`pt-BR`, `en-US`, …); callers may still override.
- **Input → Output:** hardcoded `pt-BR` STT/TTS lang → active locale.

### RF-004: Culture-aware dates
- **Description:** `dd/MM/yyyy HH:mm` literals → format via resolved `CultureInfo` (e.g. `ToString("g", culture)` or ICU `Intl` via JS) per `L.Language`.
- **Input → Output:** fixed pt-BR date shape → per-locale format.

### RF-005: Key-parity guard
- **Description:** Script (or CI check) that fails when locale key sets diverge from en-US baseline.
- **Input → Output:** drift detection → repeatable check.

### RF-006: Boot-error copy (best effort)
- **Description:** `boot.js` failure message localized to the same locale list (tiny inline map) or at minimum pt-BR+en-US fallback; document the pre-boot constraint.
- **Input → Output:** pt-BR-only boot error → localized or documented fallback.

## 5. API Contract

N/A — client-only.

## 6. Acceptance Criteria

- [ ] **Given** locale = de-DE (or any non-en/pt) **when** opening Admin → MCP/permissions sections **then** no English-fallback labels.
- [ ] **Given** locale = en-US **when** viewing banner dismiss, auth placeholder, folder title, shared/archived dates **then** English strings/format shown (no pt-BR leak).
- [ ] **Given** locale = ja-JP **when** using dictation/call **then** STT requests `ja-JP` recognition lang.
- [ ] **Given** the parity script **when** a key is added to one locale only **then** the check fails.

| Scenario | Input | Expected |
| --- | --- | --- |
| Missing key at runtime | any locale | falls back en-US→pt-BR (unchanged safety) |
| `admin.mcp_*` in fr-FR | render | French text, not English |
| Date in en-US | render | `M/d/yyyy h:mm` style, not `dd/MM` |
| New locale added later | parity check | auto-covered |

## 7. Task Plan

- [ ] **T1 — Diff:** regenerate the missing-key list per locale (en-US as baseline).
- [ ] **T2 — Translations:** RF-001 fill 58 keys × 6 locales (consistent glossary).
- [ ] **T3 — Hardcoded strings:** RF-002, RF-003, RF-004.
- [ ] **T4 — Boot copy + guard:** RF-005, RF-006.
- [ ] **T5 — Verification:** per-locale spot-checks, parity script green, build+test; fill DoD.
- [ ] **T6 — Done + PR.**

**7.1 Validation strategy:** Bugfix — suite green; parity check enforced; spot-check evidence per locale for the previously-missing sections.

## 8. Organization Guardrails

- **Branches:** `feature/devin-20261008-i18n-parity`; never `main`.
- **i18n policy:** pt-BR + en-US remain the maintained pair; translations must not alter key names or placeholders `{{var}}`/`{name}` shape used by `L.T`.
- **Scope:** strings/locales only.

## 9. Definition of Done

- [ ] RF-001…RF-006 implemented.
- [ ] All locales at key parity; parity guard enforced.
- [ ] No hardcoded user-facing strings remain in audited files.
- [ ] Build + tests green.

## Open Questions / Pending Ambiguity

- Translation quality for 6 locales (recommend machine draft + terminology reuse; human review optional per org).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/196 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/188
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/182
