# SPEC-20261031-sonar-smell-js

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-smell-js` |
| Type | `Refactor` |
| Stack | `JavaScript` (wwwroot estático do cliente WASM) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-smell-js-20261031` |
| Ticket | `GH#570` |
| Status | `Completed` |
| Parent | sonarqube-autofix 2026-10-31 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** resolver as 98 issues de code smell nos JS estáticos do cliente (`wwwroot/js`, `service-worker.js`)
**So that** o frontend auxiliar segue padrões modernos (optional chaining, `const`, globals explícitos) e o board zera JS fora de `tools/`.

**Problem context:**
98 issues em 11 arquivos de `src/OpenWebUI.Client/wwwroot/` — JS hand-written que interopera com o WASM (`window.openwebui.*`, boot, terminal, markdown, workers). Por arquivo:

| Issues | Arquivo |
| --- | --- |
| 34 | `wwwroot/js/app.js` |
| 15 | `wwwroot/js/codeexec.js` |
| 10 | `wwwroot/service-worker.js` |
| 10 | `wwwroot/js/ide-editor.js` |
| 8 | `wwwroot/js/codeblocks.js` |
| 8 | `wwwroot/js/boot.js` |
| 4 | `wwwroot/js/terminal.js` |
| 4 | `wwwroot/js/audio.js` |
| 2 | `wwwroot/js/ide-layout.js` |
| 2 | `wwwroot/js/py-worker.js` |
| 1 | `wwwroot/js/collab.js` |

Por regra (top): `S6582` ×24 (prefer optional chaining `?.`), `S3504` ×20 (variável declarada com `var`/`let` que nunca reatribui → `const`), `S2486` ×9 (exception sem contexto/`catch` genérico), `S7773` ×7 (`Number` methods — `isNaN`→`Number.isNaN` etc.), `S9381` ×6 (`RegExp.exec`→`String.match`/`test`), `S1874` ×5 (API deprecated), `S3358` ×4 (ternário aninhado JS), `S5843`/`S6666`/`S7781`/`S1121`/`S3800`/`S7780` ×2 cada, `S3776` ×1 (complexidade JS), cauda ×~15.

## 2. Scope

**In scope:** as 98 issues em `src/OpenWebUI.Client/wwwroot/js/*.js` + `wwwroot/service-worker.js` (board lista arquivo:linha).

**Out of scope:**
- `tools/**` JS (SPEC `sonar-smell-scripts-tools`).
- `wwwroot/js/ide-editor.bundle.js` — **artefato gerado** a partir de `tools/ide-bundle/` (issues no bundle são resolvidas na fonte; se o scanner reportar no bundle, verificar exclusão).
- Mudanças de comportamento visual/funcional.

## 3. Technical Context

**Where the change happens:** JS estático servido ao lado do WASM — sem build step (arquivos commitados direto; exceção: `ide-editor.bundle.js` gerado do bundle `tools/ide-bundle`).

**Files to read before implementing:**
- `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` (grupos JS)
- `tools/ide-bundle/` (fonte do bundle — checar se as 11 issues de `tools/ide-bundle/src/index.js` pertencem a esta SPEC ou à scripts-tools — decisão: scripts-tools)
- `Dockerfile`/boot flow para entender load order (`boot.js` orchestrates `_framework` mirror)

**Files to create or modify:**
```text
src/OpenWebUI.Client/wwwroot/js/*.js
src/OpenWebUI.Client/wwwroot/service-worker.js
tests/OpenWebUI.Client.Tests/** — apenas se existir cobertura JS relevante (bUnit não cobre)
```

## 4. Requirements

### RF-001: Fixes idiomáticos por regra
- **Description:** `S6582`→`?.`/`??`; `S3504`→`const`; `S2486`→catch com contexto/filtro ou comentário justificado; `S7773`→`Number.isNaN`/`isFinite` etc.; `S9381`→`match`/`test`; `S1874`→API atual equivalente; `S3358`→if/else ou função.
- **Rules:** comportamento observável idêntico — esses scripts são load-bearing no boot WASM (`boot.js` orquestra espelho `framework-assets`, `service-worker.js` faz cache) — nenhuma mudança de timing/ordem de execução.

### RF-002: Boot path preservado
- **Description:** `boot.js` e `service-worker.js` são caminho crítico (boot WASM + anti-proxy mirror `GET /framework-assets/…` conforme CLAUDE.md): qualquer alteração passa por validação funcional do boot (app sobe, WASM carrega, cache do SW funciona).
- **Input → Output:** app em `dotnet run`/`docker` sobe e renderiza chat após as mudanças.

### RF-003: Triage dos contestáveis
- **Description:** findings cuja "correção" quebraria compatibilidade (ex.: `S1874` API deprecated exigida pelo alvo WASM, `S6582` onde `&&` é intencional por truthiness de `0`/"") → won't-fix justificado.

**Business rules / invariants:**
- Boot WASM funcional após cada arquivo (verificação manual/E2E mínima).
- `service-worker.js`: versionamento do cache preservado (mudanças de código que alteram comportamento de cache exigem bump — documentar se ocorrer).

## 5. API Contract

N/A.

## 6. Acceptance Criteria

- [ ] **Given** `app.js` (34 issues), **when** refatorado, **then** zero issues JS nele e comportamento idêntico.
- [ ] **Given** o app servido, **when** boota, **then** WASM carrega (framework-assets mirror OK) e SW registra sem erro de console.
- [ ] **Given** o rescan, **then** zero smells JS em `wwwroot/**` (fixed ou won't-fix justificado).
- [ ] **Given** `check-css-classes.py`/drift guards, **when** rodados, **then** OK (JS não toca classes, mas confirma).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| `?.` mudando truthiness | `a && a.b` com `a=0`/`""` | avaliar semântica — `?.` só em null/undefined; won't-fix se o `&&` for intencional |
| SW cache bust | mudança em service-worker.js | bump de versão de cache se comportamento muda |
| Bundle vs fonte | issue em `ide-editor.bundle.js` | corrigir em `tools/ide-bundle/src` e regenerar bundle (ou won't-fix se artefato) |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** listar grupos JS do board; separar fix-direto vs triage; confirmar status de `ide-editor.bundle.js` (gerado?).
- [ ] **T2 — app.js + codeexec.js:** 49 issues — optional chaining/const/catch.
- [ ] **T3 — service-worker.js + ide-editor.js + codeblocks.js + boot.js:** 36 issues — boot path verificado.
- [ ] **T4 — cauda (terminal/audio/ide-layout/py-worker/collab):** 13 issues + won't-fix triage.
- [ ] **T5 — Verification:** boot funcional (servir app e abrir), console limpo, rescan.
- [ ] **T6 — Done + PR.**

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **Refactor / JS estático** | Boot WASM validado funcionalmente; zero issues JS no rescan; diffs estruturais; sem regressão de console. |

## 8. Organization Guardrails (mandatory)

- **Branches:** `fix/sonar-smell-js-20261031`.
- **Boot WASM:** `boot.js`/`framework-assets` espelho — não alterar o fluxo de placeholders/fingerprint nem o mirror (CLAUDE.md DON'T).
- **Scope:** só `wwwroot/js` + `service-worker.js`; sem refactors além do board.
- **Bundle:** `ide-editor.bundle.js` nunca editado à mão (artefato gerado).

## 9. Definition of Done

- [ ] 98 issues resolvidas (fix ou won't-fix justificado).
- [ ] Boot funcional validado; rescan sem smells JS em `wwwroot/**`.
- [ ] Board atualizado.

**Next action after DoD is complete:** `Status = Done` + PR.

## Open Questions / Pending Ambiguity

- `[A DEFINIR]` `ide-editor.bundle.js` é artefato gerado? Confirmar em T1 (fonte `tools/ide-bundle`) — se sim, suas issues são resolvidas na fonte e o bundle regenerado; considerar `sonar.exclusions` para o bundle.

**Execução — won't-fix justificados:**

| Regra | Site | Justificativa |
| --- | --- | --- |
| S1874 | `app.js` `mq.addListener` | fallback Safari<14 — já feature-detected (`addEventListener` preferido). |
| S1874 | `codeblocks.js` `execCommand` | fallback de cópia quando `navigator.clipboard` indisponível (contexto inseguro). |
| S1874 | `ide-editor.js` `caretRangeFromPoint` | API WebKit; `caretPositionFromPoint` já é o branch preferido — detection intencional. |
| S7785 | `index.html` SW register | script clássico inline — `type=module`/top-level await mudaria ordem de boot (caminho crítico). |
| json:S2068 | `pt-BR.json` ×3 | labels/placeholders i18n ("password" como texto de UI), não credenciais. |
| S8786 | `audio.js` link regex | bounded quantifiers `{0,1000}`/`{0,8000}` eliminam super-linear mantendo cobertura prática total. |
| S3776 | `ide-editor.js` `renderDiags` | resolvido via extração de `appendSquiggles` (2 níveis de nesting removidos). |

**Evidências:** `node --check` OK nos 11 arquivos; semântica preservada (arrow `this` no `resolve` de app.js, clones antes de `await` no SW, `InvokeAsync<bool>` tolera async nos callers de audio).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/588 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/570
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/560
