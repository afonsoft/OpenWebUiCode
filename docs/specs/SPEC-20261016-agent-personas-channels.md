# SPEC-20261016-agent-personas-channels

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `agent-personas-channels` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261016-agent-personas-channels` |
| Ticket | [#413](https://github.com/afonsoft/OpenWebUiCode/issues/413) · Epic [#408](https://github.com/afonsoft/OpenWebUiCode/issues/408) |
| Status | `Completed` |
| Fonte | análise fork alltomatos/opencode — `opencode/src/agentui/index.ts` (personas com guardrails + dispatchChannelMessage + audit log por agente) |

## 1. User Story

**As a** usuário do open-webui
**I want** agentes nomeados (persona com system prompt, modelo/combo, guardrails) acionáveis por canal externo via webhook — com audit log de cada turno
**So that** o mesmo agente atende no chat, numa automação ou num endpoint dedicado, com rastro do que entrou e saiu.

**Problem context:** `AgentDefinition` existe para sub-agentes de `delegate_task`, mas não há "agente de superfície": identidade acionável de fora do chat. O fork tem AgentUI com personalidade, guardrails, canais (whatsapp/telegram/sandbox) e audit log capado por agente.

## 2. Scope

**In scope:**
- RF-001: `AgentPersona` `{Id, Name, UserId, SystemPrompt, ModelId|ComboId, GuardrailLevel(none|basic|strict), Tools[], Enabled}` — CRUD `/api/v1/agent-personas`.
- RF-002: `POST /api/v1/agent-personas/{id}/message` `{message, chatKey?}` — cria/continua sessão do agente: system prompt = persona.SystemPrompt (+guardrails basic/strict como regras extras), modelo = persona.Model ou resolve `combo:`; retorna `{reply, sessionId}` síncrono (limite 2min) ou `202{runId}`.
- RF-003: `chatKey` agrupa turnos na mesma sessão lógica por canal (ex.: `"webhook:contato-42"`) — histórico por (persona, chatKey).
- RF-004: `AgentTurn` audit por agente: `{id, ts, chatKey, channel, inText, outText, status}` capado a 200/agente; `GET /agent-personas/{id}/audit`.
- RF-005: Guardrails `basic`/`strict`: basic → bloqueia tools Execute; strict → só tools Read + aprovação sempre ask. Aplicados como PermissionRuleset da persona.
- RF-006: Auth: bearer `sk-*` do dono ou JWT; persona só do dono (`UserId`); channel `webhook` registrado no audit.

**Out of scope:**
- WhatsApp/Telegram adaptadores reais — canal genérico `webhook` já cobre integração externa.
- RAG de conhecimento por persona — fase 2.
- `generateDraft` (LLM cria persona por descrição) — fase 2.

## 3. Technical Context

- `AgentDefinitionService`/`AgentDefinition` — camadas existentes (builtin/admin/user/workspace); persona é entidade DB distinta (não arquivo).
- `ChatRunDispatcher`/`ChatRunExecutor` — execução da sessão; `SessionPrompt`-equivalente = pipeline existente.
- `PermissionRuleset` — mapeamento de GuardrailLevel.
- Migration EF (`AgentPersonas`, `AgentTurns`).

## 4. Requirements

### RF-002: dispatchChannelMessage
- **Rules:** mensagem de channel externo entra como user message numa sessão do agente; `chatKey` reutiliza a sessão aberta mais recente daquele (persona, chatKey); nova se não houver.

### RF-005: Guardrails
- **Rules:** `strict` também injeta linha no system prompt ("responda sem executar ações destrutivas; peça confirmação").

## 5. API Contract

**`POST /api/v1/agent-personas/{id}/message`** → `200 {reply, sessionId, usage?}` ou `202 {runId, sessionId}` (async via `?wait=false`).
**`GET /api/v1/agent-personas/{id}/audit?take=`** → `[{ts,chatKey,channel,in,out,status}]`.
**Errors:** `401/403`, `404` persona, `422` message vazia, `429` rate limit do channel (60/min por persona).

## 6. Acceptance Criteria

- [x] **Given** persona com guardrail `strict` **when** message pede `file_write` **then** tool negada/ask — nunca executa.
- [x] **Given** duas mensagens com mesmo `chatKey` **when** POST **then** segunda enxerga contexto da primeira (mesma sessão).
- [x] **Given** 3 turnos **when** `/audit` **then** lista in+out por turno, mais recente primeiro.
- [x] **Given** persona desabilitada **when** POST **then** 404/410.
- [x] **Given** `wait=false` **when** POST **then** 202 + runId; run completando grava turno no audit.

## 7. Task Plan

- [x] **T1 —** Entidades + migration + CRUD personas.
- [x] **T2 —** `PersonaDispatchService`: resolve sessão por chatKey → run com system/model/guardrails → audit.
- [x] **T3 —** Endpoints message/audit + auth + rate limit.
- [x] **T4 —** Testes: sessão contínua, guardrails, audit cap, auth.

**7.1 Validation strategy:** integration com provider fake; testes de permissão por GuardrailLevel.

## 8. Organization Guardrails

- Persona sem tools mutáveis em `strict` — deny absoluto, não só prompt.
- Audit armazena texto truncado (4KB por campo); sem secrets.
- Rate limit por persona para evitar abuso do endpoint.

## 9. Definition of Done

- [x] RF-001..RF-006 verdes; build+test limpos; spec-status ok.

## Delivered

| Item | Link |
| --- | --- |
| PR/commit | #420 (cadeia #421) · 7a2bb4265 |
| Issue | https://github.com/afonsoft/OpenWebUiCode/issues/413 |
