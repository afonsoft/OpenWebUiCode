# SPEC-20261102-admin-analytics-v2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `admin-analytics-v2` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/Devin-20261102-admin-analytics-v2` |
| Ticket | `[a criar via create-issues]` |
| Status | `Approved` |

## 1. User Story

**As a** administrador do Open WebUI
**I want** uma aba Analytics com uso de tokens/custo por usuário e por modelo (LLM), série temporal de tokens e top chats por custo
**So that** eu entenda quem consome o quê, quanto a instância gasta por modelo e identifique abusos antes de estourar budgets.

**Problem context:**
A aba `/admin/analytics` atual (SPEC-20261001-analytics, entregue) mostra apenas contagens: cards de usuários/mensagens/chats/avaliações, um `BarChart` de mensagens/dia e lista de top modelos por nº de mensagens — fixa em 30 dias. O backend já persiste uso detalhado por run (`RunUsageRecord`: prompt/completion/reasoning tokens, `CostUsd`, `ModelId`, `UserId`, `ParentRunId`, `CreatedAt`) e já expõe `GET /api/v1/admin/usage` (agregado por usuário + top chats por custo) — **que o client não consome**. Não há série temporal de tokens, breakdown por modelo em tokens/custo, nem visualização de budget cap.

## 2. Scope

**In scope:**
- Backend: estender `GET /api/v1/admin/usage` com `totals`, série `tokens/dia` (prompt/completion/reasoning/cost), agregado por modelo (`ModelId` → runs/tokens/cost), colunas prompt/completion por usuário, enriquecimento de budget cap por usuário e título/email nos top chats; param `?days=` como atalho de janela.
- Ledger: novas agregações `ByDayAsync`, `ByModelAsync`; `ByUserAsync` com split prompt/completion/reasoning.
- Client (aba `analytics` do `Admin.razor`): seletor de período (7/30/90d), KPIs novos (tokens totais, custo USD, runs, usuários ativos), `LineChart` multi-série de tokens/dia, tabela ranking por usuário com barra cap-vs-uso, breakdown por modelo (tokens + custo), top chats por custo com link.
- Contratos em `Application` + método `ApiService.GetAdminUsageAsync`.
- i18n das novas chaves nos 8 locales; testes NUnit de agregação/autorização.

**Out of scope:**
- Export CSV/relatórios, drill-down por chat/run individual (existe `GET /usage/chats/{id}` para isso), alertas de budget em tempo real, dashboards de infra (OTel), gráficos de avaliações/adoção novos.
- Separação de uso próprio vs. delegado (runs filhas somam no usuário — decisão aprovada).
- Nova biblioteca de charts — reutilizar `BarChart`/`LineChart` SVG existentes (e estender `BarChart` p/ barras horizontais se necessário).

## 3. Technical Context

**Where the change happens:**
`Api` (`UsageEndpoints.AdminUsageAsync` enriquecido) · `Infrastructure` (`RunUsageLedgerService` novas agregações + join `BudgetPolicies`/`Chats`/`Users`) · `Application` (contratos novos) · `Client` (`Admin.razor` aba analytics, `ApiService`, possivelmente `BarChart`/`LineChart`).

**Files to read before implementing:**
- `src/OpenWebUI.Api/Endpoints/UsageEndpoints.cs` (admin usage existente)
- `src/OpenWebUI.Infrastructure/Services/RunUsageLedgerService.cs` (`ByUserAsync`, `TopChatsAsync`, `TotalsAsync`, `QueryAsync`)
- `src/OpenWebUI.Infrastructure/Services/BudgetPolicyService.cs` (`EffectiveAsync` — caps por usuário)
- `src/OpenWebUI.Domain/Entities.cs` (`RunUsageRecord`, `BudgetPolicy`)
- `src/OpenWebUI.Application/Contracts/AnalyticsContracts.cs` (padrão de records)
- `src/OpenWebUI.Client/Pages/Admin.razor` (aba `analytics`, ~l. 215-265)
- `src/OpenWebUI.Client/Components/{BarChart,LineChart}.razor` · `src/OpenWebUI.Client/Services/ApiService.cs` (`GetAnalyticsAsync`)
- `src/OpenWebUI.Client/wwwroot/i18n/*.json` (8 locales — paridade via `tools/check-i18n-parity.py`)

**Files to create or modify:**
```text
src/OpenWebUI.Application/Contracts/UsageContracts.cs          (novos records admin usage)
src/OpenWebUI.Api/Endpoints/UsageEndpoints.cs                  (AdminUsageAsync enriquecido)
src/OpenWebUI.Infrastructure/Services/RunUsageLedgerService.cs (ByDayAsync, ByModelAsync, split tokens)
src/OpenWebUI.Client/Pages/Admin.razor                         (aba analytics v2)
src/OpenWebUI.Client/Services/ApiService.cs                    (GetAdminUsageAsync)
src/OpenWebUI.Client/Components/BarChart.razor                 (opcional: barras horizontais/labels)
src/OpenWebUI.Client/wwwroot/i18n/*.json                       (8 locales)
tests/OpenWebUI.Api.Tests/AdminUsageTests.cs                   (novo)
```

## 4. Requirements

### RF-001: Totais e série temporal de tokens (admin, janela)
- **Description:** `GET /api/v1/admin/usage` aceita `?days={7|30|90}` (ou `from`/`to` ISO como hoje) e retorna `totals` {promptTokens, completionTokens, reasoningTokens, costUsd, runs, activeUsers} e `series[]` {day, promptTokens, completionTokens, reasoningTokens, costUsd} agregada por dia UTC na janela.
- **Rules:** admin-only (403 sem role Admin); zeros explícitos quando não há dados; `costUsd` arredondado 6 casas; days default 30, clamp 1..365.
- **Input → Output:** `?days=30` → JSON com `totals` + `series` (um item por dia, dias sem uso podem ser omitidos ou zerados — client trata ambos).

### RF-002: Agregado por modelo (LLM)
- **Description:** `models[]` {modelId, runs, promptTokens, completionTokens, reasoningTokens, tokens, costUsd} ordenado por `tokens` desc na janela.
- **Rules:** `modelId` nulo agrupa como `"unknown"`; top 20 (`?top=` clamp 1..100 reaproveitado).
- **Input → Output:** mesma request → `models[]`.

### RF-003: Ranking por usuário com budget cap
- **Description:** `users[]` estendido com `promptTokens`, `completionTokens`, `reasoningTokens` (além de `tokens`, `runs`, `costUsd`, `email`) e, quando houver `BudgetPolicy` efetiva, `capTokensPerMonth`, `capUsdPerMonth`, `usedPct`.
- **Rules:** cap resolvido via `BudgetPolicyService.EffectiveAsync` por usuário presente no ranking (N+1 tolerado — ranking tipicamente <100); `usedPct` = max(tokens/capTokens, cost/capUsd)·100, 1 casa decimal; ausência de policy → campos nulos.
- **Input → Output:** mesma request → `users[]` enriquecido.

### RF-004: Top chats por custo enriquecido
- **Description:** `topChats[]` com `chatId`, `title` (join `Chats`; "sem título"/id curto quando nulo), `userId`, `email`, `tokens`, `costUsd`.
- **Rules:** ordenado por `costUsd` desc; chats deletados aparecem com `title` nulo.
- **Input → Output:** mesma request → `topChats[]`.

### RF-005: UI analytics v2 (aba `analytics` do `/admin`)
- **Description:** Reorganizar a aba em seções: (a) seletor 7/30/90d; (b) linha de KPIs — usuários, mensagens, chats, avaliações (existentes) + tokens totais, custo USD, runs, usuários ativos; (c) `LineChart` multi-série tokens/dia (prompt/completion/reasoning) + BarChart mensagens/dia (mantido); (d) ranking por usuário (email · runs · tokens · custo · barra cap) com coluna de % do cap; (e) breakdown por modelo (tabela: modelo · runs · tokens · custo) mantendo a lista top-modelos-por-mensagens; (f) top chats por custo com link para o chat.
- **Rules:** tokens formatados compactos (ex.: `1,2M`, `340k`); custo `US$ 0.0000`; estado vazio com `admin.no_data`; loading por seção; dados do `/api/v1/admin/usage` e `/api/v1/analytics` buscados em paralelo.
- **Input → Output:** seleção de preset → refetch + re-render de todas as seções.

**Business rules / invariants:**
- Somente role `Admin` acessa `/api/v1/admin/usage` (existente — manter).
- Tokens/custo nunca estimados — somente o que o provider reportou (`CostUsd` nulo não inventa valor).
- Runs delegadas (`ParentRunId`) já carregam `UserId` do dono — somam no usuário (sem flag visual nesta versão).
- Sem PII além do já exposto a admins (email é padrão admin no upstream).

## 5. API Contract

**Endpoint:** `GET /api/v1/admin/usage?days={7|30|90}&userId={id?}&from={ISO?}&to={ISO?}&top={n?}`
**Auth:** Bearer JWT (role `Admin`; `403` caso contrário). `from`/`to` têm precedência sobre `days`.

**Response (success):**
```json
{
  "from": "2026-10-04",
  "to": "2026-11-02",
  "totals": {
    "promptTokens": 1234567, "completionTokens": 234567, "reasoningTokens": 8901,
    "costUsd": 1.234567, "runs": 321, "activeUsers": 4
  },
  "series": [
    { "day": "2026-10-04", "promptTokens": 12000, "completionTokens": 3000, "reasoningTokens": 0, "costUsd": 0.0123 }
  ],
  "models": [
    { "modelId": "gpt-4o", "runs": 40, "promptTokens": 90000, "completionTokens": 21000,
      "reasoningTokens": 0, "tokens": 111000, "costUsd": 0.42 }
  ],
  "users": [
    { "userId": "u1", "email": "a@b.c", "runs": 12, "promptTokens": 50000,
      "completionTokens": 9000, "reasoningTokens": 0, "tokens": 59000, "costUsd": 0.31,
      "capTokensPerMonth": 100000, "capUsdPerMonth": null, "usedPct": 59.0 }
  ],
  "topChats": [
    { "chatId": "c1", "title": "Análise X", "userId": "u1", "email": "a@b.c",
      "tokens": 41000, "costUsd": 0.19 }
  ]
}
```

**Expected errors:** `401` sem auth · `403` não-admin · `400` janela inválida (`to` < `from` ou `days` fora de 1..365).

## 6. Acceptance Criteria

- [ ] **Given** admin autenticado **when** `GET /api/v1/admin/usage?days=7` **then** retorna 200 com `totals`, `series` (≤7 itens), `models`, `users`, `topChats` — agregações batem com `RunUsageRecord` seedado.
- [ ] **Given** usuário não-admin **when** chama o endpoint **then** `403`.
- [ ] **Given** usuário com policy `TokensPerMonth` **when** o ranking é montado **then** sua linha traz `capTokensPerMonth` + `usedPct` correto.
- [ ] **Given** janela sem nenhuma run **then** `totals` zerado, arrays vazios — UI mostra `admin.no_data` sem erro.
- [ ] **Given** a aba analytics **when** troco preset para 90d **then** todas as seções recarregam com a nova janela.
- [ ] **Given** modelo nulo em `RunUsageRecord` **when** agrego por modelo **then** aparece como `unknown`.
- [ ] **Given** runs filhas de `delegate_task` **when** somo por usuário **then** seus tokens entram no total do dono.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| `days=0` ou `days=9999` | `?days=0` | clamp p/ 1 ou 400 — documentar o escolhido |
| `to` < `from` | `from=2026-11-02&to=2026-10-01` | `400` com `detail` genérico |
| `CostUsd` nulo em todas as runs | — | `costUsd: 0` nos agregados (não vira `null` nem negativo) |
| Usuário sem email cadastrado | — | `email: null` na linha — UI exibe `userId` truncado |
| >20 modelos na janela | `?top=` ausente | top 20 por tokens |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** ler arquivos da seção 3; confirmar shape de `BudgetPolicyService.EffectiveAsync` e campos `Chat.Title`.
- [ ] **T2 — Contratos + ledger:** records em `UsageContracts.cs`; `ByDayAsync`, `ByModelAsync`, split de tokens em `ByUserAsync`.
- [ ] **T3 — Endpoint:** enriquecer `AdminUsageAsync` (days param, totals/series/models/users+/topChats+, cap join); validação de janela.
- [ ] **T4 — Testes API:** `AdminUsageTests.cs` — agregações, 403, cap, janela vazia, model `unknown`, filhas somadas.
- [ ] **T5 — Client:** `GetAdminUsageAsync` + records; aba analytics com seletor, KPIs, `LineChart` tokens/dia, ranking usuário c/ barra cap, tabela modelos, top chats linkados; i18n ×8 locales; formatadores compactos.
- [ ] **T6 — Validação:** `dotnet test`, `dotnet build`, `python3 tools/check-css-classes.py && check-form-a11y.py && check-i18n-parity.py && check-spec-status.py`; regenerar `tailwind.css` **somente** se classes novas forem usadas (skill `tailwind-css`); drift guards verdes.
- [ ] **T7 — Done + PR:** DoD completo → `Status = Done` + PR `feature/Devin-20261102-admin-analytics-v2` → `main` referenciando o ticket.

**7.1 Validation strategy**
- .NET: testes de unidade/integração NUnit nas agregações e autorização; build Release; cobertura ≥80% nos trechos novos (baselines `.ci/`).
- Client: drift guards do repo (css/a11y/i18n/spec) + revisão visual manual da aba.

## 8. Organization Guardrails

- **Branches:** nunca em `main`; `feature/Devin-20261102-admin-analytics-v2`.
- **Workflows:** não modificar `.github/workflows/`.
- **i18n:** toda chave nova nos 8 locales (`check-i18n-parity.py` é gate de CI).
- **CSS:** não editar `wwwroot/css/tailwind.css` manualmente — regenerar via `tailwindcss` se classes novas surgirem.
- **Security:** endpoint admin-only; sem logs de tokens/emails; `CostUsd` nunca estimado.
- **Scope:** não inventar métricas fora da seção 4; paridade visual com o padrão dos cards/abas existentes.

## 9. Definition of Done

- [ ] Todos os RF-001..RF-005 implementados.
- [ ] Critérios da seção 6 cobertos por testes/evidência.
- [ ] Edge cases da tabela tratados.
- [ ] `dotnet build` + `dotnet test` + drift guards verdes localmente.
- [ ] i18n paridade nos 8 locales; a11y dos novos controles (seletor com `aria-label`).
- [ ] Guardrails da seção 8 respeitados.

**Next action after DoD is complete:** `Status = Done` + PR para `main` com `Closes #<ticket>`.

## Open Questions / Pending Ambiguity

- Nenhuma — escopo fechado em entrevista (blocos: todos; período: presets 7/30/90d; sub-runs somadas ao usuário; barra cap-vs-uso: sim).
