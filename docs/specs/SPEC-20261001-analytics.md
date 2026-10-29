# SPEC-20261001-analytics

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `analytics` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261001-analytics` |
| Ticket | `GAP-observability-analytics — Issue a criar` |
| Status | `Completed` |

## 1. User Story

**As a** administrador do Open WebUI
**I want** métricas de uso (mensagens, usuários ativos, modelos, tokens)
**So that** eu acompanhe adoção e uso da instância como no upstream (Analytics).

**Problem context:**
Sem analytics/metrics (`docs/MIGRACAO-DOTNET.md:72` — ⬜). Nenhum dado de uso agregado é calculado, embora chats/mensagens/avaliações já existam no SQLite.

## 2. Scope

**In scope:**
- Endpoint admin `/api/v1/analytics` com agregações: usuários totais/ativos, mensagens/dia, chats/dia, top modelos, média de avaliações.
- Página `/admin/analytics` (ou aba em `/admin`) com cards e gráficos simples (SVG/CSS — sem lib de chart `[A DEFINIR]`; recomendado: SVG inline simples).
- Janela de período selecionável (7/30/90 dias).

**Out of scope:**
- Export CSV/Excel (upstream issue em aberto — fase 2).
- OpenTelemetry/métricas de infraestrutura (outro tema).
- Funil/retenção avançada.

## 3. Technical Context

**Where the change happens:**
`Api` (`AnalyticsEndpoints` com queries EF), `Client` (`/admin` nova aba/página com cards).

**Files to read before implementing:**
- `src/OpenWebUI.Api/Endpoints/{UserEndpoints,ApiEndpoints}.cs`, `src/OpenWebUI.Client/Pages/Admin.razor`
- `src/OpenWebUI.Infrastructure/Data/` (DbContext — entidades Chat/Message/Evaluation/User)

**Files to create or modify:**
```text
src/OpenWebUI.Api/Endpoints/AnalyticsEndpoints.cs
src/OpenWebUI.Client/Pages/Admin.razor (+ componente de gráfico)
tests/OpenWebUI.Api.Tests/AnalyticsEndpointsTests.cs
```

## 4. Requirements

### RF-001: Agregações de uso
- **Description:** Endpoint retorna totais e séries temporais agregadas por dia no período.
- **Rules:** admin-only; queries eficientes (group-by no SQL); período default 30 dias.
- **Input → Output:** `?days=30` → `{totals, series}`

### RF-002: UI de analytics
- **Description:** Aba Analytics em `/admin` com cards de totais e gráficos de série temporal.
- **Rules:** mesmo visual upstream (cards dark, cantos arredondados); loading e empty states.

**Business rules / invariants:**
- Nenhum dado de conteúdo de mensagem exposto — apenas contagens.
- Usuário não-admin recebe 403.

## 5. API Contract

**Endpoint:** `GET /api/v1/analytics?days=30`
**Auth:** `Bearer` admin

**Response:** `200 {"users":{...},"messages":[{day,count}],"models":[{model,count}]}` · `403`

## 6. Critérios de Aceite

- [ ] Endpoint retorna contagens corretas sobre dados semeados.
- [ ] Aba renderiza cards + gráfico com dados reais.
- [ ] Não-admin → 403. Testes cobrem agregação e auth.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/34 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/20
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/14
