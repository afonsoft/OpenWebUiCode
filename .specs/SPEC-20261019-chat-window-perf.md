# SPEC-20261019-chat-window-perf

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-window-perf` |
| Type | `Performance` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261019-chat-window-perf` |
| Status | `Draft` |
| Parent | report do usuário — chat com conteúdo grande trava a tela; exibir janela ~256k/512k e carregar o resto dinamicamente no scroll + compactação automática acima de 256k |

## 1. User Story

**As a** usuário do chat
**I want** conversas longas renderizando só a janela visível (equivalente a ~256k/512k de contexto), com o histórico antigo carregando sob demanda conforme o scroll sobe/desce, e compactação automática do contexto quando ele passa de 256k
**So that** a tela não trava em chats grandes e o modelo continua com contexto gerenciável.

## 2. Scope

**In scope:**
- Endpoint paginado de mensagens: `GET /api/v1/chats/{id}/messages?before={position}&take={n}` → janela + `hasMore` + `total`; o GET do chat passa a trazer só a cauda (tail window) por padrão.
- `ChatView` renderiza janela deslizante: cauda inicial carregada; sentinel no topo dispara página anterior no scroll-up; mensagens muito acima da viewport saem do DOM no scroll-down (window slide) e re-renderizam ao voltar.
- Cap cliente: `_messages` carrega no máximo o equivalente a ~256k chars (config `chat:window_chars`) — o restante fica server-side e chega por páginas.
- Compactação proativa: `compaction:threshold` novo default 256_000 chars (era 60_000), configurável (presets 256k/512k) — `MaybeCompactAsync` já roda por run; alinhar semântica e garantir execução também no enqueue de mensagem comum.
- Preservar: colapso de grupos `role=tool` (fronteira de página nunca corta um grupo tool), marcador `compaction`, branch walk, streaming append, `scrollToEndIfPinned`.

**Out of scope:**
- Virtualização pixel-perfect tipo `Virtualize` (o componente Blazor é top-down; chat é bottom-anchored — a janela deslizante com sentinel é a abordagem).
- Mudar o formato do resumo de compactação (já coberto por SPEC-20261010-context-compaction).
- Paginação de runs/eventos SSE.

## 3. Technical Context

**Where the change happens:**
`ChatEndpoints.cs` (novo GET paginado + tail-only no GET principal ou `?tail=`), `ChatView.razor` (janela + sentinel + cap), `ApiService` client (`GetChatMessagesAsync`), `ContextCompactionService` (threshold default), config keys.

**AS-IS evidenciado:**
- `ChatEndpoints.cs:287` — `Include(c => c.Messages.OrderBy(m => m.Position))` traz TODAS as mensagens; `ChatView` faz `_messages.AddRange(chat.Messages)` (:1355/:2080/:2905).
- `ChatView.razor:207` — `for i` renderiza `_messages.Count` inteiro a cada frame; cada item é `MessageBubble` + Markdig (custo ~linear no DOM, o travamento reportado).
- Loop usa `i = lastTool` para colapsar grupos tool consecutivos (:215-226) — fronteira de página/janela PRECISA alinhar ao início do grupo, senão quebra o work log.
- `ContextCompactionService` (`compaction:threshold` default 60_000, `tail_messages` 30, `tail_chars` 25_000) — `MaybeCompactAsync` já é chamado no `ChatRunExecutor:244/456/951`.
- `_Imports.razor` já importa `Microsoft.AspNetCore.Components.Web.Virtualization` (não usado para o log).
- `scrollToEndIfPinned` (`app.js`) ancora o scroll no fim — a janela deve coexistir (prepend não pode pular o scroll).

**Files to read before implementing:**
- `src/OpenWebUI.Client/Components/ChatView.razor` (:172-280 render, `:1350`/:2080/:2905 loads, `scrollToEndIfPinned`)
- `src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs` (`GetChatAsync` :280-330)
- `src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs` (`MaybeCompactAsync`/`ThresholdsAsync`)
- `src/OpenWebUI.Client/Services/ApiService.cs` (contratos de chat)

**Files to create or modify:**
```text
src/OpenWebUI.Application/Contracts/ChatContracts.cs     # ChatMessagesPage {Messages, HasMore, Total}
src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs             # GET /{id}/messages?before&take + tail no GET
src/OpenWebUI.Client/Services/ApiService.cs              # GetChatMessagesAsync
src/OpenWebUI.Client/Components/ChatView.razor           # janela + sentinel + cap 256k
src/OpenWebUI.Client/wwwroot/js/app.js                   # helper scroll-sentinel (se faltar)
src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs  # default 256k
src/OpenWebUI.Client/wwwroot/i18n/*.json                 # chat.loading_older (8 locales)
tests/OpenWebUI.Api.Tests/*                              # paging + compaction threshold
tests/OpenWebUI.Client.Tests/*                           # janela bUnit onde der
```

## 4. Requirements

### RF-001: Endpoint paginado
- **Description:** `GET /api/v1/chats/{id}/messages?before={position}&take={n}` devolve as `take` (default 50, cap 200) mensagens anteriores a `position` ordenadas por `Position`, mais `hasMore`/`total`; `before` omitido → cauda. Dono-only (mesmo auth do chat). O GET principal do chat aceita `?tail={n}` — quando presente, `chat.Messages` vem só com a cauda + `hasMore`.
- **Rules:** `position` inválido → 400; chat inexistente → 404; `take` fora da faixa → clamp; EF `AsNoTracking` + `OrderByDescending(Position).Take()` sem carregar o resto.

### RF-002: Janela deslizante no ChatView
- **Description:** `_messages` carrega só a cauda inicial (ex.: últimas 60 mensagens OU até ~256k chars — o que bater primeiro); um sentinel/`IntersectionObserver` no topo dispara `GetChatMessagesAsync(before: primeiro.Position)` no scroll-up, prependendo a página sem mexer o scroll (altura preservada via `scrollTop += deltaAltura`). Descendo, mensagens acima de `windowTop` são removidas do DOM (re-carregáveis voltando). Cap: conteúdo mantido ≤ `chat:window_chars` (default 256_000 chars ≈ contexto 256k).
- **Rules:** fronteira de página NUNCA dentro de um grupo `role=tool` — estender a página até um boundary não-tool; prepend preserva `_messages[^1]` (streaming/atual); estado `hasMore=false` some com o sentinel; spinner `chat.loading_older` durante fetch; `aria-live` do log intacto.

### RF-003: Cap de memória cliente
- **Description:** ao prepender além do cap, o excesso sai do topo de `_messages` (marca `hasMore` para re-fetch); scrolling de volta re-busca — invariante: `_messages` nunca > ~256k chars renderizados.

### RF-004: Compactação proativa 256k
- **Description:** `compaction:threshold` default vira 256_000 chars; presets documentados 256k/512k; `MaybeCompactAsync` continua por run — adicionar chamada no caminho de mensagem comum (ChatView/endpoint de enqueue) quando o histórico passar do threshold, fora de run/agente. Semântica documentada: chars ≈ 4× tokens (256k chars ≈ 64k tokens — o limite é de caracteres, não tokens).
- **Rules:** compactação falha → log + segue (já é o contrato); marcador `role=compaction` continua visível.

## 5. Acceptance Criteria

- Chat com 500 mensagens/500k chars: GET inicial traz só a cauda; scroll-up carrega páginas anteriores; DOM nunca excede a janela; scroll não pula.
- `before` no meio de grupo tool → página começa no boundary anterior (work log nunca cortado).
- Histórico > 256k chars → `MaybeCompactAsync` dispara e o marcador `compaction` aparece.
- Chats pequenos (< cauda) comportamento idêntico ao atual (sem sentinel, sem página extra).
- `dotnet test` API + client verdes; `check-i18n-parity` OK (chave nova nos 8 locales).

## 6. Riscos

- **Scroll jank**: prepend sem ajuste de scrollTop dá salto — medir `scrollHeight` antes/depois e compensar.
- **Streaming + janela**: append durante run deve sempre render na cauda — a janela nunca pode esconder a mensagem viva.
- **Branch walk**: `_messages` janelado não pode quebrar `WalkPath`/versões — boundary alinhado a posições persistidas.
- **Regressão visual**: janela não pode deixar buracos — sentinel + hasMore coerente.
