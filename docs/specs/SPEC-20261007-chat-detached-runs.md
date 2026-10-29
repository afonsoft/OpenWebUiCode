# SPEC-20261007-chat-detached-runs

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-detached-runs` (P1 da série harness-chat) |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `devin/1791337561-chat-detached-runs` |
| Ticket | `GAP-chat-detached-runs` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** que a resposta do modelo continue sendo gerada no servidor mesmo depois de fechar a aba/recarregar a página
**So that** respostas longas e loops de tools não se percam com a conexão do navegador.

**Problem context:**
Hoje `POST /api/chat/completions` (`ApiEndpoints.ChatCompletionsAsync`) amarra a geração ao `RequestAborted` do HTTP — fechar a aba mata a run e a resposta se perde. A persistência do turno inteiro é feita pelo cliente (`ChatView.PersistAsync` → `POST /api/v1/chats/{id}` com `RemoveRange` + reinsert), então qualquer interrupção deixa o chat sem a resposta do assistant. O agent-harness resolve com `ChatRun` (agregado com estado Queued→Running→Completed), `ChatRunDispatcherService` (fila FIFO por conversa) e broadcaster com replay por `seq` — o navegador só "anexa" na run.

## 2. Scope

**In scope:**
- Entidade `ChatRun` (ChatId, UserId, Model, Status, PartialContent, InputJson, Timestamps, Error).
- `ChatRunDispatcher` (BackgroundService): fila FIFO por chat, `MaxConcurrent` global, sweep no boot que marca runs órfãs como `Interrupted`.
- `ChatRunBroadcaster`: fan-out por run com eventos `seq`-numerados + replay gap-free para attach tardio.
- Endpoints aditivos: `POST /api/v1/chats/{chatId}/messages` → `202 {runId}`; `GET /api/v1/chats/{chatId}/runs/{runId}/stream` (SSE attach); `POST /api/v1/chats/{chatId}/runs/{runId}/stop`; `GET /api/v1/chats/{chatId}/runs` (lista/estado).
- Persistência server-side por iteração (delta a delta é caro — checkpoint a cada N chars e a cada boundary de tool).
- Cliente: `SendAsync` enfileira e anexa no stream; `StopStreaming` chama o endpoint de stop; ao abrir um chat com run ativa, anexa automaticamente.
- `/api/chat/completions` (shape OpenAI) continua funcionando para paridade com o upstream.

**Out of scope:**
- Notificações (P2 — `SPEC-20261007-chat-notifications`).
- Streaming de tool_call/tool_result visível (P3 — `SPEC-20261007-chat-tool-streaming`); a run já persiste tool messages internamente, só não as emite como evento.
- Steering, fork, compaction, schedules (P4 posterior).
- Isolamento multi-instância (dispatcher é in-process; backplane Redis não cobre runs).

## 3. Technical Context

**Where the change happens:**
`Domain` (ChatRun + enum), `Infrastructure` (EF config, migration, dispatcher, broadcaster, executor que encapsula o pipeline atual), `Api` (endpoints novos), `Client` (`ChatRunStreamService`, `ChatView`).

**Files to read before implementing:**
- `src/OpenWebUI.Api/Endpoints/ApiEndpoints.cs` (ChatCompletionsAsync 155-267, RunToolLoopAsync 450-484, EnrichRequestAsync 487-649)
- `src/OpenWebUI.Client/Components/ChatView.razor` (SendAsync 578-645, StreamAssistantAsync 750-801, PersistAsync 895-910)
- `src/OpenWebUI.Client/Services/ChatStreamService.cs`
- `src/OpenWebUI.Domain/Entities.cs` (Chat, ChatMessage)
- Referência: `repos/agent-harness` `ChatRun.cs`, `ChatRunDispatcherService.cs`, `ChatRunBroadcaster.cs`

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities.cs (+ChatRun)
src/OpenWebUI.Infrastructure/Data/AppDbContext.cs (+DbSet, config)
src/OpenWebUI.Infrastructure/Migrations/*AddChatRuns*
src/OpenWebUI.Infrastructure/Chat/{ChatRunDispatcher,ChatRunBroadcaster,ChatRunExecutor}.cs
src/OpenWebUI.Api/Endpoints/ChatRunEndpoints.cs, Program.cs (map + DI)
src/OpenWebUI.Client/Services/ChatRunStreamService.cs
src/OpenWebUI.Client/Components/ChatView.razor (attach/send/stop)
tests/OpenWebUI.Api.Tests/ChatRunEndpointsTests.cs
```

## 4. Requirements

### RF-001: Enfileirar mensagem → run
- **Description:** `POST /api/v1/chats/{chatId}/messages` valida dono do chat, persiste a mensagem do usuário, cria `ChatRun` `Queued` com snapshot do request (model, params, toolIds, fileIds) e devolve `202 {runId, status}`.
- **Rules:** dono do chat ou admin; chat inexistente → 404; fila FIFO por chat (nova mensagem durante run ativa enfileira).
- **Input → Output:** `{content, model?, params?, toolIds?, fileIds?}` → `202 {runId}`

### RF-002: Dispatcher desacoplado
- **Description:** `BackgroundService` consome a fila, marca `Running`, executa o pipeline existente (enrich → arena/pipeline redirect → tool loop → stream) num `IServiceScope` próprio, finaliza `Completed|Failed|Stopped`.
- **Rules:** `MaxConcurrentRuns = 4`; token da run independe do HTTP do cliente; exceção → `Failed` + `Error`.
- **Input → Output:** run `Queued` → eventos + mensagem assistant persistida

### RF-003: Attach SSE com replay
- **Description:** `GET .../runs/{runId}/stream` envia backlog por `seq` desde `LastSeq` (query) e depois o stream ao vivo; `data:` carrega `{seq, type, delta?}` no shape OpenAI (`choices[0].delta.content`) para o cliente reaproveitar o parser.
- **Rules:** sem `seq` gap — cliente com `LastSeq` recebe tudo que perdeu; fim de stream = evento `done` + `data: [DONE]`; run inexistente/de outro usuário → 404.
- **Input → Output:** attach → replay + live stream

### RF-004: Persistência por iteração
- **Description:** assistant salva checkpoint em `ChatRun.PartialContent` a cada boundary (tool result, ~2KB de delta) e, no fim, grava `ChatMessage` completo — `UpdateChatAsync` deixa de ser o único caminho de persistir a resposta.
- **Rules:** checkpoint idempotente; falha na run preserva `PartialContent` visível no attach.
- **Input → Output:** run → `ChatMessage` assistant no histórico do chat

### RF-005: Stop cooperativo
- **Description:** `POST .../runs/{runId}/stop` seta flag → dispatcher cancela o token da run → status `Stopped`, mantém `PartialContent`.
- **Rules:** só dono; run já final → 409.
- **Input → Output:** stop → status `Stopped`

### RF-006: Boot sweep
- **Description:** no startup, runs `Queued|Running` viram `Interrupted` (não retomam — tokens não sobrevivem a restart).
- **Rules:** in-process, um sweep só; `Interrupted` mostra `PartialContent` com aviso na UI.
- **Input → Output:** restart → runs órfãs fechadas

### RF-007: Cliente anexa em run ativa
- **Description:** `ChatView` ao carregar um chat consulta `GET runs` — se há run ativa, mostra banner "gerando…" + `PartialContent` e anexa no SSE; `SendAsync` usa o endpoint de enqueue; `Stop` chama o endpoint.
- **Rules:** fechar/reabrir a aba restaura o stream no ponto certo; fallback para `/api/chat/completions` se o endpoint de runs falhar (paridade).
- **Input → Output:** reload → stream retomado sem perda

**Business rules / invariants:**
- Uma run pertence a um ChatId+UserId — nunca vaza para outro usuário.
- `POST /api/v1/chats/{id}` (full replace) durante run ativa não apaga mensagens ainda não persistidas da run (a run é a fonte da verdade do turno corrente).
- Endpoints novos são estritamente aditivos — nada do contrato atual muda.

## 5. API Contract

**Endpoint:** `POST /api/v1/chats/{chatId}/messages` · `GET /api/v1/chats/{chatId}/runs[/{runId}/stream]` · `POST /api/v1/chats/{chatId}/runs/{runId}/stop`
**Auth:** `Bearer` JWT (mesmo do restante); SSE aceita `?token=` como o stream atual.

**Response:** `202` enqueue · `200` lista/stream · `404` chat/run de outro usuário · `409` stop em run final

## 6. Critérios de Aceite

- [ ] Enviar mensagem, fechar a aba, reabrir → resposta continua/completa e aparece no chat.
- [ ] Segunda mensagem durante run ativa enfileira e executa depois (FIFO por chat).
- [ ] Attach com `?lastSeq` não repete nem pula deltas.
- [ ] Stop interrompe a run e mantém o parcial.
- [ ] Restart do servidor marca runs órfãs como `Interrupted` com o parcial visível.
- [ ] `/api/chat/completions` segue passando nos testes existentes.
- [ ] Testes: enqueue/stop/attach/auth, dispatcher FIFO + boot sweep, isolamento por UserId.

## 7. Notas

`[A DEFINIR]` rota do attach: `/api/v1/...` novo vs extensão do `/api/chat/*` — escolha: `/api/v1/chats/{id}/runs/...` (REST-consistente com ChatEndpoints). Tool streaming SSE (P3) reusa o mesmo broadcaster adicionando tipos de evento.

## Reconciliation

_Reconciliado em 2026-10-08 (SPEC-20261008-spec-status-reconciliation, Issue #203)._

| RF | Veredito | Evidência |
| --- | --- | --- |
| RF-001 Enfileirar → run | delivered | `POST /{id}/messages` (`ChatRunEndpoints.cs:24`) — valida dono, persiste mensagem, cria `ChatRun` `Queued` com snapshot, devolve `202 {runId,status}`; fila FIFO por chat |
| RF-002 Dispatcher desacoplado | delivered | `Api/Runs/ChatRunDispatcher.cs` — `BackgroundService`, `MaxConcurrentRuns = 4`, `IServiceScope` por run, `Completed|Failed|Stopped` |
| RF-003 Attach SSE com replay | delivered | `GET .../runs/{runId}/stream` (`:28`) — replay por `seq` + live, shape OpenAI `choices[0].delta.content`, `done` + `[DONE]`; 404 para run alheia |
| RF-004 Persistência por iteração | delivered | `ChatRun.PartialContent` (`Domain/Entities.cs:252`) com checkpoints por boundary em `ChatRunExecutor.cs:166,187,494,541`; `ChatMessage` final no fim |
| RF-005 Stop cooperativo | delivered | `POST .../runs/{runId}/stop` (`:29`) — flag → cancel → `Stopped` preservando `PartialContent`; já final → 409 |
| RF-006 Boot sweep | delivered | `ChatRunDispatcher.cs:69-94` — runs `Queued|Running` órfãs → `Interrupted` no startup (um sweep in-process) |
| RF-007 Cliente anexa | delivered | `ChatView.razor:940` `GetActiveRunAsync` → banner "gerando…" + `PartialContent` + attach SSE; `ChatStreamService.cs:122-126`; fallback para `/api/chat/completions` |

