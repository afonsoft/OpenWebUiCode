# SPEC-20261023-run-cost-budgets

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `run-cost-budgets` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261023-run-cost-budgets` |
| Ticket | [#494](https://github.com/afonsoft/open-webui/issues/494) |
| Status | `Draft` |
| Parent | Análise comparativa hermes-agent/OpenHarness/ECC (out/2026) — item #3. Inspirado no `cost-aware-llm-pipeline` do ECC (model routing + budget tracking) e na telemetria por run já existente (SPEC-20261016-agent-steps-usage). |

## 1. User Story

**As a** administrador da instância
**I want** definir tetos de custo/tokens por usuário e por run, com
enforcement fail-closed dentro do loop do agente
**So that** runs longas, subagents e automações não-atendidas (`routine`,
webhooks) não consumam orçamento ilimitado de provider — hoje o uso é
medido mas nunca limita nada.

**Problem context:**
SPEC-20261016-agent-steps-usage já persiste `run.MetaJson.usage`
(prompt/completion/reasoning tokens por step + custo USD quando o
provider informa preço — `ChatRunExecutor.ApplyUsageMetaAsync`).
Mas: (a) o uso fica preso no JSON da run, sem agregação consultável por
usuário/período; (b) nenhum ponto do pipeline veta uma run por custo —
`delegate_task` (depth≤3), `routine` agendada e webhooks executam sem
teto; (c) o `ModelRoleResolver` roteia por papel funcional, sem noção de
classe de preço. O ECC resolve isso com budget tracking + roteamento
cost-aware; o OpenHarness expõe cost tracking por evento no stream.

## 2. Scope

**In scope:**

- **A. Ledger de uso consultável** — nova tabela `RunUsageRecord`
  (migration EF): `Id`, `RunId`, `ChatId`, `UserId`, `ModelId`,
  `PromptTokens`, `CompletionTokens`, `ReasoningTokens`, `CostUsd`
  (nullable), `Steps`, `CreatedAt`, `ParentRunId` (agrega custo de
  subagents ao usuário que disparou a raiz). Escrita em
  `ApplyUsageMetaAsync` (mesmo evento, mesma regra: tokens só quando o
  provider reporta usage — nunca estimado).
- **B. Política de budget** — reuso de `AdminPolicyStatement`/
  `PolicyService` (SPEC-20261018-policies-admin) com `kind = "budget"`:
  sujeito = usuário/grupo/global; limites = `usdPerRun`, `usdPerMonth`,
  `tokensPerMonth`, `maxModelTier` (ver D). Sem política = sem teto
  (comportamento atual preservado).
- **C. Enforcement fail-closed** — novo `IRunInterceptor`
  (`BudgetInterceptor`, `Order` baixo):
  - `pre-step`: soma o ledger do usuário no mês + custo acumulado da run
    corrente (de `StepUsages`); estourou cap → `Rejected` com motivo
    `budget_exceeded` (a run encerra com erro explicável ao usuário, não
    silencia).
  - `tool.call`: delegação (`delegate_task`) herda o budget da cadeia —
    custo de filhos conta no usuário raiz via `ParentRunId`.
  - `pre-run` no `ChatRunDispatcher`: usuário já acima do cap mensal →
    run nem enfileira (retorna `429`-like com `error_code`).
- **D. Roteamento por tier de custo** — tabela de tiers de preço por
  modelo (config `Agent:CostTiers`, seed em `model-capabilities.json`);
  `ModelRoleResolver` aceita `maxTier` do contexto da run — filhos de
  `delegate_task` e runs de automação recebem o tier da política (barato
  por default quando configurado), nunca upgrade silencioso.
- **E. Superfície admin** — `GET /api/v1/admin/usage` (agregado por
  usuário/período, top chats por custo) + CRUD de policies `budget` na
  surface de policies existente (AdminPolicyEndpoints). UI: só a aba de
  policies/analytics já existente — sem página nova.

**Out of scope:**

- Cobrança/pagamentos (credits/billing estilo upstream) — isto é
  governança de custo, não monetização.
- Backplane multi-instância (ledger é DB — funciona; locks de quota são
  best-effort single-instance, coerente com `RateLimitService`).
- Estimativa de custo quando o provider não reporta usage — invariante
  do projeto: nunca estimar; run sem usage consome cap de tokens=0 e
  custo desconhecido documentado no ledger (`CostUsd=null`).
- Budget por chat — cap é por usuário e por run; por-chat fica para
  iteração futura se o uso pedir.

## 3. Technical Design

### Entidades/migration

```csharp
// Domain: RunUsageRecord (tabela run_usage_records)
// índices: (UserId, CreatedAt), (RunId), (ParentRunId)
```

`dotnet ef migrations add RunUsageLedger --project src/OpenWebUI.Infrastructure
--startup-project src/OpenWebUI.Api` — `DatabaseMigrator` aplica no boot.

### Fluxo

`ApplyUsageMetaAsync` (fim da run) → grava `RunUsageRecord` agregando
`StepUsages` + custo calculado com a tabela de preços já existente
(`{"cost":{"input","output"}}` USD/1M). Subagents: o record do filho
grava `ParentRunId` — a query de mês soma por `UserId` (filhos herdam o
dono da raiz porque a run filha já é do mesmo `UserId`).

`BudgetInterceptor` (`pre-step`): carrega policy `budget` do usuário via
`PolicyService.MatchAsync` (cache por request); se
`monthUsd + runUsd > usdPerMonth` ou `runUsd > usdPerRun` →
`Rejected`, motivo gravado como `interceptor.veto` (evento já existente)
+ `budget_exceeded` no resultado da run.

`ChatRunDispatcher` pré-enqueue: mesma checagem mensal — evita gasto de
uma run inteira quando o usuário já está estourado (routine/webhook
inclusive — o usuário é o dono da automação).

### Roteamento por tier

`Agent:CostTiers` = mapa `modelId → {tier: 0-3, inputUsd, outputUsd}`;
`ModelRoleResolver.Resolve(role, maxTier)` filtra candidatos ao tier.
`delegate_task` propaga `maxTier` do pai (campo em `ChatRun.MetaJson`),
automações usam o tier da policy do dono.

### Arquivos

```text
src/OpenWebUI.Domain/Entities.cs                 # RunUsageRecord
src/OpenWebUI.Infrastructure/Services/RunUsageLedgerService.cs   # novo
src/OpenWebUI.Infrastructure/Runs/BudgetInterceptor.cs           # novo
src/OpenWebUI.Infrastructure/Services/ModelRoleResolver.cs       # maxTier
src/OpenWebUI.Infrastructure/Data/AppDbContext.cs                # DbSet
src/OpenWebUI.Infrastructure/Data/model-capabilities.json        # tiers (ou Services/)
src/OpenWebUI.Api/Endpoints/AnalyticsEndpoints.cs (ou AdminPolicy) # usage report
tests/OpenWebUI.Api.Tests/RunBudgetTests.cs                       # novo
tests/OpenWebUI.Api.Tests/RunUsageLedgerTests.cs                  # novo
```

Config nova (`.env.exemplo` + `ConfigService`): `Agent:Budgets:Enabled`
(default `true`), `Agent:Budgets:WarnAtPercent` (default `80` — evento
`budget.warning` no feed da run, sem bloquear).

## 4. Requirements

### RF-001: ledger persistente
- **Description:** toda run finalizada com usage grava 1
  `RunUsageRecord`; run sem usage reportado grava record com tokens=0 e
  `CostUsd=null`.
- **Rules:** `ParentRunId` propagado; escrita idempotente por RunId.
- **Input → Output:** `StepUsages` + preços → row em `run_usage_records`.

### RF-002: cap mensal por usuário
- **Description:** soma `CostUsd` do mês corrente (UTC) por `UserId`;
  acima do cap → novas runs recusadas (`pre-run`) e run em voo recebe
  veto em `pre-step`.
- **Rules:** sem policy = ilimitado; policy de grupo vale se não houver
  de usuário (precedência existente do PolicyService).

### RF-003: cap por run
- **Description:** `runUsd > usdPerRun` → veto `pre-step` com
  `budget_exceeded`; custo de subagents conta na cadeia do pai
  (`ParentRunId` walk igual ao depth de `delegate_task`).

### RF-004: tier de custo no roteamento
- **Description:** `maxTier` da política/contexto filtra modelos
  elegíveis no `ModelRoleResolver`; `delegate_task` herda o tier do pai;
  ausência de modelo elegível no tier → fallback documentado (erro
  `no_model_in_tier`, nunca upgrade silencioso).

### RF-005: superfície admin
- **Description:** `GET /api/v1/admin/usage?userId=&from=&to=` agrega
  ledger (tokens, USD, runs, top chats); CRUD de policy `budget` na
  surface existente. Admin-only; usuário vê o próprio total do mês em
  endpoint não-admin `GET /api/v1/usage/me`.

## 5. API Contract

**Endpoint:** `GET /api/v1/usage/me`
**Auth:** Bearer/JWT

```json
{ "month": "2026-10", "promptTokens": 123456, "completionTokens": 45678,
  "costUsd": 1.234, "runs": 42, "cap": { "usdPerMonth": 10.0, "usedPct": 12.3 } }
```

**Endpoint:** `GET /api/v1/admin/usage?from=2026-10-01&to=2026-10-31`
**Auth:** admin

```json
{ "users": [{ "userId": "...", "email": "...", "costUsd": 3.21, "tokens": 900000 }],
  "topChats": [{ "chatId": "...", "userId": "...", "costUsd": 0.9 }] }
```

**Erros esperados:** `429 budget_exceeded` (pre-run/dispatcher),
`403` não-admin em `/admin/usage`. Formato de erro genérico existente.

## 6. Acceptance Criteria

- [ ] **Given** policy `usdPerRun=0.50` **when** a run acumula >$0.50
      **then** o próximo step é vetado com `budget_exceeded` e o evento
      `interceptor.veto` registrado.
- [ ] **Given** usuário com `usdPerMonth=5` e ledger do mês = $4.90
      **when** inicia run que passa de $5 **then** a run termina no veto
      de `pre-step`; nova tentativa de run é recusada no dispatcher.
- [ ] **Given** `delegate_task` filho custa $0.30 **when** o pai consulta
      **then** os $0.30 aparecem no ledger do mesmo `UserId` e no mês.
- [ ] **Given** `maxTier=1` na policy **when** `delegate_task` pede
      modelo tier-3 **then** resolver escolhe o melhor ≤1; nenhum
      elegível → erro `no_model_in_tier`.
- [ ] **Given** run sem usage do provider **then** ledger grava
      `CostUsd=null`, tokens=0 — nunca estimado.
- [ ] Regressão: `HarnessSeamsTests`, `AgentModeTests`, suíte completa
      verde; coverage ≥ baseline.

## 7. Task Plan

| ID | Task |
| --- | --- |
| T1 | Discovery: `ApplyUsageMetaAsync`, `PolicyService`, `RunInterceptorRunner`, `ModelRoleResolver`, `ChatRunDispatcher` |
| T2 | Migration + entidade `RunUsageRecord` + `RunUsageLedgerService` (escrita + queries de mês/cadeia) + testes |
| T3 | `BudgetInterceptor` (pre-step + tool.call) + pre-run check no dispatcher + testes de veto |
| T4 | Tiers de custo + `maxTier` no `ModelRoleResolver` + propagação em `delegate_task` + testes |
| T5 | Endpoints `/usage/me` + `/admin/usage` + policy `budget` + testes de contrato |
| T6 | DoD: Release build 0 warnings, suíte verde, coverage ≥ baseline, drift guards, SPEC→Issue→PR |

## 8. Organization Guardrails

- Respostas ao usuário em **pt-BR**; código/commits em inglês
  (Conventional Commits).
- Branch dedicada — nada de push direto em `main`.
- Não modificar `.github/workflows/`; sem secrets; migration nova segue
  o padrão `dotnet ef` + baseline do `DatabaseMigrator`.
- Invariante: **nunca estimar tokens/custo** — só o que o provider
  reporta; ausência é `null`, não zero inventado.
- Fail-closed nos seams: timeout/exceção do `BudgetInterceptor` segue a
  regra do `RunInterceptorRunner` (pre-step passa e loga; tool.call veta)
  — exceto a própria query de budget, cuja falha é tratada como "sem
  policy" e logada.

## Open Questions / Pending Ambiguity

| OQ | Status | Resolução |
| --- | --- | --- |
| Preço por modelo quando provider não informa | Proposta | tabela `Agent:CostTiers` com defaults conhecidos (OpenAI); `null` quando desconhecido |
| `warnAt` vira notificação push? | Adiado | v1 emite evento na run; WebPush é iteração futura |
