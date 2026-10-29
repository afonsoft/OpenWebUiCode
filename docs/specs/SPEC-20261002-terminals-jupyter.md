# SPEC-20261002-terminals-jupyter

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `terminals-jupyter` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261002-terminals` |
| Ticket | Issue #60 |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** executar código em kernels Jupyter/terminais server-side configurados pelo admin
**So that** tenho o Open Terminal do upstream, além da execução client-side já existente.

**Problem context:**
Upstream `terminals.py` proxeia para servidores Jupyter/code-server (`/{server_id}/{path}` HTTP + WS `/api/terminals/{id}`). Hoje só há execução no navegador (Pyodide/JS Worker — AD-0005).

## 2. Scope

**In scope:**
- `TerminalServer` config admin: `{id, name, url, auth_type (none|token|password), key mascarada, type: jupyter|pty}`.
- `GET /api/v1/terminals/` lista servidores; proxy autenticado `/api/v1/terminals/{server_id}/{**path}` (HTTP) + WS `/api/v1/terminals/{server_id}/api/terminals/{session}` para Jupyter.
- Code execution engine `jupyter` como opção além de `pyodide`/`worker` — selecionável em Admin → code-execution (gancho no SPEC admin-configs-v2).
- Cliente: quando engine=jupyter, blocos de código enviam para o kernel via endpoints do proxy.

**Out of scope:**
- Spawn/gerenciar o processo Jupyter (admin aponta um servidor existente); PTY direto no host (risco de segurança — só proxy para servidor externo).

## 3. Technical Context

**Where:** `Domain` (TerminalServer), `Infrastructure` (TerminalProxyService — YARP-style manual ou HttpClient + WS tunnel), `Api` (`TerminalEndpoints`), `Client` (`codeexec.js` modo jupyter).

**Files to read:**
- `src/OpenWebUI.Client/wwwroot/js/codeexec.js`
- `src/OpenWebUI.Api/Program.cs` (pipeline + WS usage)
- agent-harness `Program.cs` `/vscode` proxy como referência de WS tunnel

**Files to create/modify:**
```text
src/OpenWebUI.Domain/Entities/TerminalServer.cs
src/OpenWebUI.Application/Contracts/TerminalContracts.cs
src/OpenWebUI.Infrastructure/Services/TerminalProxyService.cs
src/OpenWebUI.Api/Endpoints/TerminalEndpoints.cs
src/OpenWebUI.Client/wwwroot/js/codeexec.js
tests/OpenWebUI.Api.Tests/TerminalEndpointsTests.cs
```

## 4. Requirements

### RF-001: Config de servidores
- **Description:** CRUD admin de terminal servers; keys mascaradas.
- **Input → Output:** `{name,url,auth}` → server `{id,...}`

### RF-002: HTTP proxy
- **Description:** `/{server_id}/{path}` encaminha método/body/headers com auth do servidor; prefixo removido + `X-Forwarded-Prefix` (padrão do proxy `/vscode` do agent-harness).
- **Rules:** allowlist de paths (`/api/*` do Jupyter); servidor off → `502`.

### RF-003: WS tunnel
- **Description:** `/api/terminals/{session}` abre WS upstream e bombeia frames bidirecional; timeout/idle configurável.
- **Input → Output:** WS frames relay

### RF-004: Engine jupyter
- **Description:** `code_execution.engine=jupyter` + `server_id` → cliente executa bloco via kernel; `pyodide`/`worker` intactos.
- **Input → Output:** código → stdout/stderr do kernel

**Invariants:** proxy nunca expõe key do terminal server; path sanitizado (sem `..`, sem scheme injection — `_sanitize_proxy_path` do upstream).

## 5. API Contract

`GET /api/v1/terminals/` · `GET|POST|PUT|DELETE /api/v1/terminals/config` (admin) · `* /api/v1/terminals/{id}/{**path}` · `WS /api/v1/terminals/{id}/api/terminals/{session}`
**Auth:** Bearer; config admin

## 6. Critérios de Aceite

- [x] Proxy repassa GET/POST ao mock Jupyter.
- [x] WS tunnel ecoa frames (mock ws server).
- [x] Path traversal bloqueado.
- [x] Testes NUnit + (se viável) teste de WS.

## 7. Notas

Implementar proxy manual com `HttpClient`+`WebSocket` ou adotar YARP — recomendo YARP (mesma lib do agent-harness `/vscode`) se dependência aceitável.

## 8. Delivered

- **Decisão YARP**: não adotado — adicionaria dependência + `IProxyConfigProvider` dinâmico para um único padrão de rota; proxy manual com HttpClient/`ClientWebSocket` cobre o escopo com ~250 linhas (o agent-harness usou YARP por ser um subtree inteiro de aplicação).
- `TerminalServerConfig` persistido em `terminals.servers` via ConfigService (sem migration): `{id,name,url,auth_type(none|token|password),key,type(jupyter|pty)}`; key nunca serializada (resposta expõe `has_key`; `********` preserva no update).
- `TerminalProxyService`: `SanitizePath` (só `api/*`, sem `..`/`%`), `ForwardAsync` (streaming, 502 quando fora), `TunnelAsync` (ClientWebSocket + pump bidirecional, idle 10min, sub-protocolo propagado, key no header upstream).
- `TerminalEndpoints`: `GET /api/v1/terminals/` (lista sem key) + `/config` admin (GET/POST/DELETE) + catch-all `/{id}/{**path}` (HTTP ou WS conforme upgrade). `UseWebSockets` habilitado; `access_token` query aceito no path `/api/v1/terminals` (mesmo padrão do `/ws` SignalR).
- Cliente: `codeexec.js` — engine `jupyter` habilitada + servidor cadastrado → Python executa via kernel real (POST /api/kernels → WS channels execute_request → stream/error/idle → DELETE kernel); fallback Pyodide se kernel falhar. Admin → Configurações: lista + formulário de terminal servers.
- Testes: `TerminalEndpointsTests` (5 casos — CRUD admin + sem key exposta, GET/POST proxied com auth injetada, traversal/404/502, **túnel WS real** com Kestrel echo).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/75 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/60
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/48
