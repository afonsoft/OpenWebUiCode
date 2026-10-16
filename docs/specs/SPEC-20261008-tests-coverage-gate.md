# SPEC-20261008-tests-coverage-gate

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `tests-coverage-gate` |
| Type | `Bugfix` |
| Stack | `.NET / NUnit / GitHub Actions` |
| Repository | `afonsoft/open-webui` |
| Branch | `fix/coverage-gate` |
| Ticket | `GAP-tests-coverage-gate` |
| Status | `Completed` |
| Priority | `high` — main vermelha, bloqueia merges futuros |

## 1. User Story

Como maintainer, quero o job `Tests + Coverage Gate (NUnit)` verde na `main` para que merges futuros não fiquem bloqueados por um gate falhando há dias.

## 2. Scope

**In scope:**

- Investigar a queda de cobertura (line 88.59% vs baseline 90.0%, branch 70.96% vs 73.0%) e identificar os pacotes/classes que regrediram.
- Adicionar testes NUnit para restaurar cobertura ≥ baseline, **ou** recalibrar `.ci/coverage-baseline.txt` e `.ci/coverage-branch-baseline.txt` somente com justificativa documentada.
- Verificar por que o job `Coverage Baseline Ratchet` aparece `skipped` e documentar a intenção (ratchet automático ou manual).

**Out of scope:**

- Aumento de cobertura além do baseline (epic de qualidade separado).
- Mudanças estruturais no workflow além do necessário — qualquer edição em `.github/workflows/` exige revisão humana explícita (hard rule do repo).

## 3. Technical Context

**AS-IS (evidência):**

- Run `37806944372` em `main@1fb8645b7`: `##[error]Line coverage 88.59% < baseline 90.0%` e `##[error]Branch coverage 70.96% < baseline 73.0%`, step `Coverage Gate + Summary`.
- Mesmo step falha em `389152867` (2026-10-08T12:49Z, **antes** dos merges do E13) e `b631328d1` (2026-10-07) → regressão anterior ao E13.
- Baselines commitados: `.ci/coverage-baseline.txt` = `90.0`, `.ci/coverage-branch-baseline.txt` = `73.00`.
- Jobs `Tailwind CSS em dia`, `Build`, `Blazor WASM Client Validation`, `Docker Image Build` passam — só o gate de cobertura falha.

**TO-BE:** cobertura reportada ≥ baselines e `🚀 CI Build & Test` verde na `main`.

## 4. Requirements

### RF-001: Diagnóstico da regressão

Baixar o artifact Cobertura do run falho (ou reproduzir localmente com `--collect:"XPlat Code Coverage"` + ReportGenerator) e listar os `package`s cujo `line-rate`/`branch-rate` mais contribuíram para a queda. Registrar a lista no relatório de entrega.

### RF-002: Restaurar cobertura ≥ baseline

Adicionar testes em `tests/OpenWebUI.Api.Tests/` cobrindo as linhas/branches identificados no RF-001 até que line ≥ 90.0% e branch ≥ 73.0% — preferência por testes de comportamento real (não testes-triviais para inflar métrica). Se a queda se provar estrutural (ex.: código morto, classes geradas), justificar remoção/exclusão no lugar de testes artificiais.

### RF-003: Ou, alternativa — recalibrar baseline

Somente se RF-002 mostrar que o baseline atual está inatingível sem testes de baixo valor: ajustar `.ci/coverage-*.txt` para o piso real medido + justificativa em `docs/` ou no corpo do PR. Nunca abaixar baseline silenciosamente.

### RF-004: Clarificar `Coverage Baseline Ratchet`

Documentar por que o job aparece `skipped` (condição `if:` esperada vs. bug) e, se a intenção for ratchet automático, corrigir — **sujeito à regra de revisão humana para workflows**.

### RF-005: Validar no CI

Abrir PR, confirmar `Tests + Coverage Gate (NUnit)` verde e o run completo `🚀 CI Build & Test` bem-sucedido antes do merge.

## 5. API Contract

N/A — trabalho de testes/CI.

## 6. Critérios de Aceite

- [ ] Relatório de diagnóstico (pacotes responsáveis pela queda) anexado ao PR.
- [ ] `dotnet test` local reporta cobertura ≥ baselines (ou baselines recalibrados com justificativa).
- [ ] `🚀 CI Build & Test` verde na branch do PR e na `main` após merge.
- [ ] `Coverage Baseline Ratchet` documentado (comportamento esperado declarado).
- [ ] Nenhum teste-trivial adicionado apenas para inflar métrica (revisão por `code-review-and-quality`).

## 7. Notas

- Falha pré-datou o E13 — não culpar os merges recentes; a regressão existe desde ≥2026-10-07 (`b631328d1`).
- Se a causa for uma mudança de instrumentação/versão do coverlet (não perda real de cobertura), RF-003 é o caminho honesto.

## Delivered

- **PR:** #207 · **Issue:** #201 — mergeado 2026-10-08.
- cobertura 88.59/70.96 → 90.60/76.34 (+102 testes); baseline ratchet subiu automaticamente no push (`b46416c68`).
