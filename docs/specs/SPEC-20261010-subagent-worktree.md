# SPEC-20261010-subagent-worktree — Worktree isolada por sub-agent

Status: Completed

## Contexto
`WorktreeService.TryCreateForRunAsync` já cria worktree por run, mas só quando `Workspace:RunIsolation=worktree` (default: shared). Sub-agents do `delegate_task` dividem o mesmo checkout por slug — workers paralelos no mesmo repo colidem. Referência: Batuta (opencode-alltomatos) exige worktree por worker, sempre.

## Objetivo
Runs filhas (`ParentRunId != null`) SEMPRE rodam num git worktree isolado, independente do flag global. Runs raiz continuam respeitando `Workspace:RunIsolation`.

## Escopo
1. `TryCreateForRunAsync` ganha `force` (default false): quando true pula o check `IsolationEnabled` (demais fallbacks intactos: sem repo git → shared, falha no `worktree add` → shared com warning).
2. `ChatRunExecutor` passa `force: run.ParentRunId is not null`.
3. Sem mudança no ciclo de vida: worktree permanece até merge explícito (`/merge`) ou `PruneOrphansAsync` (TTL) — o pai precisa dele vivo pra revisar o diff (SPEC-20261010-review-before-merge).

## Fora de escopo
Merge automático ao fim da run; review/diff tooling (SPEC-20261010-review-before-merge); mudança no default do flag.

## Acceptance Criteria
- Sub-run com `ParentRunId` em repo com binding git → `ResolveIsolated(user, runId)` retorna path diferente do workdir principal, mesmo com `RunIsolation` ausente.
- Run raiz sem flag continua compartilhando o workdir.
- Sem repo git no workdir → sub-run cai em shared com warning (não quebra).
- Tests: force=true cria worktree sem flag; force=false sem flag → shared; sem repo → shared.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/301 |
