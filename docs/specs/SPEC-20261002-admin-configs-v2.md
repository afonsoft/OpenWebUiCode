# SPEC-20261002-admin-configs-v2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `admin-configs-v2` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261002-admin-configs` |
| Ticket | Issue #51 |
| Status | `Completed` |

## 1. User Story

**As a** admin do Open WebUI
**I want** todas as seções de config do upstream (banners, defaults, domínios por feature)
**So that** a área Admin → Settings tenha paridade completa.

**Problem context:**
`configs.py` (25 eps) upstream cobre: banners, default models, prompt suggestions, code-execution config, audio/image/retrieval config, OAuth/LDAP toggles, direct connections, JWT expiry. Hoje temos conexões + admin config genérico + feature flags.

## 2. Scope

**In scope:**
- `Banners` — lista `{id, type(info|warning|error|success), title, content, dismissible, timestamp}`; exibidas no topo do app; `GET` público-auth, CRUD admin.
- `Default models` + `default_prompt_suggestions` — `GET|POST /api/v1/configs/models` e sugestões mostradas no chat vazio.
- Toggles por feature: `enable_signup`, `default_user_role`, `enable_api_key`, `jwt_expires_in`, `enable_community_sharing`, `webhook_url`, `channels` on/off — endpoints dedicados `POST /api/v1/configs/{feature}`.
- `code_execution` config (engines habilitados pyodide/jupyter) e `direct_connections` on/off.
- Admin → Settings reorganizado em tabs upstream (general, connections, models, interface, audio, images, documents, code-execution).

**Out of scope:**
- SAML config (SPEC enterprise-sso); retrieval config completa (SPEC retrieval-advanced — apenas o slot na UI aqui).

## 3. Technical Context

**Where:** `Api` (`ConfigEndpoints` novos sub-endpoints + `BannerEndpoints`), `Domain` (`Banner`), `Infrastructure` (`ConfigService` chaves novas), `Client` (Admin settings tabs, banner banner UI, sugestões no empty-state).

**Files to read:**
- `src/OpenWebUI.Api/Endpoints/ApiEndpoints.cs`, `WorkspaceEndpoints.cs`
- `src/OpenWebUI.Infrastructure/Services/ConfigService.cs`
- `src/OpenWebUI.Client/Pages/Admin.razor`, `Components/ChatView.razor` (empty state)

**Files to create/modify:**
```text
src/OpenWebUI.Domain/Entities/Banner.cs
src/OpenWebUI.Api/Endpoints/{ConfigEndpoints,BannerEndpoints}.cs
src/OpenWebUI.Client/Pages/Admin.razor + Components/BannerBar.razor
tests/OpenWebUI.Api.Tests/ConfigV2Tests.cs
```

## 4. Requirements

### RF-001: Banners
- **Description:** CRUD admin; client mostra banners ativos dismissíveis (persist dismissed em settings do usuário).
- **Input → Output:** `{type,title,content}` → banner

### RF-002: Defaults
- **Description:** `default_models` (lista ordenada) + `prompt_suggestions` `{title,content}[]`; cliente aplica no empty-state e seletor.
- **Input → Output:** config → UI

### RF-003: Feature toggles
- **Description:** Endpoints por domínio (`signup`, `api_key`, `channels`, `direct_connections`, `code_execution`, `jwt_expires_in`) — leitura auth, escrita admin.
- **Rules:** `enable_signup=false` → signup retorna `403`; `enable_api_key=false` → `sk-*` rejeitadas.

**Invariants:** configs lidas por usuário comum expõem apenas flags seguras (sem chaves).

## 5. API Contract

`GET|POST /api/v1/configs/banners` (+`/{id}` delete/update) · `GET|POST /api/v1/configs/models` · `GET|POST /api/v1/configs/{signup,api_key,channels,direct_connections,code_execution,jwt}` · `GET /api/config` (enriquecer)
**Auth:** GET banners = Bearer; writes = admin

## 6. Critérios de Aceite

- [ ] Banner ativo renderiza no topo e dismiss persiste.
- [ ] `enable_signup=false` bloqueia novos registros.
- [ ] Default models + sugestões aparecem no cliente.
- [ ] Admin settings em tabs com save por seção.
- [ ] Testes NUnit dos endpoints + efeito de toggle.

## 7. Notas

Upstream usa chaves `ENABLE_*`/`DEFAULT_*` em `DEFAULT_CONFIG` — manter nomes de env equivalentes onde houver (compat mental).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/66 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/51
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/48
