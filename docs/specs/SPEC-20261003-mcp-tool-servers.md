# SPEC-20261003-mcp-tool-servers

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `mcp-tool-servers` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261003-mcp-tool-servers` |
| Status | `Completed` |

## 1. User Story

**As a** admin do Open WebUI .NET
**I want** registrar servidores MCP (stdio, HTTP/streamable) e descobrir/usar suas tools
**So that** o ecossistema de tool-servers do upstream (MCP) exista no port .NET.

**Problem context:**
O fork só suporta duas formas de tool: HTTP POST genérico (`Tool.Url`) e código Python em subprocess (`Tool.Code`, `PythonToolExecutor`). O upstream suporta **Tool Servers** e o ecossistema migrou em massa para MCP — sem MCP, os servidores mais relevantes (filesystem, postgres, fetch, mcp-* oficiais) ficam inacessíveis. O SDK oficial `ModelContextProtocol` (NuGet) fornece o cliente stdio + streamable HTTP.

## 2. Scope

**In scope:**
- Entidade `McpServer` (id, name, transport `stdio|http`, command/args ou url, headers mascarados, `enabled`, env var refs por nome — nunca valores).
- CRUD admin em `/api/v1/mcp/servers` + aba em Admin → Configurações.
- `McpClientService`: conecta ao server, `tools/list` → converte JSON Schema de cada tool para function spec OpenAI e expõe como tools virtuais `mcp:{server}:{tool}`.
- `tools/call` dentro do `ToolExecutor`: roteia function name `mcp:{server}:{tool}` ao server correto; resultado vira texto para o modelo (truncado em 4000 chars como hoje).
- Timeout 30s por chamada; server inacessível → erro legível, nunca crash do completion.
- Cache curto (60s) de `tools/list` por server — discovery não repete a cada mensagem.

**Out of scope (documentado):**
- Servidor MCP hospedado por nós (somos cliente, não server).
- Autenticação OAuth interativa do MCP — só headers estáticos/api-key.
- Sampling/elicitation (o servidor pedir LLM ao cliente) — rejeitar com capability vazia.
- resources/prompts do MCP — fase 2 se houver demanda.

## 3. Technical Context

**Where:** `Domain` (McpServer entity + migration), `Infrastructure` (`McpClientService` com SDK `ModelContextProtocol`), `Api` (`McpEndpoints` admin), `Client` (aba MCP no Admin), `ToolExecutor` (roteamento `mcp:*`).

**Files to read:**
- `src/OpenWebUI.Infrastructure/Services/ToolExecutor.cs` (roteamento de function call)
- `src/OpenWebUI.Infrastructure/Services/PythonToolExecutor.cs` (subprocess pattern)
- `src/OpenWebUI.Api/Endpoints/ToolEndpoints.cs` (CRUD de tools — MCP servers ficam em CRUD separado, admin-only)

**Files to create/modify:**
```text
src/OpenWebUI.Domain/Entities.cs            # +McpServer
src/OpenWebUI.Application/Contracts/McpContracts.cs
src/OpenWebUI.Infrastructure/Services/McpClientService.cs
src/OpenWebUI.Infrastructure/Migrations/    # +migration
src/OpenWebUI.Api/Endpoints/McpEndpoints.cs
src/OpenWebUI.Client/Pages/Admin.razor      # aba MCP
tests/OpenWebUI.Api.Tests/McpTests.cs       # server fake stdio/http
```

## 4. Requirements

### RF-001: CRUD de servers
- **Description:** admin registra server por nome + transport. stdio: `command` + `args[]` + `env` (nomes de env vars existentes, ex. `BRAVE_API_KEY` — valores nunca salvos). http: `url` + headers opcionais (valor mascarado na leitura, como conexões LLM).
- **Input → Output:** `{name, transport, command?, args?, env?, url?, headers?}` → server

### RF-002: Discovery
- **Description:** `POST /api/v1/mcp/servers/{id}/refresh` chama `tools/list` e materializa tools como `Tool` virtuais (`SpecJson` gerado do inputSchema, `Url` interno `mcp://{serverId}/{toolName}`) ou tabela `McpTool` separada — decidir na implementação; a UI lista tools descobertas por server.
- **Rules:** server desabilitado ou inacessível → tools ficam indisponíveis, erro em `LastError`.

### RF-003: Execução
- **Description:** no `ToolExecutor.ExecuteAsync`, function name com prefixo `mcp:` roteia para `McpClientService.CallToolAsync(serverId, tool, args)`; resultado `content[]` (text/image) serializado para texto.
- **Rules:** timeout 30s; erro de protocolo → mensagem de erro para o modelo, nunca exceção no SSE.

### RF-004: Segurança
- **Description:** stdio spawna processo com env permit-list (só as vars nomeadas pelo admin); SSRF — url http validada como as conexões LLM (sem metadata IPs). Secrets somente por referência a env/secret name.

**Invariants:** SDK `ModelContextProtocol` oficial; nenhum código de terceiros executa fora do subprocess do próprio server; secrets mascarados em todas as leituras.

## 5. API Contract

`GET|POST|PUT|DELETE /api/v1/mcp/servers[/{id}]` · `POST /api/v1/mcp/servers/{id}/refresh` · `GET /api/v1/mcp/servers/{id}/tools`
**Auth:** admin Bearer.

## 6. Critérios de Aceite

- Server stdio fake (`npx -y @modelcontextprotocol/server-everything` ou binário local de teste) registra, lista tools e executa `tools/call` num completion.
- Server HTTP idem via streamable-http transport.
- Desligar `enabled` remove tools do function-calling sem deletar o server.
- Secret de header nunca retornado em GET.
- Teste E2E: chat com tool MCP selecionada → `tools/call` real → resposta do modelo cita o resultado.
