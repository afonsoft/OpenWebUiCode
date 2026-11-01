# SPEC-20261031-sidekick-orchestration

## 0. Metadata

| Field | Value |
|-------|-------|
| Feature | Orquestração lead/sidekick persistente + profiles de subagente (explore/general) |
| Type | Feature |
| Stack | .NET 10 / ASP.NET Core |
| Repository | afonsoft/OpenWebUiCode |
| Branch | feature/devin-20261031-sidekick-orchestration |
| Ticket | #575 (Epic #572/E19) |
| Status | Completed |

## 1. User Story

**As a** usuário do agente em tarefas longas
**I want** um worker persistente (sidekick) com memória entre handoffs e um protocolo de delegação disciplinado
**So that** o agente principal mantenha julgamento e revisão enquanto delega implementação/verificação sem recriar contexto a cada dispatch.

**Problem context:**
O Devin CLI tem (`docs/DEVIN-CLI-REA-ANALYSIS.md` §6, §8) um par **lead/sidekick** persistente — o sidekick (`persistent_subagent` + `affogato/subagent.rs`) acumula memória entre handoffs; o lead delega implementação/verificação/setup mas **retém** julgamento, queries a sistemas compartilhados, trabalho correctness-critical e revisão de diff; handoffs carregam código/não-prosa (snippet exato + comandos verbatim + done-criteria); resultados derivados viram insumos congelados (anti-duplicação); "verify once" no sidekick; escala após 2-3 tentativas; relatório "stand alone". Além disso os profiles `subagent_explore` (read-only + modelo router barato) e `subagent_general` (full + mesmo modelo do pai) com `max-nesting`. O OpenWebUiCode já tem `DelegateTaskBuiltinTool` (child chats síncronos/assíncronos, maxDepth 3, worktree/team/resume/ext-CLI) e `.agent.md` com frontmatter — mas cada dispatch cria contexto novo (sem memória persistente), não há profiles explore/general nem disciplina de handoff/anti-duplicação.

## 2. Scope

**In scope:**

- Profile `explore` (read-only tools + `web_search`; modelo configurável mais barato — `subagents.exploreModel`) e `general` (full tools; herda modelo do pai) como `.agent.md` nativos do sistema.
- `DelegateTaskBuiltinTool`: novos campos `persistent: bool` + `sidekick_id` — sidekick persistente reutiliza o mesmo chat/sub-contexto entre dispatches do mesmo pai na mesma sessão (memória entre handoffs).
- Protocolo de handoff no prompt do lead: seções `## Goal`, `## Frozen inputs` (resultados já derivados, com paths/valores), `## Exact commands`, `## Done criteria`, `## Report contract` (feito + evidência com paths + desvios + pendências).
- `max_nesting` no frontmatter de `.agent.md` (default 0 — filho não cria filho).
- Disciplina no system prompt do modo code: lead delega implementação/verificação/setup; retém julgamento, queries a sistemas compartilhados, trabalho correctness-critical (análise de dados/evals), UI renderizada complexa, conversa com o usuário e revisão de diff antes de "land".
- Relatório do sidekick em formato fixo (standalone) gravado como nota de contexto da run-pai.
- `Ctrl+B`-like: promover dispatch background↔foreground via API (`POST /api/v1/runs/{id}/foreground`) — o usuário pode assumir o controle.

**Out of scope:**

- Subagente com modelo diferente por call além do `exploreModel`/`subagentModel` (por-call `model` no frontmatter já existe).
- Marketplace de agentes (`.agent.md` locais já cobrem o necessário).
- Sidekick entre chats diferentes (persistência é por sessão-pai).

## 3. Technical Context

**Where the change happens:**

`src/OpenWebUI.Infrastructure/ChatTools/Tools/DelegateTaskBuiltinTool.cs` (campos persistent/sidekick_id, reuse de chat filho), `src/OpenWebUI.Infrastructure/agents/` (novos `explore.agent.md`, `general.agent.md`), `src/OpenWebUI.Api/Completions/Contributors/SystemPromptContributors.cs` (disciplina lead + handoff), `src/OpenWebUI.Infrastructure/ChatTools/Tools/ContextSnapshotBuiltinTools.cs` (relatório do sidekick como nota), endpoint de fg/bg nos runs.

**Files to read before implementing:**

- `src/OpenWebUI.Infrastructure/ChatTools/Tools/DelegateTaskBuiltinTool.cs`
- `src/OpenWebUI.Infrastructure/agents/code-reviewer.agent.md`, `researcher.agent.md`
- `src/OpenWebUI.Api/Completions/Contributors/SystemPromptContributors.cs`
- `docs/DEVIN-CLI-REA-ANALYSIS.md` §6, §8
- `src/OpenWebUI.Api/Runs/` (dispatcher, executor, parent run)

**Files to create or modify:**

```text
src/OpenWebUI.Infrastructure/ChatTools/Tools/DelegateTaskBuiltinTool.cs  (persistent/sidekick)
src/OpenWebUI.Infrastructure/agents/explore.agent.md                     (novo)
src/OpenWebUI.Infrastructure/agents/general.agent.md                     (novo)
src/OpenWebUI.Api/Completions/Contributors/SystemPromptContributors.cs   (disciplina lead)
src/OpenWebUI.Api/Endpoints/ChatRunEndpoints.cs                          (fg/bg switch)
tests/OpenWebUI.Api.Tests/DelegateTaskPersistentTests.cs                 (novo)
```

## 4. Requirements

### RF-001: Profiles nativos `explore`/`general`
- **Description:** `explore.agent.md` (tools: `file_read, file_grep, file_glob, web_search, todo_write`; modelo `subagents.exploreModel` ou advisor) e `general.agent.md` (full tools; herda modelo do pai) disponíveis via `delegate_task` `agent` field.
- **Rules:** explore é read-only (não recebe tools de escrita/exec mutável); general herda preset de permissões do pai; ambos respeitam `max_nesting`.

### RF-002: Sidekick persistente por sessão-pai
- **Description:** `delegate_task` com `persistent:true` + `sidekick_id` reutiliza o mesmo chat filho entre dispatches do mesmo pai: o segundo handoff vê o contexto acumulado dos anteriores (memória entre handoffs).
- **Rules:** um sidekick por `sidekick_id` por sessão-pai; o chat filho é criado na primeira chamada e resumido/reusado nas seguintes; `persistent` não se aplica a `ext-CLI`; dispatches consecutivos no mesmo sidekick são serializados (queue).

### RF-003: Handoff disciplinado no prompt do lead
- **Description:** Bloco de system prompt do modo code instrui o lead a montar handoffs com `## Goal`, `## Frozen inputs` (resultados derivados com paths/valores — o sidekick não re-deriva), `## Exact commands` (verbatim) e `## Done criteria`.
- **Rules:** handoff carrega código/não-prosa (snippet exato > descrição); resultados já derivados pelo lead são insumos congelados; múltiplas tarefas relacionadas vão num brief consolidado.

### RF-004: Relatório standalone do sidekick
- **Description:** O resultado do sidekick segue contrato fixo: `## Done` (o que foi feito), `## Evidence` (paths + comandos rodados + saída relevante), `## Deviations` (desvios do plano), `## Pending` (o que falta).
- **Rules:** relatório gravado como nota de contexto da run-pai (`builtin_context_note`) e devolvido no tool result; omissão de seção → o lead pede complemento.

### RF-005: `max_nesting` no frontmatter
- **Description:** `.agent.md` aceita `max_nesting: N` (default 0 — o filho não recebe `delegate_task`); respeita `MaxDepth` global (3).
- **Rules:** `max_nesting` > 0 só permite filhos até N-1 níveis adicionais; nunca excede o limite global.

### RF-006: Disciplina de retenção do lead
- **Description:** Bloco de prompt lista o que o lead **não** delega: julgamento, queries a sistemas compartilhados (texto exato da query é do lead), trabalho correctness-critical (análise de dados/evals), UI renderizada complexa, conversa com o usuário, revisão de diff antes de land.
- **Rules:** revisão de diff no ponto de landing é responsabilidade do lead; escala do sidekick após 2-3 tentativas ou blocker de ambiente/credencial vira handoff de volta com evidência.

### RF-007: Foreground/background switch
- **Description:** `POST /api/v1/runs/{id}/foreground` promove um dispatch background para foreground (o usuário passa a aprovar tools interativamente); `POST /api/v1/runs/{id}/background` faz o inverso.
- **Rules:** fg→bg herda permissões já concedidas no dispatch (não-aprovadas são auto-negadas); bg→fg re-enfileira prompts de aprovação ao usuário.

## 5. API Contract

**Endpoint:** `POST /api/v1/runs/{runId}/foreground` e `POST /api/v1/runs/{runId}/background` — alterna aprovação interativa de um dispatch filho.
**Auth:** JWT do dono da run.

## 6. Acceptance Criteria

- [x] **Given** `delegate_task` com `agent:"explore"`, **when** executado, **then** o filho roda read-only com modelo barato.
- [x] **Given** dois `delegate_task` com mesmo `persistent:true`+`sidekick_id`, **when** o segundo roda, **then** ele vê o contexto do primeiro handoff.
- [x] **Given** `.agent.md` com `max_nesting:0`, **when** o filho tenta `delegate_task`, **then** recebe erro estruturado.
- [x] **Given** sidekick retorna sem `## Evidence`, **when** o lead processa, **then** o contrato de relatório é enforçado (prompt instrui pedir complemento).
- [x] **Given** dispatch em background, **when** `POST /foreground`, **then** prompts de aprovação passam a ir ao usuário.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| `persistent` sem `sidekick_id` | persistent:true sozinho | id gerado automaticamente |
| Dois sidekicks mesmo id em paralelo | dispatches simultâneos | serializados em fila |
| Sidekick filho falha | run-filho erro | relatório de erro estruturado ao pai |
| `max_nesting` negativo | -1 | tratado como 0 |

## 7. Task Plan

- [x] **T1 — Discovery:** ler `DelegateTaskBuiltinTool`, `.agent.md` loader, `SystemPromptContributors`, runs endpoints.
- [x] **T2 — Profiles:** `explore.agent.md` + `general.agent.md` com tools/modelo.
- [x] **T3 — Persistência:** `persistent`+`sidekick_id` com reuse de chat filho + serialização.
- [x] **T4 — Prompts:** blocos lead-discipline + handoff + report-contract nos contributors.
- [x] **T5 — `max_nesting`:** frontmatter + enforcement.
- [x] **T6 — fg/bg:** endpoints de alternância.
- [x] **T7 — Testes:** unitários (profile load, persistência, nesting, relatório) + integração (dispatch persistente).
- [x] **T8 — Done + PR.**

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **.NET** | Unit tests (profile loader, persistent reuse, max_nesting, report contract); integration tests (dispatch persistente, fg/bg); 80% coverage. |

## 8. Organization Guardrails

- **Branches:** `feature/devin-20261031-sidekick-orchestration`.
- **Security:** sidekick herda permissões do pai; explore é read-only; max_nesting previne runaway.
- **Scope:** sem marketplace; sem sidekick cross-chat.
- **Architecture:** profiles são dados `.agent.md`; persistência vive no `DelegateTaskBuiltinTool`.

## 9. Definition of Done

- [x] RFs implementados; aceite coberto por testes.
- [x] Edge cases tratados; `dotnet test` verde; `check-spec-status.py` ok.
- [x] Guardrails respeitados; logs sem PII.

## Open Questions / Pending Ambiguity

- `persistent` default true ou false? (recomendo opt-in — `persistent:true` explícito, sidekick_id auto-gerado).
- O relatório standalone deve ser enforçado por parser rígido ou só prompt? (recomendo prompt-only no MVP; parser na fase 2).

## Notas de implementação (branch feature/devin-20261031-sidekick-orchestration)

- RF-002 `persistent`/`sidekick_id`: kv `r:{parentRun}:sidekick:{id}` →
  chat filho; `SidekickGate` (semaphore por par) serializa dispatches;
  2º+ handoff vai por `ResumeInExistingChatAsync`. Sidekick_id omitido →
  auto-gerado (`sk-<guid20>`). Não se aplica a ext:* (early-return antes).
- RF-005 `max_nesting` aceito como alias de `maxDepth` (clamp 0..3;
  negativo→0). Enforcement já existente via kv `agent.depth`+`agent.spawns`.
- RF-007 fg/bg: `POST /{chatId}/runs/{runId}/foreground|background`
  ajusta `Chat.ApprovalPreset` — fg=`approve-mutations` (interativo),
  bg=`allow-readonly` (mutações auto-negadas). Mapeamento documentado:
  "não-aprovadas auto-negadas" = allow-readonly.
- RF-004 relatório standalone: enforcement prompt-only (open question —
  parser rígido ficou para fase 2); contrato no DelegationPolicyContributor
  + nos agent.md explore/general.
- RF-001 profiles builtin: `explore.agent.md` (read-only + web_search +
  todo_write, max_nesting 0) e `general.agent.md` (tools [*], max_nesting 0).


## Delivered

- PR #595 — implementação e testes mergeados em `main` (2026-11-01).
