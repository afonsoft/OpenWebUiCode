# SPEC-20261022-ade-surfaces

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `ade-surfaces` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261022-ade-surfaces` |
| Ticket | [#488](https://github.com/afonsoft/OpenWebUiCode/issues/488) |
| Status | Completed |
| Parent | `SPEC-20261021-harness-seams` (merged as PR #487) — benchmark [afonsoft/agent-harness](https://github.com/afonsoft/agent-harness); direção: ADE (Agentic Development Environment) / AI Orchestrator |

## 1. User Story

**As a** operador desta plataforma como ADE (Agentic Development
Environment)
**I want** as superfícies reaproveitáveis do agent-harness — terminal
bindado à conversa/run com abas multiplexadas, o produto exposto como
servidor MCP para agentes externos e um cockpit com modelo de atenção —
**So that** o produto vira um orquestrador dirigível por humanos (web,
abas de terminal contextualizadas) e por agentes (MCP), sem perder o
estado ao alternar entre dispositivos.

**Problem context:**
O fork já tem execução em background completa (dispatcher server-side,
replay por snapshot+delta, web push — verificado em
`ChatRunDispatcher`/`ChatRunExecutor`). O terminal PTY existe
(`TerminalPtyEndpoints` + `TerminalSessionManager` com scrollback 64K e
reattach), mas as sessões são **livres**: não sabem a qual chat/run
pertencem nem em qual workdir caem, e cada sessão exige um WebSocket
próprio — o agent-harness multiplexa N abas numa conexão e abre sessões
bindadas (`OpenForRun`, `OpenForConversation`). Não existe superfície MCP:
agentes externos (Claude Code, OpenCode, outros) não conseguem nos dirigir
como ferramenta. Runs/terminais não expõem modelo de atenção — o usuário
não sabe o que terminou/parou/espera input sem abrir cada conversa.

## 2. Scope

**In scope:**

- **A. Terminal bindado + abas multiplexadas**
  - `POST /api/v1/terminal/sessions` aceita `chatId`/`runId`/`workdir`
    opcionais → resolve o workdir via `WorkspaceRepoService`
    (`chat:{id}:workspace.repo` ou worktree isolado da run).
  - Sessão persiste `OwnerChatId`/`OwnerRunId` — `GET /sessions?chatId=`
    filtra; delete de chat mata as sessões bindadas.
  - Multiplex: uma conexão WS (`/ws/terminal`) carrega N sessões com
    frames `{sessionId, type: input|output|resize|exit}` — mantém o
    endpoint por-sessão para compat.
  - UI: aba "Terminal" na view do chat abre sessão bindada (estilo
    `RunTerminal.razor` do agent-harness); lista de sessões do chat com
    badge de estado.
- **B. MCP server (`OpenWebUI.Mcp`)**
  - Novo projeto stdio (mesmo molde do `OpenWebUI.Acp`): expõe
    `chats.list/create`, `runs.start/status/stop`, `teams.status`,
    `memories.add` como tools MCP; auth via `OPENWEBUI_MCP_TOKEN`
    (Bearer sk-\* do próprio usuário).
  - Reusa `ChatRunEndpoints.EnqueueMessageAsync` + `ChatRunDispatcher` —
    nenhum caminho novo de execução.
- **C. Attention model + cockpit mínimo**
  - Chaves de atenção derivadas (`run:{id}`, `term:{id}`): run completed/
    failed/waiting-input e sessão de terminal com saída não lida viram
    itens num `GET /api/v1/attention` — lista ordenada, `seenAt`
    persistido por usuário.
  - Badge global na sidebar com contagem; cockpit v0 = página que lista
    runs ativas + atenção + sessões de terminal (reuso dos endpoints
    existentes, sem hub novo).

**Out of scope:**

- Execução remota/sandbox de terminal (SSH provider seam) — continua fora.
- CLI/MAUI nativos (agent-harness tem `Taskboard.Cli`/`Taskboard.Maui`) —
  deferido; headless+RPC cobrem o caso.
- Cockpit completo com gráficos/metrics — v0 é lista de atenção.
- Mudanças no protocolo ACP existente.

## 3. Technical Design

### A. Terminal bindado + abas

`TerminalSessionManager` (já existe, `src/OpenWebUI.Infrastructure/
Terminal/`): `SessionEntry` ganha `OwnerChatId`/`OwnerRunId`/`Workdir`.
`Create(userId, request)` resolve nessa ordem: `request.Workdir` explícito
→ worktree isolado da run (`WorktreeService.ResolveIsolated`) → binding do
chat (`WorkspaceRepoService.ResolveWorkdirAsync`) → root do usuário.
Valida que o workdir fica dentro de `DataPaths.Root` (path traversal
guard — reusar `IsPathInside` existente). Sessão sem binding mantém o
comportamento atual.

`TerminalSession` vira entidade EF? **Não** — sessões já são voláteis por
design (reattach por id em memória); persistir binding bastaria mas o
reattach pós-restart não funciona hoje mesmo. Mantém em memória + índice
`chatId → sessionIds` no manager.

Multiplex: `app.Map("/ws/terminal", ...)` — handshake lista sessões do
usuário, frames JSON `{sid, op, data}` (base64 no payload de output para
binário-safe). `terminal.js` vira multiplexador; `TerminalView.razor`
renderiza abas (xterm por aba, instância única do fit addon).

Delete de chat: `ReleaseChatDiskAsync` (adicionado na SPEC anterior) ganha
`terminals.KillForChat(chatId)` antes do release do worktree — ordem:
mata PTY → remove worktrees → checkpoints → binding.

### B. MCP server

`src/OpenWebUI.Mcp/` — console stdio, `Microsoft.Extensions.Hosting` +
protocolo MCP JSON-RPC sobre stdin/stdout (sem pacote externo se o
protocolo mínimo couber: `initialize`, `tools/list`, `tools/call`; senão
`ModelContextProtocol` NuGet oficial — decidir no T1). Tools:

| Tool | Implementação |
| --- | --- |
| `chats.list` | `GET /api/v1/chats` via HttpClient interno → ou DI direto no AppDbContext quando self-hosted (`MCP:Mode=inproc`) |
| `chats.create` | idem `POST /chats` |
| `runs.start` | `ChatRunEndpoints.EnqueueMessageAsync` compartilhado |
| `runs.status` / `runs.stop` | endpoints existentes |
| `teams.status` | `GET /chats/{id}/team` |
| `attention.list` | RF-C |

Modo `inproc` (mesmo processo, DI direto — default quando embutido no Api)
e modo `remote` (HTTP para `OPENWEBUI_URL` + `OPENWEBUI_API_KEY`) —
análogo ao `Taskboard.Mcp` que fala com o Server via API.

### C. Attention model

`AttentionItem` (memória + persistência leve): tabela `UserAttention`
(`UserId`, `Key`, `Kind` (run|terminal|team), `RefId`, `Title`,
`UpdatedAt`, `SeenAt`). Escritores: `ChatRunExecutor.FinishAsync`/
`FailAsync`/waiting-input → upsert; `TerminalSessionManager` → flag
`Unread` quando saída chega sem viewer anexado. `GET /api/v1/attention`
retorna não-vistos + counts; `POST /attention/{key}/seen` marca. `IAgentRunObserver`
(seam A da SPEC anterior) pode alimentar — zero acoplamento novo no
executor.

### Ordem de execução

| Task | Conteúdo |
| --- | --- |
| T1 | A: binding + filtro por chat + kill-on-delete + testes |
| T2 | A: multiplex WS + `terminal.js`/UI tab + testes E2E leves |
| T3 | B: `OpenWebUI.Mcp` inproc (tools 1-4) + testes de protocolo |
| T4 | C: `UserAttention` (migration) + escritores + endpoint + badge |
| T5 | B: `attention.list` no MCP + docs (`docs/` + `.env.exemplo`) |
| T6 | Drift guards + build Release + suíte completa |

## 4. Acceptance Criteria

- [x] `POST /terminal/sessions {chatId}` abre PTY no workdir do workspace do chat; `?chatId=` filtra sessões.
- [x] Delete de chat mata sessões bindadas antes de remover o worktree (ordem verificada em teste).
- [x] Uma conexão WS multiplexa ≥2 sessões; input/output/resize roteados por `sid`; endpoint legado por-sessão continua funcionando.
- [x] Tab "Terminal" no chat abre/reabre sessão bindada e mostra scrollback.
- [x] `initialize` + `tools/list` + `tools/call` no MCP stdio — `chats.create`/`runs.start` exercitados contra a API real via TestServer.
- [x] MCP rejeita tool call sem token válido; nenhum segredo vai pro stdout/log.
- [x] Run completada com app em outra aba gera item de atenção; `POST /attention/{key}/seen` limpa; badge reflete contagem.
- [x] Migration EF aplicada em base legada (baseline `webui.db`).
- [ ] `dotnet build OpenWebUI.slnx -c Release` + suíte completa verde; coverage ≥ baseline.
- [ ] `tools/check-*.py` (css, a11y, i18n, spec-status) verdes.

## 5. Risks & Open Questions

- **Multiplex vs. conexões atuais**: manter ambos (por-sessão + multiplex) dobra caminhos de teste — aceito pela compat; alternativa seria versionar `/ws/terminal/v2`.
- **MCP inproc vs remote**: inproc evita HTTP self-call mas acopla o projeto ao DI interno — começar `inproc`, `remote` só se pedirem deploy separado.
- **Attention dedup**: chave `run:{id}` faz upsert — runs com múltiplos fins (fail→retry→done) atualizam o mesmo item; `SeenAt` preserva o "visto" entre updates? Decisão: update reseta `SeenAt` quando `Kind` muda de estado (done→failed), senão mantém.
- **Terminal em worktree deletado**: se o workdir some (delete de chat/release), PTY fica órfão apontando pra inode morto — mitigado por `KillForChat` + check `Directory.Exists(workdir)` no Create.
