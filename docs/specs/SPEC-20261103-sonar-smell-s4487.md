# SPEC-20261103-sonar-smell-s4487

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-smell-s4487` |
| Type | `Refactor` |
| Stack | `.NET` (Blazor) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-smell-s4487-20261103` |
| Ticket | `GH#652` |
| Status | `Done` |
| Parent | sonarqube-autofix 2026-11-03 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** remover/consumir os 11 campos privados não lidos sinalizados por `csharpsquid:S4487` (CRITICAL)
**So that** `Ide.razor` e `MainLayout.razor` param de carregar estado morto e a maior família CRITICAL do board zera.

**Problem context:** `S4487` = "unread private fields" — campo escrito mas nunca lido (estado morto ou resto de refactor). 11 issues: `Ide.razor` ×10 (`_jobs`, `_checkpoints`, `_explorer`, `_problems`, `_errCount`, `_warnCount`, `_qoFiles`, `_qoCommands`, `_qoSymbols`, `_gitStatus`) e `MainLayout.razor` ×1 (`_settingsOpen`). Padrão provável: componentes declaravam estado que hoje vem de services/parâmetros.

| Issues | Arquivo:linhas |
| --- | --- |
| 10 | `src/OpenWebUI.Client/Pages/Ide.razor` — L176,177,181,198,199,200,205,206,207,209 |
| 1 | `src/OpenWebUI.Client/Layout/MainLayout.razor` — L42 |

## 2. Scope

**In scope:** os 11 campos — para cada um: remover (se morto) ou consumir corretamente (se a intenção era usá-lo e o uso foi esquecido — decidir por evidência de uso no markup/métodos).

**Out of scope:**
- Refator do Ide.razor como um todo; mudança de comportamento visual/UX.
- Outras regras no mesmo arquivo (`S1854` L254 vai em `sonar-smell-csharp-misc-r2`).

## 3. Technical Context

**Where the change happens:** `Ide.razor` (página IDE full-screen) e `MainLayout.razor` (shell).

**Files to read before implementing:**
- `Ide.razor` + `Ide.razor.cs`/partials se existirem — verificar se os campos são escritos em handlers e usados (ou não) em markup
- `MainLayout.razor` — `_settingsOpen` (provável resto de settings drawer)
- `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md`

**Files to create or modify:**
```text
src/OpenWebUI.Client/Pages/Ide.razor
src/OpenWebUI.Client/Layout/MainLayout.razor
```

## 4. Requirements

### RF-001: Veredito por campo — remove ou consume
- **Description:** se o campo é escrito mas nunca lido em markup/lógica → deletar campo + assignments mortos; se o leitor esperado existe mas usa outra fonte (ex.: property vs field) → corrigir a referência; se a leitura pretendida falta por bug → restaurar uso (documentar no PR).
- **Rules:** nenhum campo privado escrito-e-nunca-lido resta; sem mudança funcional visível (se mudar, foi decisão consciente documentada).

### RF-002: Zero regressão de UI
- **Description:** `Ide.razor` continua renderizando workspace/editor/jobs/explorer como antes — os campos removidos não podem ser fonte de dados em uso.

## 5. API Contract

N/A.

## 6. Acceptance Criteria

- [ ] SonarCloud não reporta `S4487` nos dois arquivos após próximo scan.
- [ ] `dotnet build -c Release` limpo (zero warnings novos); `dotnet test` verde.
- [ ] Drift guards (`check-css-classes.py`, `check-form-a11y.py`) verdes.
- [ ] Smoke manual/CI do `/ide` sem diferença visual.

## 7. Task Plan

1. `grep` de cada campo nos dois arquivos — classificar morto vs. mal-referenciado.
2. Remover campos mortos + assignments; corrigir referências quebradas.
3. Build + testes + drift guards.
4. Checklist do board.

## 8. Effort Estimate

`~1-2h`.

## Delivered

PR (este branch): 1 fix real + 10 FPs documentados — veredito por campo:

| Campo | Veredito |
| --- | --- |
| `_explorer` (`Ide.razor` L181) | **Removido** — `@ref` nunca dereferenciado (write-only real) |
| `_jobs`, `_checkpoints`, `_problems`, `_errCount`, `_warnCount`, `_qoFiles`, `_qoCommands`, `_qoSymbols`, `_gitStatus` (`Ide.razor`) | **FP** — lidos em bindings de markup (`Jobs="_jobs"`, `Checkpoints=`, `GitStatus=`, `Errors=`, `Warnings=`, `Files=`, `Commands=`, `Symbols=`); o analyzer S4487 não conta leitura via markup `.razor` |
| `_settingsOpen` (`MainLayout.razor`) | **FP** — lido em `IsOpen="_settingsOpen"` (L35) |

Ação manual pendente: marcar as 10 FPs como *False positive* no
SonarCloud (`SONAR_TOKEN`).
