# SPEC-20261010-multi-provider-connections

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `multi-provider-connections` |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/open-webui` |
| Branch | `feature/devin-20261010-multi-provider-connections` |
| Ticket | `[A DEFINIR — create-issues after approval]` |
| Status | `Completed` |
| Priority | `high` |
| Depends on | — |

## 1. User Story

**As an** admin configuring AI providers **I want** to pick Anthropic or Google AI Studio in the provider dropdown and have the official endpoint pre-filled, typing only my API key **So that** I can chat with Claude and Gemini models without hand-writing endpoint URLs or deploying OpenAI-compatible proxies.

**Problem context:** `ConnectionsConfig` today models only two provider kinds — Ollama (`OllamaBaseUrls`) and OpenAI-compatible (`OpenAiBaseUrls` + `OpenAiApiKeys`). `ProviderService` hard-codes the two protocol adapters (`/api/tags` + `/api/chat` for Ollama, `/models` + `/chat/completions` for OpenAI) and `ResolveProviderAsync` falls back to `"openai"` for any non-Ollama model. Users who want Claude (Anthropic) or Gemini (Google AI Studio) must run a compatibility shim; there is no native adapter, no pre-filled endpoint, and the connection rows in Settings ask for a URL the user must know by heart.

## 2. Scope

**In scope:**

- New provider types `anthropic` and `google` (Google AI Studio / Gemini API) in the connections model, with official endpoints pre-filled and editable: `https://api.anthropic.com` and `https://generativelanguage.googleapis.com/v1beta`.
- Provider type dropdown in the connections UI (SettingsModal `connections` tab and the Admin providers card): choosing `anthropic` or `google` pre-fills the Base URL and marks the API key field as required; `openai`/`ollama` rows keep today's behaviour.
- `ProviderService` native adapters for both providers:
  - Model listing — Anthropic `GET {base}/v1/models` (`x-api-key` + `anthropic-version` headers); Google `GET {base}/models` (`x-goog-api-key` header).
  - Chat completions — Anthropic `POST {base}/v1/messages`; Google `POST {base}/models/{model}:generateContent` and `:streamGenerateContent`, translated in both directions so the rest of the pipeline keeps speaking OpenAI SSE (`choices[0].delta.content`, `tool_calls`).
- Provider-aware routing: `ResolveProviderAsync` resolves `anthropic`/`google` models to their own adapter instead of falling back to `openai`; `ChatCompletionRequest.Connection` accepts the new provider ids.
- Env seeding: `ANTHROPIC_API_KEY(S)` and `GOOGLE_API_KEY(S)` (alias `GEMINI_API_KEY(S)`) seed first-boot connections, mirroring `OPENAI_API_*`.
- `.env.exemplo`, docs and i18n (8 locales) updates.

**Out of scope:**

- Vertex AI (GCP service-account) endpoints — only API-key AI Studio.
- AWS Bedrock / Azure OpenAI / other providers.
- Per-model workspace entities (`ModelEntry`) — unchanged.
- Model enable/disable toggles — covered by SPEC-20261010-model-visibility-settings.
- Admin-side global model filtering — same, future slice.

## 3. Technical Context

**Where the change happens:** `ConnectionsConfig` (Application contracts) gains a typed connection list; `ConfigService` reads/writes it; `ProviderService` (Infrastructure) fans out model listing and completions per provider type; `SettingsModal.razor` and `Admin.razor` render the provider dropdown + pre-filled endpoints; `ModelSelector.razor` labels the new provider groups.

**Files to read before implementing:**

- `CLAUDE.md` · `.claude/rules/global-rules.md`
- `src/OpenWebUI.Application/Contracts/ConfigContracts.cs` (`ConnectionsConfig`)
- `src/OpenWebUI.Application/Contracts/ModelContracts.cs` (`ModelInfo.Provider`, `ChatCompletionRequest.Connection`)
- `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` (adapters, `ResolveProviderAsync`, `CompleteWithTools*Async`)
- `src/OpenWebUI.Infrastructure/Services/ConfigService.cs` (`GetConnectionsAsync`, env seeding)
- `src/OpenWebUI.Client/Components/SettingsModal.razor` (`_openAiRows`/`_ollamaRows`, `ConnectionRow`)
- `src/OpenWebUI.Client/Pages/Admin.razor` (`ProvConnectionRow`, providers config tab)
- `src/OpenWebUI.Client/Components/ModelSelector.razor` (`ProviderLabel`)
- `tests/OpenWebUI.Api.Tests/` (`ConnectionsConfig([mock],[mock],["sk-x"])` pattern, fake `HttpMessageHandler` recipes)
- `.env.exemplo`, `docs/en` + `docs/pt`

**Files to create or modify:**

```text
src/OpenWebUI.Application/Contracts/ConfigContracts.cs      # ProviderConnection record + Providers list
src/OpenWebUI.Infrastructure/Services/ConfigService.cs      # read/write + env seeds ANTHROPIC_*/GOOGLE_*
src/OpenWebUI.Infrastructure/Services/ProviderService.cs    # anthropic + google adapters, routing
src/OpenWebUI.Infrastructure/Services/ProviderProxyService.cs  # passthrough (if it assumes openai shape)
src/OpenWebUI.Api/Endpoints/ConfigEndpoints.cs              # connections payload validation
src/OpenWebUI.Api/Endpoints/ModelEndpoints.cs               # provider ids passthrough
src/OpenWebUI.Client/Components/SettingsModal.razor         # provider dropdown + prefill
src/OpenWebUI.Client/Pages/Admin.razor                      # same pattern in admin card
src/OpenWebUI.Client/Components/ModelSelector.razor         # provider labels
src/OpenWebUI.Client/Services/ApiService.cs                 # typed rows contract
src/OpenWebUI.Client/wwwroot/i18n/*.json                    # 8 locales
.env.exemplo                                                # ANTHROPIC_API_KEY, GOOGLE_API_KEY
docs/en/*, docs/pt/*                                        # providers docs
tests/OpenWebUI.Api.Tests/*                                 # adapters, routing, seeding
tests/OpenWebUI.Client.Tests/*                              # SettingsModal provider combo
```

## 4. Requirements

### RF-001: Typed provider connections

- **Description:** `ConnectionsConfig` must accept entries with provider type `anthropic` or `google`, each with `BaseUrl`, `ApiKey` and optional display `Name`, without breaking the existing Ollama/OpenAI fields.
- **Rules:** new record `ProviderConnection(string Type, string BaseUrl, string? ApiKey, string? Name)` in `IReadOnlyList<ProviderConnection> Providers`; values `anthropic`/`google` (lowercase, validated on save → 400 on unknown type); existing `OllamaBaseUrls`/`OpenAiBaseUrls`/`OpenAiApiKeys`/`OllamaNames`/`OpenAiNames` untouched (backward compatible, no migration of stored configs).
- **Input → Output:** PUT connections payload with `providers: [{type:"anthropic", baseUrl:"https://api.anthropic.com", apiKey:"sk-ant-..."}]` → persisted; GET returns providers with `apiKey` masked/`keyConfigured` flag — never the raw key.

### RF-002: Provider dropdown with pre-filled official endpoints

- **Description:** each new connection row in Settings → Connections and the Admin providers card lets the user pick the provider type; selecting `anthropic` or `google` pre-fills the official Base URL, which stays editable for proxies/gateways.
- **Rules:** dropdown options `ollama` | `openai` | `anthropic` | `google`; pre-fill `https://api.anthropic.com` / `https://generativelanguage.googleapis.com/v1beta` when URL empty or still holds another official default; `ollama` hides the key field, `openai` keeps it optional, `anthropic`/`google` mark it required (client-side + server-side validation).
- **Input → Output:** user picks `anthropic` → row shows the Anthropic URL and waits only for the key.

### RF-003: Native model listing

- **Description:** `ProviderService.ListModelsAsync` must include models from `anthropic` and `google` connections, fetched with each provider's native endpoint and auth header.
- **Rules:** Anthropic → `GET {base}/v1/models` with `x-api-key` + `anthropic-version: 2023-06-01`, parse `data[].id`/`display_name`; Google → `GET {base}/models` with `x-goog-api-key`, parse `models[].name` (strip `models/` prefix for `Id`) + `displayName`; `ModelInfo.Provider` = `"anthropic"`/`"google"`; per-connection failure → empty list for that connection only (log debug, keep others); dedupe key `{provider}:{id}`; results join the existing 60s `ModelsCacheTtl` cache and the fingerprint must include the new providers list.
- **Input → Output:** Anthropic connection with key → `ModelInfo("claude-sonnet-4-5", "Claude Sonnet 4.5", "anthropic", "anthropic")` entries in `GET /api/models`.

### RF-004: Provider-aware routing

- **Description:** completions route to the adapter that owns the model; `Connection` in `ChatCompletionRequest` accepts `anthropic`/`google`.
- **Rules:** explicit `request.Connection` honoured when it is a known provider id; auto-resolution checks each provider's model list (not only Ollama then default-OpenAI); model present in several providers → deterministic order `ollama → openai → anthropic → google`; unknown model → `InvalidOperationException` like today.
- **Input → Output:** `ChatCompletionRequest{Model:"claude-sonnet-4-5"}` → anthropic adapter; `{Connection:"google", Model:"gemini-2.5-flash"}` → google adapter.

### RF-005: Anthropic chat adapter

- **Description:** translate OpenAI-shaped `ChatCompletionRequest`/SSE to the Anthropic Messages API and back, streamed and buffered, with tool calls.
- **Rules:** request → `POST {base}/v1/messages` (`x-api-key`, `anthropic-version: 2023-06-01`); system messages → top-level `system`; `messages` → `{role:user|assistant, content}`; `max_tokens` required → `params.max_tokens` or default `4096`; `tools` → Anthropic `tools` (`name`/`description`/`input_schema`); streamed `content_block_delta` `text_delta` → OpenAI `delta.content` chunks; `tool_use` blocks (`content_block_start`/`input_json_delta`) → OpenAI `tool_calls`; `message_stop`/`[DONE]` termination; buffered `CompleteWithToolsAsync` parses `content[]` `text` + `tool_use` → `ProviderCompletion` with `ToolCallsJson` in OpenAI shape for the tool loop, and re-serializes assistant tool messages as `tool_use`/`tool_result` blocks when echoing history.
- **Input → Output:** streamed Anthropic response → byte-identical OpenAI SSE shape consumed by `ChatStreamService` today.

### RF-006: Google chat adapter

- **Description:** translate to the Gemini API (`generateContent`/`streamGenerateContent`) and back, same OpenAI normalization.
- **Rules:** `POST {base}/models/{model}:generateContent` (buffered) and `:streamGenerateContent` + `?alt=sse` (streamed) with `x-goog-api-key`; system messages → `systemInstruction`; history → `contents` (`role: user|model`, `parts`); `tools` → `tools[0].functionDeclarations`; `generationConfig` ← `temperature`/`top_p`/`max_tokens`→`maxOutputTokens`; chunks `candidates[0].content.parts[].text` → OpenAI deltas; `functionCall` parts → OpenAI `tool_calls`; `functionResponse` parts for tool results; `finishReason` → `stop`/usage metadata when present.
- **Input → Output:** streamed Gemini response → OpenAI SSE chunks.

### RF-007: Env seeding

- **Description:** first-boot env vars create provider connections like `OPENAI_API_*` today.
- **Rules:** `ANTHROPIC_API_KEY` (+ optional `ANTHROPIC_BASE_URL`) and `GOOGLE_API_KEY`/`GEMINI_API_KEY` (+ optional `GOOGLE_BASE_URL`) → `Providers` entries; `;`-separated lists supported; documented in `.env.exemplo`.
- **Input → Output:** `ANTHROPIC_API_KEY=sk-ant-…` on first boot → anthropic connection present in `ConnectionsConfig`.

## 5. API Contract

**Endpoint:** `GET /api/v1/connections/config` / `PUT` (existing — response/request extended)

**Request (new field):**
```json
{
  "ollamaBaseUrls": ["http://localhost:11434"],
  "openaiBaseUrls": ["https://api.openai.com/v1"],
  "openaiApiKeys": ["sk-..."],
  "providers": [
    { "type": "anthropic", "baseUrl": "https://api.anthropic.com", "apiKey": "sk-ant-...", "name": "Claude" },
    { "type": "google", "baseUrl": "https://generativelanguage.googleapis.com/v1beta", "apiKey": "AIza...", "name": "Gemini" }
  ]
}
```

**Response (success):** keys masked — `{ "providers": [ { "type": "anthropic", "baseUrl": "...", "keyConfigured": true, "name": "Claude" } ] }`

**Expected errors:** `400` unknown provider type / missing key for anthropic|google; `401`; `403` non-admin.

## 6. Acceptance Criteria

- [ ] **Given** an admin on Settings → Connections **when** they pick `anthropic` in the provider dropdown **then** the row pre-fills `https://api.anthropic.com` and only the API key field is required.
- [ ] **Given** a saved anthropic connection **when** `GET /api/models` runs **then** Claude models appear with `provider: "anthropic"`.
- [ ] **Given** a google connection **when** `GET /api/models` runs **then** Gemini models appear with `provider: "google"`.
- [ ] **Given** a chat request for a Claude model **when** streamed **then** the client receives OpenAI-shaped deltas end-to-end (text + tool_calls when tools enabled).
- [ ] **Given** a chat request for a Gemini model **when** streamed **then** the client receives OpenAI-shaped deltas end-to-end.
- [ ] **Given** `ANTHROPIC_API_KEY`/`GOOGLE_API_KEY` env vars on an empty DB **when** the app boots **then** both connections exist in `ConnectionsConfig`.
- [ ] **Given** an unreachable anthropic/google endpoint **when** models are listed **then** that connection yields zero models and the rest still respond.
- [ ] **Given** the saved config **when** reloaded via GET **then** no raw API key is returned.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Unknown provider type | `{type:"azure"}` | 400 with message |
| Missing key | anthropic row, empty `apiKey` | 400 / disabled save |
| Model on two providers | same id on anthropic + openai | deterministic winner per RF-004 order |
| Anthropic `max_tokens` missing | no `params.max_tokens` | adapter sends default 4096 |
| Tool result echo | assistant `tool_use` + user `tool_result` | native block round-trip, no OpenAI shape leak |
| Google model id | `models/gemini-2.5-pro` | `Id` = `gemini-2.5-pro` |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** read files in section 3; confirm `ConnectionsConfig` serialization round-trips unknown fields.
- [ ] **T2 — Contract + config:** `ProviderConnection`, `Providers` on `ConnectionsConfig`/response, server validation, ConfigService read/write + env seeds (p1 PR).
- [ ] **T3 — Model listing + routing:** fetchers for both providers, cache fingerprint, `ResolveProviderAsync` provider-aware, `ModelInfo` labels (p1 PR).
- [ ] **T4 — Connections UI:** provider dropdown + prefill + required key in SettingsModal and Admin card; i18n; tailwind regen (p1 PR).
- [ ] **T5 — Anthropic adapter:** request/response/stream translation incl. tool_use/tool_result (p2 PR).
- [ ] **T6 — Google adapter:** same for generateContent/streamGenerateContent + functionCall/functionResponse (p3 PR).
- [ ] **T7 — Verification:** unit tests per adapter (fake handler), endpoint tests (seeded config, masked keys, 400s), client tests for the combo; `dotnet build` + `dotnet test` green.
- [ ] **T8 — Done + PRs:** flip SPEC `Status = Done`; open one PR per slice (p1 config/listing/UI, p2 anthropic, p3 google — stacked allowed).

**7.1 Validation strategy**

- `dotnet build OpenWebUI.slnx --configuration Release` — 0 errors.
- `dotnet test tests/OpenWebUI.Api.Tests` + `tests/OpenWebUI.Client.Tests` — 0 failed; new fixtures: anthropic/google model listing, request translation, tool round-trip, routing precedence, env seeding, masked-key GET, provider dropdown (client).
- `python3 tools/check-i18n-parity.py` + `check-css-classes.py` + `check-form-a11y.py` — clean.
- `tailwindcss` regenerated and committed.

## 8. Organization Guardrails

- Dedicated branch per slice → PR → merge; never push to `main`.
- `.github/workflows/` untouched unless explicitly approved.
- No secrets/keys in code, tests or fixtures — fake handlers only.
- Errors propagate as today (no canned values a client could persist).
- i18n: flat keys, all 8 locales; `L.T(key, ("name", val))` for interpolation.

## 9. Definition of Done

- [ ] Provider dropdown pre-fills Anthropic/Google official endpoints; only the key is required.
- [ ] `GET /api/models` lists Claude + Gemini models natively with the right provider tag.
- [ ] Chat streams from Claude and Gemini end-to-end through the OpenAI-shaped pipeline, including tool calls.
- [ ] Env seeding works for both providers.
- [ ] Keys never leave the server (masked responses).
- [ ] All tests green; coverage baseline not regressed.
- [ ] i18n parity + tailwind regenerated.
- [ ] SPEC `Status = Done`; slice PRs merged.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/319 |
