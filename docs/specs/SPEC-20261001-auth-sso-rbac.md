# SPEC-20261001-auth-sso-rbac

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `auth-sso-rbac` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261001-auth-sso-rbac` |
| Ticket | `GAP-security-auth-sso-rbac — Issue a criar` |
| Status | `Completed` |

## 1. User Story

**As a** administrador do Open WebUI
**I want** login via provedores OAuth/LDAP e grupos com permissões granulares (RBAC)
**So that** a instância se integre ao IdP corporativo e o acesso a recursos seja controlável por grupo, como no upstream.

**Problem context:**
Somente registro/login local com papel `admin`/`user`/`pending` (`docs/MIGRACAO-DOTNET.md:64-65` — ⬜). Sem OAuth/OIDC, LDAP, SAML/SCIM; sem Groups nem permissões por recurso.

## 2. Scope

**In scope:**
- Login OAuth2/OIDC com providers configuráveis por admin (Google, GitHub, Microsoft/Entra, OIDC genérico — mesmos do upstream).
- LDAP bind (servidor, DN base, filtro, atributos) — `[A DEFINIR]` se entra na primeira entrega ou fica fase 2.
- Entidade `Group` + `GroupMember` (role no grupo: admin/member).
- Permissões granulares por grupo: workspace (models/prompts/knowledge/tools/files), sharing, chat — flags como no upstream.
- Admin UI: aba de grupos em `/admin`, config de providers OAuth em Settings → Admin.

**Out of scope:**
- SAML e SCIM (fase 2).
- Group role-mapping de LDAP (upstream issue em aberto; fase 2).
- Migração de contas locais ↔ OAuth (account linking manual apenas).

## 3. Technical Context

**Where the change happens:**
`Domain` (Group, GroupMember, OAuthAccount link), `Infrastructure` (middleware OAuth, LDAP client), `Api` (`/oauth/{provider}/login|callback`, `/api/v1/groups`), `Client` (botões SSO na Auth, aba grupos no admin, checks de permissão).

**Files to read before implementing:**
- `CLAUDE.md` · `.claude/rules/dotnet.md`
- `src/OpenWebUI.Api/Endpoints/{AuthEndpoints,UserEndpoints}.cs`
- `src/OpenWebUI.Client/Pages/{Auth,Admin}.razor`, `Components/SettingsModal.razor`
- `src/OpenWebUI.Infrastructure/Services/` (auth/JWT)

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities/{Group,OAuthAccount}.cs
src/OpenWebUI.Api/Endpoints/{OAuthEndpoints,GroupEndpoints}.cs, Program.cs (AddAuthentication OAuth)
src/OpenWebUI.Infrastructure/Services/{OAuthService,LdapService}.cs
src/OpenWebUI.Client/Pages/{Auth,Admin}.razor
tests/OpenWebUI.Api.Tests/{OAuthEndpoints,GroupEndpoints}Tests.cs
```

## 4. Requirements

### RF-001: Login OAuth/OIDC
- **Description:** Botão "Continuar com {provider}" na página de auth; callback cria/vincula usuário local e emite JWT.
- **Rules:** providers configurados por admin (client id/secret via env ou settings); novo usuário OAuth respeita regra de aprovação (`pending`) quando ativa.
- **Input → Output:** callback OAuth → usuário autenticado

### RF-002: LDAP
- **Description:** Login com bind LDAP; atributos mapeados para nome/email.
- **Rules:** config admin-only; falha de bind → erro genérico sem vazar detalhes.
- **Input → Output:** `{username,password}` → sessão

### RF-003: Groups e membros
- **Description:** CRUD de grupos e membros (`/api/v1/groups`); admin de grupo gerencia membros.
- **Rules:** só admin global cria grupos; usuário pode estar em N grupos.
- **Input → Output:** `{name, memberIds[]}` → grupo

### RF-004: Permissões por grupo
- **Description:** Flags de permissão (workspace/models/prompts/knowledge/sharing/chat) avaliadas por união dos grupos do usuário; endpoints relevantes checam a flag.
- **Rules:** default = permissões atuais; admin sempre tem tudo.
- **Input → Output:** request autenticado → allow/deny por flag

**Business rules / invariants:**
- OAuth nunca cria senha local; conta OAuth sem email verificado pode ser rejeitada por config.
- Remover usuário de todos os grupos restaura permissão default (não nega tudo).

## 5. API Contract

**Endpoint:** `GET /oauth/{provider}/login`, `GET /oauth/{provider}/callback`, `GET|POST|DELETE /api/v1/groups`
**Auth:** callback público (state), grupos `Bearer` admin

**Response:** `302` redirect para cliente com token · `401` · `403` permissão insuficiente

## 6. Critérios de Aceite

- [ ] Login OAuth completo cria usuário e emite sessão válida.
- [ ] Admin cria grupo, adiciona membros, define flags; usuário restrito recebe 403 no recurso negado.
- [ ] Página auth mostra botões SSO apenas quando providers configurados.
- [ ] Testes cobrem vinculação de conta OAuth e avaliação de permissões.

## 7. Notas

`[A DEFINIR]` LDAP na primeira entrega (recomendado: sim, bind simples; mapeamento de papéis fase 2).
`[A DEFINIR]` quais providers OAuth pré-habilitar (recomendado: OIDC genérico + Google + GitHub + Microsoft).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/30 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/16
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/14
