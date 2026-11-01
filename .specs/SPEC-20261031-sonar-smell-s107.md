# SPEC-20261031-sonar-smell-s107

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-smell-s107` |
| Type | `Refactor` |
| Stack | `.NET` (ASP.NET Core Minimal APIs) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-smell-s107-20261031` |
| Ticket | `GH#568` |
| Status | `Done` |
| Parent | sonarqube-autofix 2026-10-31 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** reduzir as assinaturas com >7 parâmetros sinalizadas por `csharpsquid:S107` (53 issues, MAJOR)
**So that** endpoints e métodos internos ficam coesos e o contrato público não muda.

**Problem context:**
`S107` = "Functions should not have too many parameters" — 53 issues em 31 arquivos. Concentração (top):

| Issues | Arquivo |
| --- | --- |
| 5 | `Api/Endpoints/TerminalPtyEndpoints.cs` |
| 5 | `Api/Endpoints/WorkspaceFileEndpoints.cs` |
| 4 | `Api/Endpoints/ChatRunEndpoints.cs` |
| 4 | `Api/Endpoints/LspEndpoints.cs` |
| 3 | `Api/Endpoints/ChatEndpoints.cs` |
| 3 | `Api/Completions/ChatPipeline.cs` |
| 2 | `AutomationHookEndpoints.cs`, `RpcEndpoints.cs`, `DelegateTaskBuiltinTool.cs`, `RepoSkillEndpoints.cs` |
| 1 | `ContextCompactionService.cs`, `GatewayEndpoints.cs`, demais ~19 arquivos |

Precedente (RF-A2): `ListCatalogAsync`/`ExpandCommandAsync` usaram records `[AsParameters]` (`ModelCatalogDeps`, `ExpandCommandDeps`) — Minimal APIs ligam grupos de serviços via `[AsParameters]`, o padrão já aceito.

## 2. Scope

**In scope:** as 53 issues `S107` em `src/**`:
- **Endpoints Minimal API:** agrupar parâmetros injetados em records `[AsParameters]` (deps) e query/route/body em DTOs — **sem mudar shape HTTP**.
- **Métodos internos:** introduzir parameter objects/context records ou repartir responsabilidade.

**Out of scope:**
- Mudança de rotas, query names, body shape ou comportamento HTTP (contrato intocado).
- Handlers que o Sonar não marcou.

## 3. Technical Context

**Where the change happens:** `src/OpenWebUI.Api/Endpoints/*` (~35 issues — lambdas de Minimal API com muitos serviços injetados), `ChatPipeline.cs`, `DelegateTaskBuiltinTool.cs`, `ContextCompactionService.cs`, `GatewayEndpoints.cs`.

**Files to read before implementing:**
- `Api/Endpoints/ModelEndpoints.cs` + `RepoSkillEndpoints.cs` — records `[AsParameters]` do RF-A2 (padrão)
- Cada arquivo da tabela acima nas linhas do board

**Files to create or modify:**
```text
src/OpenWebUI.Api/Endpoints/*.cs        # records [AsParameters] + DTOs
src/OpenWebUI.Api/Completions/ChatPipeline.cs
src/OpenWebUI.Infrastructure/ChatTools/Tools/DelegateTaskBuiltinTool.cs
src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs
tests/** — contract/behavior tests onde ainda não houver
```

## 4. Requirements

### RF-001: `[AsParameters]` nos endpoints sinalizados
- **Description:** lambdas de endpoint com >7 parâmetros agrupam os serviços em record `XxxDeps` consumido via `[AsParameters]` — parâmetros de rota/query/body permanecem nomeados (contrato).
- **Rules:** nomes de query/route/body **idênticos**; binding inalterado; ordem de validação preservada.
- **Input → Output:** `(Db db, IA a, IB b, IC c, ID d, IE e, IF f, IG g)` → `([AsParameters] XxxDeps deps)` com `deps.Db`, etc.

### RF-002: Parameter objects nos métodos internos
- **Description:** métodos internos com >7 parâmetros ganham record de contexto (`XxxContext`/`XxxArgs`) ou split de responsabilidade — sem alterar resultado.
- **Rules:** callers atualizados; se o método for público/DI-contract, o param object vira parte do contrato documentado (XML doc).

### RF-003: Sem mudança de contrato
- **Description:** rotas, verbos, query keys, body shape, status codes e payload idênticos — prova via `ApiSurfaceTests`/testes de endpoint existentes verdes.

**Business rules / invariants:** `dotnet test` verde; nenhum endpoint muda de assinatura HTTP.

## 5. API Contract

N/A — **o contrato existente não muda**: `[AsParameters]` preserva binding de route/query/body com os mesmos nomes.

## 6. Acceptance Criteria

- [ ] **Given** cada endpoint sinalizado, **when** refatorado, **then** assinatura HTTP idêntica (mesmos route/query/body names; testes de endpoint verdes).
- [ ] **Given** cada método interno, **when** reescrito, **then** ≤7 parâmetros via parameter object coeso.
- [ ] **Given** o próximo scan, **then** zero `S107` em `src/**`.
- [ ] **Given** a suite, **then** verde, cobertura mantida.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Serviço opcional no grupo | `ISvc? svc = null` | `[AsParameters]` aceita nullable property — verificar binding |
| CancellationToken no fim | `ct` | permanece parâmetro explícito (convenção Minimal API) |
| Método override/interface | param object quebraria `override` | alternativa: split ou overload — não quebrar contrato de interface |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** listar os 31 arquivos e as lambdas/métodos sinalizados; revisar padrão `[AsParameters]` do RF-A2.
- [ ] **T2 — Endpoints hot:** TerminalPty (5), WorkspaceFile (5), ChatRun (4), Lsp (4), Chat (3) — records `*Deps`.
- [ ] **T3 — Endpoints restantes + Pipeline/infra:** AutomationHook, Rpc, RepoSkill, ChatPipeline, DelegateTask, ContextCompaction, Gateway.
- [ ] **T4 — Validation:** `dotnet test` (especial `ApiSurfaceTests`, `*EndpointsTests`); rescan.
- [ ] **T5 — Done + PR.**

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **Refactor / .NET** | Suite + contract tests verdes; zero `S107` no rescan; diff sem mudança de binding HTTP. |

## 8. Organization Guardrails (mandatory)

- **Branches:** `fix/sonar-smell-s107-20261031`.
- **Contrato HTTP:** nomes de route/query/body imutáveis.
- **Scope:** só `S107` em `src/**`.

## 9. Definition of Done

- [ ] 53 issues `S107` resolvidas.
- [ ] Testes de endpoint/API surface verdes; rescan limpo.
- [ ] Records `*Deps`/` *Args` nomeados consistentemente.

**Next action after DoD is complete:** `Status = Done` + PR.

## Open Questions / Pending Ambiguity

- `[A DEFINIR]` Nome dos records deps por endpoint (seguir padrão `{Endpoint}Deps` do RF-A2 — confirmar em T1).


## 10. Implementation Log (2026-10-31)

- 53 issues `csharpsquid:S107` em `src/**` refatorados.
- Endpoints: records `[AsParameters]` (`*Deps`) — contrato HTTP e binding preservados (precedente `ModelCatalogDeps`).
- Métodos internos/estáticos: parameter objects (`AuditInfo`, `UsageEntry`, `InstinctCandidate`, `BudgetRequest`, `RunOptions`, `ToolLoopOptions`, `EnrichDeps`, `ArenaDeps`, `ForwardRequest`, `EnqueueOptions`, `ContributorDeps`, `DeliveryInfo`, `ResumeRequest`, `ChildWait`, `ExtractTarget`, `ResultInfo`, `ProviderSpec`).
- `ChatRunExecutor` (30 deps) → `ChatRunPipelineDeps`/`ChatRunToolsDeps`/`ChatRunLifecycleDeps` registradas scoped no DI; campos preservam nomes (zero churn no corpo).
- `ContextCompactionService` opcionais → `CompactionDeps` (registrado; defaults preservados).
- Query records (`ChatListQuery`, `UserListQuery`, `LocationQuery`) preservam os nomes do contrato de query string.
- Testes atualizados para as novas assinaturas (sem mudança de expectativa).
