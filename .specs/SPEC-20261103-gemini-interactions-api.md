# SPEC-20261103-gemini-interactions-api

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `gemini-interactions-api` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feat/gemini-interactions-api-20261103` |
| Ticket | `GH#679` |
| Status | `Approved` |
| Parent | provider improvements sweep 2026-11-03 (gemini track) |

## 1. User Story

**As a** mantenedor da integração Gemini
**I want** a provider path migrada para a **Interactions API** (`POST /v1beta/interactions`) atrás de um feature flag por conexão
**So that** o codebase segue a API que o Google recomenda como default — `generateContent` virou "supported for existing integrations" (legacy) no `llms.txt` oficial — sem quebrar conexões existentes.

**Problem context:** a doc oficial (`aistudio.google.com/llms.txt`) declara: *"Use the Interactions API as the default across all workflows. generateContent is still supported for existing integrations, but is considered a legacy API."* Todo o caminho Google em `ProviderService.cs` (`CompleteGoogleAsync`, `CompleteGoogleWithToolsAsync`, `StreamGoogleAsync`, `StreamGoogleToolsAsync`, `BuildGooglePayload`, `NewGoogleRequest`, `ParseGoogleResponse`) fala `generateContent`/`streamGenerateContent`. A migração preserva a superfície OpenAI-compatible que o chat consome — só o wire muda.

## 2. Scope

**In scope:**
- Novo caminho `CompleteGoogleInteractionsAsync` + `StreamGoogleInteractionsAsync` roteado por flag `interactions_api` em `ProviderConnection.Settings` (opt-in por conexão; default off na fatia 1).
- Request: `POST {base}/v1beta/interactions` com headers `x-goog-api-key`, `Content-Type`, **`Api-Revision: 2026-05-20`** (header obrigatório novo).
- Mapeamento `ChatCompletionRequest` → body `{model, input, tools?, generation params, thinking config}`; `input` derivado de `Messages` (string simples ou turn list conforme doc).
- Tools: function declarations → `{type:"function",name,description,parameters}`; native tools → `{type:"google_search"|"code_execution"|"url_context"}` (mesmos nomes).
- Response parse: `steps[]` (`type:"model_output"` → content text; `type:"function_call"` → tool_calls OpenAI-shape; `type:"thought"`/`signature` → reasoning), `usage.total_input_tokens/total_output_tokens` → `ProviderUsage`.
- Streaming: `stream:true` → SSE `step.start`/`step.delta`/`step.end` com deltas tipados (`text`, `thought_signature`) → chunks OpenAI SSE existentes.
- Erros: mesma taxonomia `ProviderHttpException` (429/5xx → retryable; body de erro → mid-stream check), `Api-Revision` ausente/errado → mensagem clara.
- Contract tests file-based no padrão de `ProviderErrorContractTests`/`GeminiNativeToolsTests` (fixtures `.sse`/`.json`).

**Out of scope:**
- `previous_interaction_id` (stateful server-side turns) — fase 2; hoje o chat persiste e manda histórico completo (stateless).
- Background/long-decode agents (`gemini-4` Argon) — fase 2.
- `cachedContents` na Interactions API — verificar suporte na doc durante implementação; se não houver equivalente, `GeminiContextCacheService` permanece só no path legacy (degrade documentado).
- Remover o path `generateContent` — fica como fallback até o Google deprecar de fato.

## 3. Technical Context

**Where the change happens:** `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` — novo branch na resolução de provider Google (mesmo ponto onde `CompleteGoogleAsync` é despachado hoje); parsers/helpers novos em arquivo parcial ou `Providers/` se o padrão existir.

**Files to read before implementing:**
- `ProviderService.cs` — `CompleteGoogleAsync`, `CompleteGoogleWithToolsAsync`, `StreamGoogleAsync`, `StreamGoogleToolsAsync`, `BuildGooglePayload`, `NewGoogleRequest`, `ParseGoogleResponse`, `GoogleUsageFrom`, `ApplyProviderOverlay`
- `GeminiContextCacheService.cs` — acoplamento ao recurso cachedContents (verificar equivalente)
- `tests/OpenWebUI.Api.Tests/Providers/GeminiNativeToolsTests.cs`, `ProviderErrorContractTests.cs` — rede de segurança existente (não pode regredir)
- Doc: `https://aistudio.google.com/docs/get-started.md`, `/docs/interactions-overview.md`, `/docs/function-calling.md`

**Wire reference (get-started.md, 2026-05-20 revision):**
```jsonc
// POST /v1beta/interactions  (x-goog-api-key + Api-Revision: 2026-05-20)
{"model":"gemini-3.8-flash","input":"...","tools":[{"type":"function","name":"...","description":"...","parameters":{...}}]}
// → {"id":"v1_...","status":"completed","object":"interaction","model":"...",
//    "usage":{"total_tokens":N,"total_input_tokens":N,"total_output_tokens":N},
//    "steps":[{"type":"thought","signature":"..."},
//             {"type":"model_output","content":[{"type":"text","text":"..."}]},
//             {"type":"function_call","name":"...","arguments":{...},"id":"..."}]}
// function_result de volta: {"type":"function_result","name":"...","call_id":"<step.id>","result":[{"type":"text","text":"..."}]}
// stream:true → event: step.delta / data: {"index":N,"delta":{"text":"...","type":"text"},"event_type":"step.delta"}
```

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/ProviderService.cs (branch + builders/parsers Interactions)
tests/OpenWebUI.Api.Tests/Providers/GeminiInteractionsTests.cs (novo)
tests/OpenWebUI.Api.Tests/Fixtures/Providers/interactions-*.json/.sse (novo)
```

## 4. Requirements

### RF-001: Request shape Interactions
- **Description:** body `{model,input,...}`; `input` cobre system+user+assistant turns do histórico; headers `x-goog-api-key` + `Api-Revision: 2026-05-20` + extras da conexão. `generationConfig` → params equivalentes da nova API (temperature/top_p/max_tokens conforme doc); `thinkingConfig` → campo equivalente.

### RF-002: Tools e function calling
- **Description:** `tools[]` com `type:"function"` (name/description/parameters preservados) e `type:"google_search"|"code_execution"|"url_context"`; steps `function_call` → tool_calls OpenAI-shape com `call_id`; resultado volta via `function_result` no input seguinte.

### RF-003: Response/usage/thinking parse
- **Description:** `steps[].type` — `model_output` → texto; `thought`/`signature` → reasoning; `usage.total_input_tokens/total_output_tokens` → `ProviderUsage` (+ reasoning/cached tokens se o campo existir).

### RF-004: Streaming
- **Description:** `stream:true` → SSE `step.start`/`step.delta`/`step.end`; delta `text` → chunk content, `thought_signature` → reasoning delta; `usage` no final → usage chunk — mesma normalização OpenAI SSE do path legacy.

### RF-005: Feature flag + fallback
- **Description:** `ProviderConnection.Settings["interactions_api"]=true` ativa o novo path só naquela conexão; ausente/false → `generateContent` atual (zero regressão, suíte Gemini verde intacta).

### RF-006: Erros
- **Description:** 401/403 → credencial; 429/`RESOURCE_EXHAUSTED` → retryable; 5xx/`UNAVAILABLE`/`overloaded` → retryable; erro mid-stream → `MidStream`; `Api-Revision` errado/missing → mensagem explícita.

## 5. API Contract

Sem mudança externa — chat/OpenAI-compatible inalterado; a mudança é interna ao provider Google. Flag nova: `conn.Settings["interactions_api"]`.

## 6. Acceptance Criteria

- [ ] Conexão com flag → request vai para `/v1beta/interactions` com `Api-Revision` correto (assert via stub HTTP).
- [ ] Function call e native tools funcionam no novo wire (fixtures).
- [ ] Usage/thinking/function_call mapeados para `ProviderUsage`/tool_calls/reasoning.
- [ ] Streaming normaliza `step.delta` para SSE OpenAI-shape.
- [ ] Flag ausente → path legacy intacto (regressão Gemini suíte verde).
- [ ] Erros classificados como no legacy (429/5xx/mid-stream).
- [ ] `dotnet test` verde.

## 7. Task Plan

1. Flag + dispatch branch nos 4 métodos públicos Google (complete/stream/tools).
2. Builder `input`/tools/generation-config do wire novo.
3. Parser de `steps`/usage buffered + SSE `step.*`.
4. Fixtures + contract tests (stub HTTP por `StubHttpHandler`).
5. Regressão path legacy (flag off) + documentação no SPEC.

## 8. Effort Estimate

`~6-8h` — wire novo com superfície menor que generateContent, mas parse de steps/deltas tipados é trabalho real; rede de segurança de #660 absorve a regressão.
