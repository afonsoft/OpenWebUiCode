# ADE / Harness — Agentic Development Environment

This fork operates as an **ADE (Agentic Development Environment)** — *The AI
Orchestrator*: runs execute on the server independent of any client, are
extensible through documented seams, and controllable from multiple surfaces
(web, headless, JSON-RPC, ACP, MCP, terminal, webhooks). Inspired by
`deepseek-ai/deepseek-harness` (plugin kernel + event seams) and
`afonsoft/agent-harness` (multiplexed terminal + MCP server + attention).

Implemented by `SPEC-20261021-harness-seams` (seams + surfaces + webhooks +
teams) and `SPEC-20261022-ade-surfaces` (ADE terminal + MCP + attention).

## 1. Server-side background execution

- `ChatRunDispatcher` processes queues on the server — closing the tab does
  not kill a run.
- Durable snapshots + events (`RunEventWriter`) allow replay when reopening
  from any device (`GET /api/v1/chats/{id}/runs/{runId}/stream?lastSeq=`).
- A sweeper resumes orphaned `Running`/`Queued` runs after restart
  (`ChatRuns:ResumeStaleSeconds`, default 60s).
- Web push notifies when a run finishes while the app is closed
  (`WebPushChatRunNotifier`).

## 2. Event seams — `IAgentRunObserver` + `IRunInterceptor`

Two internal extension points, both via DI:

```csharp
// Read-only: receives every run event, cannot mutate it.
// A slow/faulty observer is isolated with a timeout (5s) + LogWarning — never aborts a run.
public interface IAgentRunObserver
{
    Task OnRunEventAsync(AgentRunEvent e, CancellationToken ct);
}

// Ordered waterfall at decision points (pre-step, tool.call).
// May veto; a veto becomes the durable interceptor_veto event.
public interface IRunInterceptor
{
    int Order { get; }
    Task<InterceptionResult> OnEventAsync(
        InterceptionContext ctx, CancellationToken ct);
}
```

- Event types: `run.started`, `run.completed`, `run.failed`,
  `run.stopped`, `run.steer`, `interceptor.veto`, `step_started`,
  `step_finished`, `tool_call`, `tool_result`, `messages_added`,
  `content_delta`, `phase`, `note`, `snapshot`.
- Payloads go through the `SecretScrubber` — observers never see secrets.
- Built-ins: `TelemetryObserver` (logs/metrics), `AttentionObserver`
  (persisted inbox), `RepeatToolGuard` (vetoes the 3rd identical
  consecutive tool call — loop protection).

## 3. System prompt contributors — `ISystemPromptContributor`

System prompt assembly moved out of the monolithic method into DI-ordered
contributors (`Order`). The original blocks became `BaseAgentContributor`,
`AlwaysAllowContributor`, `CustomModelContributor`, persona, memory, tools,
workspace, mode directives, etc. — the effective order is byte-identical to
before (golden test), so adding new context means registering a contributor
without touching the pipeline.

## 4. Control surfaces

### 4.1 Headless one-shot

```bash
dotnet run --project src/OpenWebUI.Api -- --headless \
  --user-email admin@local --prompt "summarize the README" [--model m] [--chat-id c]
```

- Runs the request on the server and exits; stdout is **JSON-lines**
  (`JsonLinesRunObserver`) — one event per line.
- Exit codes: `0` success, `2` usage/user error, `1` run failure.

### 4.2 JSON-RPC 2.0 — `POST /api/v1/rpc`

```jsonc
{ "jsonrpc": "2.0", "id": 1, "method": "runs.start",
  "params": { "chatId": "...", "content": "hi" } }
```

- Methods: `chats.create`, `runs.start`, `runs.get`.
- Standard errors: `-32700` parse, `-32600` invalid request, `-32601`
  unknown method, `-32602` invalid params, `-32000` domain error.
- Same Bearer auth as REST endpoints.

### 4.3 ACP — `src/OpenWebUI.Acp` (Agent Client Protocol)

**stdio** host for editors (Zed and similar): `session/new` creates a chat,
`session/prompt` enqueues a run and durable events become `session/update`.
Bearer via `OPENWEBUI_ACP_TOKEN`, URL via `OPENWEBUI_URL`.

### 4.4 MCP — `src/OpenWebUI.Mcp` (Model Context Protocol)

**stdio** MCP server — exposes the product as a tool for external agents
(Claude Code, OpenCode):

| Tool | Action |
|---|---|
| `chats.list` / `chats.create` | list / create chats |
| `runs.start` / `runs.status` / `runs.stop` | control runs |
| `teams.status` | roster/tasks of a team |
| `attention.list` | unified attention inbox |

```bash
export OPENWEBUI_URL=http://localhost:8080
export OPENWEBUI_API_KEY=sk-...   # required — fail-closed without it
dotnet run --project src/OpenWebUI.Mcp
```

## 5. Webhooks v2 — `POST /api/v1/hooks/{token}`

Extension of the existing `AutomationHook`:

- `Provider`: `generic` (`X-Event-Type`/`X-Delivery-Id`) or `github`
  (`X-GitHub-Event`/`X-GitHub-Delivery`).
- `Secret`: HMAC-SHA256 in `X-Hub-Signature-256`, timing-safe verification;
  secret-less hooks still accepted (flagged `unsigned` in the audit).
- `EventsJson`: event filter — out-of-list events become `filtered`
  deliveries.
- `PromptTemplate`: `{{path.dotted}}` rendered against the JSON payload.
- Optional repo/persona/mode binding for the triggered run.
- `AutomationHookDelivery`: per-delivery audit (`accepted`/`rejected`/
  `filtered`) + idempotency — a repeated delivery id returns the original
  run.
- Payload capped at 256 KiB (413 above). Audit via
  `GET /api/v1/hooks/{id}/deliveries`.

## 6. Agent teams

No Team entity — the existing chat hierarchy is the roster:

- `Chat.TeamId` = root chat id; `TeamRole` = `leader` | `member`.
- `delegate_task` with `"team": true` materializes the team on the root
  (`EnsureRootTeamAsync`, idempotent); `"resume_chat_id"` resumes an
  existing member instead of creating a new chat.
- `TeamTask` — durable shared task list (`pending`/`in_progress`/`done`).
- Tools: `team_task` (create/claim/complete), `team_message` (a durable
  message to an idle member becomes a `ChatMessage` + notification),
  `team_status` (roster + tasks + active runs).
- Inbox = the existing steer/queue; status at `GET /api/v1/chats/{id}/team`.
- `TeamService.ResolveTeamIdAsync` walks `ParentChatId` for legacy chats
  without the column.

## 7. ADE terminal

- `POST /api/v1/terminal/sessions` accepts `chatId`, `runId`, `workdir` —
  resolution order: explicit workdir (must be inside `data/`) → isolated
  run worktree → chat-bound workspace → default user workspace.
- `GET /api/v1/terminal/sessions?chatId=` filters by chat.
- **`/ws/terminal`** multiplexes N sessions on one connection:
  `{op, sid, ...}` frames with `attach`/`detach`/`input`/`resize`/
  `create`/`kill`; replies `sessions`/`output`/`exit`/`created`/`error`.
  Disconnect only detaches — sessions survive for reattach (64 KiB
  scrollback).
- Legacy `/ws/terminal/{id}` endpoint keeps working.
- The **Terminal** tab inside the chat view opens a session bound to the
  conversation's worktree.
- Deleting a chat kills bound PTYs **before** removing directories.

## 8. Attention inbox

One badge fed by two sources:

- **Persisted** (`UserAttention`): `run:{id}` (`run_completed`/`run_failed`
  via `AttentionObserver`), `term:{id}` (spontaneously exited PTY —
  kill/reap/delete does not raise an item).
- **Synthetic**: `chat:{id}` (`approval`) — chats with a run awaiting
  decision, from the in-memory `ChatRunApprovals`.

```
GET  /api/v1/attention            → merged list (approvals first)
POST /api/v1/attention/{key}/seen → dismisses a persisted item (404 if absent)
GET  /api/v1/chats/attention/count → approvals + persisted (badge)
```

- Upsert by `(UserId, Key)` — re-raising the same source never duplicates.
- Deleting a chat wipes its bound items.
- The observer writes through an independent DI scope — it never flushes
  the run's shared `DbContext`.

## 9. Workspace cleanup on chat delete

Guaranteed order in `ChatEndpoints.ReleaseChatDiskAsync`:

1. `TerminalSessionManager.KillForChatAsync` — PTYs before removing dirs.
2. Per-run isolated worktrees — `git worktree remove --force` + `prune`
   (direct removal fallback if git fails).
3. `CheckpointService.DeleteForWorkdir` — checkpoint repos and file
   manifests for each workdir.
4. `WorkspaceRepoService.ReleaseChatWorkspaceAsync` — binding + main
   directory (base repo preserved until worktrees are gone).

Same routine on delete-all. Locked directories are left for the retention
sweeper (best-effort).

## Quick reference

| Surface | Detail |
|---|---|
| Observer | `IAgentRunObserver` via DI (scoped) |
| Interceptor | `IRunInterceptor` (`Order`, veto) |
| Prompt | `ISystemPromptContributor` (`Order`) |
| Headless | `--headless --user-email --prompt` |
| RPC | `POST /api/v1/rpc` |
| ACP | `open-webui-acp` binary (stdio) |
| MCP | `dotnet run --project src/OpenWebUI.Mcp` |
| Webhook | `POST /api/v1/hooks/{token}` + `X-Hub-Signature-256` |
| Team | `GET /api/v1/chats/{id}/team` |
| Terminal | `/ws/terminal` (mux) · `/ws/terminal/{id}` (legacy) |
| Attention | `GET /api/v1/attention` · `POST /{key}/seen` |
