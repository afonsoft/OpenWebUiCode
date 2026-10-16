# SPEC-20261009-pr-ci-panel

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `pr-ci-panel` (série devin-webapp, item D1) |
| Type | `Feature` |
| Stack | `.NET 10 / Blazor WASM` |
| Repository | `afonsoft/open-webui` |
| Branch | `feature/devin-{YYYYMMDD}-pr-ci-panel` |
| Ticket | `GAP-devin-D1-pr-ci-panel` |
| Status | `Completed` |
| Priority | `medium` |
| Depends on | `SPEC-20261009-workspace-file-api` (S1), `SPEC-20261009-web-ide-surface` (S2) |

## 1. User Story

**As a** usuário com repo vinculado
**I want** ver os PRs abertos do repositório e o status dos checks de CI direto no chat/IDE
**So that** eu acompanhe o que o agente (ou eu) abriu sem sair da app — como a sessão do Devin mostra seus PRs e o estado do CI.

**Problem context:** temos token GitHub (`GitHubEndpoints`), binding e git status; nenhuma superfície lista PRs/checks.

## 2. Scope

**In scope:**
- `GET /api/v1/workspace/repo/pulls` → PRs abertos do repo vinculado (via `GitHubService` + token do usuário): `{number, title, author, branch, draft, url, checks: {state: success|failure|pending, total, failed}}`. Cache 60s.
- Painel "Pull Requests" no IDE (tab junto a Changes) e card colapsável no `ChatWorkspacePanel`: linha por PR com badge do rollup de checks.
- Empty states: sem token ("configure em Integrações"), sem PRs, repo não vinculado.
- i18n nos 8 locales; a11y listbox.

**Out of scope:**
- Criar/mergear/comentar PRs pela UI; Devin Review próprio (D12 — decisão de produto).

## 3. Requirements

### RF-001
`pulls` endpoint usa o token armazenado do usuário; sem token → 200 `{pulls: [], needsToken: true}`.

### RF-002
Checks rollup por PR: `success` (todos verdes), `failure` (≥1 falhou), `pending` (resto) — via combined status + check runs.

### RF-003
UI lista com badge colorido do estado + refresh manual; link externo abre o PR.

## 4. Tests

Api.Tests: sem token → `needsToken`; mock GitHub → lista + rollup correto. Client.Tests: render da lista, badges por estado, empty state.

## 5. Rollout

Sem flag.

## 6. Risks

- Rate limit do GitHub sem token — mitigado pelo `needsToken` + cache.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/260 |
