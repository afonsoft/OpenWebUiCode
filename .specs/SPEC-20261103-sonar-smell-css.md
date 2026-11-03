# SPEC-20261103-sonar-smell-css

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-smell-css` |
| Type | `Refactor` |
| Stack | `CSS` (Tailwind input) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-smell-css-20261103` |
| Ticket | `GH#TBD` |
| Status | `Approved` |
| Parent | sonarqube-autofix 2026-11-03 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** limpar as 6 issues de CSS em `tailwind.input.css` (`css:S9423` ×5, `css:S1874` ×1)
**So that** o stylesheet-fonte fica livre de `!important` desnecessários e keyword deprecated — sem diferença visual.

**Problem context:**

| Regra | Qtd | Linhas |
| --- | --- | --- |
| `css:S9423` — "Remove this `!important`" | 5 | `tailwind.input.css` L214–218 |
| `css:S1874` — deprecated `word-break: break-word` | 1 | `tailwind.input.css` L329 |

**⚠️ Atenção:** `tailwind.input.css` é a **fonte**; `wwwroot/css/tailwind.css` é artefato gerado (hard rule: nunca editar o gerado — regerar via CLI e commitar junto; CI `tailwind-freshness` verifica o diff).

## 2. Scope

**In scope:** as 6 linhas sinalizadas no input — remover `!important` resolvendo especificidade e trocar `break-word` pelo valor moderno equivalente.

**Out of scope:**
- Mudança visual intencional — se a remoção de `!important` alterar render, subir especificidade do seletor em vez de aceitar a regressão.
- Refator maior do stylesheet; temas/tokens.

## 3. Technical Context

**Where the change happens:** `src/OpenWebUI.Client/tailwind.input.css` → regenera `src/OpenWebUI.Client/wwwroot/css/tailwind.css`.

**Files to read before implementing:**
- `tailwind.input.css` L210–230 e L325–335 (contexto dos seletores)
- `.claude/skills/tailwind-css/SKILL.md` (procedimento de regen)
- `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md`

**Files to create or modify:**
```text
src/OpenWebUI.Client/tailwind.input.css
src/OpenWebUI.Client/wwwroot/css/tailwind.css   (artefato regerado — commitar junto)
```

## 4. Requirements

### RF-001: `!important` só onde indispensável
- **Description:** para L214–218, avaliar o conflito de especificidade real; remover o `!important` e, se necessário, reforçar o seletor (classe adicional/`@layer`) para preservar o resultado.
- **Rules:** se o `!important` for deliberado e sem alternativa razoável, justificar no PR (a regra pode ir a *Accepted* no SonarCloud — mas tentar a remoção primeiro).

### RF-002: Keyword moderna
- **Description:** `word-break: break-word` → equivalente atual (`overflow-wrap: break-word`/`anywhere` conforme a intenção) preservando o wrap.

### RF-003: Artefato regenerado
- **Description:** rodar `tailwindcss -i src/OpenWebUI.Client/tailwind.input.css -o src/OpenWebUI.Client/wwwroot/css/tailwind.css --minify` e commitar o diff junto (`tailwind-freshness` é gate de CI).

## 5. API Contract

N/A.

## 6. Acceptance Criteria

- [ ] SonarCloud não reporta as 6 issues após próximo scan.
- [ ] `tailwind-freshness` (regen + `git diff --exit-code`) verde em CI.
- [ ] Smoke visual das áreas afetadas (regras L214–218/L329) sem diferença.

## 7. Task Plan

1. Ler os seletores envolvidos; identificar conflito que motivou cada `!important`.
2. Aplicar RF-001/RF-002; regerar artefato.
3. Build + drift guards (`check-css-classes.py`) + smoke visual.
4. Checklist do board.

## 8. Effort Estimate

`~1h`.
