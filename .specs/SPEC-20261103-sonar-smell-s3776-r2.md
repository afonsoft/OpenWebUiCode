# SPEC-20261103-sonar-smell-s3776-r2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-smell-s3776-r2` |
| Type | `Refactor` |
| Stack | `.NET` (C#) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-smell-s3776-r2-20261103` |
| Ticket | `GH#653` |
| Status | `Approved` |
| Parent | sonarqube-autofix 2026-11-03 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** reduzir a complexidade cognitiva dos 3 métodos C# sinalizados por `csharpsquid:S3776` (CRITICAL)
**So that** os hotspots residuais ficam dentro do limite (≤15) sem mudar comportamento.

**Problem context:** segunda rodada — o sweep de out/31 (`SPEC-20261031-sonar-smell-s3776`) limpou o lote da época; estes 3 são de código novo/alterado desde então. As issues de `ContextCompactionService.cs` (×2) ficam de fora: arquivo coberto pelos SPECs em voo `SPEC-20261102-context-compactor-v2`/`SPEC-20261103-compactor-v3-delta`. JS/Python counterparts vão nos SPECs `sonar-smell-js-r2`/`sonar-smell-python`.

| Método (linha) | Arquivo | Complexidade atual |
| --- | --- | --- |
| ~L96 | `src/OpenWebUI.Api/Completions/ChatPipeline.cs` | 16 |
| ~L218 | `src/OpenWebUI.Infrastructure/ChatTools/Tools/DelegateTaskBuiltinTool.cs` | 20 |
| ~L1988 | `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` | 17 |

## 2. Scope

**In scope:** os 3 métodos — extração de helpers/early-returns até complexidade ≤15.

**Out of scope:**
- `ContextCompactionService.cs` (deferred — compactor SPECs).
- Mudança de comportamento/contrato; refactors além do necessário ao limite.

## 3. Technical Context

**Where the change happens:** pipeline de completions (API), builtin tool de delegação e provider service (Infrastructure).

**Files to read before implementing:**
- os 3 métodos completos + testes que os cobrem
- `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md`
- precedente: `docs/specs/SPEC-20261031-sonar-smell-s3776.md` (técnica usada na 1ª rodada)

**Files to create or modify:**
```text
src/OpenWebUI.Api/Completions/ChatPipeline.cs
src/OpenWebUI.Infrastructure/ChatTools/Tools/DelegateTaskBuiltinTool.cs
src/OpenWebUI.Infrastructure/Services/ProviderService.cs
tests/OpenWebUI.Api.Tests/** (se faltar cobertura nos helpers extraídos)
```

## 4. Requirements

### RF-001: Complexidade ≤15 por método
- **Description:** aplicar extract-method para blocos coesos (validação, ramos de erro, loops internos) e/ou inverter guards para early-return — medido pela mesma regra.
- **Rules:** assinatura pública e semântica preservadas; helpers privados nomeados por intenção.

### RF-002: Comportamento idêntico
- **Description:** testes existentes seguem verdes sem ajuste de expectativa; se um método não tem cobertura dos ramos extraídos, adicionar teste antes do refactor (TDD).

## 5. API Contract

N/A — refactor interno.

## 6. Acceptance Criteria

- [ ] SonarCloud não reporta `S3776` nos 3 métodos após próximo scan.
- [ ] `dotnet test` verde; nenhum teste existente alterado para "fazer passar".
- [ ] Cobertura das linhas movidas mantida (gate `.ci/` não regride).

## 7. Task Plan

1. Para cada método: mapa dos ramos → escolher pontos de extração → (teste se faltar) → extrair → re-medir.
2. Build + testes por arquivo.
3. Checklist do board.

## 8. Effort Estimate

`~2-3h` (3 métodos, todos perto do limite — extrações pequenas).
