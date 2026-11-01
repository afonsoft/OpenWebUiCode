# SPEC-20261031-sonar-smell-s3358

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-smell-s3358` |
| Type | `Refactor` |
| Stack | `.NET` (C# + Razor) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-smell-s3358-20261031` |
| Ticket | `GH#566` |
| Status | `Completed` |
| Parent | sonarqube-autofix 2026-10-31 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** eliminar os 104 ternários aninhados sinalizados por `csharpsquid:S3358` (MAJOR)
**So that** expressões condicionais ficam legíveis e o board limpa a 2ª maior família de smells.

**Problem context:**
`S3358` = "Ternary operators should not be nested" — 104 issues em 50 arquivos C#/.razor. Concentração (top):

| Issues | Arquivo |
| --- | --- |
| 11 | `src/OpenWebUI.Client/Pages/WorkspaceItemEditor.razor` |
| 6 | `src/OpenWebUI.Client/Components/ChatView.razor` |
| 6 | `src/OpenWebUI.Api/Endpoints/LspEndpoints.cs` |
| 6 | `src/OpenWebUI.Client/Pages/Workspace.razor` |
| 5 | `src/OpenWebUI.Client/Pages/Admin.razor` |
| 4 | `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` |
| 3 | `ChatWorkspacePanel.razor`, `WebSearchService.cs`, `PermissionRuleset.cs`, `LspResponse.cs`, `SettingsModal.razor` |
| ≤2 | demais ~40 arquivos |

Fix idiomático: `if/else`, `switch` expression, pattern matching ou extração para método/função local nomeada.

## 2. Scope

**In scope:** as 104 issues `S3358` em C# (board lista arquivo:linha).

**Out of scope:** ternários aninhados em JavaScript (`javascript:S3358` ×4 — fica na SPEC `sonar-smell-js`); mudança de comportamento.

## 3. Technical Context

**Where the change happens:** 50 arquivos em `src/OpenWebUI.Client` (Pages/Components `.razor`), `src/OpenWebUI.Api` (Endpoints/Runs), `src/OpenWebUI.Infrastructure`.

**Files to read before implementing:**
- `CLAUDE.md` · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` (grupos S3358)
- Top files da tabela acima

**Files to create or modify:**
```text
src/** — 50 arquivos conforme board
tests/** — apenas se a preservação de comportamento exigir teste novo
```

## 4. Requirements

### RF-001: Zero ternário aninhado
- **Description:** cada ocorrência reescrita como `if/else`, `switch` expression, ou método nomeado — precedência e avaliação lazy preservadas (ternário avalia só um braço; `if` equivalente obrigatório, nunca eager).
- **Input → Output:** `a ? x : b ? y : z` → `switch`/if-else/método com mesmo resultado.

### RF-002: Preservação exata de semântica
- **Rules:** short-circuit, null-propagation e side effects na mesma ordem; expressões em markup Razor (atributos/classes) podem virar função local/`@code` helper sem mudar o HTML emitido — validar classes Tailwind não alteradas (senão regenerar CSS).

### RF-003: Fatiamento revisável
- **Description:** PRs por grupo de arquivos relacionados (ex.: Pages, Components, Endpoints, Services) — máx ~30 issues por PR.

**Business rules / invariants:** `dotnet test` verde por fatia; nenhuma API pública muda.

## 5. API Contract

N/A.

## 6. Acceptance Criteria

- [x] **Given** cada ocorrência, **when** reescrita, **then** produz resultado idêntico para todos os caminhos (incl. null/short-circuit).
- [x] **Given** markup `.razor`, **when** ternário vira helper, **then** HTML/classes emitidos são idênticos (`check-css-classes.py` OK; tailwind.css regenerado apenas se classe mudar).
- [x] **Given** o próximo scan, **when** publicado, **then** zero `S3358` C# aberto.
- [x] **Given** a suite, **when** roda, **then** verde e cobertura não decresce.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Ternário em atributo Razor | `class="@(a ? "x" : b ? "y" : "z")"` | helper `@code` ou `if` no code-block — output idêntico |
| Ternário com side effects | `c ? Log(x) : d ? Log(y) : z` | `if/else` preservando lazy evaluation |
| Overlap S3776 | ternário dentro de método complexo | desaninhar ajuda a complexidade — referenciar SPEC irmã, sem bloquear |

## 7. Task Plan (agent execution)

- [x] **T1 — Discovery:** grupos `S3358` do board; mapear ocorrências por arquivo.
- [x] **T2 — Slice Pages:** WorkspaceItemEditor (11), Workspace (6), Admin (5), Ide demais.
- [x] **T3 — Slice Components:** ChatView (6), ChatWorkspacePanel (3), SettingsModal (3), demais.
- [x] **T4 — Slice Api/Infra:** LspEndpoints (6), ChatRunExecutor (4), WebSearchService/PermissionRuleset/LspResponse (9), cauda.
- [x] **T5 — Validation:** `dotnet test` por slice; `check-css-classes.py` se `.razor` tocado.
- [x] **T6 — Done + PRs.**

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **Refactor / .NET** | Suite verde; cobertura mantida; zero `S3358` no rescan; diffs estruturais. |

## 8. Organization Guardrails (mandatory)

- **Branches:** `fix/sonar-smell-s3358-20261031`; nunca `main`.
- **CSS:** `.razor` tocado → verificar `check-css-classes.py`; regenerar `tailwind.css` somente se classe mudar (artefato gerado, nunca editado à mão).
- **Scope:** só `S3358` C# — JS na SPEC irmã.

## 9. Definition of Done

- [x] 104 issues `S3358` resolvidas.
- [x] Testes verdes; drift guards (`check-css-classes.py`) OK.
- [x] Board atualizado; rescan limpo.

**Next action after DoD is complete:** `Status = Done` + PRs por slice.

## Open Questions / Pending Ambiguity

- `[A DEFINIR]` granularidade de PR (por slice de camada — recomendado).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/585 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/566
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/560
