# ADE / Harness — Agentic Development Environment

Este fork opera como um **ADE (Agentic Development Environment)** — *The AI
Orchestrator*: runs executam no servidor independente do cliente, são
extensíveis por seams documentados e controláveis por múltiplas superfícies
(web, headless, JSON-RPC, ACP, MCP, terminal, webhooks). Inspirado em
`deepseek-ai/deepseek-harness` (kernel de plugins + event seams) e
`afonsoft/agent-harness` (terminal multiplexado + MCP server + attention).

Implementado por `SPEC-20261021-harness-seams` (seams + superfícies + webhooks
+ teams) e `SPEC-20261022-ade-surfaces` (terminal ADE + MCP + attention).

## 1. Execução em background (server-side)

- `ChatRunDispatcher` processa filas no servidor — fechar a aba não mata a run.
- Snapshots + eventos duráveis (`RunEventWriter`) permitem replay ao reabrir
  de qualquer dispositivo (`GET /api/v1/chats/{id}/runs/{runId}/stream?lastSeq=`).
- Sweeper retoma runs `Running`/`Queued` órfãs após restart
  (`ChatRuns:ResumeStaleSeconds`, default 60s).
- Web push avisa quando a run termina com o app fechado
  (`WebPushChatRunNotifier`).

## 2. Event seams — `IAgentRunObserver` + `IRunInterceptor`

Dois pontos de extensão internos, ambos via DI:

```csharp
// Read-only: recebe cada evento da run sem poder mutá-la.
// Observer lento/falho é isolado com timeout (5s) + LogWarning — nunca aborta a run.
public interface IAgentRunObserver
{
    Task OnRunEventAsync(AgentRunEvent e, CancellationToken ct);
}

// Waterfall ordenado em pontos de decisão (pre-step, tool.call).
// Pode vetar; veto vira evento durável interceptor_veto.
public interface IRunInterceptor
{
    int Order { get; }
    Task<InterceptionResult> OnEventAsync(
        InterceptionContext ctx, CancellationToken ct);
}
```

- Tipos de evento: `run.started`, `run.completed`, `run.failed`,
  `run.stopped`, `run.steer`, `interceptor.veto`, `step_started`,
  `step_finished`, `tool_call`, `tool_result`, `messages_added`,
  `content_delta`, `phase`, `note`, `snapshot`.
- Payloads passam pelo `SecretScrubber` — observers nunca veem secrets.
- Built-ins: `TelemetryObserver` (logs/metrics), `AttentionObserver`
  (inbox persistido), `RepeatToolGuard` (veta a 3ª tool call idêntica
  consecutiva — proteção contra loop de tool).

## 3. Contribuidores de system prompt — `ISystemPromptContributor`

A montagem do system prompt saiu do método monolítico para contributors
ordenados por DI (`Order`). Os blocos originais viraram contributors
`BaseAgentContributor`, `AlwaysAllowContributor`, `CustomModelContributor`,
persona, memória, tools, workspace, diretivas de modo etc. — a ordem efetiva
é byte-idêntica à anterior (golden test), então adicionar contexto novo é
registrar um contributor sem tocar no pipeline.

## 4. Superfícies de controle

### 4.1 Headless one-shot

```bash
dotnet run --project src/OpenWebUI.Api -- --headless \
  --user-email admin@local --prompt "resuma o README" [--model m] [--chat-id c]
```

- Executa a run no servidor e sai; stdout em **JSON-lines**
  (`JsonLinesRunObserver`) — um evento por linha.
- Exit codes: `0` sucesso, `2` erro de uso/usuário, `1` falha da run.

### 4.2 JSON-RPC 2.0 — `POST /api/v1/rpc`

```jsonc
{ "jsonrpc": "2.0", "id": 1, "method": "runs.start",
  "params": { "chatId": "...", "content": "oi" } }
```

- Métodos: `chats.create`, `runs.start`, `runs.get`.
- Erros padrão: `-32700` parse, `-32600` request inválido, `-32601` método
  desconhecido, `-32602` params inválidos, `-32000` erro de domínio.
- Mesma auth Bearer dos endpoints REST.

### 4.3 ACP — `src/OpenWebUI.Acp` (Agent Client Protocol)

Host **stdio** para editores (Zed e similares): `session/new` cria um chat,
`session/prompt` enfileira uma run e os eventos duráveis viram
`session/update`. Bearer via `OPENWEBUI_ACP_TOKEN`, URL via `OPENWEBUI_URL`.

### 4.4 MCP — `src/OpenWebUI.Mcp` (Model Context Protocol)

Servidor MCP **stdio** — expõe o produto como ferramenta para agentes
externos (Claude Code, OpenCode):

| Tool | Ação |
|---|---|
| `chats.list` / `chats.create` | lista / cria chats |
| `runs.start` / `runs.status` / `runs.stop` | controla runs |
| `teams.status` | roster/tasks de um team |
| `attention.list` | inbox de atenção unificado |

```bash
export OPENWEBUI_URL=http://localhost:8080
export OPENWEBUI_API_KEY=sk-...   # obrigatória — fail-closed sem ela
dotnet run --project src/OpenWebUI.Mcp
```

## 5. Webhooks v2 — `POST /api/v1/hooks/{token}`

Extensão do `AutomationHook` existente:

- `Provider`: `generic` (`X-Event-Type`/`X-Delivery-Id`) ou `github`
  (`X-GitHub-Event`/`X-GitHub-Delivery`).
- `Secret`: HMAC-SHA256 em `X-Hub-Signature-256`, verificação timing-safe;
  hooks sem secret continuam aceitos (marcados `unsigned` na auditoria).
- `EventsJson`: filtro de eventos — fora da lista vira delivery `filtered`.
- `PromptTemplate`: `{{path.dotted}}` renderizado sobre o payload JSON.
- Binding opcional de repo/persona/mode para a run disparada.
- `AutomationHookDelivery`: auditoria por delivery (`accepted`/`rejected`/
  `filtered`) + idempotência — mesmo delivery id devolve a run original.
- Payload limitado a 256 KiB (413 acima). Auditoria em
  `GET /api/v1/hooks/{id}/deliveries`.

## 6. Agent teams

Sem entidade Team — a hierarquia de chats existente é o roster:

- `Chat.TeamId` = id do chat raiz; `TeamRole` = `leader` | `member`.
- `delegate_task` com `"team": true` materializa o team na raiz
  (`EnsureRootTeamAsync`, idempotente); `"resume_chat_id"` retoma um
  membro existente em vez de criar chat novo.
- `TeamTask` — task list compartilhada durável (`pending`/`in_progress`/`done`).
- Tools: `team_task` (create/claim/complete), `team_message` (mensagem
  durável para membro ocioso vira `ChatMessage` + notificação),
  `team_status` (roster + tasks + runs ativas).
- Inbox = steer/queue já existente; status em `GET /api/v1/chats/{id}/team`.
- `TeamService.ResolveTeamIdAsync` sobe `ParentChatId` para chats legados
  sem coluna.

## 7. Terminal ADE

- `POST /api/v1/terminal/sessions` aceita `chatId`, `runId`, `workdir` —
  resolução: workdir explícito (contido em `data/`) → worktree isolado da
  run → workspace bindado do chat → workspace default do usuário.
- `GET /api/v1/terminal/sessions?chatId=` filtra por chat.
- **`/ws/terminal`** multiplexa N sessões numa conexão: frames
  `{op, sid, ...}` com `attach`/`detach`/`input`/`resize`/`create`/`kill`;
  respostas `sessions`/`output`/`exit`/`created`/`error`. Disconnect só
  desanexa — sessões sobrevivem para reattach (scrollback 64 KiB).
- Endpoint legado `/ws/terminal/{id}` continua funcionando.
- Aba **Terminal** na view do chat abre sessão bindada ao worktree da
  conversa.
- Delete de chat mata PTYs bindadas **antes** de remover diretórios.

## 8. Inbox de atenção

Une duas fontes numa badge só:

- **Persistidos** (`UserAttention`): `run:{id}` (`run_completed`/`run_failed`
  via `AttentionObserver`), `term:{id}` (PTY encerrado espontaneamente —
  kill/reap/delete não gera item).
- **Sintéticos**: `chat:{id}` (`approval`) — chats com run esperando
  decisão, vindos do `ChatRunApprovals` em memória.

```
GET  /api/v1/attention            → lista fundida (aprovações primeiro)
POST /api/v1/attention/{key}/seen → dispensa item persistido (404 se ausente)
GET  /api/v1/chats/attention/count → aprovações + persistidos (badge)
```

- Upsert por `(UserId, Key)` — rearmar a mesma origem não duplica.
- Delete de chat limpa os itens ligados a ele.
- Escrita do observer usa scope DI independente — nunca flusha o
  `DbContext` compartilhado da run.

## 9. Limpeza de workspace no delete de chat

Ordem garantida em `ChatEndpoints.ReleaseChatDiskAsync`:

1. `TerminalSessionManager.KillForChatAsync` — PTYs antes de remover diretórios.
2. Worktrees isolados por run — `git worktree remove --force` + `prune`
   (fallback para remoção direta se o git falhar).
3. `CheckpointService.DeleteForWorkdir` — repos e manifests de checkpoint
   de cada workdir.
4. `WorkspaceRepoService.ReleaseChatWorkspaceAsync` — binding + diretório
   principal (repo base preservado até os worktrees saírem).

Mesma rotina no delete-all. Diretórios presos por lock ficam para o sweeper
de retenção (best-effort).

## Referência rápida

| Superfície | Detalhe |
|---|---|
| Observer | `IAgentRunObserver` via DI (scoped) |
| Interceptor | `IRunInterceptor` (`Order`, veto) |
| Prompt | `ISystemPromptContributor` (`Order`) |
| Headless | `--headless --user-email --prompt` |
| RPC | `POST /api/v1/rpc` |
| ACP | binário `open-webui-acp` (stdio) |
| MCP | `dotnet run --project src/OpenWebUI.Mcp` |
| Webhook | `POST /api/v1/hooks/{token}` + `X-Hub-Signature-256` |
| Team | `GET /api/v1/chats/{id}/team` |
| Terminal | `/ws/terminal` (mux) · `/ws/terminal/{id}` (legado) |
| Attention | `GET /api/v1/attention` · `POST /{key}/seen` |
