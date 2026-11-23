# SPEC-20261121-dev-pipeline

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `dev-pipeline` |
| Type | `Backend / Agent Tools` |
| Stack | `.NET 10 / ASP.NET Core / EF Core` |
| Repository | `OpenWebUiCode` |
| Branch | `feature/dev-pipeline` |
| Status | `Delivered` |

## 1. User Story

**As a** usuário do modo build  
**I want** uma tool que orquestre o ciclo de desenvolvimento — implementar → criar testes → validar → commitar — executada deterministicamente pelo servidor em vez de depender da disciplina do modelo  
**So that** cada tarefa delegada fecha o ciclo completo com evidência real (testes rodados, commit completo), sem o usuário precisar cobrar a mão.

**Problem context:**  
O chat `9ead0a1f` (modo build) evidenciou que prompts não bastam: `dotnet test` rodou 0 vezes, o commit foi parcial e o PR só saiu após 4 cobranças do usuário. A garantia precisa ser estrutural — um orquestrador server-side que encadeia as fases e executa validação/commit deterministicamente.

## 2. Scope

### In Scope
1. `agent_validate` — tool built-in que detecta o stack do workdir e executa a suíte de testes (evidência estruturada de validação).
2. `dev_pipeline` — tool built-in que dispara o pipeline orquestrado: chat filho em modo build recebe runs sequenciais (implement → test), seguidas de validação e commit determinísticos (server-side, sem LLM), com retry fix limitado na validação e report-back ao chat pai.
3. Estado durável da pipeline em kv (`devpipeline:{id}`) + op `resume` para retomar após restart.
4. Anúncio: `agent_validate`/`dev_pipeline` só em build (BuildOnlyTools); `agent_validate` gated no plan.

### Out of Scope
- Gate pós-run no `FinishAsync` (verificação proativa de entrega incompleta) — follow-up separado.
- Push/PR automático além de `git push -u origin HEAD` quando `push:true` (criação de PR via `gh` fica para o agente pai/orquestrador futuro).
- Paralelismo de fases ou pipelines concorrentes no mesmo chat.

## 3. Technical Context

### Onde a mudança acontece
- Tool: `src/OpenWebUI.Infrastructure/ChatTools/Tools/` (padrão `IBuiltinChatTool`)
- Orquestrador: `src/OpenWebUI.Api/DevPipeline/DevPipelineService.cs` (novo)
- Detecção/validação compartilhada: `src/OpenWebUI.Infrastructure/Services/ProjectValidation.cs` (novo)
- Registro DI: `src/OpenWebUI.Api/Program.cs`
- Ruleset: `src/OpenWebUI.Infrastructure/ChatTools/PermissionRuleset.cs`
- Bloco build: `src/OpenWebUI.Api/Completions/Contributors/SystemPromptContributors.cs`

### Padrões reutilizados
- Chat filho + run enfileirada: `DrainQueuedSteersAsync` (`ChatRunExecutor`) e `delegate_task` (`DelegateTaskBuiltinTool`)
- Poll de término: `WaitForChildAsync` (intervalo 5s + deadline)
- Herança de binding: `WorkspaceRepoService.InheritChatBinding` / `SetChatBindingAsync`
- Status git: `WorktreeService.StatusPorcelainAsync` / `RunGitAsync`
- Execução de processo: `ChatProcessRunner.RunAsync`

## 4. Requirements

### RF-001: `ProjectValidation` (helper compartilhado)

Detecta o stack pelo marcador no workdir e monta o comando de teste:

| Marcador | Comando |
| --- | --- |
| `*.sln`, `*.slnx`, `*.csproj` | `dotnet test --nologo` |
| `package.json` | `npm test` |
| `pytest.ini`, `conftest.py`, `pyproject.toml`, `requirements.txt` | `pytest -q` |
| `go.mod` | `go test ./...` |
| `Cargo.toml` | `cargo test` |

Prioridade na ordem da tabela; nenhum marcador → `null` (caller decide). `RunAsync(workdir, command, timeout, ct)` executa via `ChatProcessRunner.RunAsync` e devolve `{exitCode, timedOut, output(truncado)}` + `git status --porcelain` do mesmo diretório (não-git → null).

### RF-002: `agent_validate` builtin tool

- Params: `command` (override opcional), `timeout_seconds` (default 300, máx 600).
- Sem `command`: usa `ProjectValidation.Detect`; nada detectado → texto orientando passar o comando.
- Retorna `[validate] {cmd} → exit N` + saída truncada + linha git (`uncommitted: N` ou `workdir não é repo git`).
- Payload `{validated, command, exitCode, gitClean, uncommitted}`.
- Tier `Exec`.

### RF-003: `dev_pipeline` builtin tool

- Ops: `start` (obrigatório `task`; opcionais `context`, `push` (default false), `max_fix_attempts` (default 1)), `status` (`pipeline_id`), `resume` (`pipeline_id`).
- `start`: valida task; cria kv `devpipeline:{id}` `{id,parentChatId,parentRunId,childChatId,task,push,phase,status,runs[],validation,commit,error,fixAttempts,createdAt,updatedAt}`; cria chat filho (`Mode=build`, preset herdado, binding herdado do chat pai); dispara `DevPipelineRunner` em background; retorna `{pipelineId, childChatId, link}`.
- `status`: devolve o kv corrente.
- `resume`: se kv está `running` sem run ativa (crash/restart), re-dispara o runner a partir da fase registrada; senão orienta `status`.
- Tier `Exec`.

### RF-004: `DevPipelineRunner` (orquestrador)

Fases sequenciais; cada run LLM vai para o chat filho (histórico encadeado preserva o contexto das fases anteriores; `ParentRunId` liga a cadeia e o worktree isolado é reutilizado via sameChatParent):

1. **implement** — run LLM: "implemente a tarefa, sem testes, sem commit; liste os arquivos".
2. **test** — run LLM: "escreva/ajuste os testes do implementado, sem commit; liste os arquivos".
3. **validate** — determinístico: `ProjectValidation` no workdir do filho. Falhou e `fixAttempts < max` → run LLM **fix** ("validação falhou: {tail}") → re-valida. Sem comando detectável → reporta `validate_skipped` (não bloqueia).
4. **commit** — determinístico: `git add -A` + `git commit -m "feat: {task resumida}"`; nada a commitar → `commit_skipped`. `push:true` → `git push -u origin HEAD`.
5. **report** — mensagem `user` `[dev_pipeline]` no chat PAI com o relatório (fases, duração, validação, sha) + run filha enfileirada (padrão queue-drain) para o agente pai apresentar o resultado.

- Falha/parada de run de fase → pipeline `failed` + report com motivo.
- Deadline por run de fase: `Agent:DevPipeline.PhaseTimeoutSec` (default 1200s), poll 5s.
- Erros nunca derrubam a run pai — a tool retorna texto e o kv marca `failed`.

### RF-005: Anúncio e prompt

- `agent_validate` e `dev_pipeline` entram em `WorkspaceTools` + `BuildOnlyTools` (não anunciam em modo chat — sem repo não há pipeline); `agent_validate` entra em `PlanExecGated` (análise no plan). `dev_pipeline` permanece negado no plan.
- Bloco `Modo BUILD` ganha uma linha apontando `builtin_dev_pipeline` para tarefas multi-etapa e `builtin_agent_validate` para validação pontual.

## 5. Acceptance Criteria

- AC-1: `agent_validate` sem `command` num workdir com `*.csproj` executa `dotnet test` e devolve `validated` coerente com o exit code.
- AC-2: `dev_pipeline start` cria kv + chat filho (build, binding herdado) e enfileira a run da fase `implement`.
- AC-3: Fases `validate`/`commit` executam sem LLM — commit cria commit real em repo git temporário; tree limpa → `commit_skipped`.
- AC-4: Pipeline `done`/`failed` gera mensagem `[dev_pipeline]` no chat pai + run filha com `ParentRunId`.
- AC-5: `resume` re-dispara pipeline `running` órfã; pipeline em andamento → retorna status atual.
- AC-6: `dev_pipeline` não é anunciada em modo chat; `agent_validate` não anuncia em chat e é gated (Ask) em plan.
- AC-7: Regressão: testes NUnit novos verdes + build sem warnings.

## 6. Test Plan

- `ProjectValidation_Detect_MapeiaStacks` — marcadores → comandos esperados; diretório vazio → null.
- `AgentValidate_ComandoExplicito_RodaERetorna` — `command` arbitrário em temp dir → exit code + git info; comando inexistente → validated=false.
- `DevPipeline_Start_CriaKvChatFilhoERun` — kv gravada, chat filho com binding herdado, run `implement` enfileirada com ParentRunId.
- `DevPipeline_Commit_CommitaRepoTemp` — repo git temp com dirty file → commit aplicado, tree limpa; segunda passada → skipped.
- `DevPipeline_StatusEResume` — leitura do kv; `running` órfã re-dispara.
- `AgentValidate_ModoChat_NaoAnuncia` — `ShouldAnnounce` false em chat, Ask em plan.

## 7. Incremento — orquestração estilo Antigravity (análise do `agy` v. jetski)

Engenharia reversa do CLI Antigravity (`native_swarm`, `oneharness` invoke/manage_subagent,
`run_workflow`, prompt "DISPATCH-ONLY", heartbeat+replace) apontou 6 melhorias
incorporadas aqui — mantendo a orquestração **em código** (o deles é prompt e
pode desviar; o nosso não pode "esquecer" validate/commit).

### RF-006: Fase `plan` (planner→worker)

- Arg `plan` (default `true`): antes de `implement`, uma run LLM decompõe a
  tarefa em itens ordenados e emite `<pipeline_result>{"plan":"...","summary":"..."}</pipeline_result>`.
  O plano entra no prompt da fase `implement` (Hydrogen-style: modelo maior
  planeja, worker executa).

### RF-007: Modelo por fase

- Args `worker_model` (aplica a `plan`/`implement`/`test`/`verify`) e
  `fix_model` (aplica a `fix`) — ids literais ou refs `chain:`/`fusion:`/
  `adaptive:`; run da fase usa `request with { Model = … }`. Permite
  executor barato + corretor forte sem config extra.
- **Defaults fusion (incremento 2026-11-22)**: quando o caller não passa
  os args e a run pai é `fusion:{L}>{S}` (kv `r:{run}:fusion.{leader,
  sidekick}`), os papéis herdam o par — `plan`/`fix` ← leader
  (planner/diagnoser), `implement`/`test`/`verify` ← sidekick (kv já vem
  como `chain:{S,…,L}` com leaders de fallback final). Overrides
  explícitos sempre vencem.

### RF-008: Liveness + replace (stall recovery)

- `WaitRunAsync` observa `ChatRun.HeartbeatAt`: `Running` sem heartbeat >
  `Agent:DevPipeline:StallSec` (default 240s) por 2 polls → run considerada
  travada → `TryStop` + marca `stopped` + **substitui** com prompt de
  retomada ("continue do estado do workdir") — registrada como
  `{phase}-stall{n}`. Limitado por `Agent:DevPipeline:MaxStallRetries`
  (default 1); esgotado → pipeline `failed` com `stalled`.
- A substituta **roda a chain de modelos** (incremento 2026-11-22):
  stall pode ser hang/quota do próprio modelo — `RotateChain` avança a
  ref `chain:{…}` da fase para o candidato n (n = retry); id simples
  passa intacto.

### RF-009: Fase `verify` read-only + proteção de teste

- Após cada validação verde, uma run LLM `verify` lê `git diff` + workdir e
  emite `verdict: pass|issues` — **read-only por contrato** (nunca edita).
  `issues` conta como falha e alimenta o prompt do `fix` (que proíbe
  alterar/remover testes para passar — fecha o gap "fix vicia o próprio
  teste").

### RF-010: Structured output + handoff

- Toda fase LLM termina com `<pipeline_result>{…}</pipeline_result>`
  (`files`, `summary`, `verdict`, `issues`, `plan`, `items`) — parseado
  para `state.PhaseResults[phase]` e exibido no relatório.
- Prompt do `fix` exige `summary` no formato handoff: Observação; Causa
  raiz; Correção; **Método de verificação (comando e saída real)** —
  herdado do handoff.md do Antigravity Teamwork.

### RF-011: Fan-out — implement paralelo por item (SPEC-20261123)

- `plan` emite `items[{id,title,files[],independent}]`; ≥2
  `independent` + binding de repo → cada item vira um **worker**: chat
  irmão (`pipeline: {title}`) com `InheritChatBinding` (worktree próprio)
  + run `implement:{id}` no modelo worker — paralelo real, cap
  `Agent:DevPipeline:MaxFanout` (default 4).
- Wait-all em paralelo; stall/replace **por runKey**
  (`StallRetriesByKey`) — um worker travado não consome o budget das
  irmãs. Resume idempotente: run completed salta, ativa é aguardada.
- Merge sequencial `MergeAsync` por worker no workdir do filho;
  conflitos → `fix` recria no workdir principal (nunca forçados);
  dependentes/overflow → `implement-rest` sequencial no filho.
- `CommitAsync` generalizou o merge: última run de cada base key
  (sucessora `-stallN` representa o estado final); `plan` não mergeia.
- Arg `fanout` (default true); sem items / <2 independentes / sem
  binding → sequencial inalterado.

### Acceptance Criteria (incremento)

- AC-8: `plan:true` → run `plan` executa antes de `implement` e seu
  `pipeline_result.plan` é injetado no prompt da implementação.
- AC-9: `worker_model`/`fix_model` refletem em `ChatRun.Model` das fases.
- AC-9b: pai fusion sem args → `Models[plan|fix]`=leader,
  `Models[implement|test|verify]`=sidekick; args explícitos vencem.
- AC-10: run de fase `Running` com `HeartbeatAt` stale → substituída com
  prompt de retomada; esgotado `MaxStallRetries` → `failed`/`stalled`.
- AC-11: validação verde dispara `verify`; `verdict:"issues"` → fix com
  as issues; sem tentativas → pipeline `failed`.
- AC-12: `ParsePhaseResult` extrai `files/verdict/issues/plan` do bloco;
  bloco ausente/malformado → null sem quebrar a fase.
- AC-13: `FanoutPlan` seleciona só `independent` (cap `MaxFanout`) e
  devolve dependentes+overflow em `Remainder`; <2 independentes,
  `fanout:false` ou sem items → vazio.
- AC-14: fan-out despacha run por item em chat irmão com binding
  herdado; `Runs["implement:{id}"]` e `FanoutItemChats` registrados;
  merge aplica diffs no workdir do filho e conflitos viram issues do fix.
