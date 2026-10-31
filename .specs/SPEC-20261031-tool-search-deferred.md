# SPEC-20261031-tool-search-deferred

## 0. Metadata

| Field | Value |
|-------|-------|
| Feature | `tool_search` — deferred tool loading para economia de contexto |
| Type | Feature |
| Stack | .NET 10 / ASP.NET Core |
| Repository | afonsoft/OpenWebUiCode |
| Branch | feature/devin-20261031-tool-search-deferred |
| Ticket | [A DEFINIR] |
| Status | Approved |

## 1. User Story

**As a** usuário do agente com muitas tools/MCPs instaladas
**I want** que tools especializadas sejam carregadas sob demanda via `tool_search` em vez de todas no request
**So that** o system prompt fique enxuto (menos tokens, menos confusão de tool-choice) e o agente descubra a tool certa quando precisar.

**Problem context:**
O Devin CLI tem (`docs/DEVIN-CLI-REA-ANALYSIS.md` §1 `toolbox-core/tools/tool_search.rs`, §7) uma tool **`tool_search`** que implementa deferred loading: as tools "core" ficam sempre disponíveis; tools menos frequentes ficam "deferred" (só nome+descrição no índice) e, quando o modelo chama `tool_search` com uma query, os schemas completos das tools relevantes são injetados na conversa ("Loaded N tool(s) — they are now available"). O OpenWebUiCode hoje envia **todos** os `ToolIds` resolvidos no request de completions — com ~30 builtins + MCP servers, o overhead de schema é significativo e a escolha de tool fica ruidosa.

## 2. Scope

**In scope:**

- Conceito de tier de tools: **core** (sempre no request) vs **deferred** (só nome+descrição num índice leve).
- Nova builtin tool `tool_search(query, limit?)` que retorna os schemas das tools deferred que batem a query e as marca como "carregadas" para o resto da run.
- Índice leve de tools deferred injetado no system prompt (nome + uma linha de descrição + keywords) — muito menor que o schema completo.
- Carregamento por run: uma vez que `tool_search` retorna uma tool, ela entra no request da próxima iteração do loop (schema completo).
- Config: `BuiltinTools:Deferred` (lista de tools que começam deferred), `BuiltinTools:ToolSearchLimit` (default 5); MCP tools começam deferred por padrão quando > N servers.
- Auditoria: log de quais tools foram carregadas por run (métrica de economia de contexto).

**Out of scope:**

- Reranking semântico por embeddings (MVP usa match por substring/keyword).
- Descarregamento de tools (uma vez carregada, fica até o fim da run).
- Deferred para tools de sistema críticas (todo_write, exec, file_*, delegate_task sempre core).

## 3. Technical Context

**Where the change happens:**

`src/OpenWebUI.Infrastructure/ChatTools/BuiltinToolRegistry.cs` (tier core/deferred), `src/OpenWebUI.Api/Completions/ChatPipeline.cs` (construção do request de tools por iteração — deferred carregadas entram), `src/OpenWebUI.Api/Completions/Contributors/SystemPromptContributors.cs` (índice leve no prompt), nova `ToolSearchBuiltinTool.cs`.

**Files to read before implementing:**

- `src/OpenWebUI.Infrastructure/ChatTools/BuiltinToolRegistry.cs`
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs`
- `src/OpenWebUI.Api/Completions/Contributors/SystemPromptContributors.cs`
- `docs/DEVIN-CLI-REA-ANALYSIS.md` §7
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/SkillBuiltinTool.cs` (padrão de discovery)

**Files to create or modify:**

```text
src/OpenWebUI.Infrastructure/ChatTools/Tools/ToolSearchBuiltinTool.cs   (novo)
src/OpenWebUI.Infrastructure/ChatTools/BuiltinToolRegistry.cs           (tier core/deferred)
src/OpenWebUI.Infrastructure/ChatTools/ToolIndexService.cs              (novo — índice leve)
src/OpenWebUI.Api/Completions/Contributors/SystemPromptContributors.cs  (índice deferred)
src/OpenWebUI.Api/Completions/ChatPipeline.cs                           (merge loaded tools)
tests/OpenWebUI.Api.Tests/ToolSearchTests.cs                            (novo)
```

## 4. Requirements

### RF-001: Tiers core/deferred
- **Description:** Cada tool do registry tem um tier: `core` (schema no request sempre) ou `deferred` (só entrada no índice leve). Config `BuiltinTools:Deferred` marca quais começam deferred; padrão: tools de alta frequência (exec, file_*, todo, delegate_task, context, plan, skill) são core; o resto deferred.
- **Rules:** `tool_search` é sempre core; tools críticas de fluxo (plan_exit, run_result, approval) nunca deferred.

### RF-002: Índice leve no system prompt
- **Description:** Tools deferred aparecem num bloco compacto no system prompt: `## Available on demand — name: one-line description [keywords]`. Estimativa: ~15 tokens/tool vs ~150-400 do schema completo.
- **Rules:** índice atualizado quando tools são carregadas (linha removida do bloco deferred); sem schema no índice.

### RF-003: `tool_search` retorna schemas
- **Description:** `tool_search(query)` faz match por substring/keyword sobre nome+descrição das deferred, retorna até `limit` (default 5) schemas completos como conteúdo da tool e marca-as como carregadas na run.
- **Rules:** resultado inclui o schema JSON completo da tool (mesmo formato usado no request); tools já carregadas não são retornadas; sem match → mensagem informativa.

### RF-004: Merge de loaded tools no request
- **Description:** A cada iteração do loop, tools carregadas por `tool_search` são adicionadas ao `tools` do request de completions (schema completo), além das core.
- **Rules:** merge por run (estado em memória do executor); tools carregadas aparecem na próxima iteração; deferred já carregadas não voltam ao índice.

### RF-005: MCP tools deferred por volume
- **Description:** Quando um chat tem > N tools MCP disponíveis, tools de servers MCP começam deferred (exceto allowlist por server).
- **Rules:** `mcp__<server>__<tool>` naming; `tool_search` cobre tools MCP também; servers "always core" configuráveis.

### RF-006: Auditoria de economia
- **Description:** A run registra `{toolsCore, toolsDeferred, toolsLoaded, tokensSavedEstimate}` por iteração; evento `tool_search.loaded` no stream.

**Business rules / invariants:**
- Uma tool deferred nunca é chamável pelo modelo até ser carregada (tentativa direta → erro "tool not loaded — use tool_search").
- `tool_search` não carrega tools fora do `ToolIds` permitido do chat/preset.
- Índice leve nunca excede ~60 linhas (trunca com "…and N more — use tool_search").

## 5. API Contract

Sem API nova. `tool_search` é builtin tool (`builtin_tool_search`).

## 6. Acceptance Criteria

- [ ] **Given** 20 tools deferred, **when** a run inicia, **then** o request só leva schemas core + o índice leve.
- [ ] **Given** `tool_search("browser")`, **when** há `browser_preview` deferred, **then** seu schema é retornado e a tool fica chamável na próxima iteração.
- [ ] **Given** tool deferred chamada diretamente, **when** o modelo tenta, **then** recebe erro "not loaded — use tool_search".
- [ ] **Given** tool já carregada, **when** `tool_search` repete a query, **then** ela não aparece no resultado.
- [ ] **Given** `BuiltinTools:Deferred` vazio, **when** config desabilita, **then** comportamento atual (todas no request).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Query sem match | `tool_search("xyz")` | "no deferred tools matched" |
| Todas deferred carregadas | N chamadas | índice some; `tool_search` → "none remaining" |
| Tool permitida mas deferred | `ToolIds` inclui | carregável via search normalmente |
| Tool não permitida | fora de `ToolIds` | nunca aparece no índice nem é carregável |

## 7. Task Plan

- [ ] **T1 — Discovery:** ler `BuiltinToolRegistry`, `ChatPipeline` (construção de tools), `SystemPromptContributors`.
- [ ] **T2 — Tiers+índice:** `ToolIndexService` + tier no registry + bloco no system prompt.
- [ ] **T3 — `tool_search`:** `ToolSearchBuiltinTool` com match + retorno de schema + mark-as-loaded.
- [ ] **T4 — Merge no loop:** `ChatPipeline`/`ChatRunExecutor` adicionam loaded tools por iteração.
- [ ] **T5 — MCP deferred:** regra por volume + allowlist.
- [ ] **T6 — Auditoria:** métricas + evento.
- [ ] **T7 — Testes:** unitários (match, tiers, merge, deferred-call-error) + integração (loop com search).
- [ ] **T8 — Done + PR.**

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **.NET** | Unit tests (index, search match, merge, tiers); integration tests (loop carrega tool via search); 80% coverage. |

## 8. Organization Guardrails

- **Branches:** `feature/devin-20261031-tool-search-deferred`.
- **Security:** deferred respeita `ToolIds`/preset; nenhuma tool fora do permitido é carregável.
- **Scope:** match por substring no MVP (embeddings futuro).
- **Architecture:** tier é metadado do registry; merge acontece na construção do request.

## 9. Definition of Done

- [ ] RFs implementados; aceite coberto por testes.
- [ ] Edge cases tratados; `dotnet test` verde; `check-spec-status.py` ok.
- [ ] Guardrails respeitados; métricas de economia visíveis no stream.

## Open Questions / Pending Ambiguity

- Deferred default ligado ou opt-in? (recomendo opt-in via `BuiltinTools:Deferred` explícito — não quebrar comportamento atual).
- O índice leve vai no system prompt ou numa mensagem de contexto? (recomendo system prompt, bloco fixo).
