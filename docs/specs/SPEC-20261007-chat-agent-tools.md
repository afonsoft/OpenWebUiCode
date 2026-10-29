# SPEC-20261007-chat-agent-tools

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-agent-tools` (P4 — tools built-in do harness no open-webui) |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261007-chat-agent-tools` |
| Ticket | `GAP-chat-agent-tools` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** tools built-in de agente (gerar imagem, executar código, shell com jobs, terminal interativo) selecionáveis no chat como no harness
**So that** o chat faz o trabalho de um agente real, não só pergunta/resposta.

**Problem context:**
As tools do open-webui hoje são só "function calling" de usuário (código Python `class Tools` em subprocess, HTTP POST, MCP). Não há tool built-in de imagem (o botão de imagem existe mas não é tool do modelo), nem execução de código ad-hoc pela LLM (o code exec é Jupyter/Pyodide sob demanda do usuário), nem shell, nem terminal PTY interativo. O harness expõe `generate_image`, `code_interpreter`, `shell_exec` (+ jobs em background), `fetch_url`, `web_search` e terminal PTY (xterm.js).

## 2. Scope

**In scope:**
- `IBuiltinChatTool` + `BuiltinToolRegistry`: tools de sistema selecionáveis por ToolIds com prefixo `builtin:` no seletor de tools do chat; executam pelo mesmo `ToolExecutor` com `ChatToolContext` (UserId, workspace dir do usuário, provider base/key da conversa).
- `generate_image`: chama `/v1/images/generations` do provider de imagem configurado (reusa `ImageGenerationService`), persiste arquivo e anexa `imagePath` na tool_result → imagem inline no card do transcript.
- `code_interpreter`: snippet em `python3|node` num temp dir por usuário, timeout, árvore de processo morta no fim, saída truncada (16KB) e com scrub de secrets.
- `shell_exec`: comando no workspace do usuário com classifier de risco (recusa `rm -rf /`, `sudo`, egresso) e `run_in_background` → job durável (`ChatJob`) com `job_list`/`job_output`/`job_kill`.
- Terminal PTY na UI: página/painel com xterm.js ↔ WebSocket PTY (`/ws/terminal/{id}`), spawn via `LocalTerminalSpawner`/novo `TerminalSessionManager`, múltiplas abas, resize, kill; feature flag `TERMINAL_ENABLED` (default off, admin ativa).
- `fetch_url` e `web_search` built-in (web_search já existe como opção de request — virar tool para o modelo decidir quando usar).

**Out of scope:**
- Sandbox/container real — código roda no host com confinamento best-effort (documentado); admins desligam via config.
- Delegação/sub-agents (`run_agent`, `delegate`) — fase posterior.
- Agendamentos/jobs com cron pelo chat (P4 posterior).
- Terminal por canal/grupo — por usuário.

## 3. Technical Context

**Where the change happens:**
`Domain` (`ChatJob`, `TerminalSession` se persistir estado), `Infrastructure` (`BuiltinToolRegistry`, tools, `SecretRedactor`, `CommandRiskClassifier`, `TerminalSessionManager`, `PtySession`), `Api` (endpoints terminal + jobs), `Client` (`TerminalView.razor` + `terminal.js` xterm, seletor de tools ganha seção built-in).

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/Services/{ToolExecutor,PythonToolExecutor,McpClientService,LocalTerminalSpawner,TerminalProxyService,ImageGenerationService}.cs`
- `src/OpenWebUI.Api/Endpoints/{TerminalEndpoints,ImageEndpoints}.cs`
- `src/OpenWebUI.Client/wwwroot/js/codeexec.js`, `Components/MessageBubble.razor`
- Referência: `repos/agent-harness` `src/Taskboard.Integrations/Chat/Tools/{GenerateImageTool,CodeInterpreterTool,ShellExecTool,FileSystemTools,FetchUrlTool,WebSearchTool}.cs`, `Terminal/{PtySession,TerminalSessionManager}.cs`, `wwwroot/js/terminal.js`, `lib/xterm/`

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Chat/Tools/{IBuiltinChatTool,BuiltinToolRegistry,GenerateImageTool,CodeInterpreterTool,ShellExecTool,FetchUrlTool,WebSearchTool}.cs
src/OpenWebUI.Infrastructure/Chat/{SecretRedactor,CommandRiskClassifier}.cs
src/OpenWebUI.Infrastructure/Terminal/{IPtySession,PtySession,TerminalSessionManager}.cs
src/OpenWebUI.Api/Endpoints/TerminalPtyEndpoints.cs (+WS /ws/terminal/{id}, sessions)
src/OpenWebUI.Client/Components/TerminalView.razor, wwwroot/js/terminal.js, wwwroot/lib/xterm/*
src/OpenWebUI.Client/Components/ChatView.razor (seção builtin no seletor)
tests/OpenWebUI.Api.Tests/{BuiltinToolsTests,TerminalPtyTests}.cs
```

## 4. Requirements

### RF-001: Registry de tools built-in
- **Description:** tools de sistema com ids `builtin:<name>` listadas junto das do usuário no seletor (badge "sistema"); `ToolExecutor` resolve via `BuiltinToolRegistry` quando o id tem prefixo.
- **Rules:** respeitam o gate de aprovação do P3 (`RequiresApproval`); desligáveis por config `BUILTIN_TOOLS_DISABLED` por nome.
- **Input → Output:** ToolIds `["builtin:generate_image"]` → tool disponível no loop

### RF-002: `generate_image`
- **Description:** `prompt`+`size` opcional → `POST {imageBaseUrl}/images/generations` com o modelo de imagem configurado → salva em `data/images/{userId}/` → tool_result `{imagePath}` → UI renderiza `<img>` inline no card.
- **Rules:** sem provider de imagem → resultado de erro claro; arquivo nunca fora do diretório de dados.
- **Input → Output:** prompt → imagem persistida + renderizada no transcript

### RF-003: `code_interpreter`
- **Description:** `{language: python3|node, code}` → escreve snippet em `data/workspaces/{userId}/.chat-tmp/`, executa com timeout (60s default, max 300), mata árvore, trunca saída 16KB, scrub de secrets.
- **Rules:** runtime ausente → erro claro; código nunca executa fora do temp dir do usuário.
- **Input → Output:** snippet → `{stdout, stderr, exitCode}`

### RF-004: `shell_exec` + jobs
- **Description:** `{command, timeout_seconds?, run_in_background?}` → `CommandRiskClassifier` (Dangerous → refused; fora do workspace → refused) → executa ou cria `ChatJob` durável; `job_list/job_output/job_kill` consultam.
- **Rules:** jobs sobrevivem à run; output truncado + scrub; kill só do dono.
- **Input → Output:** comando → resultado ou jobId

### RF-005: Terminal PTY embutido
- **Description:** `POST /api/v1/terminal/sessions` cria PTY (shell do usuário no workspace); `GET /ws/terminal/{id}` multiplexa IO via WS; UI com xterm.js (abas, resize via SIGWINCH, ✕ kill); `TerminalSessionManager` com idle-timeout e limite por usuário.
- **Rules:** flag `TERMINAL_ENABLED` off por default (admin); sessão PTY pertence ao UserId criador; reconexão anexa no buffer recente.
- **Input → Output:** abrir terminal → shell interativo no browser

### RF-006: `fetch_url` / `web_search` built-in
- **Description:** `fetch_url {url}` → GET com limite de tamanho + markdown-ish text; `web_search {query}` → reusa `WebSearchService` atual (engines já configuráveis) como tool, devolvendo resultados para o modelo citar.
- **Rules:** SSRF guard básico (bloquear RFC1918/loopback) no fetch; web_search sem engine configurada → erro claro.
- **Input → Output:** url/query → texto/resultados para o modelo

**Business rules / invariants:**
- Tudo executa no workspace por usuário (`data/workspaces/{userId}`) — sem escape por path.
- Secrets nunca aparecem em tool_result nem em SSE (scrub antes).
- Tools built-in mutáveis (`shell_exec`, `code_interpreter`, HTTP/MCP write) pedem aprovação conforme preset (P3).
- Terminal off por default — opt-in de admin em Settings → Admin.

## 5. API Contract

**Endpoint:** `POST /api/v1/terminal/sessions` · `GET|DELETE /api/v1/terminal/sessions/{id}` · `WS /ws/terminal/{id}` · `GET /api/v1/jobs` · `GET /api/v1/jobs/{id}/output` · `POST /api/v1/jobs/{id}/kill`
**Auth:** `Bearer` JWT (token por query no WS, como o hub atual)

**Response:** `201` sessão/job criado · `403` flag off ou outro usuário · `404` inexistente

## 6. Critérios de Aceite

- [ ] `builtin:generate_image` no seletor → modelo gera imagem que aparece inline no transcript.
- [ ] `builtin:code_interpreter` roda `print("ok")` e devolve stdout no card de tool.
- [ ] `shell_exec` recusa `rm -rf /` e comandos fora do workspace; `run_in_background` cria job consultável.
- [ ] Terminal PTY abre shell real com xterm.js: digita, vê saída, resize funciona, múltiplas abas.
- [ ] Todas as tools passam pelo gate de aprovação quando configuradas mutáveis.
- [ ] Testes: registry, classifier (dangerous/readonly/workspace), interp timeout/truncate, job lifecycle, PTY auth/isolamento.

## 7. Notas

xterm.js entra vendored em `wwwroot/lib/xterm/` (mesmo padrão do harness — sem CDN). PTY em .NET: sem pacote novo, `Process` + redirecionamento não dá PTY real — usar `openpty`/conpty via biblioteca (ex.: `Pty.Net` 0.x) ou fallback pipe-based documentado. `web_search` como tool reusa `WebSearchService`; verificar engines configuradas antes de expor no registry.

## Reconciliation

_Reconciliado em 2026-10-08 (SPEC-20261008-spec-status-reconciliation, Issue #203)._

| RF | Veredito | Evidência |
| --- | --- | --- |
| RF-001 Registry built-in | delivered | `Infrastructure/ChatTools/BuiltinToolRegistry.cs` + `IBuiltinChatTool`; ids `builtin:<name>` com badge "sistema"; `RequiresApproval` respeitado; `BUILTIN_TOOLS_DISABLED` por nome; `BuiltinToolsTests` |
| RF-002 generate_image | delivered | `Tools/GenerateImageBuiltinTool.cs` — prompt(+size) → provider de imagem → `data/images/{userId}/` → `{imagePath}` renderizado inline no card |
| RF-003 code_interpreter | delivered | `Tools/CodeInterpreterBuiltinTool.cs` — python3/node em `.chat-tmp` do workspace do usuário, timeout, kill da árvore, saída truncada + scrub; `PythonToolExecutorTests` |
| RF-004 shell_exec + jobs | delivered | `Tools/ShellExecBuiltinTool.cs` + `CommandRiskClassifier.cs` (Dangerous → refused; fora do workspace → refused) + `JobBuiltinTools.cs` (`job_list/job_output/job_kill`, jobs duráveis); `ChatToolsEdgeTests` cobre o classifier |
| RF-005 Terminal PTY | delivered | `Api/Endpoints/TerminalEndpoints.cs` + `TerminalSessionManager` (idle-timeout, limite por usuário, reconexão) + xterm.js com abas/resize/kill; flag `TERMINAL_ENABLED` off por default; `TerminalPtyTests` |
| RF-006 fetch_url / web_search | delivered | `Tools/FetchUrlBuiltinTool.cs` (SSRF guard RFC1918/loopback, limite de tamanho, texto markdown-ish) + `WebSearchBuiltinTool.cs` reusando `WebSearchService` |

