# SPEC-20261002-users-management

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `users-management` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261002-users-management` |
| Ticket | Issue #50 |
| Status | `Completed` |

## 1. User Story

**As a** admin do Open WebUI
**I want** busca/paginação de usuários, sessões ativas e permissões default
**So that** gerencio contas em escala como no upstream `users.py` (26 eps).

**Problem context:**
`/api/v1/users` cobre perfil + admin CRUD básico. Faltam: settings persistentes por usuário (estado UI), sessões ativas, permissões default (`user.permissions.*`), busca/filtro/paginação com sort, update de role isolado, `oauth_sessions`.

## 2. Scope

**In scope:**
- `GET /api/v1/users` com `query`, `filter` (role/pending), `order_by`, `page`/`per_page` — admin.
- `GET|POST /api/v1/users/user/settings` — blob JSON de settings por usuário (UI state do upstream).
- `GET /api/v1/users/active` — usuários online (presença SignalR).
- `GET /api/v1/users/{id}` admin; `POST .../{id}/update/role` isolado.
- `GET /api/v1/users/permissions` admin — permissões default (`workspace`, `sharing`, `chat` toggles) usadas como base de novos usuários e merge com group flags.
- `GET /api/v1/users/{id}/oauth/sessions` + revogação (se `oauth_sessions` existir — senão registrar defer).

**Out of scope:**
- Password policy completa / force reset; import CSV.

## 3. Technical Context

**Where:** `Domain` (User.Settings, UserPermissions), `Api` (`UserEndpoints` filtros), `Infrastructure` (`PresenceService` a partir do Hub), `Client` (`/admin/users` com busca/paginação/sort).

**Files to read:**
- `src/OpenWebUI.Api/Endpoints/UserEndpoints.cs`, `Hubs/ChatHub.cs`
- `src/OpenWebUI.Domain/Entities/{User,Group}.cs`
- `src/OpenWebUI.Client/Pages/Admin.razor`

**Files to create/modify:**
```text
src/OpenWebUI.Domain/Entities/User.cs (Settings, LastActiveAt)
src/OpenWebUI.Api/Endpoints/UserEndpoints.cs
src/OpenWebUI.Infrastructure/Services/PresenceService.cs
src/OpenWebUI.Client/Pages/Admin.razor (tabela paginada)
tests/OpenWebUI.Api.Tests/UsersManagementTests.cs
```

## 4. Requirements

### RF-001: Listagem admin
- **Description:** `GET /users?query=&filter=&order_by=&page=` — busca por nome/email, filtro por role, sort por colunas whitelisted.
- **Input → Output:** params → `{users[], total, page}`

### RF-002: Settings do usuário
- **Description:** `GET/POST /users/user/settings` persiste JSON arbitrário por usuário (limite de tamanho).
- **Input → Output:** `{ui: {...}}` → echo persistido

### RF-003: Sessões ativas
- **Description:** `GET /users/active` retorna user_ids conectados ao Hub agora.
- **Input → Output:** — → `{ids: []}`

### RF-004: Permissões default
- **Description:** `GET /users/permissions` (admin) retorna defaults; novos usuários herdam; merge com permissões de grupo (união permissiva conforme upstream).
- **Input → Output:** — → `{workspace:{...}, sharing:{...}, chat:{...}}`

**Invariants:** usuário comum nunca lê dados de outros (apenas presença/ids se upstream permitir — verificar); ordenação whitelisted evita SQL injection.

## 5. API Contract

`GET /api/v1/users?query&filter&order_by&page` · `GET|POST /api/v1/users/user/settings` · `GET /api/v1/users/active` · `GET /api/v1/users/{id}` · `POST /api/v1/users/{id}/update/role` · `GET /api/v1/users/permissions`
**Auth:** Bearer (listagem/{id}/permissions: admin)

## 6. Critérios de Aceite

- [ ] Paginação/filtro/sort corretos e isolados por role.
- [ ] Settings por usuário persistem entre logins.
- [ ] `/active` reflete conexões do Hub.
- [ ] Defaults de permissão aplicados no signup.
- [ ] Testes NUnit cobrindo admin-only + paginação.

## 7. Notas

Verificar no upstream se `/users/active` usa Redis/in-memory — nosso Hub in-memory basta (single-instance).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/65 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/50
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/48
