# SPEC-20261018-mcp-oauth

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `mcp-oauth` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261018-mcp-oauth` |
| Status | `Approved` |
| Parent | `docs/analise-opencode-v2.md` (P1.3) |

## 1. User Story

**As a** usuário conectando MCP servers remotos (ex.: GitHub, Notion, Linear)
**I want** autenticação OAuth do browser com PKCE, registro dinâmico de cliente (DCR/CIMD) e refresh automático
**So that** eu conecte servidores que exigem OAuth sem colar tokens manualmente — paridade com opencode v2 `mcp-servers` (remote OAuth, `oauth` config por server).

## 2. Scope

**In scope:**
- Discovery: `WWW-Authenticate`/`protected_resource` metadata → authorization server metadata (`/.well-known/oauth-authorization-server`).
- Authorization Code + PKCE (S256) com state; callback `GET /api/mcp/oauth/callback`.
- DCR (`/register`) como default; `clientId`/`clientSecret` opcionais por server override; CIMD (client_id metadata URL) aceito quando oferecido.
- Tokens por (userId, serverId) criptografados em repouso; refresh automático com mutex por server; 401 → marca `AuthRequired` e UI mostra botão "Autenticar".
- Timeout config por server (connect/tool call), já alinhado ao `timeout` do v2.

**Out of scope:**
- OAuth para servidores STDIO locais (não se aplica). Device flow (v2 tbm não expõe). Troca de credencial sem re-auth (rotação manual: revogar + re-autenticar).

## 3. Technical Context

**Where the change happens:**
`McpClientService` (transporte remote já existe — headers fixos hoje) → plug `ITokenProvider`; novo `McpOAuthService` orquestra authorize/callback/store/refresh; entidade `McpOAuthToken`; UI mostra estado e inicia o fluxo (window.open para authorize URL).

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/Services/McpClientService.cs`
- `src/OpenWebUI.Api/Endpoints/McpEndpoints.cs` (ou onde MCP CRUD mora)
- `src/OpenWebUI.Domain/Entities.cs` (McpServer entity)
- `src/OpenWebUI.Client/Pages/Admin/AdminMcp.razor` ou equivalente em Settings

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/McpOAuthService.cs        # NOVO: PKCE/DCR/store/refresh
src/OpenWebUI.Infrastructure/Services/McpClientService.cs       # Authorization header via token provider; 401 → AuthRequired
src/OpenWebUI.Domain/Entities.cs                               # McpOAuthToken {UserId,ServerId,Access,Refresh,Expiry,ClientInfo}
src/OpenWebUI.Api/Endpoints/McpOAuthEndpoints.cs               # NOVO: /authorize/{serverId}, /oauth/callback
src/OpenWebUI.Client/Components/McpServerCard.razor            # botão Autenticar + status
src/OpenWebUI.Application/Contracts/McpContracts.cs            # AuthState no DTO do server
tests/OpenWebUI.Api.Tests/McpOAuthServiceTests.cs              # NOVO (PKCE, DCR mock, refresh)
```

## 4. Requirements

### RF-001: Discovery + PKCE authorize
- **Description:** Ao autenticar: fetch `/.well-known/oauth-protected-resource` (se 401 indica) → authorization-server metadata → monta authorize URL com `response_type=code`, `code_challenge` S256, `state` assinado (serverId+userId), redirect `/api/mcp/oauth/callback`. Sem metadata → erro claro "server does not advertise OAuth".
- **Rules:** verifier/state guardados em cache curto (10min) server-side, nunca no client.

### RF-002: DCR / client fixo / CIMD
- **Description:** sem `clientId` configurado → tenta `/register` (DCR) com redirect_uri + client_name "open-webui"; se o metadata tiver `client_id_metadata_document` (CIMD) usa o client_id URL; config `clientId`+`clientSecret` por server pula DCR.

### RF-003: Callback, store, refresh
- **Description:** callback troca code→tokens (com code_verifier), persiste criptografado (mesma proteção das API keys), refresh quando `Expiry-30s`, mutex por server; falha de refresh → `AuthRequired` e header removido.

### RF-004: Superfície
- **Description:** DTO do server ganha `authState: none|required|authorized`; conectar com token injeta `Authorization: Bearer`. Tool call 401 → status required + evento na UI. Botão "Autenticar" abre popup; "Desconectar" revoga e apaga tokens.

## 5. Acceptance Criteria

- Servidor mock OAuth (metadata+DCR+token) completo e2e: authorize → callback → `tools/list` autenticado.
- Token expirado → refresh transparente antes do call.
- 401 mid-session → server marcado `required`, UI mostra CTA.
- Tokens nunca em log/serialização; testes cobrem PKCE, DCR fallback, refresh mutex.
