# SPEC-20261001-missing-pages

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `missing-pages` |
| Type | `Frontend` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261001-missing-pages` |
| Ticket | `[missing-pages] Issue #22` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** as páginas secundárias do upstream que faltam (Playground, abas de Admin)
**So that** a superfície de rotas seja completa.

**Problem context:**
Rotas ausentes/parciais (`docs/MIGRACAO-DOTNET.md:85-89`): `/admin` sem abas de evals/settings completas, `/playground` inexistente, `/workspace` sem abas knowledge/tools (cobertas por SPECs próprios).

## 2. Scope

**In scope:**
- `/playground`: chat direto com modelo sem criar conversa (modo completions/raw como no upstream).
- `/admin`: aba **Avaliações** (lista de feedbacks 👍/👎 com modelo e usuário) e aba **Configurações** consolidando feature flags do `/api/config` editáveis.
- Sidebar/rotas atualizadas com os novos destinos.

**Out of scope:**
- `/channels` (SPEC `realtime-channels`), `/calendar`, `/automations` (SPEC `automations-calendar`), abas knowledge/tools de workspace (SPECs próprios).
- Compare mode de múltiplos modelos (playground avançado — fase 2).

## 3. Technical Context

**Where the change happens:**
`Client` (novas páginas/abas), `Api` (endpoints de evals list + config update se faltarem).

**Files to read before implementing:**
- `src/OpenWebUI.Client/Pages/Admin.razor`, `Pages/ChatPage.razor`, `Components/SettingsModal.razor`
- `src/OpenWebUI.Api/Endpoints/{EvaluationEndpoints,ApiEndpoints}.cs`

**Files to create or modify:**
```text
src/OpenWebUI.Client/Pages/Playground.razor
src/OpenWebUI.Client/Pages/Admin.razor (abas)
src/OpenWebUI.Api/Endpoints/{EvaluationEndpoints,ApiEndpoints}.cs (se necessário)
tests — cobertura de novos endpoints
```

## 4. Requirements

### RF-001: Playground
- **Description:** `/playground` envia prompt direto ao modelo selecionado sem persistir chat; mesma UI do chat.
- **Rules:** streaming idêntico ao chat; sair da página descarta a sessão.
- **Input → Output:** prompt → resposta streaming

### RF-002: Aba Avaliações no admin
- **Description:** Lista paginada de avaliações (usuário, modelo, rating, trecho) para admins.
- **Rules:** admin-only; sem conteúdo sensível além do necessário.

### RF-003: Aba Configurações no admin
- **Description:** Feature flags e configs editáveis persistidas via `/api/config`.
- **Rules:** somente flags seguras para runtime; restart-required flags marcadas.

## 5. API Contract

**Endpoint:** `GET /api/v1/evaluations` (admin, paginado), `POST /api/config`
**Auth:** `Bearer` admin

**Response:** `200` lista/config · `403`

## 6. Critérios de Aceite

- [ ] `/playground` responde em streaming sem criar chat na sidebar.
- [ ] Aba Avaliações lista feedbacks reais.
- [ ] Flag editada persiste e reflete no `/api/config`.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/38 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/24
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/14
