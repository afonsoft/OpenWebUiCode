# SPEC-20261124 — run_workflow (orquestração-como-código declarativa)

| Campo | Valor |
|-------|-------|
| Status | Implementado |
| Autor | Devin (agente) |
| Data | 2026-11-24 |
| Relacionadas | SPEC-20261121-dev-pipeline, SPEC-20261123-pipeline-fanout, SPEC-20261124-run-watchdog |

## Contexto

O `dev_pipeline` executa um ciclo fixo (`plan → implement → test →
validate → verify → commit`). O `run_workflow` do agy generaliza isso
em primitiva de orquestração: `agent()`, `parallel()`, `phase()`,
`call_tool()`, `trajectory()`. A versão fiel exigiria um interpretador de
código — fora de escopo. A fatia de maior valor real:

- o modelo (ou o usuário) **declara os passos em JSON** — ordem, prompts,
  itens paralelos, chamadas determinísticas;
- o servidor executa os passos **em código** — mesma garantia estrutural
  do pipeline (validate/commit/tool nunca dependem do modelo lembrar).

## Requisitos funcionais

### RF-001 Tool `builtin:run_workflow`

Args: `task` (obrigatório), `steps` (array JSON, obrigatório), `repo`,
`push` (default false), `worker_model`, `fix_model`, `max_parallel`
(default = `Agent:DevPipeline:MaxFanout`). Cria pipeline com
`State.Steps` preenchido → `DriveAsync` ramifica para `RunStepsAsync`.

### RF-002 Kinds de step

| kind | Semântica |
|------|-----------|
| `llm` | run na fase `key` no chat filho; `<pipeline_result>` → `PhaseResults[key]` |
| `parallel` | `items[]` com `id`/`prompt`/`files?` — workers em chats irmãos+worktree, ondas de `MaxFanout`, merge sequencial; exige binding |
| `tool` | executa `name` builtin com `args` server-side → `PhaseResults[key]` |
| `validate` | `ProjectValidation` no workdir do filho — falha → pipeline failed |
| `commit` | `git add -A`+commit (e push quando `push:true`) no workdir do filho |

### RF-003 Interpolação de prompt

`{task}` → `State.Task`; `{results:<key>}` → verdict ?? summary de
`PhaseResults[<key>]`; `{plan}` → `State.Plan`. Substituição simples,
antes de enfileirar (equivalente leve do `trajectory()`).

### RF-004 Execução e resume

`RunStepsAsync` itera de `State.StepIndex`; cada step bem-sucedido grava
`StepIndex = i+1` + `SaveAsync` — restart/replay retoma do ponto.
`kind:parallel` reusa workers do `FanoutItemChats` (resume idempotente:
run completed → só merge). Falha de qualquer step → `MarkFailedAsync`.

### RF-005 Modelos, stalls, wake, telemetria

Tudo herdado da maquinaria existente: `worker_model`/`fix_model` por fase
lógica (o `key` do step resolve `state.Models[key]`, caindo no default
worker), stall→replace com rotação de chain, wake event-driven e
telemetria por run no report — sem código novo.

### RF-006 Report

`BuildReport`/`ReportAsync` sem mudança — `PhaseResults` por `key`,
`FanoutItemChats` por `parallel:{key}:{id}` (chats dos workers no link).

## Não-objetivos

- Sem interpretador/DSL de código (loops, condicionais) — steps são uma
  lista ordenada; ramificação fica a cargo do autor do workflow.
- `kind:parallel` exige binding (mesma regra do fan-out); sem binding o
  step falha com erro claro — não há fallback sequencial aqui porque o
  autor pode simplesmente declarar `llm` sequenciais.
- `validate` falho para a pipeline (sem loop de fix automático — o autor
  encadeia um step `llm` de correção se quiser, lendo `{results:k}`).

## Critérios de aceite

- [ ] `run_workflow` com 3 steps (`llm` → `validate` → `commit`) executa
      na ordem, persiste `StepIndex`, grava `PhaseResults` por key.
- [ ] `{results:analyze}` num prompt posterior recebe o resultado da key.
- [ ] `kind:parallel` cria chats irmãos com binding herdado e faz merge.
- [ ] `kind:tool` executa builtin com args e registra saída.
- [ ] `kind:validate` falho → pipeline `failed` com output no erro.
- [ ] `kind:commit` gera sha (e push quando `push:true`).

## Testes

Casos em `tests/OpenWebUI.Api.Tests/DevPipelineTests.cs` (fixture de
integração sem provider programável — mesma decisão do fan-out: e2e fica
para smoke manual; o contrato e os helpers ficam sob unit test):

1. `WorkflowValidate_StepsValidos_SemErro` — os 5 kinds aceitos.
2. `WorkflowValidate_ErrosEstruturais` — vazio, key duplicada, kind
   desconhecido, parallel sem items/id, tool sem nome.
3. `Interpolate_SubstituiTaskPlanEResults` — placeholders resolvidos.
4. `AnchorRunId_UltimoStepConcluido_OuPai` — encadeamento de worktree
   (step N herda o worktree isolado da run do step N-1).
5. `WorkflowStep_DesserializaDoJson` — contrato JSON dos steps.
6. `RunWorkflowTool_SemTaskOuSteps_ErroClaro` — validação de args na tool.
