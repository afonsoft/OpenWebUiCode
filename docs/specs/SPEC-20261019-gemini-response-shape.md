# SPEC-20261019-gemini-response-shape

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `gemini-response-shape` |
| Type | `Bugfix` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `fix/devin-20261019-gemini-response-shape` |
| Ticket | `[A DEFINIR — criada via create-issues após aprovação]` |
| Status | `Delivered` |
| Fonte | 3 runs `failed` no chat `52c9929a-d3b4-45e6-9737-5d7f0688d2dc` (`/data/openwebui.db`), modelo `Gemini`, erro idêntico |

## 1. User Story

**As a** usuário com provider Gemini
**I want** a run não morrer quando o provider devolve um campo em formato inesperado
**So that** implementações longas não abortem no meio exigindo "Continue" manual.

**Problem context:**
Evidência do banco do container `openwebui` — 3 runs falharam com o MESMO erro (`ChatRuns.Error`):

```
The requested operation requires an element of type 'Object',
but the target element has type 'String'.
```

Runs afetadas: `4b72fadd` (step ~5), `accb84e6`, `d3aee8c1` — todas `failed` no meio do tool loop. A exceção é do `System.Text.Json` (`EnumerateObject`/`TryGetProperty`/`GetProperty`/`Deserialize` sobre `JsonElement`/`JsonNode` materializado como `String`). Provider `Gemini` (camada OpenAI-compatible) pode devolver campos com shape alternado — candidatos: `delta.content`/`message.content` como `string` vs array de parts, `error` como string vs objeto, `tool_calls.function.arguments` envelopado, `usage`/`logprobs` como string, ou chunk SSE `data:` com payload de erro string. O executor hoje não captura o shape real que quebrou — sem repro determinística.

## 2. Scope

**In scope:**
- RF-001: localizar o ponto de parse que lança a exceção (caminho de completions/stream do `ProviderService`/`ChatPipeline` para respostas OpenAI-compatible do Gemini).
- RF-002: normalização defensiva do shape — quando um campo esperado como `Object`/`Array` chega como `String`, tratá-lo de forma definida (ex.: `content` string→parts; `error` string→objeto sintético `{message}`; campo desconhecido → skip com log) em vez de propagar exceção que mata a run.
- RF-003: captura de evidência — ao falhar o parse de um chunk/response, logar o trecho ofensivo (truncado, sem secrets) em nível Warning para permitir diagnóstico futuro.
- RF-004: teste de regressão — replay do payload ofensivo (fixture) provando que a run sobrevive/degrada.

**Out of scope:**
- Mudança de provider ou de modelo.
- Retry policies gerais (já cobertas por `provider-resilience`).
- Normalização de shapes de outros providers além do path compartilhado OpenAI-compatible (o fix deve ficar no parser compartilhado se for lá a falha — beneficiando todos).

## 3. Technical Context

**Where the change happens:**
- `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` — chamadas de completion/stream ao provider.
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs` — `CompleteRoundAsync`/`CompleteResilientAsync` (linhas 630-740) — onde a exceção atravessa e mata a run.
- Parser SSE/OpenAI-compat: a localizar em T1 (busca por `EnumerateObject`, `TryGetProperty`, `JsonDocument.Parse`, `data:` SSE reader nos projetos Api/Infrastructure — excluir `bin/`, `obj/`, `workspaces/`).

**Files to read before implementing:**
- `CLAUDE.md`
- `src/OpenWebUI.Infrastructure/Services/ProviderService.cs`
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs` (630-780)
- `tests/OpenWebUI.Api.Tests/ToolStreamingTests.cs` (padrão de teste de stream)
- Evidência DB (já coletada): mensagem de erro exata + contexto das runs `4b72fadd`/`accb84e6`/`d3aee8c1`.

**Files to create or modify:**
```text
<arquivo do parser SSE/OpenAI-compat — identificar em T1>
src/OpenWebUI.Infrastructure/Services/ProviderService.cs   # (provável) normalização
tests/OpenWebUI.Api.Tests/*.cs                              # fixture + regression test
```

## 4. Requirements

### RF-001: Diagnóstico do ponto de falha
- **Description:** reproduzir/localizar o `EnumerateObject`/`Deserialize` que recebe `String` onde esperava `Object` no caminho Gemini; documentar o campo real (ex.: `choices[].delta.content`, `error`, `tool_calls`) no PR.
- **Input → Output:** run Gemini com payload alternado → local da exceção identificado (stack/propriedade).

### RF-002: Normalização defensiva
- **Description:** no ponto identificado, aceitar os dois shapes: `String` → coerção definida para o modelo interno (string→`{text}`/content parts; `error` string→`{message, type:"external"}`; objeto ausente→default documentado). Nunca propagar `InvalidOperationException` de shape como falha fatal de run quando houver coerção segura.
- **Rules:** coerção só para campos conhecidos; shape verdadeiramente irreconhecível → erro de run com mensagem clara (`provider-shape: campo X inesperado`); preservar comportamento para shapes corretos (zero regressão).
- **Input → Output:** chunk com campo string-instead-of-object → parse bem-sucedido/degradado, run continua.

### RF-003: Log de evidência
- **Description:** falha de parse loga Warning com o fragmento ofensivo truncado (~500 chars) e o campo — sem headers/tokens.
- **Input → Output:** shape irreconhecível → linha de log diagnóstica + run failed com erro claro.

### RF-004: Regressão
- **Description:** fixture JSON com o shape ofensivo (reconstruído a partir do erro; campo exato confirmado em T1) alimenta o parser → assert de coerção/degradação; teste no padrão `ToolStreamingTests`.

## 5. API Contract

N/A — comportamento interno de parse; nenhum endpoint muda.

## 6. Acceptance Criteria

- [ ] **Given** chunk SSE do Gemini com `<campo-identificado>` como string **when** parseado **then** coerção acontece e a run não falha.
- [ ] **Given** shape irreconhecível **when** parseado **then** run falha com `provider-shape: …` + log Warning com fragmento truncado.
- [ ] **Given** payloads corretos atuais **when** parseados **then** comportamento idêntico ao de hoje (regressão zero).
- [ ] **Given** as 3 runs que falharam **when** mesmo padrão se repete **then** run completa ou falha com erro diagnóstico — nunca com a exceção crua do JsonElement.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| `error` como string mid-stream | `data: {"error":"rate limited"}` | tratado como erro de provider (retry policy decide), não crash de parse |
| `content` null/string/array misto | delta.content variantes | coerção consistente para o acumulador de conteúdo |
| campo desconhecido string | campo fora do schema OpenAI | ignorado com log debug, sem falha |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** localizar o ponto exato (grep `EnumerateObject`/`TryGetProperty`/`Deserialize` no path de completions; se necessário, stack trace via log do container `docker logs openwebui` correlacionado aos timestamps das runs falhas 20:36-21:35 de 2026-10-18).
- [ ] **T2 — Test RED:** fixture com o shape ofensivo → teste falhando reproduzindo a exceção.
- [ ] **T3 — Fix:** normalização defensiva + log de evidência.
- [ ] **T4 — Test GREEN + regressão:** suite `dotnet test tests/OpenWebUI.Api.Tests` verde.
- [ ] **T5 — Done + PR:** `Status = Done`, mover para `docs/specs/`, PR na branch.

**7.1 Validation strategy**

| Item | Evidence |
| --- | --- |
| Build/test | `dotnet build` + `dotnet test` verdes |
| Regressão | teste novo com fixture do shape ofensivo |
| Diagnóstico | campo/shape real documentado no PR |

## 8. Organization Guardrails

- Sem commit em `main`; branch `fix/devin-20261019-gemini-response-shape`.
- Não logar conteúdo sensível no RF-003 (truncar, sem auth headers).
- Fix no parser compartilhado — não criar branch `if provider == "Gemini"` se o shape for válido para outros providers OpenAI-compatible.

## 9. Definition of Done

- [ ] Ponto de falha identificado e documentado.
- [ ] Coerção defensiva + log implementados com teste.
- [ ] `dotnet test` verde.
- [ ] SPEC arquivada em `docs/specs/` com `Status: Done` + `Delivered` (PR).
