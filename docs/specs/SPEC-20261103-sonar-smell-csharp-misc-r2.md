# SPEC-20261103-sonar-smell-csharp-misc-r2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-smell-csharp-misc-r2` |
| Type | `Refactor` |
| Stack | `.NET` (C#/Blazor) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-smell-csharp-misc-r2-20261103` |
| Ticket | `GH#657` |
| Status | `Done` |
| Parent | sonarqube-autofix 2026-11-03 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** varrer a cauda longa de code smells C# (28 issues, 19 regras, 20 arquivos)
**So that** o board zera as famílias pequenas sem SPECs individuais por regra.

**Problem context:** segunda rodada do `sonar-smell-csharp-misc`. `ContextCompactionService.cs` (11 issues: `S1135`×3, `S107`×3, `S3776`×2, `S3267`, `S1172`, `S2325`) fica **de fora** — coberto pelos SPECs em voo `SPEC-20261102-context-compactor-v2`/`SPEC-20261103-compactor-v3-delta`; re-verificar no próximo scan após merge deles.

| Regra | Qtd | Arquivo:linha |
| --- | --- | --- |
| `S8969` null-forgiving desnecessário | 3 | `WorkspaceFileEndpoints.cs` L421 · `ProviderCapabilityService.cs` L354 · `ProviderService.cs` L2618 |
| `S2589` condição sempre falsa/null check desnecessário | 3 | `ChatPipeline.cs` L1162 · `LspEndpoints.cs` L462 · `RepoBindingPanel.razor` L233 |
| `S2292` auto-property | 3 | `ModelWarmer.cs` L180,183 · `CommandHookRunner.cs` L35 |
| `S2325` método/prop static | 2 | `BrowserCloseBuiltinTool.cs` L36 · `BrowserObserveBuiltinTool.cs` L39 (`SafeInParallel`) |
| `S6580` format provider em parse | 2 | `Admin.razor` L2030,2031 |
| `S5034` `ValueTask` consumido >1 vez | 2 | `ChatRunEndpoints.cs` L545,550 |
| `S107` >7 parâmetros | 1 | `UsageEndpoints.cs` L118 |
| `S3218` shadowing de membro | 1 | `GatewayInboundService.cs` L329 |
| `S1172` parâmetro não usado | 1 | `PlanEndpoints.cs` L30 (`plans`) |
| `S1192` literal duplicada | 1 | `DelegateTaskBuiltinTool.cs` L162 (`'fresh'` ×5) |
| `S3358` ternário aninhado | 1 | `Admin.razor` L2081 |
| `S108` bloco vazio | 1 | `CommandHookRunner.cs` L120 |
| `S1118` ctor/static | 1 | `PlanFileService.cs` L18 |
| `S1481` variável não usada | 1 | `PlanFileService.cs` L125 (`root`) |
| `S4136` overloads adjacentes | 1 | `ToolExecutor.cs` L149 |
| `S6672` logger do tipo errado | 1 | `WorkspaceFileEndpoints.cs` L703 |
| `S3267` LINQ simplificável | 1 | `ChatWorkspacePanel.razor` L401 |
| `S1854` assignment inútil | 1 | `Ide.razor` L254 (`_`) |
| `S1125` literal boolean desnecessário | 1 | `DialogHost.razor` L95 |

## 2. Scope

**In scope:** as 28 issues acima — fixes mecânicos, um por regra×arquivo, commits agrupáveis por arquivo.

**Out of scope:**
- `ContextCompactionService.cs` (ver §1).
- Mudança de comportamento: `S3218` (renomear property que faz shadowing) e `S107` (assinar ≤7 params) podem tocar contrato interno — manter compat: para S107 preferir agrupar params em record/DTO interno; para S3218 renomear mantendo serialização/wire igual.

## 3. Technical Context

**Where the change happens:** 20 arquivos em `src/OpenWebUI.Api` (Endpoints, Completions), `src/OpenWebUI.Infrastructure` (Services, ChatTools, Gateway) e `src/OpenWebUI.Client` (razor).

**Files to read before implementing:** cada arquivo nas linhas sinalizadas + `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md`.

**Files to create or modify:**
```text
src/OpenWebUI.Api/Endpoints/{UsageEndpoints,LspEndpoints,PlanEndpoints,ChatRunEndpoints,WorkspaceFileEndpoints}.cs
src/OpenWebUI.Api/Completions/ChatPipeline.cs
src/OpenWebUI.Infrastructure/{Services/{ModelWarmer,ProviderService,ProviderCapabilityService,ToolExecutor},Services/Gateway/GatewayInboundService,ChatTools/{CommandHookRunner,PlanFileService},ChatTools/Tools/{BrowserCloseBuiltinTool,BrowserObserveBuiltinTool,DelegateTaskBuiltinTool}}.cs
src/OpenWebUI.Client/{Pages/{Admin,Ide}.razor,Components/{RepoBindingPanel,ChatWorkspacePanel,DialogHost}.razor}
```

## 4. Requirements

### RF-001: Um fix por issue, cirúrgico
- **Description:** cada regra resolve no formato idiomático — ex.: `S8969` remove `!` supérfluo; `S2292` vira `{ get; }`; `S2325` vira `static`; `S6580` usa `CultureInfo`/format provider; `S5034` materializa uma vez (`.AsTask()`/await único); `S3358` extrai ternário; `S1481`/`S1125`/`S1172` removem código morto.
- **Rules:** wire/serialização inalterada (especialmente `S3218`); razor muda→ regerar tailwind se classes tocadas (não deve ser o caso).

### RF-002: Cobertura mantida
- **Description:** gate `.ci/` não regride — linhas tocadas seguem cobertas pelos testes existentes; se um fix expõe branch não testada, adicionar teste.

## 5. API Contract

N/A — sem mudança de wire (`S3218`/`S107` preservam contrato externo).

## 6. Acceptance Criteria

- [ ] SonarCloud não reporta as 28 issues após próximo scan.
- [ ] `dotnet build -c Release` limpo; `dotnet test` verde.
- [ ] Drift guards verdes se algum `.razor` foi tocado.

## 7. Task Plan

1. Ordenar por arquivo; para cada um: ler contexto → fix → build incremental.
2. Commits agrupados por arquivo/regra.
3. `dotnet test` + drift guards + checklist do board.

## 8. Effort Estimate

`~3-4h` (28 fixes pequenos; os de razor exigem re-verificação visual leve).

## Delivered

14 issues corrigidas em código; 14 won't-fix/FP documentados:

**Fixes aplicados**

| Regra | Fix |
| --- | --- |
| `S2292` ×3 | `ModelWarmer.Tick`/`InitialDelay` e `CommandHookRunner.ShellOf` → auto-props (backing fields removidos) |
| `S1118` + `S1172` | `PlanFileService` era 100% estática → `static class`; `AddSingleton` e o param `plans` morto de `PlanEndpoints` removidos |
| `S1481` | `var root` não usada em `PlanFileService.List` removida |
| `S108` | catch `InvalidOperationException` vazio ganhou comentário (S108 aceita bloco comentado) |
| `S1192` | literal `"fresh"` ×5 → `private const string SubagentFresh` |
| `S6580` ×2 | `DateOnly.TryParse` com `CultureInfo.InvariantCulture` + `@using System.Globalization` |
| `S3358` | `CapBarClass` → switch expression |
| `S2589` | `RepoBindingPanel` — `_branches is null` era dead-code (Branches não-nulo no contrato) |
| `S4136` | overload de `ToolExecutor.ExecuteAsync` movido para adjacente |
| `S107` | `AdminUsageAsync` — 5 query params agrupados em `AdminUsageQuery` `[AsParameters]` (wire preservada: nomes dos params inalterados) |

**Won't-fix / FP / stale**

| Regra | Justificativa |
| --- | --- |
| `S8969` ×3 | `item!` e `from!`/`to!` são exigidos pelo compilador (CS8602/CS8604) — Sonar FP; o terceiro ponto (ProviderService L2618) não tem `!` no código atual (stale) |
| `S2589` ×2 | `LspEndpoints`: `from` é atribuído condicionalmente via `ref` — pode ser null, `??` necessário; `ChatPipeline` L1162: nenhuma condição sempre-verdadeira localizável (stale) |
| `S2325` ×2 | `SafeInParallel` implementa `IBuiltinChatTool` — membro de interface não pode ser `static` |
| `S5034` ×2 | `ChatRunEndpoints` já materializa com `.AsTask()` — consumo único garantido |
| `S6672` | `WorkspaceFileEndpoints` é `static class` — `ILogger<WorkspaceFileEndpoints>` é CS0718; `ILogger<Program>` é o padrão minimal-API |
| `S3218`, `S3267`, `S1125`, `S1854` | sem alvo localizável no código atual (drift de linha) ou idiomático (`_ =` fire-and-forget suprime CS4014) |

Ação manual: resolver won't-fix como *Accepted/FP* no SonarCloud (`SONAR_TOKEN`).
