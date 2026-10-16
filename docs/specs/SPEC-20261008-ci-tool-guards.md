# SPEC-20261008-ci-tool-guards

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `ci-tool-guards` |
| Type | `Feature` (CI/chore) |
| Stack | `GitHub Actions / Python` |
| Repository | `afonsoft/open-webui` |
| Branch | `ci/tool-guards` |
| Ticket | `GAP-automation-ci-guards` |
| Status | `Completed` |
| Priority | `medium` |
| ⚠ Atenção | Toca `.github/workflows/` — **hard rule: exige revisão humana explícita antes de executar** |

## 1. User Story

Como maintainer, quero que os guards de drift entregues pelo E13 (`tools/check-*.py`) rodem no CI, para que regressões de CSS/form-semantics/i18n-parity quebrem o build em vez de passarem silenciosamente.

## 2. Scope

**In scope:**

- Adicionar step(s) que executam `tools/check-css-classes.py`, `tools/check-form-a11y.py`, `tools/check-i18n-parity.py` no workflow apropriado (`ci-build-test.yml`, job `Blazor WASM Client Validation`, ou job dedicado leve).
- Garantir que falha em qualquer guard falha o job.
- Documentar os guards em `CLAUDE.md`/`tools/README` (seção "comandos" ou agent loop).

**Out of scope:**

- Novos guards além dos 3 existentes.
- Refatorar os scripts (só integração).

## 3. Technical Context

**AS-IS (evidência):**

- `tools/` contém exatamente: `check-css-classes.py`, `check-form-a11y.py`, `check-i18n-parity.py` (guardas exigidos como RF-003-style drift protection nos SPECs do E13, PRs #191/#193/#196).
- `grep -rn "tools/\|check-" .github/workflows/` → **0 referências** — nada os executa.
- Guards rodam localmente (foram usados como gate verde durante as slices).

**TO-BE:** qualquer PR que reintroduza classes CSS indefinidas, controles de formulário sem nome acessível, ou chaves i18n sem paridade falha no CI.

**Restrição:** hard rule do repo — "Não modificar `.github/workflows/` sem revisão humana". Este SPEC propõe a mudança; a execução precisa de aprovação explícita do usuário no gate/revisão.

## 4. Requirements

### RF-001: Executar os 3 guards no CI

Rodar `python3 tools/check-css-classes.py`, `check-form-a11y.py` e `check-i18n-parity.py` como steps do `ci-build-test.yml` (posição sugerida: dentro ou ao lado do job `Blazor WASM Client Validation`, após o build do client — os guards inspecionam `.razor`/`.json`/CSS fonte, não precisam de artefatos). Falha em qualquer um → step/job falha com output do guard no log.

### RF-002: Fail-fast informativo

Step com nome claro (ex.: `Drift guards (css-classes, form-a11y, i18n-parity)`) para que o log do GitHub mostre exatamente qual guard quebrou e por quê.

### RF-003: Documentação

Mencionar os guards e como rodá-los localmente em `CLAUDE.md` (seção Comandos) para que agentes os executem antes de abrir PRs que tocam `.razor`/i18n.

### RF-004: Revisão humana do diff de workflow

O diff em `.github/workflows/` deve ser minimal (somente os novos steps) e explicitamente aprovado pelo usuário — o PR deve chamar atenção para o arquivo de workflow alterado.

## 5. API Contract

N/A.

## 6. Critérios de Aceite

- [ ] Os 3 guards executam em todo PR/push para `main` e falham o job em drift.
- [ ] Prova positiva: introduzir temporariamente uma classe indefinida num commit de teste da branch → job falha (reverter antes do merge).
- [ ] `CLAUDE.md` documenta os 3 comandos.
- [ ] Diff de `.github/workflows/` revisado e aprovado por humano (regra explícita do repo).

## 7. Notas

- Guards são determinísticos e rápidos (segundos) — custo de CI negligível.
- Alternativa aceitável a novo job: steps adicionais no job existente de validação do client.

## Delivered

- **PR:** #206 · **Issue:** #202 — mergeado 2026-10-08.
- step `Drift guards (css-classes, form-a11y, i18n-parity)` ativo no job `Blazor WASM Client Validation`; 4 violações aria-label corrigidas; comandos documentados em CLAUDE.md.
