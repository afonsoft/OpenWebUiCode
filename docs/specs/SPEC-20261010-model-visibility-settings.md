# SPEC-20261010-model-visibility-settings

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `model-visibility-settings` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/open-webui` |
| Branch | `feature/devin-20261010-model-visibility-settings` |
| Ticket | `[A DEFINIR — create-issues after approval]` |
| Status | `Completed` |
| Priority | `high` |
| Depends on | — (integrates provider labels from SPEC-20261010-multi-provider-connections when available) |

## 1. User Story

**As a** user with many configured models **I want** a settings page listing every model grouped by provider where I can disable the ones I don't use **So that** the chat model picker only shows the models I selected — like opencode's provider→models list — with everything enabled by default.

**Problem context:** `GET /api/models` returns every model from every connection and `ModelSelector` shows all of them grouped by provider. Users with many connections (several Ollama servers, OpenAI-compat gateways, Anthropic/Google after SPEC-20261010-multi-provider-connections) get a long, noisy picker with no way to hide unused models. There is no per-user model preference anywhere — `UserSettingsJson` exists but holds no model list.

## 2. Scope

**In scope:**

- Per-user disabled-model set persisted server-side inside `UserSettingsJson` (key `disabledModels`: array of `"{provider}:{modelId}"`), readable/writable through the existing `GET/POST /api/v1/users/user/settings`.
- New **Models** section in `SettingsModal` (all users, next to the existing tabs): lists every known model grouped under its provider header (opencode-style), each row with a toggle; search filter; per-provider "enable all / disable all"; refresh button re-fetching the live list.
- `GET /api/models` returns only models **enabled** for the authenticated user (default: all enabled); `GET /api/v1/models/all` (new, any authenticated user) returns the full catalog with an `enabled` flag per model for the settings page.
- `ModelSelector` keeps grouping by provider and automatically reflects the filtered list; if the persisted selection points to a now-disabled model, it falls back to the first enabled model.
- i18n keys in all 8 locales; regenerated `tailwind.css`.

**Out of scope:**

- Admin-global model visibility (workspace-level allow/deny for all users) — separate future slice.
- `ModelEntry` workspace models (`/api/v1/models/*` CRUD) — unchanged; custom models keep their own activation semantics.
- Ordering/pinning of models in the picker — only enable/disable.
- Per-chat model defaults.

## 3. Technical Context

**Where the change happens:** `ModelEndpoints` filters `GET /api/models` per user and adds `/api/v1/models/all`; `UserEndpoints` settings payload carries `disabledModels`; `SettingsModal.razor` gains the Models tab; `ModelSelector` handles the disabled-selected fallback.

**Files to read before implementing:**

- `CLAUDE.md` · `.claude/rules/global-rules.md`
- `src/OpenWebUI.Api/Endpoints/ModelEndpoints.cs`
- `src/OpenWebUI.Api/Endpoints/UserEndpoints.cs` (`GetUserSettingsAsync`/`UpdateUserSettingsAsync`)
- `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` (`ListModelsAsync`)
- `src/OpenWebUI.Client/Components/SettingsModal.razor`
- `src/OpenWebUI.Client/Components/ModelSelector.razor`
- `src/OpenWebUI.Client/Services/ApiService.cs` (`GetModelsAsync`, settings wrappers)
- `src/OpenWebUI.Client/wwwroot/i18n/*.json`
- `tests/OpenWebUI.Api.Tests/` (per-user settings + models fixtures)

**Files to create or modify:**

```text
src/OpenWebUI.Api/Endpoints/ModelEndpoints.cs          # filter + /all catalog
src/OpenWebUI.Api/Endpoints/UserEndpoints.cs           # disabledModels helpers (if needed)
src/OpenWebUI.Application/Contracts/ModelContracts.cs  # ModelInfo.Enabled or CatalogModel record
src/OpenWebUI.Client/Components/SettingsModal.razor    # Models tab
src/OpenWebUI.Client/Components/ModelSelector.razor    # fallback when selection disabled
src/OpenWebUI.Client/Services/ApiService.cs            # GetAllModelsAsync / settings helpers
src/OpenWebUI.Client/wwwroot/i18n/*.json               # 8 locales
tests/OpenWebUI.Api.Tests/*                            # filter, catalog, settings round-trip
tests/OpenWebUI.Client.Tests/*                         # toggles, groups, fallback
```

## 4. Requirements

### RF-001: Persisted per-user disabled set

- **Description:** each user's disabled models live in `UserSettingsJson.disabledModels`, a JSON array of `"{provider}:{modelId}"` strings.
- **Rules:** default (key absent) = everything enabled; ids matched case-insensitively on `{provider}:{id}`; unknown/stale entries ignored (a model that disappears stays in the set harmlessly and re-enables nothing); no schema migration — `SettingsJson` is free-form JSON already.
- **Input → Output:** `POST /api/v1/users/user/settings` body containing `disabledModels:["openai:gpt-4o-mini"]` → persisted; `GET` returns it back.

### RF-002: Filtered model list

- **Description:** `GET /api/models` returns only models enabled for the requesting user.
- **Rules:** unauthenticated → 401 as today; user with no `disabledModels` → full list; disabled entries removed before serialization; response shape unchanged (`{data:[ModelInfo]}`).
- **Input → Output:** user disabled `openai:gpt-4o-mini` → `GET /api/models` omits it while other users still see it.

### RF-003: Full catalog endpoint for settings

- **Description:** `GET /api/v1/models/all` returns every known model with `enabled` resolved for the caller.
- **Rules:** any authenticated user (not admin-only — every user curates their own picker); item = `{id, name, provider, ownedBy, enabled}`; provider grouping preserved client-side.
- **Input → Output:** → `200 {data:[{id:"gpt-4o-mini", provider:"openai", enabled:false}, ...]}`.

### RF-004: Models section in Settings

- **Description:** a **Models** tab in `SettingsModal` renders the catalog grouped by provider with a toggle per model, opencode-style.
- **Rules:** provider header (icon/label via `ModelSelector.ProviderLabel` equivalent + count `enabled/total`); rows: model name (+ id subtitle), accessible toggle (`role="switch"`, `aria-checked`); search box filters by name/id across groups; "enable all"/"disable all" per provider group; refresh re-pulls the catalog; empty state `models.none`; changes persist immediately per toggle (debounced) or on Save — pick one and keep consistent with the modal's Save pattern (recommended: same Save button flow as other tabs); mobile-friendly (list scrolls, toggles ≥ 44px hit area).
- **Input → Output:** toggling a model flips its `enabled` in the UI and updates `disabledModels` on save → survives reload.

### RF-005: Picker fallback

- **Description:** when the persisted chat selection (`webui.model`) is disabled or gone, `ModelSelector` falls back to the first enabled model.
- **Rules:** no crash/empty send; the combo never lists a disabled model; placeholder `models.none` only when zero enabled models across all providers; send stays disabled in that case (current behaviour).
- **Input → Output:** stored `webui.model=gpt-4o-mini` + it disabled → picker shows first enabled model selected.

## 5. API Contract

**Endpoint:** `GET /api/v1/models/all`
**Auth:** Bearer/JWT (any role)

**Response (success):**
```json
{
  "data": [
    { "id": "llama3.2", "name": "llama3.2", "provider": "ollama", "ownedBy": "ollama", "enabled": true },
    { "id": "gpt-4o-mini", "name": "gpt-4o-mini", "provider": "openai", "ownedBy": "openai", "enabled": false }
  ]
}
```

**Expected errors:** `401` unauthenticated.

**Endpoint:** `GET/POST /api/v1/users/user/settings` (existing — payload extended)
**Request (field):** `"disabledModels": ["openai:gpt-4o-mini", "anthropic:claude-sonnet-4-5"]`

## 6. Acceptance Criteria

- [ ] **Given** a fresh user **when** they open Settings → Models **then** every known model shows grouped by provider, all toggled ON.
- [ ] **Given** a model toggled off **when** saved and the chat picker reopens **then** the model is absent from `GET /api/models` and the combo.
- [ ] **Given** another user **when** they list models **then** the first user's disabled model still appears for them.
- [ ] **Given** the disabled model was the stored selection **when** the picker loads **then** it falls back to the first enabled model.
- [ ] **Given** every model disabled **when** the picker loads **then** it shows `models.none` and send stays disabled.
- [ ] **Given** a provider group **when** "disable all" is clicked **then** all its models toggle off together (and "enable all" restores).
- [ ] **Given** search text **when** typed **then** only matching models stay visible under their provider headers.
- [ ] **Given** `GET /api/v1/models/all` **when** called by a non-admin user **then** it returns 200 with the caller's `enabled` flags.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Stale disabled id | model removed upstream | entry ignored; no error |
| Duplicate model ids across providers | same id on ollama+openai | disabled key `{provider}:{id}` disables only that pair |
| Concurrent toggles | two tabs | last write wins (settings doc replace) |
| Zero models configured | no connections | settings list shows `models.none` |
| Disabled + admin workspace model | ModelEntry active | workspace visibility rules unchanged |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** read section-3 files; confirm `SettingsJson` round-trips new fields untouched.
- [ ] **T2 — Server:** `disabledModels` parsing, `/api/models` per-user filter, `GET /api/v1/models/all` catalog + tests.
- [ ] **T3 — Client:** Models tab (groups, toggles, search, enable/disable-all, save via existing settings flow), `ApiService` wrappers, picker fallback, i18n, tailwind regen.
- [ ] **T4 — Verification:** `dotnet build` + `dotnet test` (api + client) green; i18n parity + a11y + css-class guards clean.
- [ ] **T5 — Done + PR:** SPEC `Status = Done`, single PR `feature/devin-20261010-model-visibility-settings`.

**7.1 Validation strategy**

- `dotnet test tests/OpenWebUI.Api.Tests`: filter per user, default-enabled, stale ids, catalog flags, non-admin 200.
- `dotnet test tests/OpenWebUI.Client.Tests`: toggle persistence round-trip, grouped render, fallback selection.
- `python3 tools/check-i18n-parity.py`, `check-form-a11y.py`, `check-css-classes.py` — clean; tailwind regenerated.

## 8. Organization Guardrails

- Dedicated branch → PR → merge; `main` untouched.
- Settings writes keep the whole settings doc (full-replace semantics already in `UpdateUserSettingsAsync`) — client must merge `disabledModels` into the loaded document, not post a partial blob that drops other keys.
- No per-request provider fan-out regressions: filtering uses the cached `ListModelsAsync` + in-memory set diff.

## 9. Definition of Done

- [ ] Settings shows every model grouped by provider with enable/disable toggles (default all on).
- [ ] Chat combo lists only the user's enabled models, grouped by provider.
- [ ] Disabled-selection fallback works.
- [ ] All tests green; coverage baseline not regressed.
- [ ] i18n parity + tailwind regenerated.
- [ ] SPEC `Status = Done`; PR merged.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/319 |
