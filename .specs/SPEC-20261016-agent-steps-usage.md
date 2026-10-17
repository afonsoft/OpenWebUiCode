# SPEC-20261016-agent-steps-usage

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `agent-steps-usage` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261016-agent-steps-usage` |
| Ticket | [#405](https://github.com/afonsoft/open-webui/issues/405) · Epic [#398](https://github.com/afonsoft/open-webui/issues/398) |
| Status | `In implementation` |
| Fonte | análise opencode v2.0.31 (`agent` `steps` + `llm.ts:226-244` toolChoice none no último step; `session/usage.ts` custo por step) |

## 1. User Story

**As a** mantenedor
**I want** limite de steps por agent definição, aviso de "último step" ao modelo e métricas de tokens/custo por step
**So that** agentes baratos/rodeiros têm teto ajustável, o modelo se encerra graciosamente e eu vejo o custo real de cada run.

**Problem context:**
O teto do tool loop é `maxRounds=5` global fixo (`ChatPipeline.cs:523`) e o modelo só descobre ao bater no teto — recebe round final forçado sem tools, sem aviso. Não há contabilidade de tokens/custo por step visível na run. O opencode permite `steps:` por agent, injeta prompt de finalização no último step com `toolChoice:"none"` e calcula custo por modelo+tokens.

## 2. Scope

**In scope:**
- RF-001: `steps` opcional em `*.agent.md` frontmatter + `AgentDefinition` (default: ilimitado→teto global config `AgentLoop:MaxRounds`, default 5 hoje → subir para 25 quando per-agent existir).
- RF-002: no último step permitido: tools desanunciadas (`toolChoice none` equivalente — `Tools=null`) + mensagem assistant sintética "último passo — resuma o estado e próximos passos" (padrão `MAX_STEPS_PROMPT`).
- RF-003: steer/input do usuário reseta o contador (já existe via `round=-1` — manter semântica: input novo recomeça turno).
- RF-004: `usage` por round: `{promptTokens, completionTokens, reasoningTokens?}` capturados do provider quando disponíveis; acumulados na run (`run.Meta.usage` + por-step lista).
- RF-005: custo estimado quando `ModelEntry.Meta` tiver `cost:{input,output}` por 1M tokens; exibir no painel da run (endpoint `GET /runs/{id}` meta).
- RF-006: endpoint/feed expõe contagem de steps (`step`, `maxSteps`) na UI da run.

**Out of scope:**
- Billing/quota enforcement.
- Custos de providers que não reportam usage (estimar por tokenizer aproximado é fase 2).

## 3. Technical Context

- `src/OpenWebUI.Infrastructure/Services/AgentDefinitionService.cs` — parse de `*.agent.md`.
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs` — loop, `CompleteRoundAsync` (onde usage chega do provider — verificar se `ProviderCompletion` carrega usage).
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` — persistir `run.Meta`, publicar no SSE.
- `src/OpenWebUI.Domain/Entities.cs` — `ChatRun`/`ModelEntry` MetaJson.

## 4. Requirements

### RF-001: `steps` por agent
- **Input → Output:** frontmatter `steps: 8` → `AgentDefinition.Steps`; run com agent selecionado usa `min(steps, MaxRounds)`; sem agent → `MaxRounds`.

### RF-002: Último step
- **Description:** quando `round == max-1`, round executa com `Tools=null` + prompt sintético de wrap-up antes das mensagens (não no histórico do usuário).

### RF-003: Reset por input
- **Description:** steer promovido reseta allowance (comportamento atual); notas de contexto NÃO resetam.

### RF-004: Usage por round
- **Description:** se provider retorna `usage` no final do stream, acumular `{round, prompt, completion}` em `run.Meta`; ausente → omite (não estima neste SPEC).

### RF-005: Custo
- **Description:** `ModelEntry.Meta` opcional `{"cost":{"input":x,"output":y}}` (USD/1M) → `cost = in*pin + out*pout`; `run.Meta.usage.costUsd` + exibido no detalhe da run.

### RF-006: Step counter SSE
- **Description:** evento `phase: generating` passa a incluir `{step, maxSteps}` quando conhecido.

## 5. API Contract

`GET /api/v1/runs/{id}` → meta inclui:
```json
{ "usage": { "steps": 4, "promptTokens": 120030, "completionTokens": 8021, "costUsd": 0.041 } }
```

## 6. Acceptance Criteria

- [x] **Given** agent com `steps: 3` **when** a run consome 3 rounds com tools **then** o 3º round roda sem tools + prompt de wrap-up.
- [x] **Given** steer do usuário no meio **when** promovido **then** contador volta a 1.
- [x] **Given** provider com usage **when** run termina **then** `run.Meta.usage` agrega tokens por step e total.
- [x] **Given** `ModelEntry.Meta.cost` presente **when** usage existe **then** `costUsd` calculado e exposto.
- [x] **Given** provider sem usage **when** run termina **then** `usage.steps` presente, campos de token omitidos (sem valor inventado).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| `steps: 0` ou negativo | frontmatter | ignora → default |
| `steps` > `MaxRounds` | `steps: 99` | min() — MaxRounds vence |
| Cost parcial (só input) | meta | cost só com ambos os campos |

## 7. Task Plan

- [x] **T1 —** `AgentDefinition.Steps` + parse + validação.
- [x] **T2 —** Loop: resolução de max por run + wrap-up round (Tools=null + prompt).
- [x] **T3 —** Captura de usage no `ProviderCompletion` (stream + buffered) + persistência em `run.Meta`.
- [x] **T4 —** Cost calc + exposição no endpoint.
- [x] **T5 —** SSE `{step,maxSteps}` + UI mínima (badge).
- [x] **T6 —** Testes + build; DoD; PR.

**7.1 Validation strategy:** unit tests (steps resolve, cost calc, reset) + integration run com usage fake; ≥80%.

## 8. Organization Guardrails

- Branch `feature/devin-20261016-agent-steps-usage`.
- Usage nunca inventada — ausência é omitida, não zero.
- `cost` é estimativa exibida como tal (`est.`), não billing.
- Prompt de wrap-up não entra no histórico persistido como mensagem do usuário.

## 9. Definition of Done

- [x] RF-001..RF-006 implementados e testados.
- [x] Critérios da seção 6 verdes.
- [x] build/test verdes.

## Open Questions / Pending Ambiguity

- `ProviderCompletion` precisa de campo `Usage` — confirmar shape no T3 (OpenAI `usage.prompt_tokens`/`completion_tokens`; Ollama `eval_count`/`prompt_eval_count`).
