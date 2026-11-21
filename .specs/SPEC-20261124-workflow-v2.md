# SPEC-20261124-workflow-v2 — steps condicionais (`when`) + gate humano

## Status: Implementado

## Motivação

O `run_workflow` v1 executa steps estritamente em sequência e qualquer
falha mata a pipeline. Dois buracos vs. orquestração real (agy):

1. **Sem condicional**: `validate` falha → pipeline morre; "rode `fix`
   só se `lint` falhou" não é expressável.
2. **Sem gate humano**: o requisito "merge/deploy approval-gated" não
   cabe num workflow — não há step que pause para decisão do dono.

## Design

### `when` — condição por step (string?)

- `null`/`""`/`"always"` → roda sempre (default).
- `"passed:<key>"` → roda se `PhaseResults[key].Verdict == "pass"`.
- `"failed:<key>"` → roda se `PhaseResults[key].Verdict != "pass"`
  (qualquer não-pass, ex.: `"issues"`).
- Key sem resultado (step pulado ou ainda não rodado) → condição **não**
  satisfeita → step pulado (cadeia de skips é legítima).
- Formato inválido → erro de `Validate()` na autoria; em runtime
  (kv editado à mão) → falha do workflow, nunca skip silencioso.
- `Validate()` exige que a key referenciada seja de um step **anterior**.

Steps pulados vão para `State.SkippedSteps` (auditáveis no report).

### `optional` — falha não-fatal por step (bool?)

`optional:true` → falha do step registra `PhaseResults[key].Verdict =
"issues"` e o workflow **continua**. Sem optional, falha marca a pipeline
`failed` como hoje. Aplica aos kinds determinísticos
(tool/validate/commit/parallel/gate); `llm` mantém falha fatal (o runner
LLM marca a pipeline internamente).

Padrão retry-on-fail:

```json
{"kind":"validate","key":"lint","optional":true},
{"kind":"llm","key":"fix","when":"failed:lint",
 "prompt":"corrija: {results:lint}"},
{"kind":"validate","key":"lint2"}
```

### `gate` — aprovação humana

`{"kind":"gate","key":"deploy-ok","prompt":"Subir para produção?"}`

- Sem resposta → a pipeline **estaciona**: `AwaitingGate=key` +
  `Status="running"` persistidos (resume reinicia o drive no ponto),
  mensagem postada no chat pai (mesmo mecanismo do report) explicando
  como responder.
- `dev_pipeline op=answer pipeline_id=X approved=true|false
  answer="texto"` → grava `GateAnswers[key]`, limpa `AwaitingGate` e
  re-dispara o runner.
- Resolvido: `approved` → verdict `"pass"`; `rejected` → `"issues"`,
  fatal salvo `optional:true` (rejeição segue o fluxo — próximo step
  pode ser `when:"passed:gate"` para deploy-condicional).

Sem depender de `ask_user`/preset: preset auto-pick auto-aprovaria —
o gate é **máquina de estado da pipeline**, não uma tool call.

## Mudanças

- `State`: `GateAnswers`, `AwaitingGate`, `SkippedSteps`.
- `WorkflowStep`: `When`, `Optional`.
- `RunStepsAsync`: eval `when` → gate → dispatch; falha opcional-aware.
- `FailStepAsync` → `IssueAsync` nos runners (registra verdict sem
  marcar — o loop decide fatal × optional).
- `ReportAsync` → extrai `PostToParentAsync` (reuso pelo gate).
- `BuildReport`: linhas `Gate pendente:` / `Pulados (when):`.
- `DevPipelineService.AnswerGateAsync` + `dev_pipeline op=answer`
  (`approved`, `answer`); `status` mostra gate pendente.
- `run_workflow`: kinds + `gate`, campos `when`/`optional`, `Validate()`
  com checagem de `when` (formato + ref a step anterior).

## Testes

- `EvaluateWhen`: truth table (always/passed/failed/missing/inválido).
- `Validate`: kind gate; `when` válido/inválido/ref posterior/self-ref.
- Roundtrip JSON dos novos campos de `State`/`WorkflowStep`.

## Não-objetivos

- UI de aprovação (question_asked/buttons) — v1 é texto+op=answer.
- Loop `foreach`, `while`, sub-workflows — P2.
- `when` avaliando expressões (`>`, `&&`) — só veredito por key.
