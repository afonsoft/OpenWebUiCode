# SPEC-20261031-sonar-smell-s3776

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-smell-s3776` |
| Type | `Refactor` |
| Stack | `.NET` (C# + Razor) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-smell-s3776-20261031` |
| Ticket | `GH#565` |
| Status | `Approved` |
| Parent | sonarqube-autofix 2026-10-31 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** reduzir a complexidade cognitiva dos 106 métodos sinalizados pela regra `csharpsquid:S3776` (CRITICAL)
**So that** o código fica legível, testável e o board do SonarCloud zera a maior família de code smells do repo.

**Problem context:**
`S3776` = "Cognitive Complexity of functions should not be too high" — 106 issues em 65 arquivos. Concentração (top):

| Issues | Arquivo |
| --- | --- |
| 8 | `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` |
| 7 | `src/OpenWebUI.Client/Components/ChatView.razor` |
| 5 | `src/OpenWebUI.Api/Endpoints/LspEndpoints.cs` |
| 4 | `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` |
| 3 | `FileBuiltinTools.cs`, `WorkspaceFiles.cs`, `ModelFilterService.cs`, `WebSearchService.cs` |
| 2 | `AutomationHookEndpoints.cs`, `DelegateTaskBuiltinTool.cs`, `TeamBuiltinTools.cs`, `AgentDefinitionService.cs` |
| ≤2 | outros ~50 arquivos (lista completa no ToDo Board) |

Precedente no repo: o sweep de 19/out já resolveu `S3776` em `SettingsModal`, `ChatView`, `ChatRunDispatcher`, `RunRecoveryPlanner`, `CommandExpansion` via extração de métodos por branch (`SONAR_FIX_TODO_BOARD.md` RF-A4…A9) — mesmo padrão se aplica.

## 2. Scope

**In scope:** as 106 issues `csharpsquid:S3776` abertas em `src/**` — extração de métodos auxiliares, early returns, switch expressions e separação de branches até a complexidade passar no gate do SonarCloud.

**Out of scope:**
- Mudanças de comportamento/API (refactor puro, safe-refactor).
- Outras regras (S3358, S1192, S107, misc) — SPECs irmãs.
- Otimizações de performance.

## 3. Technical Context

**Where the change happens:** métodos sinalizados em `src/OpenWebUI.Api`, `src/OpenWebUI.Infrastructure`, `src/OpenWebUI.Client` — cada issue no board lista arquivo:linha.

**Files to read before implementing:**
- `CLAUDE.md` · `.claude/rules/`
- `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` (grupos S3776 com issue IDs/linhas)
- Os 8 arquivos top da tabela acima primeiro (maior densidade)

**Files to create or modify:**
```text
src/** — 65 arquivos (conforme board; nenhum arquivo novo esperado)
tests/OpenWebUI.Api.Tests/*.cs — testes de preservação de comportamento onde ausentes
```

## 4. Requirements

### RF-001: Complexidade cognitiva dentro do limite
- **Description:** cada método sinalizado é refatorado até não ser mais reportado por `S3776` — técnicas: extrair métodos privados por branch responsável, early-return/guard clauses, switch expressions, tabela de decisão, separar parsing de efeitos.
- **Rules:** comportamento observável idêntico (mesma saída, mesmos efeitos, mesma ordem); nenhuma assinatura pública alterada sem necessidade.
- **Input → Output:** método com complexidade >15 (ou limiar do Sonar) → conjunto de métodos cada um dentro do limiar.

### RF-002: Ordem de ataque por densidade
- **Description:** executar primeiro os 12 arquivos top (≈50% das issues), depois a cauda — manter commits/PRs fatiados por arquivo ou grupo pequeno (3-5 arquivos por PR máx) para revisão viável.

### RF-003: Sem mudança comportamental
- **Description:** refactor puro — qualquer alteração que mude comportamento exigível por teste existente é revertida ou promovida a Bugfix próprio.

**Business rules / invariants:**
- `dotnet test` permanece verde a cada fatia; cobertura não decresce.
- `.razor`: regenerar `wwwroot/css/tailwind.css` apenas se classes mudarem (improvável aqui, mas obrigatório se ocorrer).

## 5. API Contract

N/A.

## 6. Acceptance Criteria

- [ ] **Given** os 12 arquivos top, **when** refatorados, **then** nenhum método neles é reportado por `S3776` no próximo scan.
- [ ] **Given** a suite NUnit, **when** roda após cada fatia, **then** 100% verde (preservação provada por teste).
- [ ] **Given** o board, **when** todas as fatias mergem, **then** zero issues `S3776` em `src/**`.
- [ ] **Given** cada PR fatiado, **when** revisado, **then** diff puramente estrutural (sem lógica nova).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Método "irredutível" | lógica intrinsecamente complexa | extrair sub-métodos nomeados ou justificar supressão localizada documentada |
| Razor handlers | `@code` com muitos branches | extrair para `*.razor.cs`/métodos privados — sem alterar markup/classes |
| Ternários aninhados dentro | overlap com S3358 | resolver ambos no mesmo método quando aplicável (referenciar SPEC irmã) |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** filtrar grupos `S3776` do board; mapear os métodos por arquivo (top 12 primeiro).
- [ ] **T2 — Slice 1:** ProviderService (8) + ChatView (7) — testes relevantes verdes.
- [ ] **T3 — Slice 2:** LspEndpoints (5) + ChatRunExecutor (4) + FileBuiltinTools/WorkspaceFiles/ModelFilterService/WebSearchService (12).
- [ ] **T4 — Slice 3:** cauda (~74 issues nos demais ~50 arquivos).
- [ ] **T5 — Validation por slice:** `dotnet test` + build; rescan local se `sonar-scanner` disponível.
- [ ] **T6 — Done + PRs:** DoD por fatia → `Status = Done` ao final + PRs.

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **Refactor / .NET** | Suite completa verde a cada fatia; cobertura não decresce; `S3776` ausente no próximo scan do SonarCloud; diffs estruturais. |

## 8. Organization Guardrails (mandatory)

- **Branches:** `fix/sonar-smell-s3776-20261031` (+ sub-branches por slice se preferido, sempre fora de `main`).
- **Workflows:** `.github/workflows/` intocado.
- **Scope:** somente `S3776` — smells de outras regras no mesmo arquivo só se bloquearem a refatoração (e anotar).
- **Architecture:** respeitar camadas (Api→Infra→Application→Domain); extrações ficam na mesma camada.

## 9. Definition of Done

- [ ] As 106 issues `S3776` resolvidas (fixed ou supressão documentada).
- [ ] Suite NUnit verde em todas as fatias; cobertura ≥ baseline.
- [ ] Board atualizado (checkboxes) e rescan sem `S3776`.

**Next action after DoD is complete:** `Status = Done` + PRs por fatia na branch `fix/sonar-smell-s3776-20261031`.

## Open Questions / Pending Ambiguity

- `[A DEFINIR]` Fatiamento: um PR por fatia (3 PRs) vs PR único grande — recomendação: por fatia.
