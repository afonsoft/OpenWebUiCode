# SPEC-20261023-agent-evals

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `agent-evals` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261023-agent-evals` |
| Ticket | [#495](https://github.com/afonsoft/open-webui/issues/495) |
| Status | `Approved` |
| Parent | Análise comparativa hermes-agent/OpenHarness/ECC (out/2026) — item #6. Inspirado em `evals/` + batch runner do hermes-agent e nos verification loops (graders, pass@k) do ECC. |

## 1. User Story

**As a** mantenedor desta plataforma e dos prompts/skills que ela injeta
**I want** um harness de avaliação que rode datasets contra agentes/
modelos com graders objetivos, pass@k e diff contra baseline
**So that** mudanças de system prompt, skills, tools e roteamento de
modelo sejam medidas com evidência — hoje a única "avaliação" é feedback
humano de arena, que não mede o comportamento do agente.

**Problem context:**
`EvaluationEndpoints` cobre 👍/👎 + arena battles + leaderboard ELO —
avaliação subjetiva de respostas soltas. Nada mede o *loop do agente*:
acertou a tool? respeitou o budget de steps? produziu o artefato certo?
O hermes-agent tem `evals/` + `batch_runner.py` + geração de
trajetórias; o ECC tem `eval-harness`/`verification-loop` com tipos de
grader e métricas pass@k. Temos toda a infra para montar isso barato:
`HeadlessRunner` (runs one-shot com JSONL), `IChatRunDispatcher`,
`ChatJobService` (jobs duráveis) e `RunEventWriter` (trajetória
completa por run).

## 2. Scope

**In scope:**

- **A. Datasets** — entidade `EvalDataset` + `EvalCase`: nome, descrição,
  tags; casos com `prompt`, `expected` (valor), `grader`
  (`exact|contains|regex|rubric|trajectory`), `rubric` (texto),
  `expectTools`/`forbidTools`/`maxSteps` (trajectory). Import via JSONL
  (`POST /datasets/{id}/cases:import`) e CRUD inline.
- **B. EvalRun** — execução de um dataset contra um alvo
  (`modelId` + opcional `persona`/`agent` + `toolset`) com `k`
  repetições por caso (pass@k) e `concurrency` (default 2). Cada caso
  vira uma run isolada num chat descartável — reusa
  `IChatRunDispatcher` + `ChatRunExecutor` (o loop real, não um mock).
- **C. Graders**
  - `exact`/`contains`/`regex`: sobre a resposta final do assistente.
  - `rubric`: LLM-as-judge via `ProviderService` (task role) — prompt
    fixo com rubric + resposta → score 0-1 + justificativa.
  - `trajectory`: sobre `RunEventWriter` events — tools chamadas
    (presente/ausente), contagem de steps, veto de interceptor,
    finalizado vs. erro.
- **D. Resultados + baseline** — `EvalResult` por (evalRun, caso,
  repetição): `pass`, `score`, `graderDetail`, `ChatRunId`
  (transcript link). `EvalBaseline` = ponteiro nomeado para EvalRun.
  `GET /evals/runs/{id}/compare?baseline=` → diff por caso
  (melhora/regressão/novo) + agregados; relatório em markdown para
  download/artefato.
- **E. Endpoints** — grupo `/api/v1/evals/*` autenticado (workspace);
  admin não é exigido — o dono do eval paga o custo das runs (integra
  com `run-cost-budgets` quando existir).

**Out of scope:**

- UI dedicada (v1 = API + relatório markdown; aba em `/admin` ou
  `/workspace` é iteração futura).
- Data generation/trajectory mining do hermes (sintetizar casos a
  partir de chats reais) — candidato a SPEC própria, depende desta.
- Arena/ELO existente — fica como está (superfície diferente).
- Execução distribuída de evals — mesmo processo, jobs duráveis
  (`ChatJobService`) bastam.

## 3. Technical Design

### Entidades (migration `AgentEvals`)

```text
EvalDataset(Id, UserId, Name, Description, Tags, CreatedAt)
EvalCase(Id, DatasetId, Prompt, Expected, Grader, Rubric,
         ExpectToolsJson, ForbidToolsJson, MaxSteps, CreatedAt)
EvalRun(Id, DatasetId, UserId, ModelId, PersonaId?, ToolsetJson,
        K, Concurrency, Status[pending|running|done|failed],
        StartedAt, FinishedAt)
EvalResult(Id, EvalRunId, CaseId, Rep, ChatRunId, Pass, Score,
           GraderDetailJson, CreatedAt)
EvalBaseline(Id, UserId, Name, EvalRunId, CreatedAt)
```

### Execução

`EvalRunnerService` (hosted, mesmo padrão do `AutomationScheduler`):
consome `EvalRun.Status=pending`, para cada caso × repetição cria chat
`eval:{runId}:{caseId}:{rep}` (oculto da sidebar — flag
`Chat.IsEphemeral` ou folder técnico), despacha via
`IChatRunDispatcher`, aguarda conclusão, lê eventos da run
(`RunEventWriter`), aplica o grader, grava `EvalResult`. Concorrência
por `SemaphoreSlim(concurrency)`; falha de caso vira `Pass=false` com
detalhe, nunca derruba o EvalRun.

Custo: cada run filha grava seu `RunUsageRecord` normal (quando #3
existir) — o eval reporta custo total agregando `ChatRunId`s.

### Compare

`GET /evals/runs/{id}/compare?baseline={baselineId}`: casa resultados
por `CaseId`, calcula `pass@k` por caso e por dataset, score médio, e
classifica por caso: `improved|regressed|unchanged|new`. Saída JSON +
`?format=md` para relatório.

### Arquivos

```text
src/OpenWebUI.Domain/Entities.cs                      # 5 entidades
src/OpenWebUI.Infrastructure/Services/EvalRunnerService.cs   # novo
src/OpenWebUI.Infrastructure/Services/EvalGraderService.cs   # novo (4 graders)
src/OpenWebUI.Api/Endpoints/EvalEndpoints.cs                 # novo grupo
tests/OpenWebUI.Api.Tests/EvalGraderTests.cs                 # novo
tests/OpenWebUI.Api.Tests/EvalEndpointsTests.cs              # novo
tests/OpenWebUI.Api.Tests/EvalRunnerTests.cs                 # novo
```

Config: `Agent:Evals:MaxK` (default `5`), `Agent:Evals:MaxConcurrency`
(default `2`), `Agent:Evals:JudgeModel` (opcional — default = task role).

## 4. Requirements

### RF-001: dataset CRUD + import
- **Description:** datasets e casos por usuário; `cases:import` aceita
  JSONL (`{"prompt","expected","grader","rubric",...}`), valida grader
  e limita 500 casos/dataset.
- **Input → Output:** JSONL → `EvalCase`s; erros por linha no response.

### RF-002: execução real do loop
- **Description:** cada caso×rep roda via dispatcher/executor — tools,
  skills e políticas idênticos ao chat real; chat efêmero nomeado
  `eval:{runId}:{caseId}:{rep}`.
- **Rules:** k ≤ `Agent:Evals:MaxK`; concorrência ≤ MaxConcurrency;
  progresso do EvalRun atualizado por caso.

### RF-003: graders determinísticos + rubric
- **Description:** `exact` (trim+casefold), `contains`, `regex`;
  `rubric` chama o provider com prompt fixo retornando JSON
  `{score, rationale}` — resposta inválida do juiz → `Score=null`,
  `Pass=false`, detalhe registrado.

### RF-004: trajectory grader
- **Description:** lê eventos da run: `ExpectTools` ⊆ tools chamadas,
  `ForbidTools` ∩ chamadas = ∅, `steps ≤ MaxSteps`, run finalizada sem
  erro. Cada assert vira linha em `GraderDetailJson`.

### RF-005: pass@k + compare
- **Description:** `pass@k` por caso e média do dataset; `compare` contra
  baseline classifica cada caso; relatório markdown listando
  regressões primeiro.

## 5. API Contract

**Endpoint:** `POST /api/v1/evals/runs` — **Auth:** Bearer/JWT

```json
{ "datasetId": "...", "modelId": "qwen3:14b", "personaId": null,
  "k": 3, "concurrency": 2, "toolset": ["fs_*","shell_exec"] }
```

**Response:** `{ "id": "...", "status": "pending", "totalJobs": 150 }`

**Endpoint:** `GET /api/v1/evals/runs/{id}` → status + agregados
(`passAtK`, `meanScore`, `costUsd` quando houver ledger).

**Endpoint:** `GET /api/v1/evals/runs/{id}/compare?baseline={bid}&format=md`

**Erros:** `404` dataset/baseline; `409` run já em execução; `400` k >
MaxK; formato de erro genérico existente.

## 6. Acceptance Criteria

- [ ] **Given** dataset com 3 casos, k=2 **when** `POST /evals/runs`
      **then** 6 runs efêmeras executam e 6 `EvalResult`s gravam com
      `ChatRunId` válido.
- [ ] **Given** grader `exact` com expected="42" **when** resposta é
      "42." **then** `Pass=true` (trim+pontuação tolerada).
- [ ] **Given** `trajectory` com `expectTools=["fs_write"],
      maxSteps=5` **when** a run chama `fs_write` em 3 steps **then**
      pass; se chama `shell_exec` com `forbidTools=["shell_exec"]` →
      fail com detalhe por assert.
- [ ] **Given** baseline nomeada **when** `compare` **then** casos
      regredidos aparecem primeiro no markdown com link do chat.
- [ ] **Given** juiz rubric retorna texto não-JSON **then** resultado é
      `Pass=false` + `graderDetail.error`, run do eval não falha.
- [ ] Regressão: suíte completa verde; coverage ≥ baseline.

## 7. Task Plan

| ID | Task |
| --- | --- |
| T1 | Discovery: `HeadlessRunner`, `IChatRunDispatcher`, `ChatJobService`, `RunEventWriter`, `EvaluationEndpoints` |
| T2 | Migration + entidades + endpoints de dataset/casos/import + testes |
| T3 | `EvalGraderService` (4 graders) + testes unitários por grader |
| T4 | `EvalRunnerService` (execução, concorrência, progresso) + testes com provider fake |
| T5 | Compare + baseline + relatório markdown + testes de contrato |
| T6 | DoD: Release build 0 warnings, suíte verde, coverage ≥ baseline, drift guards, SPEC→Issue→PR |

## 8. Organization Guardrails

- Respostas ao usuário em **pt-BR**; código/commits em inglês.
- Branch dedicada; não modificar `.github/workflows/`; sem secrets.
- Runs de eval **consomem provider real** — expor `costUsd` agregado e
  honrar `run-cost-budgets` quando existir; nunca rodar eval alheio.
- Chats de eval não poluem sidebar/memória: efêmeros e excluídos do
  `MemorySyncService` (instincts/#5 também os ignora).
- Juiz é provider externo — prompt do juiz é fixo do sistema; rubric do
  usuário entra como `<untrusted_input>` (doutrina já existente).

## Open Questions / Pending Ambiguity

| OQ | Status | Resolução |
| --- | --- | --- |
| Ephemeral chats: `IsEphemeral` novo ou folder técnico? | Proposta | flag `Chat.IsEphemeral` (migration pequena, semântica limpa — reutilizável por instincts/tests) |
| Grader `trajectory` sobre eventos ou transcript? | Resolvido | `RunEventWriter` events (estruturado, já persistido) |
| Timeout por caso | Proposta | `Agent:Evals:CaseTimeoutMinutes` default 10 |
