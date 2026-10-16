# SPEC-20261016-parallel-tool-calls

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `parallel-tool-calls` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261016-parallel-tool-calls` |
| Ticket | [#401](https://github.com/afonsoft/open-webui/issues/401) · Epic [#398](https://github.com/afonsoft/open-webui/issues/398) |
| Status | `Approved` |
| Fonte | análise opencode v2.0.31 (`session/runner/step.ts:86-151` — tool calls executam como fibers concorrentes durante o stream) |

## 1. User Story

**As a** usuário cujo modelo emite várias tool calls independentes num round
**I want** que leituras/greps/globs/fetches paralelos executem concorrentemente
**So that** um round de exploração com 5 leituras não demore 5× o tempo sequencial.

**Problem context:**
`ChatPipeline.RunToolLoopAsync` executa `foreach (var call in step.ToolCalls)` sequencial (`ChatPipeline.cs:623`). Modelos frequentemente emitem batches de `file_read`/`file_grep`/`fetch_url` independentes — no opencode cada call vira fiber concorrente e os resultados se materializam em ordem estável. Nossa execução serial desperdiça latência real em runs de exploração/codificação.

## 2. Scope

**In scope:**
- RF-001: classificação de calls paralelizáveis — somente tools declaradas `SafeInParallel` (leitura pura: `file_read`, `file_grep`, `file_glob`, `file_list`, `web_search`, `fetch_url`, `lsp_*`, `skill`, `job_output`, `browser_screenshot`, `n8n_list_workflows`, readers de memória/agent).
- RF-002: execução concorrente de um batch paralelizável com resultados **em ordem da emissão** (tool message index = call index) — o modelo não percebe diferença.
- RF-003: tools mutáveis (`file_write`, `file_edit`, `apply_patch`, `shell_exec`, `worktree_*`, `delegate_task`, `code_interpreter`, `job_*` escrita, MCP/HTTP externas, `plan_exit`, `new_context`, `checkpoint`, `todo_write`?) executam **sequenciais** como hoje — um batch misto divide em grupos: paralelos primeiro? Não — ordem preservada: executa-se em "runs" contíguos de paralelizáveis.
- RF-004: approvals/gates mantêm comportamento — o prompt de aprovação continua sequencial por call (não dispara N prompts simultâneos ao usuário); gate é avaliado na ordem e resultados de deny respeitam a posição.
- RF-005: `OnPhaseAsync`/`OnCallAsync`/`OnResultAsync` continuam emitindo eventos; paralelismo visível na UI como "running 3 tools" — eventos individuais por call (callId), sem quebra de ordem no feed.
- RF-006: cap de concorrência configurável (`BuiltinTools:MaxParallel`, default 4).

**Out of scope:**
- Execução de tools durante o streaming do provider (opencode inicia a fiber quando o `tool-call` chega mid-stream) — fase 2; aqui paraleliza pós-round.
- Cancelamento seletivo por tool.

## 3. Technical Context

- `src/OpenWebUI.Api/Completions/ChatPipeline.cs` — `RunToolLoopAsync` (foreach sequencial, linhas 623-664).
- `src/OpenWebUI.Infrastructure/ChatTools/IBuiltinChatTool.cs` — contrato (adicionar `bool SafeInParallel` ou atributo).
- `src/OpenWebUI.Infrastructure/Services/ToolExecutor.cs` — `ExecuteAsync` (thread-safety: sem estado compartilhado entre calls — verificar `BuiltinToolContext` immutável por call).
- `ChatRunExecutor` callbacks — são async e tocam `DbContext` (`PublishToolResultAsync`) — **EF Core não é thread-safe**: resultados devem ser publicados sequencialmente após `Task.WhenAll` (ou serializar o callback com SemaphoreSlim).

## 4. Requirements

### RF-001: Metadado `SafeInParallel`
- **Description:** `IBuiltinChatTool` ganha `bool SafeInParallel => false` (default conservador); tools read-only optam `true`. Tools externas (HTTP/MCP/python DB) nunca paralelizam nesta fase.

### RF-002: Batch paralelo com ordem estável
- **Description:** `Task.WhenAll` no grupo contíguo de calls `SafeInParallel && !RequiresApproval pendente`; `results[i]` posicional → `tool` messages na ordem exata das calls.

### RF-003: Partição do batch
- **Description:** percorre `step.ToolCalls` agrupando runs contíguos de paralelizáveis; cada grupo executa concorrente; entre grupos e calls sequenciais, ordem estrita.

### RF-004: Gates sequenciais
- **Description:** `GateAsync` avaliado antes do dispatch em ordem; calls negadas entram no resultado posicional com mensagem de deny — não bloqueiam as demais do grupo.

### RF-005: Eventos e callbacks seguros
- **Description:** `OnCallAsync` dispara na ordem; `OnResultAsync` publica após join na ordem; nenhum callback roda dentro de `Task.WhenAll` (serialização com lock/SemaphoreSlim se necessário).

### RF-006: Concorrência limitada
- **Description:** `SemaphoreSlim(BuiltinTools:MaxParallel ?? 4)` dentro do grupo; config lida por run.

## 5. API Contract

Sem mudança de API — o SSE passa a poder emitir `tool_result` fora de ordem estrita de término, mas `tool_call`/`tool_result` continuam pareados por `id`.

## 6. Acceptance Criteria

- [ ] **Given** round com 3× `file_read` independentes **when** o loop executa **then** as 3 rodam concorrentes (elapsed ≈ máx, não soma) e tool messages saem na ordem das calls.
- [ ] **Given** batch misto `file_read` + `file_write` + `file_read` **when** executa **then** read#1 paralelo-ok → write sequencial → read#3 — ordem de resultado preservada.
- [ ] **Given** call que exige aprovação **when** agrupada com paralelizáveis **then** o gate roda em sequência e o deny não cancela as irmãs.
- [ ] **Given** `BuiltinTools:MaxParallel=2` e 5 calls paralelizáveis **when** executa **then** nunca mais de 2 simultâneas.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Uma call paralela lança exceção | — | vira `ToolExecutionOutcome` de erro na posição; demais completam |
| Cancel mid-batch | usuário cancela | tasks remanescentes canceladas; resultados parciais publicados |
| `DbContext` nos callbacks | publish concorrente | serializado — nenhum acesso EF dentro de WhenAll |

## 7. Task Plan

- [ ] **T1 —** `IBuiltinChatTool.SafeInParallel` + marcação nas tools read-only.
- [ ] **T2 —** Partição/execução de grupos no `RunToolLoopAsync` + semaphore + join ordenado.
- [ ] **T3 —** Serialização dos callbacks de resultado; fase agregada `running_tool` com contagem.
- [ ] **T4 —** Testes: paralelismo medido (delays artificiais), ordem de mensagens, deny mid-batch, cap, cancel.
- [ ] **T5 —** build+test; DoD; PR.

**7.1 Validation strategy:** unit tests do partitioner + integration do loop com tools fake; ≥80%.

## 8. Organization Guardrails

- Branch `feature/devin-20261016-parallel-tool-calls`.
- EF Core single-threaded — nunca dentro do paralelo.
- `SecretScrubber`/outlet rules aplicam a cada resultado como hoje.
- Mutáveis nunca paralelizam — classificação conservadora por default.

## 9. Definition of Done

- [ ] RF-001..RF-006 implementados e testados.
- [ ] Critérios da seção 6 verdes.
- [ ] build/test verdes; sem warnings novos.

## Open Questions / Pending Ambiguity

- `delegate_task` (subagent) — sequencial nesta fase (subagentes paralelos = fase 2, requer isolamento de workdir).
