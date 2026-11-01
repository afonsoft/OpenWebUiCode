# SPEC-20261031-sonar-smell-scripts-tools

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-smell-scripts-tools` |
| Type | `Refactor` |
| Stack | `Shell` + `Python` + `JavaScript` (tooling do repo) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-smell-scripts-tools-20261031` |
| Ticket | `GH#571` |
| Status | `Completed` |
| Parent | sonarqube-autofix 2026-10-31 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** resolver as 43 issues de code smell em `scripts/`, `tools/` (drift guards, perf tooling, ide-bundle fonte)
**So that** o tooling do repo fica limpo no SonarCloud — e o guard `check-pr-test-policy.sh` que roda no CI fica idiomático.

**Problem context:**
43 issues em 8 arquivos de tooling (exclui `S7679` em `ci-build-test.yml` → SPEC vuln-ci-workflows; `docker:S7031` em `Dockerfile` → SPEC vuln-code):

| Issues | Arquivo | Regras principais |
| --- | --- | --- |
| 11 | `tools/ide-bundle/src/index.js` | `javascript:S6582` ×7 (optional chaining), `S3863`, `S1128`, `S7755`, `S7785` |
| 10 | `tools/perf/loadtest.py` | `python:S3776` ×4 (complexidade), `S7503` ×2, `S1172`, `S1481`, `S3358`, `S7484` |
| 8 | `scripts/check-pr-test-policy.sh` | `shell:S7688` ×8 — **executa no CI**: expansão de var sem `${}`/`$()` moderno |
| 5 | `tools/perf/mock_ollama.py` | `python:S1172`, `S8786` ×2, `S3457`, `S8495` |
| 3 | `tools/check-spec-status.py` | `python:` misc |
| 3 | `scripts/classify-changes.sh` | `shell:S7688`, `S131`, `S7679` |
| 2 | `tools/check-form-a11y.py` | `python:` misc |
| 1 | `scripts/check-env-drift.sh` | `shell:S7688` |

Notas: `shell:S7688` = "Use modern expansions" — substituir `` `cmd` `` → `$(cmd)` e `$[expr]`/`$((old))` legados; `python:S7503` = f-string/format moderno.

## Execução (2026-11-01)

**Status:** leva aplicada — 43 findings dos 8 arquivos processados.

| Arquivo | Aplicado |
| --- | --- |
| `scripts/check-pr-test-policy.sh` | 8× S7688 `[`→`[[` |
| `scripts/classify-changes.sh` | S7688 + S7679 (`local pattern="$1"`) |
| `scripts/check-env-drift.sh` | S131 `*)` default no `case` |
| `tools/check-spec-status.py` | S8786 ×2 (regex sem backtracking ambíguo) + S1192 `SPEC_GLOB` |
| `tools/check-form-a11y.py` | S108 (elif morto) + S3776 `main()`→4 helpers `check_*` (46→<15) |
| `tools/perf/loadtest.py` | S1172 ×3 (`expected`/`args`/`metrics`), S1481 `_`, S3457 f-string, S3358 if/elif, S3776 ×3 (`_drain_sse_events`, `_rps_dispatch`, `_subagent_one`/`_settle_children`), S7484 polling→`asyncio.Event`+`wait_for` |
| `tools/perf/mock_ollama.py` | S8495 tuplas uniformes (`_plan`→3-tuple sempre) |
| `tools/ide-bundle/src/index.js` | S1128 `Prec`, S3863 import fundido, S6582 ×7 `?.`, S7755 `.at(-1)` + bundle regenerado |

**Won't-fix justificado:**
- `python:S7503` ×4 em `mock_ollama.py` (`tags`/`show`/`version`/`embeddings`): handlers aiohttp **precisam** ser corotinas — remover `async` quebra o contrato do framework; `await` artificial seria código morto.

**Validação:** `shellcheck -S error` OK; `bash -n` OK; `py_compile` OK; `node --check` em `index.js` + bundle; drift guards idênticos a `main` (`check-form-a11y` 72 arquivos OK, `check-spec-status` mesmo veredito, `check-i18n-parity` 1266 chaves, `check-css-classes` OK); `_plan` testado por import; `loadtest --help` OK.

## 2. Scope

**In scope:** as 43 issues em `scripts/*.sh` e `tools/**/*.py`/`tools/ide-bundle/src/index.js`.

**Out of scope:**
- `S7679` `ci-build-test.yml` (SPEC vuln-ci-workflows) e `docker:S7031` `Dockerfile` (SPEC vuln-code).
- `tools/ide-bundle` build tooling além das issues (sem refatorar o bundler).
- Reescrita funcional dos scripts (mesmo comportamento — os 3 drift guards rodam no CI).

## 3. Technical Context

**Where the change happens:** scripts de guard do repo (`tools/check-*.py`, `scripts/*.sh` — 3 rodam no job "Blazor WASM Client Validation" do CI), perf tooling (`tools/perf/`), e a fonte do bundle IDE (`tools/ide-bundle/src/index.js` → gera `wwwroot/js/ide-editor.bundle.js`).

**Files to read before implementing:**
- `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` (grupos shell/python/js-tools)
- `tools/check-pr-test-policy.sh`? — atenção: arquivo real é `scripts/check-pr-test-policy.sh`
- `tools/ide-bundle/` (build do bundle — regenerar `ide-editor.bundle.js` se a fonte mudar)
- `CLAUDE.md` (drift guards no CI)

**Files to create or modify:**
```text
scripts/check-pr-test-policy.sh
scripts/classify-changes.sh
scripts/check-env-drift.sh
tools/perf/loadtest.py
tools/perf/mock_ollama.py
tools/check-spec-status.py
tools/check-form-a11y.py
tools/ide-bundle/src/index.js (+ regenerar src/OpenWebUI.Client/wwwroot/js/ide-editor.bundle.js)
```

## 4. Requirements

### RF-001: Shell idiomático (shell:S7688 + S131 + S7679)
- **Description:** substituir expansões legadas por `$(...)`, `${var}` explícitas, `local` para posicionais (S7679) e remover o S131 (`for` sobre saída de comando → `while read`/array).
- **Rules:** scripts passam em `shellcheck -S error` (mesmo gate do AGENTS.md); comportamento/exit codes idênticos — `check-pr-test-policy.sh` decide merge no CI.

### RF-002: Python idiomático (perf + checks)
- **Description:** `S7503`→f-string; `S1172`→param morto removido; `S3776`→extrair subfunções; `S8786`/`S3457`/`S1481`/`S3358`/`S7484`/`S8495`/`S108`→fix canônico; `check-spec-status.py`/`check-form-a11y.py` — guards do CI permanecem com vereditos idênticos.
- **Input → Output:** mesma saída/exit code para os mesmos inputs.

### RF-003: ide-bundle source (js:S6582 etc.)
- **Description:** fixes em `tools/ide-bundle/src/index.js` (optional chaining, vars, imports) + regenerar `wwwroot/js/ide-editor.bundle.js` pelo build do bundle e commitar o artefato.
- **Rules:** bundle regenerado via comando do `tools/ide-bundle` (nunca editado à mão).

### RF-004: Guards verdes
- **Description:** `check-css-classes.py`, `check-form-a11y.py`, `check-i18n-parity.py`, `check-spec-status.py`, `check-pr-test-policy.sh` continuam PASSANDO (e ainda detectam drift — smoke test com um drift artificial descartado depois).

**Business rules / invariants:** exit codes e stdout dos guards byte-compatíveis onde o CI depende; bundle regenerado pela fonte.

## 5. API Contract

N/A.

## 6. Acceptance Criteria

- [ ] **Given** `check-pr-test-policy.sh`, **when** `shellcheck -S error` roda, **then** limpo — e o script classifica PRs igual antes (teste com casos do CI).
- [ ] **Given** `loadtest.py`/`mock_ollama.py`, **when** executados, **then** mesma saída funcional.
- [ ] **Given** `index.js` alterado, **when** o bundle rebuilda, **then** `ide-editor.bundle.js` regenerado e commitado; editor abre no browser.
- [ ] **Given** os 4 drift guards, **when** executados no repo limpo, **then** todos PASS.
- [ ] **Given** o rescan, **then** zero issues shell/python/tools no board.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Guard com drift real | spec/i18n fora de paridade | script ainda falha corretamente (smoke test descartável) |
| Bundle sem build doc | `tools/ide-bundle` sem comando claro | documentar comando no SPEC/README se ausente antes de regen |
| `$(cmd)` vs `` `cmd` `` | outputs multiline | preservar quoting — nenhuma word-splitting nova |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** listar issues do bucket; rodar `shellcheck` baseline; localizar comando de build do ide-bundle.
- [ ] **T2 — Shell:** os 3 scripts — `$(...)`, `${}`, `local`, `while read`.
- [ ] **T3 — Python:** `loadtest.py` (10) + `mock_ollama.py` (5) + `check-spec-status.py` (3) + `check-form-a11y.py` (2).
- [ ] **T4 — ide-bundle:** fix `index.js` + regen do bundle.
- [ ] **T5 — Validation:** shellcheck, execução dos guards (PASS + smoke drift), rescan.
- [ ] **T6 — Done + PR.**

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **Refactor / tooling** | `shellcheck -S error` limpo; guards PASS no repo limpo e FALHA em smoke de drift; bundle regenerado pela fonte; rescan sem as issues do bucket. |

## 8. Organization Guardrails (mandatory)

- **Branches:** `fix/sonar-smell-scripts-tools-20261031`.
- **CI guards:** comportamento/vereditos preservados — os scripts protegem PRs; nenhum afrouxamento de gate.
- **Bundle:** `ide-editor.bundle.js` só muda via rebuild.
- **Scope:** 43 issues do bucket — nada além.

## 9. Definition of Done

- [ ] 43 issues resolvidas (fix ou won't-fix justificado).
- [ ] shellcheck limpo; guards validados (PASS + smoke).
- [ ] Bundle regenerado; rescan limpo para o bucket.

**Next action after DoD is complete:** `Status = Done` + PR.

## Open Questions / Pending Ambiguity

- `[A DEFINIR]` Comando exato de rebuild do `ide-editor.bundle.js` (package.json do `tools/ide-bundle` — confirmar em T1).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/589 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/571
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/560
