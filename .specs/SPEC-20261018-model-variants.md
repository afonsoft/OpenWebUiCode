# SPEC-20261018-model-variants

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `model-variants` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261018-model-variants` |
| Status | `Approved` |
| Parent | `docs/analise-opencode-v2.md` (P1.4 + P3.7/3.8) |

## 1. User Story

**As a** usuário com modelos de reasoning (o3, Claude, DeepSeek-R1) via OmniRoute/direto
**I want** variants `model#high`/`model#low` no seletor, settings/headers/body por variant, e overlays `headers`/`body` por provider
**So that** eu selecione nível de reasoning ou perfis de request sem duplicar entradas de modelo — paridade com opencode v2 `models` (`model#variant`, `capabilities.reasoning`, aliases, `compatibility.reasoningField`/`cacheFormat`) e `providers` (settings/headers/body overlays).

## 2. Scope

**In scope:**
- `ModelEntry.variants[] {id, settings{...}, headers{}, body{}}`; resolução `model#variant` merge settings: provider → model → variant → persona/command.
- `capabilities.reasoning.effort[]` em model/providers gera variants sintéticas (`low`,`medium`,`high`,`max`) quando `variants` não declarado.
- `compatibility.reasoningField` (nome do campo: `reasoning_effort`/`thinking`/`reasoning`) e `compatibility.cacheFormat: anthropic` (insere `cache_control` em system+tools+última user msg) — respeitando `OMNIROUTE` (gateway já traduz → flag off por default quando OmniRoute detectado).
- Overlays `ProviderEntry.headers`/`body`/`settings` aplicados em todo request do provider.
- Aliases de modelo (`aliases[]`) resolvíveis no seletor e em `model:` de commands/personas.

**Out of scope:**
- Provider-specific SDKs; variants criadas pela UI (v1 via config/JSON). `options` específicas de Responses API.

## 3. Technical Context

**Where the change happens:**
`ModelFilterService`/`ProviderCatalogService` (lista com `#variants`), `ChatPipeline`/`LlmClient` (resolução e montagem do request body/headers), `ModelEndpoints` (DTO enriquecido), seletor do composer.

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/Services/ModelFilterService.cs`
- `src/OpenWebUI.Infrastructure/Llm/*` (montagem de payload OpenAI-compatible)
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs`
- `src/OpenWebUI.Domain/Entities.cs` (Provider, Model entry shapes)

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities.cs                       # ModelVariant, CompatibilityBlock, aliases
src/OpenWebUI.Infrastructure/Llm/RequestComposer.cs    # NOVO: merge settings+headers+body
src/OpenWebUI.Infrastructure/Llm/AnthropicCacheFormat.cs # NOVO: cache_control injection
src/OpenWebUI.Infrastructure/Services/ModelFilterService.cs # expande #variants, resolve alias
src/OpenWebUI.Client/Components/ModelSelector.razor    # lista variants como entradas
tests/OpenWebUI.Api.Tests/ModelVariantTests.cs         # NOVO
tests/OpenWebUI.Api.Tests/CacheFormatTests.cs          # NOVO
```

## 4. Requirements

### RF-001: Variants declaradas
- **Description:** `variants:[{id:"high",settings:{reasoning_effort:"high"}},{id:"fast",settings:{reasoning_effort:"low"},headers:{...}}]`; `model#id` resolve merge com precedência variant > model > provider > global; variante inexistente → 400 `unknown variant`.
- **Rules:** id de variant regex `[a-z0-9-]+`; settings/headers/body são merge profundo (não replace de objeto inteiro).

### RF-002: Variants sintéticas por capability
- **Description:** `capabilities.reasoning.effort:["low","medium","high"]` gera variants `#{effort}` → `settings[reasoningField]=effort` quando `variants` vazio e provider não é OmniRoute-auto.

### RF-003: Compatibility
- **Description:** `compatibility.reasoningField` escolhe o nome do campo no payload; `cacheFormat:"anthropic"` adiciona `cache_control:{type:"ephemeral"}` a system+tools+última user text — via `AnthropicCacheFormat` desligável por `anthropic_cache:false` ou quando gateway já cacheia.

### RF-004: Aliases + provider overlays
- **Description:** `aliases:["gpt5","flagship"]` resolvidos em `model:` refs e no seletor (mostra id real + chip alias). `ProviderEntry.headers/body/settings` aplicados em cada request (headers não sobrescrevem `Authorization`/`Content-Type` por segurança — usar `options.headers` documentado).

## 5. Acceptance Criteria

- `claude#high` envia `reasoning_effort:"high"` no payload; `claude#low` envia low; base envia nada.
- `gpt5` alias resolve para o id real e grava o id real na run.
- Provider com `headers:{"x-team":"a"}` envia o header em todo request.
- cacheFormat anthropic injeta `cache_control` nos pontos corretos; desligado sob OmniRoute.
- Seletor mostra variants agrupadas sob o modelo; testes cobrem merge order, alias, compatibility.
