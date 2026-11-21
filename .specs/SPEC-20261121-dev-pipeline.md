# SPEC-20261121-dev-pipeline

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `dev-pipeline` |
| Type | `Backend / Agent Tools` |
| Stack | `.NET 10 / ASP.NET Core / EF Core` |
| Repository | `OpenWebUiCode` |
| Branch | `feature/dev-pipeline` |
| Status | `In implementation` |

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
