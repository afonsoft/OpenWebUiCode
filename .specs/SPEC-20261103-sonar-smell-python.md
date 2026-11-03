# SPEC-20261103-sonar-smell-python

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-smell-python` |
| Type | `Refactor` |
| Stack | `Python` (tools/) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-smell-python-20261103` |
| Ticket | `GH#656` |
| Status | `Approved` |
| Parent | sonarqube-autofix 2026-11-03 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** limpar as 6 issues Python em `tools/` (`S7503` ×4, `S3776` ×1, `S8786` ×1)
**So that** o harness de perf e o drift guard de SPECs ficam livres de smells sem mudar saída.

**Problem context:**

| Regra | Arquivo:linha | Problema |
| --- | --- | --- |
| `python:S7503` ×4 | `tools/perf/mock_ollama.py` L95,118,124,135 | função `async` sem uso de `await` — remover `async` ou usar features assíncronas |
| `python:S3776` | `tools/perf/loadtest.py` L244 | complexidade cognitiva 16 (>15) |
| `python:S8786` | `tools/check-spec-status.py` L27 | regex com runtime super-linear — simplificar |

## 2. Scope

**In scope:** as 6 issues nos 3 arquivos.

**Out of scope:**
- Mudança de protocolo do `mock_ollama.py` (o harness `perf.yml` depende dos gatilhos `__delegate__`/`__words__`/`__sleep__`).
- Mudança de formato de saída do `check-spec-status.py` e do `loadtest.py` (CI consome).

## 3. Technical Context

**Where the change happens:** `tools/perf/` — mock Ollama + load harness usados pelo workflow `perf.yml`; `tools/check-spec-status.py` — drift guard de bookkeeping `.specs/` vs `docs/specs/` (PR checklist).

**Files to read before implementing:**
- `tools/perf/mock_ollama.py` L90–140 (as 4 funções async), `tools/perf/loadtest.py` L240+ (função alvo), `tools/check-spec-status.py` L20–35 (regex)
- `tools/perf/README.md` — contratos dos cenários
- `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md`

**Files to create or modify:**
```text
tools/perf/mock_ollama.py
tools/perf/loadtest.py
tools/check-spec-status.py
```

## 4. Requirements

### RF-001: `async` coerente (`S7503` ×4)
- **Description:** se a função não faz `await`, remover `async` (e ajustar callers — se caller espera coroutine, preferir manter `async` e usar `await` real, ex.: `await asyncio.sleep(0)` só se semanticamente correto; melhor: tornar sync ou consumir o await de verdade).

### RF-002: Complexidade ≤15 (`S3776`)
- **Description:** extrair blocos da função em `loadtest.py` L244 para helpers nomeados — saída/relatório idênticos.

### RF-003: Regex linear (`S8786`)
- **Description:** reescrever a regex de `check-spec-status.py` L27 eliminando backtracking super-linear (âncoras, possessive/atomic equivalente em Python via grupos não ambíguos, ou parser simples) — mesmas strings aceitas; cobrir com casos atuais.

## 5. API Contract

CLI/stdout preservados: `loadtest.py` reporta as mesmas métricas; `check-spec-status.py` mantém exit codes e mensagens (CI/PR checklist consome).

## 6. Acceptance Criteria

- [ ] SonarCloud não reporta as 6 issues após próximo scan.
- [ ] `python3 tools/check-spec-status.py` produz o mesmo veredito do estado atual do repo.
- [ ] `mock_ollama.py` atende aos gatilhos documentados; `loadtest.py --scenario rps --duration 10 --workers 2` roda contra o mock sem regressão.

## 7. Task Plan

1. Ajustar as 4 funções async no mock (testar com `curl` nos endpoints).
2. Extrair helpers no loadtest; medir.
3. Reescrever regex do spec-status com testes de equivalência.
4. Checklist do board.

## 8. Effort Estimate

`~1-2h`.
