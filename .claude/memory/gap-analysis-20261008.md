# Gap Analysis — 20261008

- Repository: `/home/ubuntu/repos/open-webui` | Branch: `main` | Commit: `1fb8645b7`
- Phase reached: `specs` (Draft SPECs gerados, aguardando gate)
- Mode: full
- Contexto: executado após o merge completo do Epic E13 (8 PRs #191–#198, issues #183–#190 fechadas).

## 1. Source inventory

| Source | Status | Notes |
| --- | --- | --- |
| `.specs/` | present | apenas `README.md`; zero `SPEC-*.md` ativos (SPECs do E13 removidos após entrega) |
| `docs/specs/` | present | 48 SPECs: 35 Completed, 5 Approved, 7 Draft, 1 In Progress |
| `docs/` | present | incl. `MIGRACAO-DOTNET.md` (3 rows 🟡 = decisões documentadas) |
| `docs/architecture/` | present | ADRs + README |
| `.claude/CONTEXT.md`, `.claude/MEMORY.md`, `.claude/memory/`, `.claude/rules/`, `.claude/agents/` | present | |
| `CLAUDE.md` / `README.md` | present | convenção do repo é `CLAUDE.md` (plataforma table) |
| `AGENTS.md` (raiz) | absent | não é gap — convenção documentada usa `CLAUDE.md` |
| tests / linters / CI | present | NUnit (723+4skip API, 25 client), 5 workflows, guards em `tools/` |
| `gh` auth + remote | ok | `afonsoft/OpenWebUiCode`, sem issues abertas, PR aberto apenas #180 (dependabot) |
| `ORCHESTRATOR-ROADMAP.md` | absent | não referenciado por nenhuma fonte ativa |

## 2. AS-IS × TO-BE matrix

| Topic | AS-IS | TO-BE | Sources |
| --- | --- | --- | --- |
| CI main | `🚀 CI Build & Test` **falha** no job `Tests + Coverage Gate (NUnit)` — line 88.59% < 90.0%, branch 70.96% < 73.0% (run `37806944372` em `1fb8645b7`; mesmo step falha em `389152867` pré-E13 e `b631328d1` em 2026-10-07) | main verde; cobertura ≥ baseline ou baseline recalibrado com justificativa | `.github/workflows/ci-build-test.yml`, `.ci/coverage-baseline.txt`, logs |
| Guards de drift (`tools/check-*.py`) | 3 scripts existem (`check-css-classes`, `check-form-a11y`, `check-i18n-parity`), entregues pelos SPECs do E13, mas **zero referências** em `.github/workflows/` — nada os executa | guards rodam no CI e quebram o build em drift | `tools/`, workflows |
| Status de SPECs entregues | 5 SPECs `Approved` (série 20261003) com `## Delivered` preenchido e Issues #83/#86–#89 fechadas; 8 SPECs `Draft`/`In Progress` (série 20261007) sem `## Delivered` mas com implementação evidenciada (refs `RF-xxx` no código: `SignalRChatRunNotifier` RF-002, `ChatRunPauses` RF-014, git bar RF-018, 13 builtin tools, `ToolCallCard`, theme default light) | status reflete realidade; RFs residuais identificados e tratados | `docs/specs/*.md`, `gh issue list`, `src/` |
| Cobertura funcional | 748 testes verdes localmente; nenhum gap funcional novo identificado — features da série 20261007 implementadas | — | `dotnet test` |
| Perf boot | mobile 63 / desktop 68 (era 34/58); TBT ~5.1s residual do bundle WASM | melhoria contínua — SPEC perf-boot entregue e aceito | runs Lighthouse E13 |

## 3. Candidates and verdicts

| Key | Category | Verdict | Priority | Spec | Issue | Evidence |
| --- | --- | --- | --- | --- | --- | --- |
| `GAP-tests-coverage-gate` | tests | **CONFIRMADO** | high | `.specs/SPEC-20261008-tests-coverage-gate.md` | — | run `37806944372` log: `Line coverage 88.59% < baseline 90.0%`, `Branch coverage 70.96% < baseline 73.0%`; falha idêntica em `389152867` (12:49Z, pré-E13) |
| `GAP-automation-ci-guards` | automation | **CONFIRMADO** | medium | `.specs/SPEC-20261008-ci-tool-guards.md` | — | `grep -rn "tools/\|check-" .github/workflows/` → 0 hits; `tools/` contém 3 guards |
| `GAP-housekeeping-spec-status` | hygiene | **CONFIRMADO** | low | `.specs/SPEC-20261008-spec-status-reconciliation.md` | — | `Status:` vs `## Delivered` em `docs/specs/`; Issues #83/#86–#89 CLOSED; refs `RF-xxx` em `src/` |
| `GAP-impl-requestedPanelTab-warning` | hygiene | REJEITADO | — | — | — | warning CS0414 cosmético, sem divergência SPEC/doc; pode entrar como fix trivial futuro |
| `GAP-perf-mobile-tbt` | perf | REJEITADO | — | — | — | residual aceito e documentado do SPEC perf-boot (34→63); nenhum critério DoD violado; bundle WASM é trabalho de epic próprio, não gap de entrega |
| `GAP-docs-agents-md` | docs | REJEITADO | — | — | — | convenção do repo é `CLAUDE.md` (ver tabela de plataformas no próprio arquivo) |
| `GAP-docs-migracao-stale` | docs | REJEITADO | — | — | — | 3 rows 🟡 são decisões documentadas por design, não pendências |
| `GAP-deps-dependabot-180` | deps | INCONCLUSIVO | — | — | — | PR #180 aberto é decisão de usuário (merge de dependência), não gap de engenharia |
| `GAP-spec-20261007-residual-rfs` | spec | INCONCLUSIVO → absorvido | — | — | — | verificação RF-a-RF dos 8 SPECs é o escopo do SPEC de reconciliação; residuais, se houver, viram SPECs próprios |

## 4. Approval gate

- Decision: approved | By: user | Date: 2026-10-08
- SPECs Draft gerados:
  - `.specs/SPEC-20261008-tests-coverage-gate.md` (high — destrava a main)
  - `.specs/SPEC-20261008-ci-tool-guards.md` (medium — toca `.github/workflows/`, exige revisão humana explícita)
  - `.specs/SPEC-20261008-spec-status-reconciliation.md` (low — docs/verificação)

## 5. Issues

- Epic: gap-analysis-20261008 → https://github.com/afonsoft/OpenWebUiCode/issues/200
- Slices: #201 (tests-coverage-gate), #202 (ci-tool-guards), #203 (spec-status-reconciliation)

## 6. Orchestrator handoff

- Não executado — gate pendente.

## 7. Pendencies

- Coverage gate é **bloqueador de main** desde ≥2026-10-08T12:49Z — recomenda-se priorizar `GAP-tests-coverage-gate`.
- O job `Coverage Baseline Ratchet` existe no workflow mas aparece `skipped` — investigar se a intenção era ratchet automático de baseline (dentro do SPEC de coverage).
- Reconciliação dos SPECs 20261007 pode revelar RFs residuais → viram candidatos numa próxima rodada.
- `_requestedPanelTab` (warning) anotado como fix trivial opcional.
- PR #180 (dependabot) aguardando decisão humana.
