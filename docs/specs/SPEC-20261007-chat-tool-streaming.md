# SPEC-20261007-chat-tool-streaming

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-tool-streaming` (P3 da série harness-chat; depende de P1) |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261007-chat-tool-streaming` |
| Ticket | `GAP-chat-tool-streaming` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** ver em tempo real quando o modelo chama uma tool (nome, status, resultado) e aprovar/barrar tools mutáveis antes de executarem
**So that** o loop de tools deixa de ser uma caixa-preta e eu controlo ações com efeito colateral.

**Problem context:**
`RunToolLoopAsync` (`ApiEndpoints.cs:450`) roda até 5 rounds invisíveis: o cliente só vê o texto final (ou nada, se morrer). Não há indicador de "executando X", nem resultado, nem gate de aprovação — e a mensagem `role=tool` só aparece depois de persistida. O harness emite `ChatToolCallEvent`/`ChatToolResultEvent`/`ChatStatusEvent`/`ChatApprovalAskedEvent` no SSE com cards de tool e prompt de permissão na UI (`ToolCallCard`, `PermissionPromptCard`).

## 2. Scope

**In scope:**
- Eventos SSE no broadcaster da run (P1): `tool_call` `{id, name, argsPreview}`, `tool_result` `{id, name, ok, preview, imagePath?}`, `status` `{phase, label}` ("executando tool…").
- Mensagens `role=assistant` com `ToolCallsJson` e `role=tool` persistidas na run (boundary events do P1).
- UI: `ToolCallCard` no `MessageBubble`/transcript — chip collapsed "🔧 name" → expande args + resultado; chip de status ao vivo "Executando tool: name".
- Gate de aprovação: tools marcadas `requires_approval` (ou preset por conversa) pausam a run em `AwaitingApproval`; `POST .../runs/{id}/approvals/{callId}` `{decision: approve|deny, remember?}` retoma; card de permissão na UI com args.
- Preset de permissão por chat: `allow-readonly` (default) | `approve-mutations` | `always-allow` (por conversa, alterável mid-run, vale no próximo tool call).
- `data: [DONE]` mantém compat — parser novo reusa `ChatStreamService` estendido para os novos tipos.

**Out of scope:**
- Edição de args no card de aprovação (harness tem; fase 2).
- Tools de canal (chat em grupo) — mesma seam, depois.
- Aprovação em `/api/chat/completions` legado (sem SSE novo — tools seguem invisíveis lá; quem quer UX usa o endpoint de runs).

## 3. Technical Context

**Where the change happens:**
`Application` (contratos de evento), `Infrastructure` (`ToolExecutor` → callback de eventos; gate antes de executar; `ChatRunExecutor` publica), `Api` (approval endpoint), `Client` (`ChatStreamService` parser + `ToolCallCard` + `PermissionPromptCard`).

**Files to read before implementing:**
- `src/OpenWebUI.Api/Endpoints/ApiEndpoints.cs` (RunToolLoopAsync)
- `src/OpenWebUI.Infrastructure/Services/ToolExecutor.cs`, `PythonToolExecutor.cs`, `McpClientService.cs`
- `src/OpenWebUI.Client/Services/ChatStreamService.cs`, `Components/MessageBubble.razor`
- Referência: `repos/agent-harness` `ChatService.cs` (event records), `ToolCallCard.razor`, `PermissionPromptCard.razor`, preset chip

**Files to create or modify:**
```text
src/OpenWebUI.Application/Contracts/ChatRunEvents.cs (records de evento)
src/OpenWebUI.Infrastructure/Services/ToolExecutor.cs (+IChatToolEventSink param)
src/OpenWebUI.Api/Endpoints/ChatRunEndpoints.cs (+approval)
src/OpenWebUI.Client/Components/{ToolCallCard,PermissionPromptCard}.razor
src/OpenWebUI.Client/Services/ChatStreamService.cs (+tipos)
tests/OpenWebUI.Api.Tests/{ToolStreamingTests,ToolApprovalTests}.cs
```

## 4. Requirements

### RF-001: Streaming de tool_call/tool_result
- **Description:** antes de executar cada tool a run emite `tool_call`; ao terminar emite `tool_result` (ok, preview truncado, `imagePath` quando a tool gerou imagem — ver SPEC-20261007-chat-agent-tools); ambos com `seq` do broadcaster.
- **Rules:** args completos persistidos; SSE carrega preview truncado (2KB); erro de tool vira `tool_result` com `ok:false` (não mata a run).
- **Input → Output:** execução de tool → 2 eventos ordenados no stream

### RF-002: Status ao vivo
- **Description:** `status` evento `{phase: running_tool|running_mcp|generating, label}` antes de cada fase; UI mostra chip "Executando tool: X" no lugar do spinner genérico.
- **Rules:** fases discretas; UI ignora fase desconhecida.
- **Input → Output:** fase → chip de status na tela

### RF-003: Gate de aprovação
- **Description:** tool `requires_approval` (propriedade da Tool/MCP, ou classifier) com preset ≠ always-allow pausa a run em `AwaitingApproval` e emite `approval_asked {callId, toolName, kind, argsPreview}`; aprovação/denegação retoma; timeout de aprovação → `deny` default.
- **Rules:** decisão só do dono do chat; deny injeta tool_result "negado pelo usuário" para o modelo seguir; `remember` vale só para a conversa.
- **Input → Output:** ask → POST decisão → run retoma

### RF-004: Preset por conversa
- **Description:** `Chat.ApprovalPreset` (`allow-readonly` default | `approve-mutations` | `always-allow`) editável via `PATCH /api/v1/chats/{id}` e chip na toolbar do chat; mudança mid-run vale no próximo tool call.
- **Rules:** mutável a qualquer momento; default protege tools mutáveis (`shell_exec`, `code_interpreter`, HTTP POST tools, MCP write).
- **Input → Output:** preset → política aplicada por call

### RF-005: Cards na UI
- **Description:** `ToolCallCard` renderiza tool messages (ícone terminal, nome, duração, args/resultado collapsible, badge refused/denied); `PermissionPromptCard` renderiza `approval_asked` pendente com botões Aprovar/Negar + args.
- **Rules:** tool refused/denied abre expandido; imagem gerada renderiza inline no card.
- **Input → Output:** eventos → cards no transcript

**Business rules / invariants:**
- `tool_result` nunca vaza args/secret — previews passam por truncamento; secret-scrub básico (patterns `sk-*`, `Bearer `) antes do SSE.
- Aprovação pendente sobrevive a detach/attach (replay do broadcaster inclui `approval_asked` não resolvido).
- Run `AwaitingApproval` não conta como final — attach reabre o prompt.

## 5. API Contract

**Endpoint:** `POST /api/v1/chats/{chatId}/runs/{runId}/approvals/{callId}` `{decision, remember?}` · `PATCH /api/v1/chats/{id}` `{approvalPreset}`
**Auth:** `Bearer` JWT

**Response:** `200` decisão registrada · `404` run/call inexistente · `409` já decidida · `410` run final

## 6. Critérios de Aceite

- [ ] Loop de tools mostra cards de tool_call/tool_result em ordem no transcript.
- [ ] Tool mutável com preset default pausa e pede aprovação; aprovar executa, negar injeta negação.
- [ ] Fechar e reabrir a aba durante `AwaitingApproval` reexibe o prompt.
- [ ] Preset `always-allow` executa sem perguntar; mudança mid-run aplica no próximo call.
- [ ] `ChatStreamService` ignora eventos desconhecidos sem quebrar (forward-compat).
- [ ] Testes: emissão de eventos com seq, gate por preset, deny path, isolamento por usuário.

## 7. Notas

Classificação mutável vs readonly: para tools do banco adicionar flag `IsMutable`/`RequiresApproval` na entidade `Tool`; MCP tools herdam do servidor (campo `readOnlyHint` quando exposto) com fallback `requires_approval` = true (conservador). O preset chip fica na toolbar do `ChatView` (padrão do harness).

## Reconciliation

_Reconciliado em 2026-10-08 (SPEC-20261008-spec-status-reconciliation, Issue #203)._

| RF | Veredito | Evidência |
| --- | --- | --- |
| RF-001 tool_call/tool_result | delivered | eventos `tool_call`/`tool_result` com `seq` emitidos pela run (`Api/Runs/ChatRunExecutor.cs`); `ChatStreamService` parseia; erro de tool → `tool_result ok:false` sem matar a run |
| RF-002 Status ao vivo | delivered | evento `status` `{phase,label}` → `_statusLabel` chip "Executando tool: X" (`ChatView.razor:1723-1744`) no lugar do spinner |
| RF-003 Gate de aprovação | delivered | `ChatRunApprovals.cs` — run pausa em `AwaitingApproval` + `approval_asked {callId,toolName,kind,argsPreview}`; deny injeta "negado pelo usuário"; timeout → deny; `remember` por conversa |
| RF-004 Preset por conversa | delivered | `Chat.ApprovalPreset` (`Domain/Entities.cs:144`, default `approve-mutations`); `PATCH /api/v1/chats/{id}` (`ChatEndpoints.cs:412-420`); chip na toolbar (`ChatView.razor:403-410`); valores `allow-readonly|approve-mutations|smart|always-allow|auto` |
| RF-005 Cards na UI | delivered | `ToolCallCard.razor` (ícone, duração, args/resultado collapsible, badge refused/denied, imagem inline) + `PermissionPromptCard.razor` Aprovar/Negar |

