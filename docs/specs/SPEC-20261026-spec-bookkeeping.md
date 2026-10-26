# SPEC-20261026-spec-bookkeeping

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `spec-bookkeeping` |
| Type | `Docs` (SPEC bookkeeping / drift fix) |
| Stack | `Docs` (Markdown + `tools/check-spec-status.py` guard) |
| Repository | `afonsoft/open-webui` |
| Branch | `feature/agent-pipe_10cb088ca2d9439e884234def1454dd0-single-agent` |
| Ticket | `#506` ([Epic] gap-analysis-20261024 — gateway hardening + orphan approved SPECs) |
| Status | `Completed` |

**Approval note:** pipeline autônoma (builder, sem humano no loop). Autoridade =
Epic #506 (bookkeeping de SPECs órfãs) + convenção documentada em
`.specs/README.md` ("entregue → `docs/specs/` + `Status: Completed` + seção
`Delivered`") + evidência de merges em `main`. Nenhuma regra de negócio nova é
inventada; apenas a convenção existente é aplicada.

## 1. User Story

**As a** orchestrator/mantenedor do pipeline SDD
**I want** que SPECs entregues estejam arquivadas com status terminal e a issue
épica reflita o estado real
**So that** o drift guard (`tools/check-spec-status.py`) volte a passar e o
bookkeeping da épica #506 fique verdadeiro.

**Problem context:**
A auditoria `gap-analysis-20261024` gerou a épica #506. Desde então os slices
#507–#512 mergearam, mas restaram dois desvios de bookkeeping em `main`:

1. `docs/specs/SPEC-20261018-model-variants.md` foi arquivada (PR #526) com
   `Status: Approved` — o drift guard falha: arquivado com status não-terminal.
2. `.specs/SPEC-20261018-references.md` foi entregue (PR #525, merge `90188d4`)
   mas nunca arquivada — continua em `.specs/` como se estivesse em voo; a
   issue #512 segue aberta/`todo`.

## 2. Scope

**In scope:**
- Corrigir `Status` de `docs/specs/SPEC-20261018-model-variants.md` para
  `Completed` e adicionar seção `## Delivered` (PR #524, issue #511,
  archive PR #526).
- Mover `.specs/SPEC-20261018-references.md` → `docs/specs/`, marcar
  `Status: Completed` e adicionar `## Delivered` (PR #525, issue #512).
- Registrar follow-up em `.claude/memory/gap-analysis-20261024.md`.
- Validar com `python3 tools/check-spec-status.py` → exit 0.

**Out of scope:**
- Implementar `themes-custom` (#513 — `in-progress` sob outro agente).
- `policies-admin` (In implementation) e `pr-review-sweep` (Draft) — fora de
  escopo da épica.
- `permissions-v2` (Approved, depende de policies-admin — permanece em voo).
- Fechar/re-etiquetar issues remotas (#512 e labels stale de #507/#511):
  reportado à épica; decisão do orchestrator.
- Alterar código de aplicação, testes ou `tools/check-spec-status.py`.

## 3. Technical Context

**Where the change happens:** bookkeeping de SPECs — diretórios `.specs/`
(em voo) e `docs/specs/` (arquivo), governados por `.specs/README.md` e pelo
guard `tools/check-spec-status.py` (local/pre-PR; não roda em CI).

**Files to read before implementing:**
- `.specs/README.md` · `tools/check-spec-status.py`
- `docs/specs/SPEC-20261018-agent-polish.md` (formato de `## Delivered`)
- `.claude/memory/gap-analysis-20261024.md`

**Files to create or modify:**
```text
docs/specs/SPEC-20261018-model-variants.md   (Status + Delivered)
.specs/SPEC-20261018-references.md → docs/specs/SPEC-20261018-references.md
                                          (git mv + Status + Delivered)
.claude/memory/gap-analysis-20261024.md      (seção Follow-up)
.specs/SPEC-20261026-spec-bookkeeping.md     (esta SPEC → arquivar ao final)
```

## 4. Requirements

### RF-001: model-variants com status terminal
- **Description:** `docs/specs/SPEC-20261018-model-variants.md` deve ter
  `Status: Completed` no metadata e seção `## Delivered` com referências
  (PR de implementação #524, issue #511, archive PR #526).
- **Rules:** não alterar nenhum outro conteúdo da SPEC.
- **Input → Output:** spec arquivada não-terminal → spec arquivada terminal.

### RF-002: references arquivada como entregue
- **Description:** `.specs/SPEC-20261018-references.md` move para
  `docs/specs/` (preservando histórico via `git mv`), `Status: Completed`,
  seção `## Delivered` (PR #525 merge `90188d4`, issue #512).
- **Rules:** conteúdo da SPEC intacto; `## Delivered` segue o formato das
  demais arquivadas (`- PR:` + `- Issue:`).

### RF-003: drift guard verde
- **Description:** `python3 tools/check-spec-status.py` deve sair com exit 0
  e listar `.specs/` sem SPECs terminais e `docs/specs/` sem SPECs
  não-terminais.

### RF-004: rastreabilidade
- **Description:** `.claude/memory/gap-analysis-20261024.md` ganha seção de
  follow-up registrando verificação pós-merge (8 fixes presentes em main via
  squash #497, slices #507–#512, pendências #513 + labels stale + issue #512
  remota aberta) e esta correção.

## 6. Acceptance Criteria

- [ ] **Given** `docs/specs/SPEC-20261018-model-variants.md` **when** lido o
  header `Status` **then** é `Completed` e existe seção `## Delivered`.
- [ ] **Given** `.specs/SPEC-20261018-references.md` **when** `git status`
  **then** o arquivo consta renomeado para `docs/specs/` e seu `Status` é
  `Completed` com `## Delivered` (PR #525, issue #512).
- [ ] **Given** a árvore final **when** `python3 tools/check-spec-status.py`
  **then** exit 0 com mensagem `OK: N SPECs em voo, M arquivados`.
- [ ] **Given** o diff final **when** inspecionado **then** nenhum arquivo de
  código, workflow ou SPEC não-listada foi modificado.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| `.specs/` residual | SPECs ainda em voo | `permissions-v2`, `policies-admin`, `themes-custom`, `pr-review-sweep`, e esta SPEC permanecem não-terminais em `.specs/` |
| Esta SPEC ao entregar | self-reference | movida a `docs/specs/` com `Status: Completed`/`Delivered` no mesmo commit |

## 7. Task Plan (agent execution)

- [x] **T1 — Discovery:** verificar merges (#497 gateway, #524/#525 slices),
  estado das SPECs, colunas das issues (#506–#513) e falha do drift guard.
- [x] **T2 — Implementation:** editar model-variants (Status+Delivered);
  `git mv` references → `docs/specs/` (Status+Delivered); follow-up no memory.
- [x] **T3 — Verification:** `python3 tools/check-spec-status.py` → exit 0
  (`OK: 5 SPECs em voo, 159 arquivados`); diff limitado aos arquivos da §3.
- [x] **T4 — Validation:** Docs-only — sem build/testes de código; evidência =
  drift guard + diff review (tabela 7.1, linha `Docs`).
- [x] **T5 — Done:** DoD completo → arquivar esta SPEC em `docs/specs/` no
  mesmo commit (sem PR — regra da pipeline proíbe abrir PR nesta sessão).

**7.1 Validation strategy:** Type `Docs` — markdown íntegro, convenção de
arquivo aplicada, drift guard exit 0. Sem cobertura de código exigida.

## 8. Organization Guardrails

- **Branches:** trabalho na branch da pipeline
  `feature/agent-pipe_10cb088ca2d9439e884234def1454dd0-single-agent`; nunca
  commit em `main`.
- **Workflows:** `.github/workflows/` intocado.
- **Scope:** somente os 4 arquivos da seção 3; não fechar issues remotas.
- **Security:** sem secrets; sem `.env`; sem conteúdo sensível nos arquivos.

## 9. Definition of Done

- [x] RF-001..RF-004 implementados.
- [x] Critérios da seção 6 verificados (drift guard exit 0).
- [x] Diff limitado aos arquivos listados.
- [x] Guardrails da seção 8 respeitados.

## Delivered

- **Issue:** #506 (épica gap-analysis-20261024 — slice de bookkeeping)
- **Branch:** `feature/agent-pipe_10cb088ca2d9439e884234def1454dd0-single-agent`
- **Commit:** entrega nesta mesma mudança (pipeline sem PR — ver regras da sessão)
- **Validação:** `python3 tools/check-spec-status.py` → `OK: 5 SPECs em voo, 159 arquivados`

## Open Questions / Pending Ambiguity

- Fechamento remoto da issue #512 (entregue, open/`todo`) — fora de escopo do
  builder; sinalizado no relatório final ao orchestrator.
- Labels stale `todo`/`in_progress` em issues fechadas (#507/#511) — mesmo
  tratamento.
