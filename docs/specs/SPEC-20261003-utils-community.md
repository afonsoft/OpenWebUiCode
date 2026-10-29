# SPEC-20261003-utils-community

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `utils-community` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261003-utils-community` |
| Ticket | Issue #89 |
| Status | `Completed` |

## 1. User Story

**As a** usuário
**I want** endpoints utilitários (gravatar, format) e integração com a comunidade openwebui.com
**So that** os routers `utils` e a superfície de comunidade existem.

**Problem context:**
Upstream `utils`: `/gravatar` (avatar por email), `/litellm/config`, `/code/format`, `/marked` (render markdown server-side? verificar). Comunidade: share de tools/prompts/modelos para openwebui.com (post de link público).

## 2. Scope

**In scope:**
- `GET /api/v1/utils/gravatar?email=` → proxy do gravatar (ou retorna URL calculada por hash MD5 — sem chamada externa no cliente).
- `POST /api/v1/utils/code/format` → format server-side via `dotnet`/formatters instalados? [A DEFINIR] — upstream formata Python/JS; no .NET oferecer `black`/`prettier` externos opcionais ou omitir com feature flag.
- Comunidade: botão "Compartilhar na comunidade" em tools/prompts/modelos → abre URL openwebui.com com payload (upstream usa postMessage/form); validar payload assinado? Não — upstream é só redirect com dados.

**Out of scope:**
- `/litellm/config` se LiteLLM não for target; render server-side de markdown.

## 3. Technical Context

**Files to read:**
- upstream `routers/utils.py`, `utils/misc.py` (fetch para detalhar contratos)
- `src/OpenWebUI.Client/Components/ChatView.razor` (code blocks já existem)

## 4. Requirements

### RF-001: Gravatar
- **Rules:** hash MD5 do email normalizado; resposta `{url}` ou imagem proxied.

### RF-002: Code format
- **Rules:** whitelist de linguagens; executor ausente → 501.

### RF-003: Comunidade
- **Rules:** nunca envia dados sem ação explícita do usuário; abre em nova aba.

## 5. API Contract

`GET /api/v1/utils/gravatar` · `POST /api/v1/utils/code/format` · share links gerados no cliente.

## 6. Critérios de Aceite

- [ ] Gravatar por e-mail retorna URL/hash correto.
- [ ] Format de código funciona para ao menos `json` (formatter interno) e 501 sem executor.
- [ ] Botão de share gera URL upstream-compatível.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/99
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/89
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/79

Nota: o upstream usa **SHA256** (não MD5) para o hash do gravatar — seguido o upstream.

## Reconciliation

_Reconciliado em 2026-10-08 (SPEC-20261008-spec-status-reconciliation, Issue #203)._

| RF | Veredito | Evidência |
| --- | --- | --- |
| RF-001 Gravatar | delivered | `Api/Endpoints/UtilsEndpoints.cs:30-43` — hash **SHA256** do email normalizado (upstream usa SHA256, não MD5 — divergência já documentada neste SPEC); retorna URL sem chamada externa |
| RF-002 Code format | delivered | `UtilsEndpoints.cs:47` — whitelist de linguagens; `json` tem formatador interno; executor ausente → 501 |
| RF-003 Comunidade | delivered | `Client/wwwroot/js/app.js:64` `shareCommunity` — só dispara por ação explícita do usuário e abre em nova aba |

