# SPEC-20261106-adaptive-fusion-mode

## 0. Metadata

| Field | Value |
|-------|-------|
| Feature | Modo Adaptive/Fusion no seletor de modelos — Leader orquestra, Sidekick executa via sub-agentes em team |
| Type | Feature |
| Stack | .NET 10 / Blazor WASM |
| Repository | afonsoft/OpenWebUiCode |
| Branch | `feature/devin-20261106-adaptive-fusion` |
| Status | `Completed` |

## 1. Análise — Devin Fusion (fontes)

- `docs.devin.ai/cli/fusion`: Fusion pareia um **lead** frontier (planeja, raciocina,
  revisa) com um **sidekick** econômico (executa o plano — escreve código, roda
  builds/testes, verifica). No picker: família **Fusion** → Lead, Effort, Sidekick.
- `cognition.com/blog/devin-fusion`: dois agentes paralelos, cada um com toolset e
  contexto cacheado próprios. O lead delega e monitora — retém julgamento,
  interpretação de ambiguidade e revisão final. Handoffs disciplinados;
  roteamento dinâmico mid-session.
- `therundown.ai/tools/devin-fusion`: não é produto separado — é um harness de
  roteamento dentro do agente; delegação context-aware sem chamadas frias.

## 2. Análise — este repositório (o que já existe)

| Peça Fusion | Já existe |
|---|---|
| Sub-agente persistente | `builtin:delegate_task` `persistent:true` + `sidekick_id` (SPEC-20261031) |
| Profiles | `explore.agent.md` (read-only) / `general.agent.md` (full) |
| Comunicação entre sub-agentes | `builtin:team_task`/`team_message`/`team_status` + `Chat.TeamId`/`TeamRole` (SPEC-20261021) |
| Modelos virtuais | prefixos `pipeline:{id}`, `arena:{model}`, `combo:{id}` no executor |
| UI de sub-conversa | `SubConversationPanel` + `ToolCallCard` (thread do filho inline) |
| Modelo do filho | `model:` explícito > `role:` > `parentRun.Model` (DelegateTaskBuiltinTool) |
| Disciplina lead/sidekick | `DelegationPolicyContributor` + `AgentDisciplineContributor` |

## 3. Design

**Ref de modelo:** `fusion:{leaderId}>{sidekickId}` — id composto que viaja no
campo `Model` existente (mesmo padrão `combo:`/`arena:`). Zero mudança de
contrato; o pareamento persiste por chat em `ModelsJson` e em `webui.model`.

**Fluxo:**

```
ModelSelector (UI) ─ Value "fusion:l>s" ─→ EnqueueChatRunRequest.Model
    → ChatRunExecutor.ExecuteAttemptAsync
        → ApplyFusionPairingAsync:
            kv r:{run}:fusion.{leader,sidekick}
            request.Model = leader
            + system prompt de orquestração fusion
            + garante builtin:delegate_task/team_*/agent_catalog/todo_write
            + budget check nos dois modelos
    → Leader roda o tool loop; delegate_task cria sub-agentes
        → ResolveChildModelAsync: default = kv fusion.sidekick
        → team:true → membros conversam via team_message/team_task
```

**Degradados:** playground `/api/chat/completions` e tasks auxiliares
(título/follow-ups/tags) resolvem `fusion:` → leader.

## 4. Task Plan

- [x] T1 — Discovery: ModelSelector, ChatView, ChatRunExecutor, DelegateTaskBuiltinTool, gates.
- [x] T2 — `FusionModelRef` (Application.Contracts): Prefix/TryParse/Format/LeaderOrSelf.
- [x] T3 — Executor: `ApplyFusionPairingAsync` (kv + rewrite + prompt + tools + budget).
- [x] T4 — `DelegateTaskBuiltinTool`: default do filho = sidekick do par fusion.
- [x] T5 — Playground (`ApiEndpoints`) + `TaskEndpoints`: strip → leader.
- [x] T6 — `ModelSelector.razor`: item Adaptive/Fusion + selects Leader/Sidekick.
- [x] T7 — `ChatView.razor`: modelo efetivo das tasks auxiliares = leader.
- [x] T8 — i18n (8 locales) + testes + drift guards.
- [x] T9 — E2E com mock Ollama + screenshots + README.
