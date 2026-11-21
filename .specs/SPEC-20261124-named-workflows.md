# SPEC-20261124-named-workflows — workflows nomeados reutilizáveis

## Status: Implementado

## Motivação

O `run_workflow` exigia o JSON de steps inline a cada chamada — orquestrações
recorrentes (lint-fix-review, deploy-gated) pediam repetição. O agy salva
subagentes/workflows como biblioteca.

## Design

- kv `wf:{userId}:{name}` (mesmo ConfigService do `devpipeline:{id}`);
  nome `[A-Za-z0-9._-]{1,64}` — isolamento por usuário na chave.
- `NamedWorkflow(Steps, Push, WorkerModel, FixModel, MaxParallel)` —
  steps + defaults; `task`/`context` seguem por chamada (o objetivo varia).
- `run_workflow` ganha `op`: `run` (default) | `list` | `delete`.
  - `run` + `workflow:"nome"` → carrega steps+defaults; args inline
    (steps, push, modelos, max_parallel) sobrescrevem o salvo.
  - `run` + `save_as:"nome"` → persiste antes de disparar.
  - `delete` → grava null (kv append-only; GetAsync devolve ausente).
  - `list` → prefix scan filtrando removidos.

## Testes

- `ValidWorkflowName` — alfabeto/limite.
- Roundtrip service-level: save → get → list → delete (+ isolamento
  por userId).
