# SPEC-20261016-automation-actions

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `automation-actions` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261016-automation-actions` |
| Ticket | [#409](https://github.com/afonsoft/OpenWebUiCode/issues/409) · Epic [#408](https://github.com/afonsoft/OpenWebUiCode/issues/408) |
| Status | `Completed` |
| Fonte | análise fork alltomatos/opencode — `core/src/schedule/runner.ts` + callers (`agentui/mcp/reminder/skill`) |

## 1. User Story

**As a** usuário do open-webui
**I want** automações agendadas que executam ações além de "prompt em chat" — rodar uma skill, chamar uma tool MCP, enviar um lembrete/notificação ou disparar um webhook
**So that** rotinas de manutenção, lembretes e integrações rodem sozinhas sem eu abrir o app.

**Problem context:** `Automation` hoje só carrega `Prompt`+`ModelId` — todo disparo vira uma run de chat. O runner do fork tem callers plugáveis por tipo de ação (skill/mcp/reminder/agente), cada um com payload próprio, modo de permissão e reuso de sessão.

## 2. Scope

**In scope:**
- RF-001: `Automation.ActionKind` (`prompt` default | `skill` | `mcp` | `reminder` | `webhook`) + `ActionJson` com payload por tipo.
- RF-002: Caller `skill`: cria run real cujo primeiro input é o corpo interpolado da skill (mesma expansão de `/skill` do composer); reusa `ChatId` da automação quando existir.
- RF-003: Caller `mcp`: chama `tools/call` num servidor `env:*` ou cadastrado com args JSON; resultado gravado no `AutomationRun`.
- RF-004: Caller `reminder`: cria `Notification` (kind `automation`) com título/corpo interpolados — sem LLM.
- RF-005: Caller `webhook`: POST do `ActionJson` para URL http(s) configurada, timeout 15s, corpo resumido no `AutomationRun`.
- RF-006: `Automation.PermissionMode` (`default` | `auto`): `auto` roda a run com preset permissivo; `default` respeita gates.
- RF-007: `AutomationRun` registra `ActionKind`, resumo do output e erro durável; erro não reagenda — `NextRunAt` normal.
- RF-008: API `GET/POST/PUT /automations` aceita os novos campos; validação por `ActionKind`.

**Out of scope:**
- Canal externo (WhatsApp/Telegram) — ver SPEC agent-personas-channels.
- Edição UI completa das novas ações — backend + payload JSON editável; form rico é fase 2.

## 3. Technical Context

- `src/OpenWebUI.Domain/Entities.cs` — `Automation` (~L983), `AutomationRun` (~L1032), `Notification` (~L1208).
- `src/OpenWebUI.Infrastructure/Services/AutomationService.cs` — `AutomationSchedule.ComputeNextRun` + dispatcher.
- `SkillDiscoveryService` + `SkillBuiltinTool` — resolução de skill por nome.
- `McpClientService.CallToolAsync` / `GetServerAsync`.
- `ChatRunDispatcher` — enfileirar run com prompt da skill.
- Migration EF necessária (novos campos).

## 4. Requirements

### RF-001: ActionKind + ActionJson
- **Rules:** default `prompt` preserva automações existentes (migration backfill). `ActionJson` validado por kind: skill→`{skill,args?,model?}`; mcp→`{server,tool,args?}`; reminder→`{title,body?}`; webhook→`{url,method?,headers?}`.

### RF-002..RF-005: Callers
- **Rules:** cada caller retorna `(ok, output|error)` gravado no AutomationRun; exceções viram `status="error"` com mensagem curta — nunca derrubam o dispatcher.

### RF-006: PermissionMode
- **Rules:** `auto` só afeta runs `skill`/`prompt`; webhook/mcp/reminder ignoram.

## 5. API Contract

**Endpoint:** `PUT /api/v1/automations/{id}` aceita `{actionKind, actionJson, permissionMode}` além dos campos atuais.
**Errors:** `400` kind inválido/payload malformado; `404` skill/servidor inexistente na validação (salvamento não exige que exista — falha na execução registra erro).

## 6. Acceptance Criteria

- [x] **Given** automação `skill` com `skill:"x"` **when** dispara **then** cria run cujo conteúdo injetado é o corpo da skill.
- [x] **Given** automação `reminder` **when** dispara **then** cria Notification sem chamar LLM.
- [x] **Given** automação `mcp` com server `env:calc` **when** dispara **then** `CallToolAsync` recebe tool+args e output vai ao AutomationRun.
- [x] **Given** automação `webhook` com URL mock **when** dispara **then** POST recebido; 500 → `status=error` e `NextRunAt` futuro.
- [x] **Given** actionJson malformado **when** `PUT` **then** 400 com campo inválido.

## 7. Task Plan

- [x] **T1 —** Entidade/migration: `ActionKind`, `ActionJson`, `PermissionMode`, `ChatId?` reuse.
- [x] **T2 —** Dispatcher: switch por kind + callers skill/mcp/reminder/webhook.
- [x] **T3 —** Contratos/endpoints + validação.
- [x] **T4 —** Testes: callers unitários, dispatcher integration, migration.

**7.1 Validation strategy:** unit tests por caller (server MCP e webhook fakes), integration no dispatcher com Automation seedada, migration test.

## 8. Organization Guardrails

- Webhook: SSRF guard — só http(s), bloquear loopback/metadata (169.254.169.254) como no `fetch_url`.
- Nunca executar `ActionJson` como código; payload é dados.
- Migration com backfill `action_kind='prompt'` — zero downtime.

## 9. Definition of Done

- [x] RF-001..RF-008 implementados e verdes.
- [x] `dotnet build`/`dotnet test` limpos; `check-spec-status.py` ok.
- [x] SSRF/validação cobertos por teste.

## Delivered

| Item | Link |
| --- | --- |
| PR/commit | #416 (cadeia #421) · 7a2bb4265 |
| Issue | https://github.com/afonsoft/OpenWebUiCode/issues/409 |
