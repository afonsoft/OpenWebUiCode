# SPEC-20261009-port-preview

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `port-preview` (série devin-webapp, item D4) |
| Type | `Feature` |
| Stack | `.NET 10 / Blazor WASM` |
| Repository | `afonsoft/open-webui` |
| Branch | `feature/devin-{YYYYMMDD}-port-preview` |
| Ticket | `GAP-devin-D4-port-preview` |
| Status | `Completed` |
| Priority | `low` (alto valor, maior risco) |
| Depends on | `SPEC-20261009-web-ide-surface` (S2) |

## 1. User Story

**As a** usuário com um dev server rodando via terminal/jobs
**I want** abrir `localhost:{port}` do host da API dentro de um painel do IDE
**So that** eu veja o app que o agente subiu sem sair da página — como o preview de portas do Devin.

**Problem context:** terminal/jobs executam no host da API; portas locais não são alcançáveis pelo browser do usuário.

## 2. Scope

**In scope:**
- Reverse proxy auth-only: `ANY /preview/{port}/{**path}` → `http://127.0.0.1:{port}/{path}` no host (HTTP forward manual com HttpClient ou YARP já referenciado — verificar dependências). WebSocket/SSE passthrough se simples (HMR).
- Aba "Preview" no painel direito do IDE: input de porta + iframe `/preview/{port}/`; auto-detect opcional listando portas em LISTEN (via `/proc/net/tcp*` ou job metadata) — `[A DEFINIR]`.
- Segurança: allowlist de portas (1024–65535), exigir auth, nunca bind externo; bloquear `localhost` de loopback não-127.0.0.1 já é moot pois proxy é server-side.
- i18n 8 locales.

**Out of scope:**
- Expor portas publicamente (sempre atrás da auth da app); múltiplos hosts/agents remotos.

## 3. Requirements

### RF-001
Proxy preserva path, query, headers essenciais e method/body; timeout 30s; falha → 502 com página amigável.

### RF-002
Cookies do app proxied com path rewrite mínimo necessário pra funcionar em iframe (strip `SameSite` restritivo quando `Secure` ausente — documentar).

### RF-003
UI: aba Preview com input de porta, reload, open-in-new-tab; iframe com sandbox `allow-scripts allow-same-origin allow-forms`.

## 4. Tests

Api.Tests: proxy echo (spinup http listener em porta alta), 502 em porta fechada, auth required, path/query preservados. Client.Tests: aba renderiza iframe com src correto.

## 5. Rollout

Flag `preview.enabled` (default ON?) — `[A DEFINIR]`; admin config como `ide.enabled`.

## 6. Risks

- SSRF: o proxy permite alcançar qualquer porta TCP local do host — restringir a faixa 1024–65535 e documentar; nunca encaminhar `Authorization` do usuário para o upstream.
- Apps com base path absoluto quebram no subpath — limitação conhecida (iframe usa URL direta do proxy).

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/263 |
