# SPEC-20261016-spec-status-sweep-r2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `spec-status-sweep-r2` |
| Type | `Docs/Process` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Status | `Approved` |
| Ticket | [#393](https://github.com/afonsoft/open-webui/issues/393) · Epic [#392](https://github.com/afonsoft/open-webui/issues/392) |
| Fonte | `gap-analysis` run 2026-10-16 — gap `GAP-doc-stale-specs-r2` (recorrência do GAP-doc-stale-specs do run 2026-10-10) |

## 1. User Story

**As a** mantenedor do fork
**I want** os SPECs de `.specs/` com status fiel ao estado real (entregues arquivados em `docs/specs/` conforme a convenção do `.specs/README.md`) e um guard que detecte drift
**So that** `.specs/` reflita apenas trabalho em voo e auditorias futuras não reportem falso backlog.

**Problem context:**
Run 2026-10-16 encontrou 10 SPECs em `.specs/` divergentes do código mergeado. Na execução, o sweep cobriu **toda** a população com status terminal (52 SPECs movidos para `docs/specs/`):

- `Approved` mas já implementadas (evidência de código + PRs mergeados): `agent-memory` (MemorySyncService, migration `20261010132044_AgentMemory`), `agent-routines` (RoutineBuiltinTools), `context-compaction` (ContextCompactionService + Program.cs), `delegate-persona` (DelegateTaskBuiltinTool), `memory-autosync-retention` (RetentionService), `steer-queue` (PollSteerAsync em ChatRunExecutor), `subagent-worktree` (WorktreeService + ChatRunDispatcher), `worktree-review` (WorktreeBuiltinTools `worktree_diff`/`worktree_merge` + `merge` em delegate_task).
- `Em execução`: `ide-bundle` (fases A–D mergeadas em #334–#337; pendente apenas validação visual → ver SPEC-20261016-ide-visual-validation).
- `In implementation`: `plan-spec-sdd` (mergeado em #380; testes em `tests/OpenWebUI.Api.Tests/PlanSpecSddTests.cs`).
- Sem header `Status` (viola o frontmatter da convenção): `ide-intellisense`, `ide-search-scm` (ambas implementadas — LspEndpoints, LspClient, IdeSearchView/IdeScmView).

Sweep anterior (SPEC-20261008-spec-status-reconciliation, Completed) resolveu uma vez, mas o drift recorreu → o mecanismo é manual e não roda por conta própria.

## 2. Scope

**In scope:**
- Reconciliar status das 10 SPECs listadas: mover entregues para `docs/specs/` com `Status: Completed` + subseção `Delivered` (PR/commit), conforme `.specs/README.md`.
- Adicionar header `Status` às SPECs IDE que não o têm.
- **RF-1 (anti-recorrência, a decidir na entrevista):** `tools/check-spec-status.py` no padrão dos drift guards existentes (`check-css-classes.py` etc.) — detecta SPEC em `.specs/` com `Status: Completed`/`Done` não arquivado e SPEC sem header `Status`. Opcional: ligar ao CI.
- Atualizar `.specs/README.md` se a convenção mudar.

**Out of scope:**
- Reescrever conteúdo dos SPECs; mexer em `docs/specs/` arquivados; editar workflows (hard rule — se RF-1 for ao CI, fica como follow-up humano).

## 3. Technical Context

- Convenção: `.specs/README.md` — entregues movem para `docs/specs/` (63 arquivados hoje).
- Drift guards existentes: `tools/check-css-classes.py`, `check-form-a11y.py`, `check-i18n-parity.py`.
- SPECs afetados: ver §1 (lista completa com evidência).

## 4. Requirements

- **RF-001:** cada SPEC entregue sai de `.specs/` para `docs/specs/` com `Status: Completed` e `Delivered` apontando PR/commit.
- **RF-002:** `ide-intellisense` e `ide-search-scm` ganham header `Status` (Implemented/Completed conforme entrega).
- **RF-003:** `ide-bundle` permanece em `.specs/` até a validação visual (SPEC-20261016-ide-visual-validation) fechar o último item — atualizar texto do status para refletir isso.
- **RF-004 (opcional/entrevista):** guard `tools/check-spec-status.py` + documentação no CLAUDE.md.

## 5. Acceptance Criteria

- [x] `ls .specs/` só contém trabalho realmente em voo (4 SPECs: `ide-bundle` aguardando validação visual, 3 Drafts deste run de gap-analysis).
- [x] Cada SPEC movido tem `Delivered` com referência de PR/commit verificável.
- [x] Nenhum SPEC em `.specs/` sem header `Status` (guard falha se houver).
- [x] Guard roda limpo (`python3 tools/check-spec-status.py` → `OK: 4 SPECs em voo, 115 arquivados, todos com Status`) e está documentado em `.specs/README.md` + `CLAUDE.md`.

## 6. Task Plan

- [x] T1 — auditar SPECs com status terminal, confirmar PR/commit de entrega de cada uma (52 movidos).
- [x] T2 — mover/arquivar + corrigir headers.
- [x] T3 — RF-004 implementado: `tools/check-spec-status.py`.
- [x] T4 — validação: guard + revisão do diff.

## Delivered

- Branch `docs/Devin-20261016-gap-analysis-r2`: 52 SPECs terminais movidos de `.specs/` → `docs/specs/` com `Status: Completed` + `Delivered`; `ide-bundle` permanece ativo aguardando #395; novo guard `tools/check-spec-status.py` (falha em: ativo sem header `Status`, terminal esquecido em `.specs/`, não-terminal em `docs/specs/`); convenção documentada em `.specs/README.md` e comando registrado em `CLAUDE.md`.
