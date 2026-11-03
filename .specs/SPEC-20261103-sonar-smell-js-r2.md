# SPEC-20261103-sonar-smell-js-r2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-smell-js-r2` |
| Type | `Refactor` |
| Stack | `JavaScript` (Blazor WASM wwwroot) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-smell-js-r2-20261103` |
| Ticket | `GH#654` |
| Status | `Approved` |
| Parent | sonarqube-autofix 2026-11-03 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** limpar as 18 issues JS abertas em `wwwroot/js` + `index.html`
**So that** o harness JS do boot/IDE fica livre de smells (exceções engolidas, APIs deprecated, regex complexa, complexidade).

**Problem context:** segunda rodada do sweep JS (`SPEC-20261031-sonar-smell-js` limpou o lote anterior). Todas em código do client WASM.

| Regra | Qtd | Arquivos:linhas |
| --- | --- | --- |
| `javascript:S2486` (empty catch) | 9 | `app.js` L124,350,372,406 · `codeblocks.js` L134,158,174 · `boot.js` L386 · `codeexec.js` L238 |
| `javascript:S1874` (deprecated API) | 5 | `app.js` L25 ×2 (`addListener`) · `codeblocks.js` L155 (`execCommand`) · `ide-editor.js` L154 ×2 (`caretRangeFromPoint`) |
| `javascript:S3776` (cognitive) | 1 | `ide-editor.js` L85 (complexidade 16) |
| `javascript:S5843` (regex) | 1 | `boot.js` L147 (complexidade 26) |
| `javascript:S6582` (optional chain) | 1 | `codeexec.js` L47 |
| `javascript:S7785` (top-level await) | 1 | `index.html` L60 |

## 2. Scope

**In scope:** as 18 issues nas 6 unidades acima.

**Out of scope:**
- Reescrita do boot pipeline (`boot.js` é caminho crítico de boot WASM — mudança mínima por regra).
- Migrações de API deprecated que quebrem compatibilidade com browsers-alvo do projeto (verificar baseline antes de trocar `caretRangeFromPoint`/`execCommand` — podem ser "deprecated mas único caminho" → justificar no PR e resolver via SonarCloud *Accepted* se for o caso).

## 3. Technical Context

**Where the change happens:** JS estático servido ao client — `app.js` (helpers gerais), `boot.js` (boot WASM `autostart=false`), `codeblocks.js` (toolbar de code blocks), `codeexec.js`, `ide-editor.js` (editor do `/ide`), `index.html`.

**Files to read before implementing:**
- cada arquivo nas linhas sinalizadas (+ contexto do catch/regex/função)
- `.claude/rules/` seções de client, se houver
- `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md`

**Files to create or modify:**
```text
src/OpenWebUI.Client/wwwroot/js/{app,boot,codeblocks,codeexec,ide-editor}.js
src/OpenWebUI.Client/wwwroot/index.html
```

## 4. Requirements

### RF-001: Empty catches (`S2486` ×9)
- **Description:** tratar (log `console.warn`/`debug` com contexto) ou remover o `catch` quando a intenção é propagar; se engolir é deliberado (ex.: storage indisponível em modo privado), comentário explicativo curto resolve a regra.

### RF-002: Deprecated APIs (`S1874` ×5)
- **Description:** `addListener` → `addEventListener('change', …)`; `execCommand` → manter só se for o único caminho de copy-fallback (justificar) senão `navigator.clipboard`; `caretRangeFromPoint` → `caretPositionFromPoint` com fallback documentado.

### RF-003: Complexidade (`S3776` ×1, `S5843` ×1)
- **Description:** `ide-editor.js` L85 → extrair helpers até ≤15; `boot.js` L147 regex → simplificar (named groups/passos separados) até ≤20 — sem alterar o conjunto de strings aceitas (cobrir com casos de teste/manual: é o gate do espelho `/framework-assets`).

### RF-004: Estilo (`S6582`, `S7785`)
- **Description:** optional chain em `codeexec.js` L47; top-level `await` no script de `index.html` L60 (confirmar `type="module"`/contexto antes).

## 5. API Contract

N/A — comportamento observable preservado (regex aceita o mesmo input; APIs trocadas mantêm feature parity).

## 6. Acceptance Criteria

- [ ] SonarCloud não reporta as 18 issues após próximo scan (ou as deprecated justificadas movidas a *Accepted* com nota no board).
- [ ] Boot WASM + `/ide` + code blocks funcionam (smoke via CI e2e/a11y).
- [ ] Nenhuma regressão no espelho `framework-assets` (regex do boot).

## 7. Task Plan

1. RF-001 arquivo a arquivo (9 catches — a maioria provável "deliberate swallow" → comentário).
2. RF-002 com verificação de baseline de browser por API.
3. RF-003/RF-004.
4. Smoke manual dos caminhos tocados + checklist do board.

## 8. Effort Estimate

`~2-3h`.
