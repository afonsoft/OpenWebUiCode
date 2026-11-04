# SPEC-20261104-dreaming-lite

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `dreaming-lite` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feature/Devin-20261104-dreaming-lite` |
| Ticket | `GH#697` — https://github.com/afonsoft/OpenWebUiCode/issues/697 |
| Status | `Approved` |

## 1. User Story

**As a** usuário que gerou uma análise de sessão (insights L2)
**I want** materializar os action items sugeridos como `KnowledgeNote`/`AgentMemory`/rascunho de `Skill` com um clique
**So that** o aprendizado da sessão feche o loop e melhore chats futuros — versão lite do "dreaming" do Devin.

**Problem context:**
`POST /insights/generate` (SPEC-20261104-session-insights-v2) já produz `actionItems` com `kind` (`knowledge|skill|memory|...`) e texto. Hoje eles são exibidos no `InsightsPanel` e morrem aí — nada consolida. `KnowledgeNote` (trigger+pinned, escopo user/org) e `AgentMemory` (scope global|repo, upsert por título) já existem; `MemorySyncScheduler` mostra o padrão de `BackgroundService`.

## 2. Scope

**In scope:**
- Botão por action item no `InsightsPanel` + "Aplicar todas as sugestões".
- `kind == "knowledge"` → cria `KnowledgeNote` pessoal (`UserId=caller`, `Trigger`=resumo da nota, `Pinned=false`) via endpoint customize existente.
- `kind == "memory"` → cria/upserta `AgentMemory` (`Scope="repo"`+`RepoSlug` se o chat tiver repo bound, senão `"global"`).
- `kind == "skill"` → **não autocria**: abre `/customize?tab=skills` com rascunho pré-preenchido (query param) — skill ativa injeta no prompt, sensível demais para autocriar.
- Outros kinds → ignorados com tooltip "tipo não suportado".
- Endpoint backend `POST /api/v1/chats/{chatId}/insights/apply` aceitando lista de itens a materializar (server-side enforce ownership + escopo pessoal — nunca cria global).
- i18n ×8; NUnit + bUnit.

**Out of scope:**
- Job agendado automático consolidando sem interação (extensão futura — documentada).
- Consolidação de issues heurísticas recorrentes (barulhento; timeline já visibiliza).
- Action items → global/org (só admin promove manualmente em `/customize`).

## 3. Technical Context

**Where the change happens:**
- `InsightsEndpoints`/`ChatInsightsService` — novo endpoint `apply` que recebe `{ items: [{kind, text}] }`, mapeia kind→entidade e persiste.
- `AgentMemory`: upsert por (UserId, Scope, RepoSlug, Title) — título derivado do texto (primeiras ~60 chars ou resumo).
- `InsightsPanel.razor` — botões "Salvar" por item + "Aplicar todas"; estado `applied` por item.
- `Customize.razor` — ler query `?tab=skills&draft=...` para pré-preencher o editor de skill.

**Files to read before implementing:**
- `src/OpenWebUI.Api/Endpoints/InsightsEndpoints.cs` + `ChatInsightsService.cs`
- `src/OpenWebUI.Api/Endpoints/CustomizeEndpoints.cs` (KnowledgeNote create — reutilizar lógica ou chamar serviço)
- `src/OpenWebUI.Domain/Entities.cs:1594` (`AgentMemory`), `:1561` (`KnowledgeNote`)
- `src/OpenWebUI.Client/Components/InsightsPanel.razor`
- `src/OpenWebUI.Client/Pages/Customize.razor` (query param support — já tem `@page` múltiplos?)

**Files to create or modify:**
```text
src/OpenWebUI.Api/Endpoints/InsightsEndpoints.cs      (POST apply)
src/OpenWebUI.Infrastructure/Services/ChatInsightsService.cs (ApplyActionItemsAsync)
src/OpenWebUI.Application/Contracts/InsightsContracts.cs    (ApplyRequest/Response)
src/OpenWebUI.Client/Components/InsightsPanel.razor  (botões + estado applied)
src/OpenWebUI.Client/Pages/Customize.razor           (draft via query param)
src/OpenWebUI.Client/Services/ApiService.cs          (ApplyInsightSuggestionsAsync)
src/OpenWebUI.Client/wwwroot/i18n/*.json             (insights.apply_* ×8)
tests/OpenWebUI.Api.Tests/InsightsEndpointsTests.cs  (+3-4)
tests/OpenWebUI.Client.Tests/InsightsPanelTests.cs   (+2)
```

## 4. Requirements

### RF-001: Endpoint apply
- **Description:** `POST /api/v1/chats/{chatId}/insights/apply` — body `{items:[{kind,text}]}`. Ownership do chat enforceada (404 inacessível). Para cada item: `knowledge`→KnowledgeNote pessoal; `memory`→AgentMemory upsert (repo se bound); `skill`→400-ish skip com flag `requiresDraft` (client abre editor); demais→skip.
- **Input → Output:** `{items:[...]}` → `{applied:[{kind,refId,title}], skipped:[{kind,reason}]}`.
- **Rules:** nunca cria `UserId=null` (global); dedup — se já existe nota/memória com mesmo título pro usuário, retorna `skipped: duplicate`; transação por item (falha num não aborta outros).

### RF-002: UI — botões por item + bulk
- **Description:** `InsightsPanel` action items ganham botão "Salvar" (ícone por kind: nota/memória) + estado `applied`/`skipped`. "Aplicar todas" chama apply com todos os items suportados. Kind `skill` vira link "Editar em Skills" → `/customize?tab=skills&draft=<urlencoded>`.

### RF-003: Rascunho de skill via query param
- **Description:** `Customize.razor` lê `?tab=` (já existe rota `{tab}`?) e `?draft=` — com draft, abre o editor de nova skill pessoal com `Content` pré-preenchido.

### RF-004: Escopo server-side
- **Description:** todo item materializado vira pessoal (`UserId=caller`); `AgentMemory.Scope="repo"` somente se `Chat.RepoSlug` (ou binding equivalente) existir.

### RF-005: i18n + testes
- **Description:** `insights.apply`, `insights.applied`, `insights.apply_all`, `insights.apply_skip_dup`, `insights.edit_skill` ×8. NUnit: ownership 404, knowledge→note, memory→repo/global, dup skip. bUnit: botão aplica + estado applied; skill→link.

## 5. API Contract

**Endpoint:** `POST /api/v1/chats/{chatId}/insights/apply` — Auth: JWT/`sk-*`.

**Request:**
```json
{"items":[{"kind":"knowledge","text":"Use pino V2 ao invés de V1 no plugin X"},{"kind":"memory","text":"..."},{"kind":"skill","text":"..."}]}
```
**Response 200:**
```json
{"applied":[{"kind":"knowledge","refId":"<guid>","title":"..."},{"kind":"memory","refId":"<guid>","title":"..."}],"skipped":[{"kind":"skill","reason":"requires_draft"},{"kind":"x","reason":"unsupported"},{"kind":"memory","reason":"duplicate"}]}
```
**Erros:** 404 chat inacessível; 400 body vazio. Nunca 5xx por item individual — falhas viram `skipped`.

## 6. Acceptance Criteria

- [ ] **Dado** insight com item `knowledge` **quando** clica "Salvar" **então** `KnowledgeNote` pessoal criada e botão vira `applied`.
- [ ] **Dado** item `memory` em chat com repo bound **quando** aplicado **então** `AgentMemory{Scope:repo, RepoSlug}` upsertada.
- [ ] **Dado** item `skill` **quando** clicado **então** navega para `/customize?tab=skills&draft=...` com editor pré-preenchido.
- [ ] **Dado** nota com mesmo título já existente **quando** reaplicada **então** `skipped:duplicate`, sem duplicar.
- [ ] **Dado** chat de outro usuário **quando** POST apply **então** 404.
- [ ] **Dado** "Aplicar todas" com 3 items **quando** 1 falha **então** 2 aplicados + 1 skipped — sem rollback indevido.

## 7. Task Plan

- [ ] **T1:** `ApplyActionItemsAsync` no `ChatInsightsService` + contratos + endpoint (TDD: testes primeiro).
- [ ] **T2:** `ApiService.ApplyInsightSuggestionsAsync` + botões no `InsightsPanel`.
- [ ] **T3:** `Customize.razor` — query `draft` → pré-preencher editor de skill.
- [ ] **T4:** i18n ×8 + tailwind.
- [ ] **T5:** testes NUnit + bUnit; build+guards+DoD → PR.

## 8. Organization Guardrails

- **Security:** apply nunca cria registros globais; titles derivados do texto — sanitizar/trim (max 120 chars); sem PII em logs.
- **Scope:** sem auto-consolidação agendada; sem promoção a org pelo apply.
- **Architecture:** lógica de persistência no `ChatInsightsService` (Infrastructure); endpoint fino.
- **Upstream parity:** espelha Devin "knowledge suggestions" mas materializa só sob ação explícita do usuário.

## 9. Definition of Done

- [ ] RF-001..RF-005 + critérios BDD cobertos por testes.
- [ ] Build Release + suites verdes; drift guards.
- [ ] i18n paridade; SPEC → `Done` + arquivada.

**Next action after DoD is complete:** PR `feature/Devin-20261104-dreaming-lite`.

## Open Questions / Pending Ambiguity

- Extensão futura (fora de escopo, documentada): `DreamingScheduler` BackgroundService que varre insights com action items não aplicados e **sugere** (badge "N sugestões pendentes" no Customize hub) — nunca autocria.
