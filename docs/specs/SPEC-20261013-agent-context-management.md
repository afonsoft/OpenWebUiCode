# SPEC-20261013-agent-context-management

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `agent-context-management` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261013-agent-context-management` |
| Ticket | [#353](https://github.com/afonsoft/open-webui/issues/353) |
| Status | `Completed` |

## 1. User Story

**As a** agente executando uma run longa
**I want** marcar um checkpoint de exploração e depois "rewind" colapsando esse trecho num relatório, manter um notebook persistente que sobrevive à compaction, e pedir rollover de contexto quando julgar necessário
**So that** explorações verbosas (leituras, greps, tentativa-e-erro) não inflem a janela e o contexto carregue só o essencial.

**Problem context:**
Nossa compaction (`ContextCompactionService`) é só automática — dispara quando o histórico estoura e resume tudo. oh-my-pi entrega o gerenciamento ao próprio modelo: `checkpoint(goal)`/`rewind(report)` como par de tools (marca → explora → colapsa), `context_notes` (notebook persistente por branch que sobrevive a rollover) e `new_context` (o agente pede a compactação). Sem isso, runs exploratórias gastam janela com ruído que o modelo já sabe descartar.

## 2. Scope

**In scope:**
- Par de builtins `checkpoint`/`rewind`: `checkpoint(goal)` marca a posição atual da conversa na run; `rewind(report)` fecha o trecho desde o último checkpoint — mensagens persistidas ficam, mas o contexto reconstruído substitui o intervalo pelo `report`.
- `context_notes` builtin: sem `text` lê o notebook; com `text` sobrescreve (replace, não append); notebook por chat persistido em kv e injetado no system context de toda run — sobrevive a compaction e reload.
- `new_context` builtin: o agente pede compactação imediata — força `MaybeCompactAsync` agora em vez de esperar o limiar.
- Gate de feature: setting admin `EnableContextTools` (default on para builds; ou por config — decidir) + anúncio das tools só quando ligado.
- UI: marcador visual sutil nos pontos de checkpoint/rewind (linha "contexto colapsado" estilo compaction atual) — reusa o padrão da entrada `Role="compaction"`.

**Out of scope:**
- Snapcompact (histórico→bitmap) e compaction-v2-streaming do omp — nossa compaction atual permanece o motor.
- Notebook por branch (não temos branches ainda — ver SPEC chat-branching; aqui é por chat).
- Edição do notebook pela UI (read-only na UI em v1; o agente escreve).
- Selective compaction por sub-seção.

## 3. Technical Context

**Where the change happens:**
Novas tools em `ChatTools/Tools/` (`CheckpointBuiltinTool`, `ContextNotesBuiltinTool`, `NewContextBuiltinTool`). Estado por chat em kv via `ConfigService`: `chat:{id}:context-boundaries` = `[{fromPos, toPos, report}]` e `chat:{id}:context-notes` = string. Rebuild do contexto (enqueue em `ChatRunEndpoints` e `PersistAsync`/histórico legado) filtra intervalos cobertos por boundaries substituindo pelo report — reusa o ponto onde `ContextCompactionService.ApplyAsync` já reescreve o head. Checkpoint/rewind executam dentro da run (têm `BuiltinToolContext` com ChatId/RunId) e só atuam sobre mensagens persistidas — portanto precisam do fluxo detached-run (mensagens persistidas por posição); em playground/legado, devolvem "indisponível neste modo".

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs` (ApplyAsync, kv pattern, marcador `Role="compaction"`)
- `src/OpenWebUI.Infrastructure/Services/ConfigService.cs` (kv por usuário/chat)
- `src/OpenWebUI.Api/Endpoints/ChatRunEndpoints.cs` (rebuild do histórico por `Position`, `EnqueueMessageAsync`)
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs` (montagem de mensagens/system)
- `src/OpenWebUI.Infrastructure/ChatTools/BuiltinToolRegistry.cs` + `IBuiltinChatTool.cs` + um tool simples de referência (`TodoWriteBuiltinTool.cs`)
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` (tool loop, onde o estado da run vive)

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/ChatTools/Tools/CheckpointBuiltinTool.cs    # NOVO checkpoint+rewind (par)
src/OpenWebUI.Infrastructure/ChatTools/Tools/ContextNotesBuiltinTool.cs  # NOVO context_notes
src/OpenWebUI.Infrastructure/ChatTools/Tools/NewContextBuiltinTool.cs    # NOVO new_context
src/OpenWebUI.Infrastructure/Services/ContextBoundaryService.cs          # NOVO kv boundaries + filtro de contexto
src/OpenWebUI.Api/Endpoints/ChatRunEndpoints.cs                          # aplica boundaries no rebuild
src/OpenWebUI.Api/Completions/ChatPipeline.cs                            # injeta notebook no system
src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs        # ponto de forced-compaction p/ new_context
src/OpenWebUI.Application/Contracts/ConfigContracts.cs                   # EnableContextTools (admin)
src/OpenWebUI.Client/Components/MessageBubble.razor / ChatView.razor     # marcador visual (se aplicável)
src/OpenWebUI.Client/wwwroot/i18n/*.json
tests/OpenWebUI.Api.Tests/                                               # filtro de contexto, par checkpoint/rewind, notes, new_context
```

## 4. Requirements

### RF-001: Par checkpoint/rewind
- **Description:** `checkpoint(goal)` registra a posição (`Position`) da última mensagem na run + `goal`; `rewind(report)` exige checkpoint aberto — grava boundary `{fromPos, toPos=última, report}`, marca o checkpoint como fechado e injeta o report como mensagem system `[contexto colapsado]`.
- **Rules:** só 1 checkpoint aberto por run (segundo `checkpoint` sem `rewind` devolve erro instruindo o modelo); `report` vazio → erro; checkpoints de runs passadas fechadas continuam cobrindo seu intervalo em rebuilds futuros; rewind sobre intervalo vazio → erro suave.
- **Input → Output:** tool calls → boundary persistida → contexto futuro sem o intervalo.

### RF-002: Filtro de rebuild
- **Description:** Na montagem do histórico (`EnqueueMessageAsync` e caminho legado), mensagens com `Position` dentro de boundaries fechadas são substituídas por uma mensagem system única com o `report`.
- **Rules:** boundaries só cobrem mensagens persistidas do chat (não atravessam chats filhos); mensagem em boundary aberta (checkpoint sem rewind — run morreu) permanece intacta; ordenação preservada.

### RF-003: `context_notes`
- **Description:** Notebook por chat (kv `chat:{id}:context-notes`); `context_notes` sem `text` lê, com `text` sobrescreve; conteúdo injetado no system prompt como seção "Notas de contexto persistentes".
- **Rules:** cap ~8k chars por escrita; injeção omitida quando vazio; sobrevive a compaction (não entra no resumo — é sibling).

### RF-004: `new_context`
- **Description:** Solicita compactação imediata: agenda `MaybeCompactAsync` forçado na próxima fronteira da run (ou executa no fim do tool call).
- **Rules:** sem args; idempotente por turno; resposta ao modelo confirma "nova janela pedida"; não apaga o notebook.

### RF-005: Gate e anúncio
- **Description:** As 3 tools só são anunciadas/registradas quando `EnableContextTools` (admin) está on; `checkpoint`/`rewind` são par — pedir um inclui o outro.
- **Rules:** em playground/legado sem persistência de posição, `checkpoint` devolve erro explicando o requisito — nunca crash.

**Business rules / invariants:**
- Mensagens persistidas nunca são deletadas — boundaries são filtros de contexto, não destruição.
- Boundary só cobre mensagens do próprio chat (sem atravessar delegate/subruns).
- Notebook é texto livre do agente — sanitize só de tamanho; o modelo é autor e leitor (mesma confiança que tool results).

## 5. API Contract

Sem endpoints novos. Tools `builtin:checkpoint`, `builtin:rewind`, `builtin:context_notes`, `builtin:new_context`; SSE reusa `tool_call`/`tool_result` existentes.

**Expected errors (tool-level, viram tool_result ao modelo):** checkpoint duplo aberto; rewind sem checkpoint; report vazio; modo não persistido.

## 6. Acceptance Criteria

- [ ] **Dado** run com `checkpoint` + 10 mensagens + `rewind(report)` **quando** o próximo turno monta contexto **então** o intervalo some e só o report aparece.
- [ ] **Dado** boundary fechada **quando** recarrego e envio nova mensagem **então** o rebuild do servidor continua filtrando o intervalo.
- [ ] **Dado** `rewind` sem `checkpoint` aberto **quando** chamado **então** tool_result explica o erro e a run segue.
- [ ] **Dado** `context_notes` com texto **quando** a próxima run monta o system **então** as notas aparecem; **quando** compaction dispara **então** as notas persistem intactas.
- [ ] **Dado** `new_context` chamado **quando** a run continua **então** a compaction roda na fronteira seguinte (marcador `compaction` aparece).
- [ ] **Dado** `EnableContextTools=false` **quando** o modelo chama `checkpoint` **então** a tool não estava anunciada / call devolve indisponível.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Run morre com checkpoint aberto | boundary aberta | intervalo intacto; boundary aberta ignorada |
| Boundaries sobrepostas | rewind cobrindo boundary já fechada | união dos intervalos; reports concatenados em ordem |
| Notebook gigante | write >8k chars | erro de tamanho ao modelo |
| Playground sem persistência | checkpoint | tool_result "requer chat persistido" |
| Report contendo PII | texto livre | mesmo tratamento de qualquer tool result — sem log extra |

## 7. Task Plan

- [ ] **T1 — Discovery:** ler seção 3; confirmar como `Position` e rebuild funcionam no caminho detached vs legado.
- [ ] **T2 — `ContextBoundaryService`:** kv + API `Mark/Commit/List/Filter` + filtro aplicado no rebuild.
- [ ] **T3 — Tools:** checkpoint/rewind (par), context_notes, new_context; registro/gate no registry.
- [ ] **T4 — Pipeline:** injeção do notebook + forced compaction hook.
- [ ] **T5 — Marcador visual + i18n.**
- [ ] **T6 — Testes NUnit:** lifecycle do par, filtro de rebuild, notes persistentes, gate off.
- [ ] **T7 — Validation + DoD + PR.**

**7.1 Validation:** NUnit unit (boundary math, caps) + integration (enqueue→rebuild filtrado); ≥80%.

## 8. Organization Guardrails

- Branch `feature/Devin-20261013-agent-context-management`.
- Nunca deletar mensagens persistidas; boundaries são projeção.
- Gate admin default: decidir on/off na implementação (palpite: on, tools read/write-tiered conforme SPEC approval-tiers se já existir).
- Sem dependência nova.

## 9. Definition of Done

- [ ] RF-001..RF-005 implementados/testados.
- [ ] Contexto reconstruído provado menor pós-rewind (teste).
- [ ] build/test verde; nenhum efeito quando gate off.

**Next action after DoD:** `Status = Done` + PR.

## Open Questions / Pending Ambiguity

- `EnableContextTools` default on ou off? Palpite: on (tools são auto-contidas; quem decide é o modelo).
- Notebook visível na UI (aba no painel lateral)? Palpite: v2 — v1 só via tool.

## Delivered

| Item | Link |
| --- | --- |
| Commit | https://github.com/afonsoft/open-webui/commit/1110f9e6c |
| Issue | https://github.com/afonsoft/open-webui/issues/353 |
