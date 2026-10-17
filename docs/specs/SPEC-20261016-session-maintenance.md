# SPEC-20261016-session-maintenance

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `session-maintenance` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261016-session-maintenance` |
| Ticket | [#411](https://github.com/afonsoft/open-webui/issues/411) · Epic [#408](https://github.com/afonsoft/open-webui/issues/408) |
| Status | `Completed` |
| Fonte | análise fork alltomatos/opencode — `tool/maintenance.ts` + commits `e4b6ac49` (synthesize sessions to memory before purge, retention config, auto vacuum) |

## 1. User Story

**As a** usuário do open-webui
**I want** manutenção automática de higiene: sessões velhas são purgadas após síntese de fatos na memória, SQLite compactado, e vejo as estatísticas do último ciclo
**So that** a base não cresce para sempre e o conhecimento útil das conversas sobrevive ao expurgo.

**Problem context:** `RetentionService` expurga apenas dados operacionais (AutomationRuns, Notifications, steers) — chats/mensagens do usuário crescem indefinidamente. O fork sintetiza sessões→memória ANTES de purgar e expõe stats do último ciclo.

## 2. Scope

**In scope:**
- RF-001: Poda de chats por idade: config `retention.chatDays` (default 0=off, opt-in); chats sem mensagens nos últimos N dias são elegíveis.
- RF-002: Antes de deletar, cada chat elegível passa pela destilação do `MemorySyncService` (mesmo pipeline `auto:{chat}`), preservando fatos como `AgentMemory`.
- RF-003: Poda em lotes (cap 20 chats/tick, cap de tempo), dry-run retornando candidatos.
- RF-004: `GET /api/v1/maintenance/status` — stats do último ciclo: `chatsPurged`, `memoriesSynthesized`, `opsPurged`, `lastRunAt`, `nextRunAt`, `dbSizeBytes`.
- RF-005: `POST /api/v1/maintenance/run` (admin ou dono dos dados): ciclo sob demanda com `dryRun=true|false`.
- RF-006: Exclusões: chats `Pinned`, chats com runs ativas, chats com workspace vinculado nunca são podados por idade.

**Out of scope:**
- Purge de arquivos de upload órfãos (já há job separado se existir) — manter fora.
- VACUUM — já no RetentionService (registrar no status).

## 3. Technical Context

- `RetentionService` — tick diário existente; estende com poda de chats.
- `MemorySyncService` — pipeline de destilação reusável (extrair método público `DistillChatAsync(chatId)`).
- `Chat`, `Message`, `AgentMemory` entities; `Notification` para resumo opcional.

## 4. Requirements

### RF-002: Síntese antes do purge
- **Rules:** falha na destilação de um chat → chat NÃO é deletado (skip + log); só chats destilados com sucesso saem.

### RF-006: Exclusões
- **Rules:** elegibilidade = `UpdatedAt < cutoff` AND `!Pinned` AND sem `ChatRun` em `running|paused` AND `WorkdirId == null`.

## 5. API Contract

**`GET /api/v1/maintenance/status`** → `{lastRun:{startedAt,finishedAt,chatsPurged,memoriesSynthesized,opsPurged,vacuumed}, config:{chatDays}, nextRunAt}`.
**`POST /api/v1/maintenance/run`** `{dryRun}` → `200` com mesmo payload de resultado (sem deletar em dryRun).
**Errors:** `403` não-admin tentando ciclo global; `409` ciclo já em andamento.

## 6. Acceptance Criteria

- [x] **Given** chat de 40d sem pin e `chatDays=30` **when** tick **then** destilado→memória→deletado; `status` reflete contadores.
- [x] **Given** destilação falha (provider down) **when** tick **then** chat permanece, erro logado, run segue para os demais.
- [x] **Given** chat pinado/com run ativa/com workspace **when** tick **then** intacto.
- [x] **Given** `dryRun` **when** POST **then** retorna candidatos sem deletar nada.
- [x] **Given** `chatDays=0` **when** tick **then** poda de chats desligada (só ops).

## 7. Task Plan

- [x] **T1 —** Extrair `DistillChatAsync` público do MemorySyncService.
- [x] **T2 —** RetentionService: poda elegível+destilação em lotes + stats persistidas (kv).
- [x] **T3 —** Endpoints status/run + dryRun + guards.
- [x] **T4 —** Testes: elegibilidade, falha-de-destilação, dryRun, status.

**7.1 Validation strategy:** integration tests com SQLite (chats seedados em idades variadas), provider fake para destilação.

## 8. Organization Guardrails

- Opt-in: `chatDays=0` default desliga — nunca deletar dado do usuário sem configuração explícita.
- Logs sem conteúdo de mensagens (só ids/contadores).
- Poda respeita concorrência: lock de ciclo único.

## 9. Definition of Done

- [x] RF-001..RF-006 verdes; build+test limpos; spec-status ok; docstring de config documentada.

## Delivered

| Item | Link |
| --- | --- |
| PR/commit | #418 (cadeia #421) · 7a2bb4265 |
| Issue | https://github.com/afonsoft/open-webui/issues/411 |
