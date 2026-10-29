# SPEC-20261001-ef-migrations

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `ef-migrations` |
| Type | `Refactor / Infra` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261001-ef-migrations` |
| Ticket | `GAP-architecture-ef-migrations — Issue a criar` |
| Status | `Completed` |

## 1. User Story

**As a** mantenedor do Open WebUI .NET
**I want** migrações EF Core formais em vez do `SchemaBootstrap` incremental
**So that** o schema evolua de forma versionada, revisável e reversível (rollback), como boa prática .NET.

**Problem context:**
`SchemaBootstrap` evolui o SQLite de forma ad-hoc (colunas/tabelas criadas por código — `docs/MIGRACAO-DOTNET.md:74` 🟡). Funciona, mas sem histórico de migrações, sem rollback e com rebuilds manuais (ex.: PK composta de `ChatMessages`).

## 2. Scope

**In scope:**
- Adotar EF Core Migrations (`dotnet ef migrations add`) para o `WebUIContext` com migração inicial equivalente ao schema atual.
- `Database.Migrate()` no startup substituindo `SchemaBootstrap` (compat: detectar base legada e baselinar a migration inicial).
- Documentar comandos de migração no CLAUDE.md/docs.
- `[A DEFINIR]` estratégia de baseline para bases existentes — recomendado: tabela `__EFMigrationsHistory` marcada quando o schema já bate com o snapshot inicial.

**Out of scope:**
- Migração para outro SGBD (PostgreSQL/SQL Server) — fase 2.
- Down migrations automatizadas no startup.

## 3. Technical Context

**Where the change happens:**
`Infrastructure` (`WebUIContext`, pasta `Migrations/`, remoção gradual do `SchemaBootstrap`), `Api` (startup `MigrateAsync`).

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/Data/` (DbContext + SchemaBootstrap)
- `src/OpenWebUI.Api/Program.cs`

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Migrations/*.cs
src/OpenWebUI.Infrastructure/Data/SchemaBootstrap.cs (deprecar/remover)
src/OpenWebUI.Api/Program.cs
docs/technologies.md (comandos ef)
```

## 4. Requirements

### RF-001: Migration inicial
- **Description:** Snapshot EF do schema atual gerando migração baseline.
- **Rules:** `dotnet ef` funciona na solução; designer review do diff.
- **Input → Output:** `migrations add Initial` → migration que reproduz o schema

### RF-002: Migrate no startup
- **Description:** `Database.Migrate()` aplica migrações pendentes na inicialização.
- **Rules:** base legada (schema já atual, sem `__EFMigrationsHistory`) é baselinada sem recriar tabelas; falha → log e aborta boot.

### RF-003: Fluxo documentado
- **Description:** Comandos de criar/aplicar/inspecionar migração documentados; CI valida que o modelo gera SQL aplicável.

**Business rules / invariants:**
- Nunca perder dados de `webui.db` existente — baseline, não recreate.
- SchemaBootstrap removido somente após migração inicial validada contra base real.

## 5. API Contract

N/A (infra).

## 6. Critérios de Aceite

- [ ] `webui.db` novo sobe com schema completo via migrations.
- [ ] `webui.db` legado (criado pelo SchemaBootstrap) faz baseline e continua funcionando (teste com dump real).
- [ ] Adicionar coluna nova via migration aplica sem código manual.
- [ ] Testes existentes seguem verdes.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/29 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/15
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/14
