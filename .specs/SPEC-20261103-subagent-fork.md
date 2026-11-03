# SPEC-20261103-subagent-fork

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `subagent-fork` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/Devin-20261103-subagent-fork` |
| Ticket | `#641` |
| Status | `Done` |

## 1. User Story

**As a** usuário do agente
**I want** um `subagent_type:"fork"` que herde o contexto completo do pai e filtre seu tool-output
**So that** delegações multi-step não re-carreguem contexto do zero e a janela do pai não fique poluída com ruído de tools do sub-agente.

**Problem context:**
A REA do Claude Code (§2/§6) documenta três modos de sub-agente:

- **`fork`**: "inherits your context but keeps its tool output out of your context" — ideal para trabalho multi-step que continua o raciocínio do pai;
- **`fork-background`**: idem, async;
- **fresh agents** (nosso `delegate_task` atual): "begin with zero context" — e a regra **"Never delegate understanding"**: "Don't write 'based on your findings, fix the bug' — include file paths, line numbers, what to change";

com o invariante "fork não cria fork" e "If you ARE the fork — execute directly; do not re-delegate". O nosso `builtin:delegate_task`/`sidekick` sempre nasce fresh: delegações que dependem do histórico exigem o prompt carregar tudo de novo, e o tool-output do sub-agente não tem canal de filtragem separado.

## 2. Scope

**In scope:**
- **`subagent_type:"fork"`** no `delegate_task` (e variante background): filho recebe o histórico da run do pai (mensagens, não o tool-bracket do executor) com role-annotation `[fork of <parent>]`, roda na mesma janela lógica, e seu tool-output **não** volta ao transcript do pai — só o resultado final.
- **Fresh default mantido**: `fork` é opt-in via parâmetro; o default continua fresh.
- **Prompt guidance**: descrição da tool ganha "Never delegate understanding — include file paths, line numbers, what to change; don't write 'based on your findings'"; fork recebe "If you ARE the fork — execute directly; do not re-delegate".
- **Invariante**: `fork` não pode chamar `delegate_task`/`sidekick` com `subagent_type:"fork"` (deny).
- **Telemetria**: `fork_spawned {parent_run, mode}` + `fork_completed {duration_ms}`.

**Out of scope:**
- `SendMessage` entre agentes (team comms) — SPEC futura.
- Workflow scriptable (`agent()/parallel()/pipeline()`) — SPEC maior separada.
- Modelo por worker (o Claude omite para herdar; nós já temos role-resolver — manter).

## 3. Technical Context

**Where the change happens:**
`builtin:delegate_task`/`sidekick` (implementação em `ChatRunExecutor`/`*Tool*Service`) ganha `subagent_type`; o fork reusa o mecanismo de run destacada mas recebe o transcript do pai serializado (sem tool-output do executor) e retorna só o resumo final.

**Files to read before implementing:**
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` (`delegate_task`/`sidekick` dispatch)
- implementação de run destacada / sidekick
- `RunEventWriter`

**Files to create or modify:**
```text
<arquivo do delegate_task>          (subagent_type + fork path)
src/OpenWebUI.Api/Runs/ChatRunExecutor.cs   (invariante fork-não-fork + eventos)
tests/OpenWebUI.Api.Tests/
```

## 4. Requirements

### RF-001: `subagent_type:"fork"`
- **Description:** `delegate_task` aceita `subagent_type ∈ {"fresh"(default),"fork","fork-background"}`; `fork` injeta o histórico do pai (filtrado de tool-output do executor) como prefixo da run filha com `[fork of {parent}]`; tool-output do filho não volta ao transcript do pai — só o resultado final.
- **Input → Output:** {task, subagent_type} → run filha com contexto herdado.

### RF-002: `Never delegate understanding`
- **Description:** Descrição da tool exige prompt detalhado ("include file paths, line numbers, what to change"); `fork` recebe "execute directly; do not re-delegate".

### RF-003: `Fork-não-fork`
- **Description:** Uma run `fork` que tente `delegate_task` com `subagent_type:"fork"`/`"fork-background"` → denial ("forks cannot create forks"); `fresh` permitido.

### RF-004: `Filtragem de tool-output`
- **Description:** O transcript injetado ao fork exclui `tool` messages do executor e `<context_usage>`/`turn_reminder`; inclui `user`/`assistant` (texto) e o resumo corrente se houver.

### RF-005: `Flags`
- **Description:** `agent:subagent_fork` (default true) desliga o `fork`; flag off → `subagent_type:"fork"` → denial claro.

### RF-006: `Eventos`
- **Description:** `fork_spawned {parent_run, mode}` + `fork_completed {duration_ms, output_tokens}`.

## 6. Acceptance Criteria

- [ ] **Given** `delegate_task` com `subagent_type:"fork"` **when** a run filha inicia **then** o transcript do pai (sem tool-output) está presente com `[fork of {id}]`.
- [ ] **Given** `subagent_type` ausente **when** delega **then** run fresh (comportamento atual).
- [ ] **Given** run `fork` **when** tenta `delegate_task` com `fork` **then** denial "forks cannot create forks".
- [ ] **Given** fork rodando **when** filho executa tools **then** o tool-output não volta ao transcript do pai; só o resultado final.
- [ ] **Given** `agent:subagent_fork=false` **when** `subagent_type:"fork"` **then** denial claro.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Transcript pai vazio | run sem histórico | prefixo `[fork]` com contexto mínimo, sem erro |
| Pai compactado | resumo aplicado | fork recebe o resumo + mensagens recentes |
| fork-background | async | run destacada com contexto herdado; `turn_end_wait` cobre |

## 7. Task Plan

- [ ] **T1 — Discovery:** dispatch do `delegate_task`; serialização do transcript; mecanismo de run destacada.
- [ ] **T2 — Fork path:** `subagent_type` + prefixo `[fork]` + filtragem.
- [ ] **T3 — Invariante + guidance:** deny fork-crea-fork; texto da tool.
- [ ] **T4 — Tests:** critérios da seção 6.
- [ ] **T5 — DoD + PR.**

## 8. Organization Guardrails

- **Branches:** `feature/Devin-20261103-subagent-fork` — nunca `main`.
- **Workflows:** não modificar `.github/workflows/`.
- **Security:** não logar transcript herdado; apenas metadados.
- **Scope:** não inventar requisitos.

## 9. Definition of Done

- [ ] Todos os RFs implementados.
- [ ] Critérios da seção 6 cobertos por testes.
- [ ] Edge cases tratados.
- [ ] Build + suíte passando; cobertura ≥ baseline.
- [ ] Guardrails respeitados.

**Next action after DoD:** `Status = Done` e PR na branch da feature.

## Open Questions / Pending Ambiguity

- O `fork` herda também os arquivos/artefatos referenciados via `history://`? → **recomendo sim** (mesma store de recursos da sessão).
