# SPEC-20261003-permissions-granular

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `permissions-granular` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261003-permissions` |
| Ticket | Issue #86 |
| Status | `Completed` |

## 1. User Story

**As a** admin
**I want** permissões granulares por usuário (sobrepondo grupo) e allowlist de domínios de grupos
**So that** `users`/`groups` cobrem o modelo de permissões upstream.

**Problem context:**
Hoje permissões vêm do papel (admin/user/pending) + flags de grupo. Upstream permite overrides por usuário (workspace/sharing/chat/features) e grupos com `allowed_domains` para auto-membership no signup.

## 2. Scope

**In scope:**
- `User.PermissionsJson` (override): admin edita flags por usuário (workspace models/knowledge/prompts/tools, sharing public_chats, chat file_upload/web_search/...); `PermissionService` mescla grupo → usuário (usuário vence).
- `Group.AllowedDomainsJson`: signup com e-mail de domínio listado entra no grupo automaticamente (e herda permissões).
- UI: painel de permissões no editor de usuário admin + campo domains no editor de grupo.

**Out of scope:**
- Herança multi-grupo com prioridades; ACLs por chat individual (já coberto por access_grants).

## 3. Technical Context

**Files to read:**
- `src/OpenWebUI.Infrastructure/Services/PermissionService.cs`
- `src/OpenWebUI.Api/Endpoints/{UserEndpoints,AuthEndpoints}.cs` (signup)
- `src/OpenWebUI.Client/Pages/Admin.razor` (users/groups editors)

## 4. Requirements

### RF-001: Override por usuário
- **Rules:** merge determinístico (user > grupo > default papel); admin-only para editar.

### RF-002: Domínios de grupo
- **Input → Output:** signup `x@empresa.com` com grupo "empresa.com" → membro automático.

## 5. API Contract

`GET|PUT /api/v1/users/{id}/permissions` (admin); `Group` payload ganha `allowed_domains`; `/api/v1/groups/{id}` aceita edição.

## 6. Critérios de Aceite

- [ ] Override desliga feature para 1 usuário sem afetar o grupo.
- [ ] Signup com domínio entra no grupo; sem match → fluxo atual.
- [ ] Testes NUnit cobrindo merge e auto-membership.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/96
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/86
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/79

## Reconciliation

_Reconciliado em 2026-10-08 (SPEC-20261008-spec-status-reconciliation, Issue #203)._

| RF | Veredito | Evidência |
| --- | --- | --- |
| RF-001 Override por usuário | delivered | merge determinístico user > grupo > papel em `Infrastructure/Services/PermissionService.cs`; edição admin-only; `tests/.../PermissionsGranularTests.cs` |
| RF-002 Domínios de grupo | delivered | `Api/Endpoints/AuthEndpoints.cs:82` → `JoinDomainGroupsAsync` (`:465-490`): signup `x@dominio.com` entra no grupo com `AllowedDomainsJson` contendo o domínio |

