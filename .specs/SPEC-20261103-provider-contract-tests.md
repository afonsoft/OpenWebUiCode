# SPEC-20261103-provider-contract-tests

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `provider-contract-tests` |
| Type | `Refactor` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feat/provider-contract-tests-20261103` |
| Ticket | `GH#660` |
| Status | `Approved` |
| Parent | provider improvement sweep 2026-11-03 (Gemini/Anthropic) |

## 1. User Story

**As a** mantenedor do pipeline de providers
**I want** testes de contrato com fixtures reais (SSE + JSON) para Anthropic e Google
**So that** refactors no `ProviderService` (hoje ~3800 linhas) e os SPECs irmãos desta tranche tenham rede de segurança determinística.

**Problem context:** os paths Anthropic (`BuildAnthropicPayload`, `AnthropicStreamCtx`, tool_use/tool_result, cache_control) e Google (`generationConfig` remap, `functionCall` parts, `part.thought`) não têm testes dedicados — só `ModelVariantTests` os toca indiretamente; `ProviderServiceTests` cobre dispatch/listagem. Qualquer refactor de shape (ex.: os 4 SPECs acima) pode quebrar o wire sem alarme.

## 2. Scope

**In scope:**
- Golden fixtures versionadas em `tests/OpenWebUI.Api.Tests/Fixtures/Providers/`:
  - Anthropic: payload Messages (system+user+tool_use+tool_result+image), SSE stream completo (`message_start`, `content_block_start/delta` text+thinking+tool_use input_json_delta, `message_delta` usage, `message_stop`), `anthropic-error` event, response non-stream com usage+cache tokens.
  - Google: payload generateContent (system_instruction + contents + functionDeclarations + generationConfig), SSE streamGenerateContent (text parts, `functionCall` completo, `thought:true`, `usageMetadata` final, `promptFeedback` block), response non-stream.
- Testes NUnit: (a) payload builders → snapshot JSON; (b) stream parsers → sequência de deltas/tool calls/usage; (c) classification de erros.
- HttpClient mockável (handler fake) para os paths de fetch/stream — sem rede.
- Sem secrets nos fixtures — payloads sintéticos.

**Out of scope:**
- Testes de UI/E2E (chat-e2e já existe para fluxo feliz).
- Cobertura de OpenAI/Ollama (já têm testes indiretos; pode entrar depois no mesmo harness).

## 3. Technical Context

**Where the change happens:** `tests/OpenWebUI.Api.Tests/` (novo diretório `Providers/`); pontos testáveis: métodos `private static` de build — expor via `InternalsVisibleTo` já existente? verificar; senão extrair builders para classe `internal`/testável (`AnthropicPayloadBuilder`, `GooglePayloadBuilder`) — decisão na T1 seguindo o padrão do arquivo.

**Files to read before implementing:**
- `ProviderService.cs` — assinaturas dos builders/parsers, `AnthropicStreamCtx`, `ProviderUsage`, `ProviderToolCall`
- `ProviderServiceTests.cs`, `ModelVariantTests.cs` — padrão de teste + fakes existentes (HttpMessageHandler mock)
- `OpenWebUI.Api.Tests.csproj` — InternalsVisibleTo/pacotes

**Files to create or modify:**
```text
tests/OpenWebUI.Api.Tests/Providers/AnthropicContractTests.cs
tests/OpenWebUI.Api.Tests/Providers/GoogleContractTests.cs
tests/OpenWebUI.Api.Tests/Fixtures/Providers/{anthropic,google}/*.{json,sse.txt}
src/OpenWebUI.Infrastructure/Services/ProviderService.cs (só se extração de builders for necessária)
```

## 4. Requirements

### RF-001: Fixtures versionadas
- **Description:** arquivos `.json`/`.sse` no repo, sintéticos, cobrindo happy path + thinking + tool calls + usage + erro.

### RF-002: Payload builder snapshots
- **Description:** request → `JsonObject` comparado com fixture (normalizado); diffs falham com mensagem clara.

### RF-003: Stream parser contract
- **Description:** replay de fixture SSE → asserts de deltas ordenados, tool calls completas (args acumulados), `Usage` final, `DoneReason`; `thought`/thinking no canal reasoning.

### RF-004: Error classification
- **Description:** 429/529/`anthropic-error`/Gemini `promptFeedback.BLOCK`/`error` object → exceção/falha classificada correta (coerente com `ProviderRetryPolicy`).

## 5. API Contract

N/A (testes). Extração de builders mantém wire idêntico.

## 6. Acceptance Criteria

- [ ] ≥15 testes novos cobrindo os 4 eixos por provider.
- [ ] Quebra intencional de um campo no builder/parsers faz o teste correspondente falhar (sanity check).
- [ ] `dotnet test` verde; fixtures sem dados reais.

## 7. Task Plan

1. Definir harness (mock handler + acesso aos builders).
2. Fixtures Anthropic + testes.
3. Fixtures Google + testes.
4. Sanity check de mutação + gate.

## 8. Effort Estimate

`~4-6h`.
