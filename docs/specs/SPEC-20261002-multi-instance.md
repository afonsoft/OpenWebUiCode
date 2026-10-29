# SPEC-20261002-multi-instance

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `multi-instance` |
| Type | `Infra` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261002-multi-instance` |
| Ticket | Issue #63 |
| Status | `Completed` |

## 1. User Story

**As a** operador do Open WebUI
**I want** Postgres, Redis backplane e storage externo
**So that** rodo múltiplas instâncias atrás de balanceador, como o upstream em produção.

**Problem context:**
Hoje: SQLite single-writer, SignalR in-process, uploads em disco local — impossível escalar horizontalmente. Upstream suporta Postgres + Redis (websocket manager) + S3/GCS storage.

## 2. Scope

**In scope:**
- `DatabaseProvider` por config/env: `sqlite` (default) ou `postgresql` (`ConnectionStrings__Default` + `DATABASE_PROVIDER`); EF migrations por provider (assembly separado ou migrations multi-provider).
- SignalR backplane Redis opcional (`REDIS_URL`): `AddSignalR().AddStackExchangeRedis(url)`; sem Redis → comportamento atual.
- Storage abstrato `IFileStorage`: `local` (atual `data/uploads`) + `s3` (AWS SDK ou compatível MinIO) por config.
- `DatabaseMigrator` adaptado por provider; healthcheck `/health` verifica db.

**Out of scope:**
- MySQL/MSSQL; GCS/Azure Blob (interface permite depois); Redis para cache de sessão; K8s manifests completos (documentar compose multi-réplica).

## 3. Technical Context

**Where:** `Infrastructure` (provider EF selection, `IFileStorage`, Redis), `Api` (Program.cs wiring), `Domain`/`Application` (interface storage), Dockerfile/compose (variante postgres).

**Files to read:**
- `src/OpenWebUI.Api/Program.cs` (wiring atual)
- `src/OpenWebUI.Infrastructure/Data/{AppDbContext,AppDbContextFactory,DatabaseMigrator}.cs`
- `src/OpenWebUI.Api/Endpoints/FileEndpoints.cs` (upload path)
- `docker-compose.yaml`, `Dockerfile`

**Files to create/modify:**
```text
src/OpenWebUI.Application/Interfaces/IFileStorage.cs
src/OpenWebUI.Infrastructure/Storage/{LocalFileStorage,S3FileStorage}.cs
src/OpenWebUI.Infrastructure/Data/* (provider switch)
src/OpenWebUI.Api/Program.cs
docker-compose.postgres.yaml (exemplo)
tests/OpenWebUI.Api.Tests/MultiInstanceTests.cs (smoke)
```

## 4. Requirements

### RF-001: Postgres
- **Description:** `DATABASE_PROVIDER=postgresql` + `ConnectionStrings__Default` → `UseNpgsql`; sqlite continua default.
- **Rules:** migrations separadas por provider; seed/behavior idêntico.
- **Input → Output:** env → provider selecionado

### RF-002: Redis backplane
- **Description:** `REDIS_URL` presente → SignalR escala entre instâncias; ausente → in-process.
- **Input → Output:** env → backplane ativo

### RF-003: File storage
- **Description:** `STORAGE_PROVIDER=local|s3`; S3 com `{bucket, endpoint, region, access/secret mascarados}`; `files/{id}/content` serve via storage.
- **Rules:** falha de storage → `502`; nunca expor credenciais.

### RF-004: Healthcheck
- **Description:** `/health` verifica DB (e storage quando configurado) — pronto para LB.

**Invariants:** default single-instance intacto (zero mudança operacional para quem não configurar).

## 5. API Contract

Sem endpoints novos — operacional (`/health` enriquecido).

## 6. Critérios de Aceite

- [ ] App boota com Postgres (compose) e migra schema.
- [ ] Upload/serve via S3-compatível (MinIO em teste ou local).
- [ ] SignalR entre duas instâncias entrega mensagem (teste manual documentado).
- [ ] Testes NUnit de seleção de provider + storage.

## 7. Notas

EF multi-provider: manter migrations em `Migrations/Sqlite/` + `Migrations/Postgres/` ou projeto separado — `[A DEFINIR]` no spike. S3 via `AWSSDK.S3` official.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/78 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/63
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/48
