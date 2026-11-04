# SPEC-20261104-session-insights-v2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `session-insights-v2` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feature/Devin-20261104-session-insights-v2` |
| Ticket | `GH#691` — https://github.com/afonsoft/OpenWebUiCode/issues/691 |
| Status | `Approved` |

## 1. User Story

**As a** usuário ou administrador do Open WebUI .NET
**I want** insights de sessão estilo Devin — tamanho da sessão (XS–XL), categoria, timeline de issues detectadas e, sob demanda, uma análise gerada por LLM com prompt melhorado e action items — no painel do chat e numa visão agregada para o admin
**So that** entendo onde as sessões travam, melhoro meus prompts e configuro conhecimento/skills que evitam retrabalho.

**Problem context:**
A camada de dados está completa (`RunUsageLedgerService` por run + `/api/v1/usage/me|chats/{id}` + `/api/v1/admin/usage` + `/api/v1/analytics`), mas as superfícies são só métricas brutas: `ChatWorkspacePanel→info` (tokens/custo/charts) e `Admin→analytics` (usuários/mensagens/modelos). Não há: classificação de tamanho/categoria, detecção de issues (retries, erros, dead-ends), análise acionável (improved prompt, action items, knowledge usada) — que é o que o Devin Session Insights entrega (docs.devin.ai/product-guides/session-insights).

## 2. Scope

**In scope (Nível 1 — heurístico/barato):**
- **Session size chip** no chat e na listagem: `XS|S|M|L|XL` por heurística sobre tokens+mensagens+runs (faixas documentadas no código; L/XL = "unhealthy" badge).
- **Categoria da sessão**: classificação determinística por sinais (tools usadas, arquivos tocados, prompt inicial) nas categorias do Devin (feature, bugfix, refactor, tests, docs, research, devops…) — armazenada por chat.
- **Issue timeline** no painel `info` do chat: eventos de erro/retry/gate-approval das runs (dados já emitidos no ledger/eventos de run) mapeados para timeline cor-codificada (vermelho alto, amarelo médio, cinza evento, verde valor) + lista agrupada de issues com impacto.
- **Visão admin** na aba analytics: distribuição de tamanho, breakdown por categoria, top issues agregadas, sessões unhealthy.

**In scope (Nível 2 — stub acionável):**
- Botão **"Gerar análise"** no painel: chama o modelo default do usuário com o resumo estruturado da sessão (NÃO o chat inteiro — transcript truncado) → retorna `{summary, issues[], improvedPrompt, changes[], actionItems[], knowledgeUsed[]}`; persiste por chat (entidade `ChatInsight`), com "Regenerar"; "Usar prompt melhorado" pré-preenche um novo chat.
- i18n + testes + migration EF (`ChatInsights`).

**Out of scope:**
- ACU billing real (Devin cobra por compute; aqui mostramos tokens/custo USD já existentes).
- Análise automática em todo encerramento de sessão (v1 é sob demanda + automática só para L/XL se barato — opt-in por flag `features.sessionInsightsAuto`, default off).
- Dreaming/consolidação de memória (conceito Devin separado).
- Export/compartilhamento de insights.

## 3. Technical Context

**Where the change happens:**
- `Domain/Entities.cs` — novo `ChatInsight` (ChatId PK, UserId, SizeClass, Category, IssuesJson, AnalysisJson, ModelId, GeneratedAt). Migration `AddChatInsights`.
- `Infrastructure/Services/ChatInsightsService.cs` — NOVO: heurística de tamanho/categoria, coleta de issues dos eventos de run, chamada LLM para análise (usa `ProviderService` com a conexão efetiva do usuário — respeitando o gate da SPEC-20261104).
- `Api/Endpoints/InsightsEndpoints.cs` — NOVO grupo `/api/v1/chats/{id}/insights` (GET, POST generate) + extensão de `/api/v1/admin/usage` ou `analytics` com agregados de insights.
- `Client`: `ChatWorkspacePanel.razor` aba `info` → seção Insights (chip + categoria + timeline + botão gerar); `Admin.razor` aba `analytics` → cards novos; `ApiService`.

**Decisões de design (fechadas):**
- Nível 1 entrega valor sem custo de LLM; Nível 2 é sob demanda (botão) — sessões L/XL não geram análise automática na v1 (flag `features.sessionInsightsAuto` prepara o caminho).
- Análise usa o **modelo default do usuário** via `ProviderService` (mesma infra de tasks) → falha se usuário sem modelo configurado → mensagem clara.
- Transcript enviado ao LLM é truncado (system+primeira+últimas N msgs + resumo de issues) — budget ~8k tokens.

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/Services/RunUsageLedgerService.cs` — shape `RunUsageRecord`
- `src/OpenWebUI.Api/Endpoints/UsageEndpoints.cs`, `AnalyticsEndpoints.cs` — agregados existentes
- `src/OpenWebUI.Client/Components/ChatWorkspacePanel.razor` — aba `info` (`:503-630,842-849`)
- `src/OpenWebUI.Client/Pages/Admin.razor` — aba `analytics` (`:216`)
- Eventos de run (erros/retries) — `ChatPipeline`/run events persistidos (ver `Run*` entities)
- `Components/{LineChart,BarChart}.razor`

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities.cs                          # + ChatInsight
src/OpenWebUI.Application/Contracts/InsightsContracts.cs  # NOVO
src/OpenWebUI.Infrastructure/Services/ChatInsightsService.cs # NOVO
src/OpenWebUI.Api/Endpoints/InsightsEndpoints.cs          # NOVO
src/OpenWebUI.Api/Program.cs                              # map grupo
src/OpenWebUI.Client/Components/ChatWorkspacePanel.razor  # seção insights
src/OpenWebUI.Client/Components/InsightsPanel.razor       # NOVO (timeline+análise)
src/OpenWebUI.Client/Pages/Admin.razor                    # cards insights
src/OpenWebUI.Client/Services/ApiService.cs
src/OpenWebUI.Client/wwwroot/i18n/{8 locales}.json
tests/OpenWebUI.Api.Tests/InsightsEndpointsTests.cs       # NOVO
tests/OpenWebUI.Client.Tests/InsightsPanelTests.cs        # NOVO
```

## 4. Requirements

### RF-001: Classificação de tamanho e categoria
- **Description:** `ChatInsight` computado lazy no primeiro GET (ou atualizado a cada run): `SizeClass` por score = f(tokens totais, nº mensagens, nº runs) — faixas XS<5k, S<25k, M<100k, L<300k, XL≥300k tokens-equivalente (calibrar com dados reais, documentar constantes); `Category` por regras (ex.: >50% tool calls `file_*`/`apply_patch` → feature/bugfix; só `web_search`/`fetch` → research; testes via keywords+files `*Test*`…).
- **Rules:** chat sem runs → `XS`/`uncategorized`; recalculo incremental (não reprocessa runs já contadas — guardar `LastRunId`); nunca bloqueia a request — falha → `null` gracioso.

### RF-002: Issue timeline (Nível 1)
- **Description:** `GET /api/v1/chats/{id}/insights` retorna `{sizeClass, category, metrics{...}, issues:[{label, impact, description, count}], timeline:[{at, kind, title, ref}]}`. Issues derivadas de eventos persistidos das runs: provider errors/retries (alto se ≥3), gate approvals negadas/expiradas (médio), tool failures repetidas (médio), context-overflow recoveries (médio), dead-end runs vazias (alto).
- **Rules:** agrupar por `label+impact`; timeline ordenada com cap 100 eventos; só dados do próprio chat (permissão do owner; admin vê qualquer chat? — sim, como hoje no admin usage).

### RF-003: Análise por LLM sob demanda (Nível 2 stub)
- **Description:** `POST /api/v1/chats/{id}/insights/generate` → monta prompt estruturado (métricas + issues + transcript truncado) → completion via `ProviderService` (modelo default do usuário) → parse JSON `AnalysisJson` `{summary, issues[], improvedPrompt, changes[{what,why}], actionItems[{kind(machine|repo|skill|knowledge),text}], knowledgeUsed[]}` → persiste. `GET` retorna última análise; `POST` de novo = regenerar.
- **Rules:** usuário sem modelo configurado → `422` com mensagem; resposta não-JSON → retry 1x com instrução reforçada, depois `502`; `ModelId` registrado; timeout 120s; flag `features.sessionInsightsAuto` (admin) existe mas inativa na v1.

### RF-004: UI do chat — seção Insights no painel `info`
- **Description:** Chip de tamanho (`XS…XL`, L/XL com cor de alerta), categoria, cards de métricas existentes mantidos, **Issue timeline** (lista cor-codificada), bloco "Análise" com botão Gerar/Regenerar (spinner ~min), improved-prompt com highlight das mudanças + botão "Novo chat com este prompt", action items com link contextual (ex.: "criar skill" → `/customize/skills`).
- **Rules:** lazy-load só ao abrir a seção; erro de geração exibe mensagem sem quebrar painel; a11y (lista timeline com `role="list"`).

### RF-005: Visão admin agregada
- **Description:** Na aba analytics: cards — distribuição de tamanho (bar), breakdown por categoria (bar), top-5 labels de issue agregadas (tabela), contagem de chats unhealthy (L/XL) no período.
- **Rules:** dados via `GET /api/v1/admin/insights/summary?days=`; respeita o filtro de dias existente; sem dados → empty-state.

### RF-006: i18n + testes
- Chaves `insights.*` ×8; NUnit: heurística, issues, generate com provider mockado, permissões; bUnit: chip, timeline, estados do botão.

## 5. API Contract

**Endpoint:** `GET /api/v1/chats/{chatId}/insights`
**Auth:** owner ou admin (`403/404`)
**Response:**
```json
{ "sizeClass": "M", "category": "feature_development",
  "metrics": { "tokensIn": 1234, "tokensOut": 5678, "costUsd": 0.12,
               "runs": 3, "userMessages": 5 },
  "issues": [{ "label": "provider_retry", "impact": "medium",
               "description": "…", "count": 2 }],
  "timeline": [{ "at": 1762214400, "kind": "error|event|value",
                 "title": "…" }],
  "analysis": null }
```

**Endpoint:** `POST /api/v1/chats/{chatId}/insights/generate` → `200` com `analysis` populado.
**Expected errors:** `401/403/404` · `422` sem modelo configurado · `502` falha LLM/parse.

**Endpoint:** `GET /api/v1/admin/insights/summary?days=30` (admin)
**Response:** `{ "sizeDistribution": {"XS":n,…}, "byCategory": [{category,count}], "topIssues": [{label,impact,count}], "unhealthyChats": n }`.

## 6. Acceptance Criteria

- [x] **Dado** chat com 3 runs e retries **quando** abro painel info→Insights **então** vejo chip de tamanho correto, categoria, e issues de retry na timeline.
- [x] **Dado** chat XL **quando** listo no admin **então** conta em unhealthy e aparece na distribuição.
- [x] **Dado** usuário com modelo configurado **quando** clico "Gerar análise" **então** em ~1min vejo summary+improvedPrompt+actionItems; "Novo chat com este prompt" abre composer preenchido.
- [x] **Dado** usuário sem modelo **quando** clico Gerar **então** erro 422 amigável.
- [x] **Dado** usuário comum tentando insights de chat alheio **então** 403/404.
- [x] Edge: chat vazio, análise não-JSON do LLM, regeneração sobrescreve sem duplicar histórico (v1 guarda só a última).

## 7. Task Plan

| # | Task | Validation |
| - | - | - |
| T1 | `ChatInsight` entity + migration | build |
| T2 | `ChatInsightsService` heurística (size/category/issues) + GET | NUnit faixas/issues |
| T3 | `POST generate` (LLM via ProviderService, parse, persist) | NUnit provider mock |
| T4 | `InsightsPanel.razor` + seção no painel + admin cards | bUnit |
| T5 | i18n ×8 + tailwind + guards | `check-*.py` |
| T6 | Build/test + DoD | `dotnet build/test` |

## 8. Organization Guardrails

- Branch `feature/Devin-20261104-session-insights-v2`; migration obrigatória; transcript truncado — nunca logar/enviar chat inteiro; respeitar gate de modelos (SPEC-20261104) na chamada de análise.

## 9. Definition of Done

- [x] Size chip + categoria + issue timeline funcionais com dados reais.
- [x] "Gerar análise" produz improved prompt + action items persistidos.
- [x] Admin summary agrega distribuição/categorias/issues.
- [x] Migration + i18n ×8 + guards + testes verdes; SPEC `Done` + Issue linkada.
