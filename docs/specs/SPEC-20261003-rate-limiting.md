# SPEC-20261003-rate-limiting

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `rate-limiting` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261003-rate-limiting` |
| Ticket | Issue #90 |
| Status | `Completed` |

## 1. User Story

**As a** admin/operador
**I want** limites de uso por usuário/modelo e proteção de auth contra brute-force
**So that** a instância resiste a abuso como o upstream (rate limits + account lockout).

**Problem context:**
Upstream: `WEBUI_AUTH_TRUSTED_*`, limite de tentativas de login, rate limit opcional por usuário. Hoje: sem nenhum limite — login ilimitado, completions ilimitadas.

## 2. Scope

**In scope:**
- Login throttle: lockout progressivo após N falhas (por e-mail+IP), com reset admin.
- Rate limit de completions: `AspNetCore.RateLimiting` token-bucket por usuário autenticado (`ENABLE_RATE_LIMIT`, `RATE_LIMIT_PER_MIN`) + por modelo arena/pipeline.
- Headers `Retry-After` + `429` com `detail` localizável.
- Config admin: toggles e valores.

**Out of scope:**
- Quotas de tokens/custo por billing; rate limit distribuído via Redis (multi-instância guarda para depois — notar que backplane já existe).

## 3. Technical Context

**Files to read:**
- `src/OpenWebUI.Api/Endpoints/AuthEndpoints.cs` (signin)
- `src/OpenWebUI.Api/Endpoints/ApiEndpoints.cs` (completions)
- `src/OpenWebUI.Api/Program.cs` (middleware pipeline)

## 4. Requirements

### RF-001: Lockout de login
- **Rules:** 5 falhas → bloqueio 5min (configurável); registro em config; admin pode resetar.

### RF-002: Rate limit completions
- **Rules:** off por default (compat); por usuário (não IP) quando autenticado.

## 5. API Contract

Respostas `429`/`423` documentadas; `GET|POST /api/v1/configs/ratelimit` (admin).

## 6. Critérios de Aceite

- [ ] 6ª tentativa de login falha com 423/429 após lockout.
- [ ] Completions limitadas com Retry-After quando habilitado.
- [ ] Testes NUnit: lockout, janela de rate limit, desabilitado default.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/93
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/90
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/79
