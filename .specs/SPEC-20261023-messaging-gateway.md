# SPEC-20261023-messaging-gateway

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `messaging-gateway` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261023-messaging-gateway` |
| Ticket | [#497](https://github.com/afonsoft/open-webui/issues/497) |
| Status | `Approved` |
| Parent | Análise comparativa hermes-agent/OpenHarness/ECC (out/2026) — item #4. Inspirado no messaging gateway do hermes-agent (25+ adapters, session routing, pairing, slash commands) e nos channels do ohmo/OpenHarness (Telegram/Slack/Discord/Feishu). |

## 1. User Story

**As a** usuário da plataforma
**I want** conversar com o meu agente pelo Telegram (e, depois,
Slack/Discord) sem abrir a web UI — com sessão persistente, ferramentas
restritas e pareamento de identidade
**So that** o agente está onde eu já estou; hoje o único caminho de
entrada é a UI/webhook — não existe canal bidirecional com plataformas
de mensagem.

**Problem context:**
O hermes-agent é essencialmente um produto de gateway: adapters por
plataforma → resolução de sessão → agent loop → resposta, com
autorização por pairing/allowlist e hooks de ciclo de vida. No nosso
sistema a peça pesada já existe — `IChatRunDispatcher` serializa runs
por chat, `AgentRunEventBus`/`IAgentRunObserver` notificam conclusão,
`AutomationHookEndpoints` já prova o padrão de endpoint anônimo +
token + rate-limit + auditoria (`FireAsync`, `X-Webhook-Signature`),
`PersonaDispatchService` resolve persona e `Channels` já são um chat
coletivo interno. O que falta é a camada de adapters externos + o
modelo de identidade externa↔interna.

## 2. Scope

**In scope:**

- **A. Abstração `IGatewayAdapter`** — contrato único por plataforma:
  `VerifySignature(HttpRequest)`, `ParseInbound` → `GatewayMessage`
  {externalChatId, externalUserId, text, attachments?, messageId},
  `SendReplyAsync(channel, peer, text)` via API da plataforma
  (`HttpClient` + `SsrfGuard` na URL; markdown→formato nativo, chunking
  por limite da plataforma).
- **B. Entidades** — `GatewayChannel` (Id, OwnerUserId, Platform
  `telegram`, ConfigJson{botToken ref secreto, mode=webhook},
  Token GUID do endpoint, ToolsPolicy `restricted|standard`,
  DefaultModelId?, PersonaId?, Enabled), `GatewayPeer` (ChannelId,
  ExternalUserId, ExternalChatId, PairingCode?, PairedUserId?,
  PairedAt, Blocked), `GatewaySession` (PeerId → ChatId persistente),
  `GatewayDelivery` (audit, mesmo espírito das hook deliveries:
  direction in/out, status, messageId, excerpt).
- **C. Inbound endpoint** — `POST /api/v1/gateway/{platform}/{token}`
  `AllowAnonymous` + verificação de assinatura da plataforma +
  rate-limit por IP/token (mesmo padrão do `FireAsync`: anônimo e
  limitado). Resposta 200 imediata; processamento assíncrono.
- **D. Session routing + pairing** — mensagem → resolve channel pelo
  token → resolve peer: não-pareado recebe **pairing code** (6 dígitos,
  TTL 15min, one-shot) como resposta — o usuário vincula em Settings na
  web UI (`POST /api/v1/gateway/pair`); pareado → `GatewaySession` com
  Chat persistente por (peer) — continuidade de conversa igual a uma
  conversa na UI (o chat aparece na sidebar do usuário, marcado
  `gateway:{platform}`).
- **E. Execução** — texto vira mensagem no chat → enfileira
  `IChatRunDispatcher` (serialização por chat já resolve rajadas) →
  `GatewayReplyService` (`IAgentRunObserver` em `run.completed`) envia
  a resposta final do assistente de volta à plataforma. Texto externo
  entra no prompt embrulhado em `<untrusted_input>` (doutrina da SPEC
  sdd-orchestrator-prompts) — conteúdo de terceiros nunca é instrução.
- **F. Política de tools** — `restricted` (default): sem
  `shell_exec`/`fs_write`/`browser_*`/`delegate_task` — o model roda
  read-only + memory + web; `standard`: toolset normal do chat com os
  approval tiers existentes (aprovação vira mensagem interativa quando
  a plataforma suporta; v1: comando `/approve` na conversa).
- **G. Telegram v1** — adapter concreto: webhook com
  `X-Telegram-Bot-Api-Secret-Token`, `setWebhook` helper endpoint,
  sendMessage com `parse_mode=MarkdownV2` (fallback texto puro),
  chunk 4000 chars. Slack/Discord ficam **projetados mas fora** —
  interfaces prontas, adapters em SPEC seguinte.
- **H. Superfície do usuário** — CRUD `GET/POST/PATCH/DELETE
  /api/v1/gateway/channels` (dono-only), `POST /gateway/pair`,
  `GET /gateway/peers` (list/block); aba "Gateway" em Settings (criar
  canal, colar bot token, ver peers, inserir pairing code).

**Out of scope:**

- Slack/Discord/Feishu/WhatsApp adapters (contrato pronto; cada um é
  SPEC própria — assinatura e formato divergem bastante).
- Grupos/rooms multiusuário na plataforma (v1 = DM 1:1 por peer; menção
  em grupo é P2).
- Voz/áudio/mídia rica — v1 texto + anexo como link; OCR/visão depois.
- Edição/typing indicators/streaming parcial — resposta vai inteira no
  `run.completed` (chunked por limite da plataforma).
- Recebimento por polling — v1 só webhook (docs explicam `setWebhook`).

## 3. Technical Design

### Fluxo inbound

```
POST /gateway/telegram/{token}  (AllowAnonymous, rate-limited)
 → GatewayInboundService: resolve channel (cache 60s, token=Id)
 → adapter.VerifySignature (secret_token do header vs config)
 → adapter.ParseInbound → GatewayMessage
 → peer = find(channel, externalChatId+externalUserId)
   ├─ blocked → 200 silent
   ├─ unpaired → SendReply(pairing code + instruções) → delivery audit
   └─ paired → session.ChatId (cria se novo: Chat{UserId=paired,
               Title="telegram:{user}", MetaJson.gateway={peer}})
 → mensagem no chat (content embalado <untrusted_input>)
 → dispatcher.Enqueue(chat, run) → 200 OK
```

### Fluxo outbound

`GatewayReplyService : IAgentRunObserver` — em `run.completed` de chat
com `MetaJson.gateway.peer`: pega o texto final do assistente →
`adapter.SendReply` → `GatewayDelivery(out, ok|fail)`. Falha de envio →
marca delivery `failed` (re-tentativa manual via endpoint, sem loop).

### Segredos

`botToken` nunca em `ConfigJson` plano — vai para `UserSecretsService`
(mesmo cofre dos segredos de tools); config guarda `secretRef`. Logs
sempre com `SecretScrubber`.

### Tools policy

`BuiltinToolRegistry` já resolve tools por chat/run — o filtro entra na
montagem do toolset quando `chat.MetaJson.gateway.policy=restricted`
(allowlist fixa: `web_search`, `fetch_url`, `agent_memory_*`,
`context_*`, `todo_write`, `ask_user`, `skill`, `specs list|show`).
`standard` = toolset default; approvals de tier `Write`/`Dangerous` na
conversa externa via `/approve {id}` lido pelo dispatcher
(`ChatRunApprovals` já tem a mecânica — v1: aprovação só via UI web,
mensagem externa avisa "aprovação pendente na web").

### Arquivos

```text
src/OpenWebUI.Domain/Entities.cs                       # 4 entidades
src/OpenWebUI.Infrastructure/Services/Gateway/GatewayInboundService.cs    # novo
src/OpenWebUI.Infrastructure/Services/Gateway/GatewayReplyService.cs      # novo
src/OpenWebUI.Infrastructure/Services/Gateway/IGatewayAdapter.cs          # novo
src/OpenWebUI.Infrastructure/Services/Gateway/TelegramAdapter.cs          # novo
src/OpenWebUI.Infrastructure/Services/Gateway/GatewayPairingService.cs    # novo
src/OpenWebUI.Api/Endpoints/GatewayEndpoints.cs                            # novo
tests/OpenWebUI.Api.Tests/GatewayInboundTests.cs                           # novo
tests/OpenWebUI.Api.Tests/TelegramAdapterTests.cs                          # novo
tests/OpenWebUI.Api.Tests/GatewayPairingTests.cs                           # novo
```

Config: `Agent:Gateway:Enabled` (default `false` — opt-in),
`Agent:Gateway:MaxInboundChars` (8000), `Agent:Gateway:PairingTtlMinutes`
(15), `Agent:Gateway:PublicBaseUrl` (necessária p/ `setWebhook`).

## 4. Requirements

### RF-001: verificação obrigatória
- **Description:** toda chamada inbound valida assinatura da plataforma
  (Telegram `X-Telegram-Bot-Api-Secret-Token` == configurado) antes de
  qualquer processamento; token de endpoint inexistente → `404` genérico
  (não vaza plataforma).
- **Input → Output:** request válida → `200` + processamento async;
  inválida → `401/404` sem side-effect.

### RF-002: pairing
- **Description:** peer desconhecido → reply com código de 6 dígitos
  (TTL 15min, one-shot, 5 tentativas); usuário vincula em
  `POST /gateway/pair`; peer `Blocked` nunca executa run nem recebe
  código novo.

### RF-003: sessão persistente por peer
- **Description:** um Chat por (channel, peer); mensagens chegam na
  mesma conversa visível na UI; `MetaJson.gateway` guarda peer/policy;
  serialização por chat absorve mensagens em rajada.

### RF-004: reply via observer
- **Description:** `run.completed` → texto final enviado pela API da
  plataforma com chunking (≤4000 Telegram) e fallback texto puro quando
  markdown falha; delivery auditado in/out.

### RF-005: política de tools
- **Description:** `restricted` limita à allowlist read-only;
  `standard` usa toolset normal; nenhuma run de gateway executa
  `shell_exec`/`fs_write`/`delegate_task` em `restricted` — mesmo que
  prompt injetado peça (defesa é server-side, não prompt).

### RF-006: untrusted input
- **Description:** todo texto externo entra no histórico dentro de
  `<untrusted_input source="gateway:telegram" field="text">`; diretivas
  embutidas não são obedecidas (doutrina existente).

## 5. API Contract

**Endpoint:** `POST /api/v1/gateway/telegram/{token}` — **Auth:**
anonymous + `X-Telegram-Bot-Api-Secret-Token`

```json
{ "update_id": 1, "message": { "chat": {"id": 123}, "from": {"id": 456},
  "text": "resume o PR #12" } }
```

**Response:** `200 {}` imediato.

**Endpoint:** `POST /api/v1/gateway/channels` — **Auth:** Bearer (dono)

```json
{ "platform": "telegram", "botToken": "123:abc", "toolsPolicy":
  "restricted", "defaultModelId": "qwen3:14b" }
→ { "id": "...", "webhookUrl": "/api/v1/gateway/telegram/{token}",
    "setWebhookHint": "https://api.telegram.org/bot<token>/setWebhook?url=..." }
```

**Endpoint:** `POST /api/v1/gateway/pair` — `{ "code": "123456" }` →
`{ "paired": true, "channel": "telegram" }`

**Erros:** `401` assinatura; `404` token; `429` rate-limit; `400`
código expirado/errado. Formato genérico existente.

## 6. Acceptance Criteria

- [ ] **Given** webhook com secret errado **when** POST inbound **then**
      `401` e nenhuma run/delivery criada.
- [ ] **Given** peer novo **when** manda "oi" **then** recebe código de
      pareamento; após `pair` na UI, próxima mensagem cria sessão e run.
- [ ] **Given** peer pareado **when** manda 3 mensagens em rajada
      **then** viram runs serializadas no mesmo chat (ordem preservada).
- [ ] **Given** run completa **when** resposta >4000 chars **then** é
      enviada em chunks ordenados; markdown inválido cai p/ texto puro.
- [ ] **Given** policy `restricted` **when** o modelo tenta
      `shell_exec` **then** a tool nem é ofertada no toolset da run.
- [ ] **Given** payload com `</untrusted_input>` ou diretiva "ignore
      previous" **then** entra escapado como dado; `SecretScrubber`
      cobre botToken em qualquer log.
- [ ] **Given** `Agent:Gateway:Enabled=false` **then** endpoint inbound
      `404` e nenhum serviço de gateway ativo.
- [ ] Regressão: `AutomationHookV2Tests`, `HarnessSeamsTests`, suíte
      verde; coverage ≥ baseline.

## 7. Task Plan

| ID | Task |
| --- | --- |
| T1 | Discovery: `AutomationHookEndpoints.FireAsync`, `ChatRunDispatcher`, `AgentRunEventBus`/`IAgentRunObserver`, `ChatRunApprovals`, `UserSecretsService`, `SsrfGuard` |
| T2 | Migration + entidades + `GatewayPairingService` + testes |
| T3 | `IGatewayAdapter` + `TelegramAdapter` (verify/parse/send/chunk) + testes |
| T4 | `GatewayInboundService` + endpoint anônimo + routing/pairing gate + testes |
| T5 | `GatewayReplyService` (observer→send) + delivery audit + testes |
| T6 | Tools policy `restricted` no toolset da run + `/approve` notice + testes |
| T7 | CRUD channels/peers + aba Settings (colar token, pairing code) + testes |
| T8 | DoD: Release build 0 warnings, suíte verde, coverage ≥ baseline, drift guards, SPEC→Issue→PR |

## 8. Organization Guardrails

- Respostas ao usuário em **pt-BR**; código/commits em inglês.
- Branch dedicada; não modificar `.github/workflows/`; sem secrets —
  `botToken` só via `UserSecretsService`, nunca em config/log.
- Endpoint anônimo = superfície de ataque: assinatura antes de tudo,
  rate-limit, cap de payload, `SsrfGuard` em qualquer URL derivada de
  payload, `<untrusted_input>` obrigatório.
- Runs de gateway custam provider do usuário pareado — honrar
  `run-cost-budgets` quando existir; `restricted` é o default.
- Sem dados de terceiros em memória: peers externos não alimentam
  `MemorySyncService`/instincts do dono sem opt-in explícito
  (`remember` manual via UI).

## Open Questions / Pending Ambiguity

| OQ | Status | Resolução |
| --- | --- | --- |
| Aprovação interativa na plataforma (botões)? | Adiado | v1 avisa "aprovação pendente na web"; `/approve` por texto fica para o adapter v2 |
| Canal por usuário ou por instância? | Resolvido | por usuário-dono (cada um pluga seu bot) — igual ao modelo de channels/hooks existente |
| Streaming parcial via editMessage? | Adiado | custo de API calls × UX; resposta inteira primeiro |
