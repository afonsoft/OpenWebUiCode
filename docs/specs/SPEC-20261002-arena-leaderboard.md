# SPEC-20261002-arena-leaderboard

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `arena-leaderboard` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261002-arena-leaderboard` |
| Ticket | Issue #56 |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** comparar modelos em modo arena e ver um leaderboard de avaliações
**So that** posso escolher o melhor modelo empiricamente, como no upstream.

**Problem context:**
Evaluations hoje = feedback 👍/👎 + lista admin. Upstream `models.py`/`evaluations.py` têm arena models (batalha anônima A/B) e leaderboard por rating.

## 2. Scope

**In scope:**
- Arena models: `/workspace/models` tipo `arena` — conjunto de modelos; chat em arena gera 2 respostas anônimas e voto escolhe o vencedor.
- Rating ELO simplificado por modelo (win/loss/draw das batalhas + feedbacks agregados).
- `GET /api/v1/evaluations/leaderboard` + aba Leaderboard em `/admin/evaluations`.
- Access grants por modelo (public/private/groups) — campo `access_grants` em `Model` reaproveitando padrão de grupos.

**Out of scope:**
- Matchmaking inteligente; export de dataset de batalhas; ELO sofisticado (média móvel etc.).

## 3. Technical Context

**Where:** `Domain` (`Model.AccessGrants`, `ArenaBattle`, rating), `Infrastructure` (ArenaService, EloService), `Api` (endpoints arena + leaderboard), `Client` (UI de voto A/B, leaderboard admin).

**Files to read:**
- `src/OpenWebUI.Api/Endpoints/{EvaluationEndpoints,ModelEndpoints,ChatEndpoints}.cs`
- `src/OpenWebUI.Domain/Entities/` (Feedback, Model)

**Files to create/modify:**
```text
src/OpenWebUI.Domain/Entities/{ArenaBattle,Model}.cs
src/OpenWebUI.Application/Contracts/EvaluationContracts.cs (leaderboard DTOs)
src/OpenWebUI.Api/Endpoints/EvaluationEndpoints.cs
src/OpenWebUI.Client/{Components/ChatView.razor,Pages/Admin.razor}
tests/OpenWebUI.Api.Tests/ArenaTests.cs
```

## 4. Requirements

### RF-001: Arena model
- **Description:** Modelo `base_model_id = null` + `meta.arena` lista modelos concorrentes; completar em arena gera 2 respostas de modelos distintos sorteados.
- **Input → Output:** prompt + arena model → `battle_id` + 2 respostas anonimizadas

### RF-002: Voto
- **Description:** `POST /api/v1/evaluations/arena/feedback {battle_id, winner: a|b|tie|both_bad}` grava resultado e revela modelos.
- **Rules:** um voto por battle; atualiza rating dos dois modelos.
- **Input → Output:** voto → `{models: [a,b] revelados, ratings}`

### RF-003: Leaderboard
- **Description:** Rating ELO (K=32) recalculado incremental; `GET /evaluations/leaderboard` ordena por rating; aba admin renderiza tabela.
- **Input → Output:** — → `[{model_id, rating, battles, wins}]`

### RF-004: Access grants em modelos
- **Description:** `Model.AccessGrants` (user/group × read/write); listagem filtra por grants.
- **Rules:** sem grants = comportamento atual (visibilidade por `is_active`/owner).

**Invariants:** batalhas sem voto não afetam rating; modelo anonimizado até o voto.

## 5. API Contract

`POST /api/v1/evaluations/arena/feedback` · `GET /api/v1/evaluations/leaderboard` · arena completion via `/api/chat/completions` com modelo `arena:*`
**Auth:** Bearer

## 6. Critérios de Aceite

- [ ] Chat arena retorna 2 respostas sem revelar modelos.
- [ ] Voto revela identidades e atualiza leaderboard.
- [ ] Grants restringem listagem de modelos.
- [ ] Testes NUnit: arena flow, ELO, grants.

## 7. Notas

ELO K=32 com start 1000 (padrão chess.com-like); leaderboard pode exigir mín. de batalhas (ex.: 3) para exibição — `[A DEFINIR]` threshold.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/71 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/56
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/48
