# SPEC-20261103-agent-turn-guard

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `agent-turn-guard` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/Devin-20261103-agent-turn-guard` |
| Ticket | `TBD` |
| Status | `Draft` |

## 1. User Story

**As a** usuário de runs autônomas
**I want** o agente impedido de encerrar o turno com plano/promessa no lugar do trabalho
**So that** "vou fazer X" nunca substitui X — e o turno termine cedo só quando de fato não há trabalho pendente.

**Problem context:**
A REA do Claude Code (§1, modo autônomo) documenta a guarda de fim de turno, verbatim:

> "Before ending your turn, check your last paragraph. If it is a plan, an
> analysis, a question, a list of next steps, or a promise about work you
> have not done ('I'll…', 'let me know when…'), do that work now with tool
> calls… Do not stop because the context or session is long."

e a regra simétrica: "If the only work left is waiting for a background task… end your turn now: you will be notified when it finishes." O nosso loop (`ChatRunExecutor`/`ChatPipeline`) não tem verificação equivalente: o modelo pode fechar o turno com "next steps" sem tool-call final. O `StallWatchdog` cobre silêncio de stream, não encerramento-prematuro.

## 2. Scope

**In scope:**
- **Turn-end nudge mecânico**: turno que termina sem tool-calls **e** texto final casando padrão de "plano/promessa" (heurística pt+en configurável) → `turn_reminder` + re-prompt **máx 1× por turno**.
- **Background-wait rule**: ≥1 task/run destacada viva → encerrar com `turn_end_wait {waiting_on}`, sem nudge.
- **Bloco "operating autonomously"** no system prompt de runs destacadas/scheduled.
- **Telemetria**: `turn_nudge {reason, nudge_count}`, `turn_end_wait {waiting_on}`.

**Out of scope:**
- Detecção por LLM (classifier) — heurística local.
- Múltiplos nudges por turno.
- Rewind de notificações queued.

## 3. Technical Context

**Where the change happens:**
`ChatPipeline` detecta "turno fechou sem tool-call" na fronteira de rodada e injeta o reminder; `ChatRunExecutor` consulta tasks destacadas vivas para decidir `turn_end_wait` vs nudge; o contribuidor de prompt injeta o bloco autônomo quando `DetachedRun`/`Scheduled`.

**Files to read before implementing:**
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs`
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs`
- contribuidores de prompt (`*PromptContributor*`)

**Files to create or modify:**
```text
src/OpenWebUI.Api/Completions/ChatPipeline.cs   (nudge + re-prompt)
src/OpenWebUI.Api/Runs/ChatRunExecutor.cs       (waiting_on lookup)
<contribuidor de prompt>                        (bloco autônomo)
tests/OpenWebUI.Api.Tests/
```

## 4. Requirements

### RF-001: `Nudge plano/promessa`
- **Description:** Turno sem tool-calls com texto final casando `agent:turn_guard_patterns` (default ~12 padrões pt+en: "I will", "vou", "next steps", "let me know", "deixe-me saber") → reminder + re-prompt, `nudge_count` máx 1.
- **Input → Output:** texto final → reminder user-note ou fim normal.

### RF-002: `Wait-note com background vivo`
- **Description:** ≥1 task/run destacada viva do chat → não nudgear; emitir `turn_end_wait {waiting_on}`.

### RF-003: `Bloco autônomo`
- **Description:** Run destacada/scheduled → system prompt ganha "operating autonomously" (prosseguir em reversível, parar em destrutivo/escopo, evidência-antes-de-estado); chat interativo não.

### RF-004: `Flags`
- **Description:** `agent:turn_guard` (default true) desliga RF-001/RF-002; `agent:turn_guard_patterns` (JSON array) override.

### RF-005: `Nudge não interfere`
- **Description:** Nunca injeta com tool-calls pendentes ou `stop_reason` de erro.

### RF-006: `Eventos`
- **Description:** `turn_nudge {reason, nudge_count}` e `turn_end_wait {waiting_on}` via `RunEventWriter`.

## 6. Acceptance Criteria

- [ ] **Given** texto "I will now fix X" sem tool-call **when** o turno fecha **then** reminder + re-prompt.
- [ ] **Given** mesmo texto com tool-call **when** fecha **then** fim normal.
- [ ] **Given** 2º turno consecutivo casando **when** fecha **then** não re-nudgea (anti-loop).
- [ ] **Given** task destacada viva **when** fecha sem tool-call **then** `turn_end_wait`, sem nudge.
- [ ] **Given** run destacada **when** prompt monta **then** bloco autônomo presente; chat interativo ausente.
- [ ] **Given** `agent:turn_guard=false` **when** qualquer caso **then** comportamento atual.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Padrões custom | `agent:turn_guard_patterns=["FIXME:"]` | só casa os novos |
| Texto longo | reminder no meio, final limpo | sem nudge |
| Erro de stream | `stop_reason=error` | sem nudge |

## 7. Task Plan

- [ ] **T1 — Discovery:** fronteira de rodada do `ChatPipeline`; lookup de tasks vivas no executor.
- [ ] **T2 — Heurística + nudge:** detecção + reminder + re-prompt único.
- [ ] **T3 — Wait-note + bloco autônomo.**
- [ ] **T4 — Tests:** critérios da seção 6.
- [ ] **T5 — DoD + PR.**

## 8. Organization Guardrails

- **Branches:** `feature/Devin-20261103-agent-turn-guard` — nunca `main`.
- **Workflows:** não modificar `.github/workflows/`.
- **Security:** não logar conteúdo do texto final; apenas `reason`/flags.
- **Scope:** não inventar requisitos.

## 9. Definition of Done

- [ ] Todos os RFs implementados.
- [ ] Critérios da seção 6 cobertos por testes.
- [ ] Edge cases tratados.
- [ ] Build + suíte passando; cobertura ≥ baseline.
- [ ] Guardrails respeitados.

**Next action after DoD:** `Status = Done` e PR na branch da feature.

## Open Questions / Pending Ambiguity

- Aplicar RF-003 também aos prompts de `delegate_task`/sidekick? → **recomendo sim** (sub-agentes são autônomos por natureza).
