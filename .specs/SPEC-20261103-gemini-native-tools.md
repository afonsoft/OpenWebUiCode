# SPEC-20261103-gemini-native-tools

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `gemini-native-tools` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feat/gemini-native-tools-20261103` |
| Ticket | `GH#TBD` |
| Status | `Approved` |
| Parent | provider improvement sweep 2026-11-03 (Gemini/Anthropic) |

## 1. User Story

**As a** usuário com conexão Google AI Studio (Gemini)
**I want** ativar tools nativas do Gemini (Google Search grounding, code execution, URL context) por modelo
**So that** respostas venham fundamentadas em busca real/executáveis sem depender de tool calls do cliente.

**Problem context:** `ProviderService` mapeia `functionDeclarations`/`functionCall` (function calling ~L2603, L2899) mas não emite as tools nativas da API (`google_search`, `code_execution`, `url_context` no array `tools`). O pipeline de retrieval do OpenWebUI usa google_pse externo; grounding direto do Gemini traria `groundingMetadata` (queries + `groundingChunks` com URIs + `searchEntryPoint`) que hoje não temos parser — fontes não renderizariam.

## 2. Scope

**In scope:**
- Knob por conexão/variant: `native_tools: google_search;code_execution;url_context` → entradas no `tools[]` do payload Gemini (`{google_search:{}}`, `{code_execution:{}}`, `{url_context:{}}` — shapes da API v1beta).
- Parse de `groundingMetadata`: `webSearchQueries`, `groundingChunks[].web.{uri,title}`, `groundingSupports` → sources da mensagem (mesmo shape do pipeline de retrieval atual).
- `executableCode`/`codeExecutionResult` parts → bloco colapsável na mensagem (como os builtin tools renderizam execução) ou, no mínimo, texto demarcado.
- `thinkingConfig`/`thinkingBudget` opt-in para modelos 2.5 (mesmo knob de budget do SPEC `anthropic-thinking-config`, payload Gemini: `generationConfig.thinkingConfig:{thinkingBudget:N}`).

**Out of scope:**
- Vertex AI / service account (escopo é AI Studio API key).
- Imagen/video tools (image engine já tem `GeminiImageEngine`).
- Context caching — SPEC separada (`gemini-context-caching`).

## 3. Technical Context

**Where the change happens:** `ProviderService.cs` — build de payload Google (~L2457-2663: `generationConfig` remap, tools/functionDeclarations), stream/non-stream parse (~L2899-3050: `part.functionCall`, `thought`), variant/connection config; UI de sources já existe (retrieval/web_search).

**Files to read before implementing:**
- `ProviderService.cs` — `BuildGooglePayload`, `StreamGoogle*`, parse de parts
- Como sources são renderizados (web_search/retrieval precedente)
- `model-capabilities.json` — flags por modelo (`supports_thinking`?, grounding por modelo)
- `.env.exemplo` GOOGLE_* seeds

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/ProviderService.cs
src/OpenWebUI.Infrastructure/Services/model-capabilities.json
src/OpenWebUI.Application/** (campo native_tools na connection/variant)
tests/OpenWebUI.Api.Tests/**
docs/en|pt/** (docs de conexão Google)
```

## 4. Requirements

### RF-001: Native tools no payload
- **Description:** variant `native_tools` (csv) vira entries `{google_search:{}}`/`{code_execution:{}}`/`{url_context:{}}` em `tools[]`, coexistindo com `functionDeclarations`; validação por capability do modelo (search só 1.5+/2.x, url_context 2.x).

### RF-002: Grounding → sources
- **Description:** `groundingMetadata` (stream chunk e response) parseado → lista de sources com title/URI + queries usadas; renderizado igual ao retrieval atual; `searchEntryPoint` descartado.

### RF-003: Code execution visível
- **Description:** parts `executableCode`/`codeExecutionResult` preservadas em stream e non-stream → bloco demarcado na mensagem (lang `python`, outcome do result); não misturar com texto comum.

### RF-004: Thinking budget Gemini
- **Description:** `thinking_budget` na variant → `generationConfig.thinkingConfig.thinkingBudget` (0=off, -1=dynamic); `part.thought` já detectado → confirma que vai ao canal reasoning.

### RF-005: Compatibilidade
- **Description:** sem native_tools configuradas → payload idêntico ao atual (zero mudança de wire).

## 5. API Contract

`tools[]` aceita mix de functionDeclarations + natives; `sources` seguem shape do retrieval.

## 6. Acceptance Criteria

- [ ] Modelo com `google_search` responde com grounding → sources renderizadas com URIs.
- [ ] `code_execution` produz bloco de código executável + outcome.
- [ ] `thinking_budget` refletido em `generationConfig.thinkingConfig` (fixture).
- [ ] Payload sem native_tools byte-idêntico ao de hoje.
- [ ] `dotnet test` verde.

## 7. Task Plan

1. Campo `native_tools` + mapeamento para `tools[]`.
2. Parse `groundingMetadata` → sources (stream + non-stream).
3. Parts de execução → bloco demarcado.
4. thinkingConfig.
5. Capabilities map + testes.

## 8. Effort Estimate

`~5-7h`.
