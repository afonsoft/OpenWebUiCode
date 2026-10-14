# SPEC-20261013-chat-branching

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-branching` |
| Type | `Epic` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261013-chat-branching` |
| Ticket | [#354](https://github.com/afonsoft/open-webui/issues/354) |
| Status | `Draft` |
| Child SPECs (prováveis) | `chat-branching-schema`, `chat-branching-ux`, `branch-summary` |

## 1. User Story

**As a** usuário do chat
**I want** criar um ramo a partir de qualquer mensagem do histórico — explorar um caminho alternativo sem perder o original — e navegar entre os ramos do mesmo chat
**So that** conversas viram uma árvore (como o session tree do oh-my-pi: cada entry tem `parentId`, a posição ativa é um leaf, `/fork` ramifica e `branch_summary` resume o ramo abandonado).

**Problem context:**
Hoje `ChatMessage` é uma lista linear ordenada por `Position`; `VersionsJson` cobre apenas variantes de **uma** mensagem, não subárvores. Editar/antiga mensagem e resender perde o caminho anterior; "começar outra conversa dali" exige duplicar manualmente. Isso é a maior diferença estrutural de produto vs oh-my-pi — e é um Epic porque toca schema, rebuild de contexto, UI de navegação e migração de dados legados.

## 2. Scope

**In scope (Epic — v1 funcional):**
- `ChatMessage.ParentId` (nullable) → os nós do chat formam uma árvore; `Position` permanece para ordenação estável dentro do ramo.
- `Chat.LeafMessageId` → leaf do ramo ativo; rebuild de histórico caminha leaf→root por `ParentId` (em vez de `Position` linear).
- Ação "ramificar daqui" em qualquer mensagem: move o leaf para aquele nó e a próxima mensagem enviada cria um filho-irmão (novo ramo).
- Lista de ramos: endpoint + popover na UI mostrando os leafs divergentes (nº de mensagens, último trecho, timestamp); trocar de ramo muda `LeafMessageId` e re-renderiza o caminho.
- `branch_summary`: ao criar/trocar de ramo, gera resumo do ramo abandonado via `IContextSummarizer` (papel `memory` se SPEC model-roles existir) — exibido no popover e opcionalmente injetado como nota no novo ramo.
- Migração: dados legados (ParentId null) tratados como cadeia linear por `Position` — sem rewrite destrutivo.

**Out of scope (fases futuras do Epic):**
- Visualização gráfica da árvore (render tipo `/tree` do omp) — v1 é lista de ramos.
- Merge de ramos, cherry-pick de mensagens entre ramos.
- Fork para chat novo (temos `ParentChatId` p/ delegação — pode ser evoluído depois; in-chat cobre o caso principal).
- Branching em chats de subagentes/jobs.
- Diff lado a lado de ramos.

## 3. Technical Context

**Where the change happens:**
Schema: `ChatMessage.ParentId` + `Chat.LeafMessageId` (migration EF; legado ParentId null). Rebuild: `ChatRunEndpoints.EnqueueMessageAsync` e `ChatEndpoints` (load do chat) hoje ordenam por `Position` — passam a caminhar `LeafMessageId→root` via ParentId (com fallback linear para ParentId null, tratando legado e garantindo que chat antigo funcione idêntico). Envio: nova mensagem grava `ParentId=LeafMessageId` e atualiza o leaf. `Versions` permanece por mensagem (ortogonal). `ContextCompactionService` e boundaries (SPEC agent-context-management, se merged) precisam operar sobre o caminho do leaf ativo, não sobre Position absoluta — registrar como invariante. Client: ação por mensagem em `MessageBubble`, popover de ramos, indicador de ramo ativo; `Chat.GetChats`/`GetChat` devolvem `branchCount`/`leafs` summary.

**Files to read before implementing:**
- `src/OpenWebUI.Domain/Entities.cs` (`ChatMessage.Position`, `Chat`, `VersionsJson`)
- `src/OpenWebUI.Api/Endpoints/ChatRunEndpoints.cs` (`EnqueueMessageAsync` — rebuild por Position)
- `src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs` (load/serialize de mensagens, edit flow)
- `src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs` (cutoff por posição — interage com caminho de leaf)
- `src/OpenWebUI.Client/Components/ChatView.razor` (`_messages`, edit/regenerate flow)
- `src/OpenWebUI.Client/Components/MessageBubble.razor` (ações por mensagem)
- `src/OpenWebUI.Application/Contracts/ChatContracts.cs` (`ChatMessageModel`, `ChatModel`)
- `src/OpenWebUI.Infrastructure/Services/ContextSummarizer.cs` (`IContextSummarizer` p/ branch_summary)
- migration mais recente em `src/OpenWebUI.Infrastructure/Migrations/` (template de schema change)

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities.cs                        # ChatMessage.ParentId, Chat.LeafMessageId
src/OpenWebUI.Infrastructure/Migrations/*_ChatBranching*# migration
src/OpenWebUI.Application/Contracts/ChatContracts.cs    # ParentId/BranchSummary em ChatMessageModel; branches em ChatModel
src/OpenWebUI.Application/Contracts/ChatRunContracts.cs # opcional: ForkFromMessageId no enqueue
src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs            # GET /chats/{id}/branches; POST .../activate-leaf
src/OpenWebUI.Api/Endpoints/ChatRunEndpoints.cs         # rebuild por caminho de leaf; novo msg → ParentId
src/OpenWebUI.Infrastructure/Services/BranchService.cs  # NOVO: walk leaf→root, leafs divergentes, summaries
src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs # cutoff relativo ao caminho ativo
src/OpenWebUI.Client/Components/ChatView.razor          # ação ramificar, popover de ramos, indicador
src/OpenWebUI.Client/Components/MessageBubble.razor     # botão "ramificar daqui"
src/OpenWebUI.Client/Services/ApiService.cs             # GetBranches/ActivateLeaf
src/OpenWebUI.Client/wwwroot/i18n/*.json
tests/OpenWebUI.Api.Tests/                              # walk, fork, legado linear, boundary×leaf
```

## 4. Requirements

### RF-001: Modelo de árvore
- **Description:** `ChatMessage.ParentId` referencia a mensagem anterior no ramo (null = raiz ou legado); `Chat.LeafMessageId` aponta o leaf ativo; cada envio grava `ParentId` do leaf corrente e move o leaf.
- **Rules:** ciclos impossíveis por construção (ParentId sempre aponta para mensagem já persistida do mesmo chat); `LeafMessageId` nunca aponta para mensagem de outro chat; migração legada = cadeia implícita por `Position` (não requer backfill obrigatório).
- **Input → Output:** enqueue → nó novo na árvore + leaf atualizado.

### RF-002: Rebuild por caminho de leaf
- **Description:** Todo carregamento de histórico (GET chat, enqueue, compaction, boundaries) caminha `LeafMessageId→root`; ParentId null em nó intermediário continua a subida por `Position` (compatibilidade legada).
- **Rules:** resultado idêntico ao atual para chats sem branching; mensagens fora do caminho ativo **não** entram no contexto do modelo; `Role="compaction"`/boundaries valem só se pertencerem ao caminho.

### RF-003: Ramificar
- **Description:** "Ramificar daqui" em qualquer mensagem: ativa aquele nó como leaf-pai (a mensagem seguinte pendura ali, criando sibling); o ramo anterior permanece intacto e listável.
- **Rules:** ramificar não deleta nada; enviar no novo ramo cria o filho e move `LeafMessageId`; UI sinaliza "você está num ramo alternativo" com caminho para voltar.

### RF-004: Lista de ramos
- **Description:** `GET /api/v1/chats/{id}/branches` devolve leafs divergentes `{leafId, depth, messageCount, preview, updatedAt, isActive}`; popover na UI permite ativar outro leaf (`POST activate-leaf`) → re-render com o caminho correspondente.
- **Rules:** só leafs (pontas); ramo ativo marcado; previews truncados ~80 chars; sem expor mensagens de outros usuários (ownership atual).

### RF-005: `branch_summary`
- **Description:** Ao sair de um ramo com ≥4 mensagens, gera resumo via `IContextSummarizer` (modelo do papel `memory`, fallback chat) e guarda por leaf (`BranchSummaryJson` no leaf ou kv `chat:{id}:branch:{leafId}`); popover mostra o resumo; opção "injetar resumo do ramo anterior" adiciona nota system no novo caminho.
- **Rules:** resumo é metadata — falha → sem resumo, sem erro; regenera se o ramo ganhar mensagens novas depois.

**Business rules / invariants:**
- Nada é deletado ao ramificar/trocar de leaf; detach de mensagem permanece operação explícita existente.
- Compaction/boundaries/`context_notes` operam sobre o caminho do leaf ativo — nunca sobre a árvore inteira.
- Delegate/subagente (ParentChatId) é ortogonal — forks de ramo não criam chats novos.

## 5. API Contract

**`GET /api/v1/chats/{id}/branches`** → `{branches:[{leafId,depth,messageCount,preview,summary?,updatedAt,isActive}]}`.
**`POST /api/v1/chats/{id}/branches/activate`** → body `{leafId}`; seta `LeafMessageId`, devolve `ChatModel` do caminho ativo.
**`ChatMessageModel`** ganha `parentId`; **`ChatModel`** ganha `activeLeafId` + `branchCount`.
**`EnqueueChatRunRequest`** (opcional) ganha `forkFromMessageId` — quando presente, grava `ParentId` ali em vez do leaf.

**Expected errors:** `401`; `404` chat/leaf inexistente ou alheio; `400` leaf de outro chat — `{detail}` atual.

## 6. Acceptance Criteria

- [ ] **Dado** chat com 10 msgs **quando** ramifico da msg 5 e envio "alternativa" **então** novo sibling existe, leaf move, e o modelo recebe só msgs 1-5 + alternativa.
- [ ] **Dado** dois ramos **quando** `GET branches` **então** ambos os leafs listados; ativo marcado; trocar de leaf re-renderiza o caminho certo.
- [ ] **Dado** chat legado (sem ParentId) **quando** carrego e envio **então** comportamento idêntico ao atual; ParentId do novo nó aponta para o último.
- [ ] **Dado** ramo abandonado com ≥4 msgs **quando** troco de leaf **então** resumo é gerado e aparece no popover; summarizer falhando → lista segue sem resumo.
- [ ] **Dado** compaction existente no ramo ativo **quando** rebuild roda **então** cutoff/boundary valem só para o caminho ativo.
- [ ] **Dado** tentativa de ativar leaf de outro chat/usuário **quando** POST activate **então** 404/403, sem mudança.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Ramificar da raiz | leaf=msg1 | novo caminho só com a nova mensagem |
| Leaf deletado | delete na ponta | leaf volta ao pai; branches recalcula |
| Fork em mensagem com `Versions` | msg com variantes | o filho pendura na versão ativa |
| Chat share (read-only público) | GET branches | incluído no payload se share expõe; sem activate (read-only) |
| Race: dois envios simultâneos | enqueue×2 | último leaf vence; ambos os nós persistem (siblings) |

## 7. Task Plan (Epic → fases)

- [ ] **T1 — Discovery:** ler seção 3; mapear todos os pontos que assumem `Position` linear (enqueue, GET chat, compaction, boundaries, busca).
- [ ] **T2 — Schema:** `ParentId`, `LeafMessageId`, migration, backfill opcional para chats recentes.
- [ ] **T3 — `BranchService`:** walk leaf→root com fallback legado; lista de leafs; ativação; summary hook.
- [ ] **T4 — Endpoints + contratos:** branches/activate, `parentId` nos models, enqueue por caminho.
- [ ] **T5 — Compatibilidade:** compaction e boundaries reescritos para "caminho ativo"; regressão de chats lineares.
- [ ] **T6 — Client:** ação ramificar em MessageBubble, popover de ramos, indicador de ramo, summaries; i18n ×8.
- [ ] **T7 — Testes:** walk/fork/legado/ownership/compaction-no-caminho; teste de migração.
- [ ] **T8 — Validation + DoD + PR** (PR grande — considerar sub-SPECs por fase: schema+rebuild → UX → summaries).

**7.1 Validation:** NUnit unit (walk, leafs, compat legado) + integration (enqueue branched, activate); teste explícito de migração sobre base legada; ≥80%.

## 8. Organization Guardrails

- Branch `feature/Devin-20261013-chat-branching`; Epic — PR pode ser dividido em fases (schema → endpoints → UX).
- Nunca destrutivo: ramificar não deleta; migração preserva ordem legada.
- Compaction/boundaries/memory valem por caminho ativo — invariante registrado para SPECs dependentes.
- Sem mudança de comportamento para chats que nunca ramificam.

## 9. Definition of Done

- [ ] RF-001..RF-005 implementados/testados.
- [ ] Chat legado 100% compatível (teste de migração).
- [ ] build/test/drift guards verdes.
- [ ] Registrado como base para fases futuras (tree visual, merge, fork-to-chat).

**Next action after DoD:** `Status = Done` + PR; abrir filhas se o PR ficar grande demais.

## Open Questions / Pending Ambiguity

- v1 in-chat tree (esta SPEC) vs fork-to-new-chat mais simples — escolhi in-chat (espelha omp e não prolifera chats); se o custo de UX assustar, fase 1 pode ser só schema+rebuild sem popover.
- Resumo por leaf em coluna vs kv — palpite: kv (`chat:{id}:branch:{leafId}`) para não inflar a entidade.
- `Versions` × branching: filho pendura na versão ativa — confirmar se é o comportamento esperado.
