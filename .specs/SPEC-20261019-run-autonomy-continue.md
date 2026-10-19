# SPEC-20261019-run-autonomy-continue

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `run-autonomy-continue` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261019-run-autonomy-chain` |
| Ticket | `[A DEFINIR — criada via create-issues após aprovação]` |
| Status | `Done` |
| Fonte | análise do chat `52c9929a-d3b4-45e6-9737-5d7f0688d2dc` no `/data/openwebui.db` (container `openwebui`), 2026-10-19 |

## 1. User Story

**As a** usuário em modo `build` com preset `always-allow`
**I want** a run continuar automaticamente ao bater o teto de steps com trabalho pendente, sem que o modelo pare para perguntar "posso continuar?"
**So that** implementações multi-SPEC executem end-to-end sem babysitting de "Continue".

**Problem context:**
Evidência do banco do container `openwebui` (chat `52c9929a`, 38 runs, 607 `step_started`):

- **14+ runs terminaram exatamente em 25 steps** — `agentLoop.maxRounds` default (`ChatPipeline.cs:588-589`, `ChatRunExecutor.cs:411`). No último round o pipeline remove tools e injeta `MaxStepsPrompt` ("resuma o que foi feito e os próximos passos") → run `completed` → usuário digita "Continue" para uma NOVA run. Mensagens do usuário na sessão: ≥12 são "Continue"/"Sim"/"execute as 3 opções".
- **`always-allow` só auto-aprova o gate de tool calls** (`GateToolCallAsync`) — não impede o modelo de encerrar o turno com texto/pergunta. O prompt base já diz "never end your turn just to ask whether to continue" (`ChatPipeline.cs:48-54`), mas não menciona o preset; o auto-continue `PendingTasksPrompt` (máx 3 nudges) só dispara quando `pendingTodos > 0` — ou seja, depende de o modelo manter `builtin_todo_write`, o que nem sempre acontece.
- Runs falharam também por bug de provider (tratado em `SPEC-20261019-gemini-response-shape`) — fora de escopo aqui.

## 2. Scope

**In scope:**
- RF-001: `ToolLoopOutcome` expõe `HitStepCap` e `PendingTodos` (contagem mais recente de itens `pending`/`in_progress` da lista `builtin_todo_write`).
- RF-002: auto-chain no teto — run que termina com `HitStepCap && PendingTodos > 0` dispara continuação automática (steer queued sintético ou nova run encadeada), limitada por `agentLoop.maxAutoChain` (default 3, i.e. até 4×25 rounds).
- RF-003: supressão de perguntas sob `always-allow` — o prompt base ganha linha explícita quando o preset é `always-allow` ("approvals are auto-granted — never ask for confirmation; ask only for inputs only the user can provide"); e o nudge de pending dispara também quando o turno termina no teto sem lista de tarefas populada (tratado pela chain do RF-002 como fallback `PendingTodos == -1` → continuar se `HitStepCap` e modo `build`).
- RF-004: `agentLoop.maxRounds` (default agora **99999**), `agentLoop.autoChain` e `agentLoop.maxAutoChain` configuráveis **por usuário** — chaves `u:{userId}:agentLoop.*` sobrescrevem a camada global `agentLoop.*`; expostos no Settings do usuário (aba Agents, seção "Agent loop") via `GET/PUT /api/v1/configs/user/agent-loop`.
- Telemetria: `run.MetaJson` registra `autoChain: {index, of}` e a run filha referencia `ParentRunId` (coluna já existe) para trilha de cadeia.

**Out of scope:**
- ~~Mudança do default `agentLoop.maxRounds`~~ — decisão revista pelo usuário: o default global passa a **99999** (efetivamente ilimitado; `agent.steps` explícito e config continuam vencendo), aplicado nesta mesma branch.
- Bug de deserialização do provider Gemini (→ `SPEC-20261019-gemini-response-shape`).
- Auto-chain para runs `failed` (recovery de falha é `run-recovery`/`provider-resilience`, já implementado).
- UI de visualização da cadeia além do link pai já existente (`chat.parent` chip).
- Budget de custo por cadeia (custo por run já existe via SPEC-20261016-agent-steps-usage).

## 3. Technical Context

**Where the change happens:**
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs` — `RunToolLoopAsync` (teto ~linhas 588/755/1073-1091), `ToolLoopOutcome` (definido no mesmo arquivo ou adjacente), `pendingTodos` (linhas 628/987-1001), `PendingTasksPrompt` (1111-1116), `DefaultSystemPrompt` (29-54).
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` — resolução de `maxSteps` (405-411), `FinishAsync` (1901-1981), dispatcher/`Enqueue` em `ChatRunDispatcher.cs:46`, steers em `PollSteerAsync` (763-820).
- `src/OpenWebUI.Api/Endpoints/ApiEndpoints.cs:364` — segundo callsite de `maxSteps` (chat completion legado) — mesmo comportamento de config.
- Config UI: `src/OpenWebUI.Client/Components/SettingsModal.razor` (aba Agents) + endpoints `configs/user/agent-loop` em `ConfigEndpoints.cs` — padrão de chaves `u:{id}:agentLoop.*` (mesma convenção `u:{id}:memsync.*`).

**Files to read before implementing:**
- `CLAUDE.md` · `docs/specs/SPEC-20261016-agent-steps-usage.md` · `docs/specs/SPEC-20261010-steer-queue.md` (se existir em docs/specs)
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs` (560-1124)
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` (390-520, 763-820, 1779-1999)
- `src/OpenWebUI.Api/Runs/ChatRunDispatcher.cs`
- `tests/OpenWebUI.Api.Tests/ChatRunEndpointsTests.cs` / `RunCoverageTests.cs` (padrão de teste de runs)

**Files to create or modify:**
```text
src/OpenWebUI.Api/Completions/ChatPipeline.cs        # outcome HitStepCap/PendingTodos + prompt preset-aware
src/OpenWebUI.Api/Runs/ChatRunExecutor.cs            # auto-chain após FinishAsync + u:{id}: override
src/OpenWebUI.Api/Endpoints/ConfigEndpoints.cs       # GET/PUT /configs/user/agent-loop
src/OpenWebUI.Api/Endpoints/ApiEndpoints.cs          # u:{id}: override no callsite legado
src/OpenWebUI.Application/Contracts/ConfigContracts.cs # AgentLoopPrefs/AgentLoopResponse
src/OpenWebUI.Client/Services/ApiService.cs          # Get/SetAgentLoopAsync
src/OpenWebUI.Client/Components/SettingsModal.razor  # seção "Agent loop" na aba Agents
src/OpenWebUI.Client/wwwroot/i18n/*.json             # 7 chaves novas × 8 locales
tests/OpenWebUI.Api.Tests/AgentLoopAutoChainTests.cs # testes de chain/cap/settings
```

## 4. Requirements

### RF-001: Sinalização de teto no outcome
- **Description:** `ToolLoopOutcome` ganha `HitStepCap` (loop saiu pelo `for` esgotando `maxRounds`) e `PendingTodos` (última contagem conhecida; `-1` quando nenhum `builtin_todo_write` executou na run).
- **Input → Output:** término do loop → outcome com os dois campos preenchidos, sem alterar `FinalContent`/`TotalSteps` existentes.

### RF-002: Auto-chain de continuação
- **Description:** em `ChatRunExecutor`, após `FinishAsync` de run `completed` com `HitStepCap`, se `agentLoop.autoChain != false` (default on) e `PendingTodos > 0` — ou `PendingTodos == -1` e modo `build` — despachar continuação: nova `ChatRun` com `ParentRunId` = run atual, conteúdo sintético de retomada ("continue o trabalho pendente do ponto onde parou — não resuma, execute"), sem mensagem `user` persistida no chat (meta-only, ou steer `queue` que se autopromove na 1ª fronteira).
- **Rules:** `meta.autoChain` carrega `{index, of: maxAutoChain}`; parar quando `index >= maxAutoChain` (default 3), quando `PendingTodos == 0`, quando a run falha/é pausada/interrompida, ou quando modo != build/agente (decisão: chain restrito a `build`/`always-allow` — ver RF-003 e AC). Steer/queue do usuário na cadeia funciona normal.
- **Input → Output:** run hit-cap com pendências → próxima run enfileirada automaticamente; usuário vê execução contínua.

### RF-003: Prompt preset-aware
- **Description:** quando `chat.ApprovalPreset == "always-allow"`, o system prompt inclui a linha "User approvals are set to always-allow: tool permissions are auto-granted — do not ask for confirmation before acting; ask only for inputs only the user can provide (decision, secret, or a genuinely destructive step)."
- **Rules:** injeção como bloco adicional após `DefaultSystemPrompt` (mesmo padrão dos demais `systemParts`); sem mudar o texto base existente.
- **Input → Output:** request enriquecido carrega a diretiva sob always-allow.

### RF-004: Config de loop por usuário
- **Description:** expor `agentLoop.maxRounds` (int, default efetivo 99999, clamp 1-99999), `agentLoop.autoChain` (bool, default true) e `agentLoop.maxAutoChain` (int, default 3, clamp 0-10) como **override por usuário** (`u:{id}:agentLoop.*` sobre a camada global) na seção "Agent loop" da aba Agents do Settings do usuário. Requisito revisto pelo usuário: "se o usuario configurar o limite para um valor inferior tem de ter esses ajustes" — por isso per-user, não só admin.
- **Input → Output:** usuário ajusta valores na UI → `PUT /configs/user/agent-loop` grava `u:{id}:agentLoop.*` → executor lê `u:{id}:... ?? agentLoop.* ?? default` na próxima run; `null` no PUT remove o override (volta a herdar).

## 5. API Contract

- `GET /api/v1/configs/user/agent-loop` → `AgentLoopResponse { maxRounds, autoChain, maxAutoChain, global, user }` (efetivo + camadas cruas).
- `PUT /api/v1/configs/user/agent-loop` ← `AgentLoopPrefs { maxRounds?, autoChain?, maxAutoChain? }` — `null` num campo remove o override do usuário; valida `maxRounds ∈ [1,99999]`, `maxAutoChain ∈ [0,10]` (400).
- `GET /api/v1/runs/{id}` passa a poder incluir `meta.autoChain: {index, of}` e `parentRunId` quando encadeada.

## 6. Acceptance Criteria

- [x] **Given** run que atinge `maxRounds` com `PendingTodos > 0` **when** `FinishAsync` completa **then** uma run filha (`ParentRunId` preenchido, `meta.autoChain.index = 1`) é despachada e executa sem input do usuário. — `AutoChain_CapComPendencias_CriaFilhasAteOLimite`
- [x] **Given** chain index = `maxAutoChain` **when** a run encadeada bate o cap de novo **then** a cadeia para (run final completed, sem nova filha). — idem (3 runs com maxAutoChain=2)
- [x] **Given** run hit-cap com `PendingTodos == -1` (sem lista) em modo `build` **when** completa **then** chain dispara igualmente (fallback autônomo). — `AutoChain_SemTodoList_FallbackBuildEncadeia`
- [x] **Given** run hit-cap em modo `plan` ou preset não-`always-allow` **when** completa **then** NÃO há chain — comportamento atual preservado. — `AutoChain_PresetOuModoErrados_NaoEncadeia`
- [x] **Given** `always-allow` **when** request enriquecido **then** system prompt contém a diretiva de não pedir confirmação. — entregue no PR #458 (testes de preset)
- [x] **Given** usuário altera `agentLoop.maxRounds` para valor inferior **when** próxima run **then** `maxSteps` da run = valor do usuário. — todos os testes de chain rodam com `u:{id}:agentLoop.maxRounds = 2`

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Usuário pausa a cadeia | pause na run filha | cadeia interrompe; resume manual reabilita a run, não a cadeia |
| Run filha falha | provider error | chain para; erro visível (sem retry infinito) |
| Steer durante chain | usuário envia msg na run filha | promove normalmente na fronteira; chain segue contando index |
| `autoChain=false` | config off | hit-cap sempre termina como hoje |

## 7. Task Plan (agent execution)

- [x] **T1 — Discovery:** confirmar assinatura de `ToolLoopOutcome`, ciclo de criação de run no dispatcher, onde `ApprovalPreset` chega no EnrichRequest, e padrão de chaves de config por usuário (`u:{id}:*`).
- [x] **T2 — Outcome:** `HitStepCap`/`PendingTodos` no outcome + preenchimento no loop (cap marcado no round final normal e no fallback patológico).
- [x] **T3 — Chain:** continuação automática no executor com `autoChain`/`maxAutoChain`/`ParentRunId`/meta; testes de integração em `AgentLoopAutoChainTests` (mock Ollama, padrão `AgentModeE2ETests`).
- [x] **T4 — Prompt:** linha preset-aware sob `always-allow` + teste de conteúdo do system prompt (PR #458).
- [x] **T5 — Config UI (per-user):** seção "Agent loop" na aba Agents do `SettingsModal` + endpoints `configs/user/agent-loop` + 7 chaves i18n × 8 locales.
- [x] **T6 — Validation:** `dotnet build` 0 erros; `AgentLoopAutoChainTests` 5/5; drift guards (i18n/css/a11y/spec) verdes.
- [x] **T7 — Done + PR:** `Status = Done`, PR aberto.

**7.1 Validation strategy**

| Item | Evidence |
| --- | --- |
| Build/test | `dotnet build` + `dotnet test tests/OpenWebUI.Api.Tests` verdes |
| Regressão cap | teste novo: outcome reporta HitStepCap/PendingTodos |
| Chain | teste: run hit-cap dispara filha com ParentRunId/meta; respeita maxAutoChain e config off |
| Prompt | teste: always-allow injeta diretiva; outros presets não |

## 8. Organization Guardrails

- Sem commit em `main`; branch `feature/devin-20261019-run-autonomy-continue`.
- Não remover/alterar `MaxStepsPrompt`/`PendingTasksPrompt` existentes — estender.
- Chain é decisão de autonomia sensível a custo: default on apenas para `build` + `always-allow`; demais modos inalterados.
- Não criar mensagem `user` falsa persistida no histórico visível — continuação é meta-only (consistência com prompts sintéticos existentes).

## 9. Definition of Done

- [ ] RF-001..004 implementados com testes.
- [ ] `dotnet test` verde incluindo novos casos de chain/cap/prompt.
- [ ] Config documentada em `.env.exemplo`/`docs`.
- [ ] SPEC arquivada em `docs/specs/` com `Status: Done` + `Delivered` (PR).
