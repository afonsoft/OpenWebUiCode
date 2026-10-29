# SPEC-20261002-enterprise-sso

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `enterprise-sso` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261002-enterprise-sso` |
| Ticket | Issue #61 |
| Status | `Completed` |

## 1. User Story

**As a** admin enterprise do Open WebUI
**I want** SAML 2.0 e provisionamento SCIM 2.0
**So that** o app integra ao IdP corporativo como no upstream.

**Problem context:**
OAuth/OIDC + LDAP já entregues (SPEC auth-sso-rbac). Upstream tem `scim.py` (15 eps) e SAML via config. Faltam ambos.

## 2. Scope

**In scope:**
- **SCIM 2.0** (`/scim/v2/`): `Users` CRUD (`GET/POST/PUT/PATCH/DELETE`), `Groups` básico, `ServiceProviderConfig`, filtro `userName eq`, token dedicado (`SCIM_TOKEN` ou admin JWT).
- **SAML**: login SP-initiated — metadata config (IdP SSO URL, entity id, cert), `GET /saml/metadata`, `POST /saml/acs` (assertion → sessão JWT), `GET /saml/login` redirect. Biblioteca: `ITfoxtec.Identity.Saml2` (madura, MIT) ou Sustainsys — `[A DEFINIR]` após spike.
- Role mapping: claim/attribute → role (mesmo padrão `OAUTH_*` existente); criação de usuário just-in-time.

**Out of scope:**
- SLO (single logout); signed authn requests avançadas; multi-IdP; SCIM bulk/patch complexo.

## 3. Technical Context

**Where:** `Api` (`ScimEndpoints`, `SamlEndpoints`), `Infrastructure` (`ScimService` sobre users/groups, `SamlService`), config admin.

**Files to read:**
- `src/OpenWebUI.Api/Endpoints/OAuthEndpoints.cs` (fluxo OAuth existente)
- `src/OpenWebUI.Infrastructure/Services/{OAuthService,LdapService}.cs`

**Files to create/modify:**
```text
src/OpenWebUI.Api/Endpoints/{ScimEndpoints,SamlEndpoints}.cs
src/OpenWebUI.Infrastructure/Services/{ScimService,SamlService}.cs
src/OpenWebUI.Application/Contracts/ScimContracts.cs
tests/OpenWebUI.Api.Tests/{ScimTests,SamlTests}.cs
```

## 4. Requirements

### RF-001: SCIM Users
- **Description:** CRUD SCIM: `userName`, `emails`, `active`, `name`; cria → usuário `pending`/role default; `active=false` → desativa.
- **Input → Output:** SCIM JSON → `201/200` com `id`, `meta.resourceType`

### RF-002: SCIM Groups
- **Description:** `GET/POST /scim/v2/Groups` mapeia para `Group` existente; membros por `members[].value`.
- **Input → Output:** SCIM group → `Group`

### RF-003: SAML
- **Description:** `GET /saml/login` → redirect IdP; `POST /saml/acs` valida assertion (assinatura + audience) → cria/login usuário → JWT cookie/redirect.
- **Rules:** assertion inválida → `401`; cert expirado → erro claro.
- **Input → Output:** SAMLResponse → sessão autenticada

### RF-004: Config
- **Description:** `saml.*` e `scim.*` em Admin → config; `enabled` toggles; metadados expostos em `/saml/metadata`.

**Invariants:** SCIM exige token dedicado (não JWT de usuário); SAML valida issuer/audience/assinatura antes de qualquer login.

## 5. API Contract

`/scim/v2/{Users,Groups,ServiceProviderConfig}` · `/saml/{metadata,login,acs}`
**Auth:** SCIM = `Authorization: Bearer <SCIM_TOKEN>`; SAML = público com validação de assertion

## 6. Critérios de Aceite

- [ ] `POST /scim/v2/Users` cria usuário compatível com login interno.
- [ ] Assertion SAML assinada (mock) autentica; assertion inválida rejeita.
- [ ] Toggle off → endpoints `404/501`.
- [ ] Testes NUnit com SAML response fixture e SCIM calls.

## 7. Notas

Spike rápido para escolher lib SAML (ITfoxtec vs Sustainsys) — critério: .NET 10 + manutenção ativa + licença. SCIM shape seguir RFC 7643/7644 mínimo.

## 8. Delivered

- **PR**: #76 · **Issue**: #61
- SCIM 2.0 (`/scim/v2/`): `ServiceProviderConfig`, `Users` (GET lista com `startIndex`/`count` + filtro `userName eq`, GET/POST/PUT/PATCH/DELETE por id) e `Groups` (listar + criar com membros). Content-type `application/scim+json`, erros no formato SCIM.
- Auth SCIM: bearer **dedicado** via config `scim.token` (JWT de usuário/admin rejeitado — testado). Toggle `scim.enabled`; desligado → 401.
- `active=false` (PUT/PATCH `op=replace`) → `role=pending` (não autentica); `active=true` reativa para `user`; admin nunca rebaixado. Create → papel default do admin config, sem senha local.
- SAML 2.0 SP-initiated (HTTP-POST): `GET /saml/metadata` (EntityDescriptor, `WantAssertionsSigned`), `GET /saml/login` (AuthnRequest DEFLATE+Base64 → redirect IdP), `POST /saml/acs` (form `SAMLResponse` → validação → JIT via `OAuthService.LinkOrCreateAsync("saml", …)` → redirect `/?oauth_token=`).
- Validação manual via `SignedXml` (BCL `System.Security.Cryptography.Xml` — já no shared framework, sem pacote extra): assinatura contra cert do IdP (PEM ou Base64), issuer, `NotBefore`/`NotOnOrAfter` (±1min) e `AudienceRestriction` (ACS URL ou SP entity id). XML parseado com `DtdProcessing.Prohibit` + `XmlResolver=null` (anti-XXE).
- Role mapping: config `saml.role_attribute` → valor `admin`/`user` aplica no usuário após JIT.
- Config admin: `GET|POST /api/v1/configs/{saml,scim}` (admin-only) com cert/token mascarados (`********` preserva) + seções na aba Configurações do admin.
- **Decisão (spike da SPEC)**: sem lib SAML (ITfoxtec/Sustainsys) — o BCL cobre XML-DSig e o escopo (assertion validada → JIT user) cabe em ~150 linhas auditáveis, evitando dependência com superfície de config maior que o uso.
- Fora de escopo (mantido): SLO, AuthnRequests assinados, multi-IdP, SCIM bulk/patch complexo, `InResponseTo` (documentado no código).
- Testes: `ScimTests` (9) + `SamlTests` (7 — assertion assinada com cert self-signed gerado em teste; tampered/issuer/audience → 401; disabled → 404).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/76 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/61
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/48
