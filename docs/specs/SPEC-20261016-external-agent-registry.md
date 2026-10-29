# SPEC-20261016-external-agent-registry

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `external-agent-registry` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261016-external-agent-registry` |
| Ticket | [#412](https://github.com/afonsoft/OpenWebUiCode/issues/412) · Epic [#408](https://github.com/afonsoft/OpenWebUiCode/issues/408) |
| Status | `Completed` |
| Fonte | análise fork alltomatos/opencode — `core/src/external-agent-{registry,detect,skill}.ts` + `docs/agents/batuta-external-agents.md` |

## 1. User Story

**As a** usuário do open-webui
**I want** que o app detecte CLIs de agentes externos instalados (claude, codex, devin), instale neles uma skill ponte (`openwebui-cli`) e delegue tarefas via `delegate_task`
**So that** agentes externos passam a ser ferramentas da minha run — orquestração multi-agente real.

**Problem context:** `delegate_task` só delega a `AgentDefinition` internos. O fork registra CLIs conhecidos, detecta via PATH, instala uma skill nos `skillsDir` deles e os expõe como workers.

## 2. Scope

**In scope:**
- RF-001: Registry estático `KNOWN_EXTERNAL_AGENTS`: `{id, name, bin, skillsDir}` — mínimo `claude`→`~/.claude/skills`, `codex`→`~/.codex/skills`, `opencode`→`~/.opencode/skills`, `devin`→`~/.devin/skills`, `gemini`→`~/.gemini/skills`.
- RF-002: `ExternalAgentService.DetectAsync()` → scan de PATH por `bin` (sem spawn — só `which`/PATH walk) → `{id, installed, version?}`.
- RF-003: `GET /api/v1/external-agents/detect` → lista por agente; setting `u:{id}:extagents.selected` filtra quais participam (ausente=todos detectados).
- RF-004: Toggle instala/remove `~/{skillsDir}/openwebui-cli/SKILL.md` por agente selecionado+detectado — skill descreve como o agente externo chama nossa API (endpoint de run + auth via chave `sk-*` do usuário).
- RF-005: `delegate_task` aceita `agent:"ext:{id}"` → executa `{bin} -p "<prompt>"` (ou equivalente headless do agente) como `shell_exec` confinado no workdir, tier `Execute` (approval como shell); saída vira resultado do delegate.

**Out of scope:**
- Form de workers/agentui completo — só o registry+detect+skill+delegate.
- Descoberta dinâmica de agentes além do registry estático.

## 3. Technical Context

- `AgentDefinitionService`/`delegate_task` — ponto de integração do `ext:{id}`.
- `ShellExecBuiltinTool` — confinamento/approvals reutilizados.
- `SkillDiscoveryService` — `~/{skillsDir}` já faz parte do escopo global (E17 #399).

## 4. Requirements

### RF-002: Detecção
- **Rules:** varre `PATH` (`:`/o separador do SO) checando arquivo executável; timeout total 2s; cache 5min; falha → `installed:false` silencioso.

### RF-004: Skill ponte
- **Rules:** SKILL.md gerado com `name: openwebui-cli`, descrevendo `POST /api/v1/chats/runs` + bearer token placeholder (o agente externo lê a chave de env/arg — nunca embutimos segredo no arquivo).

### RF-005: Delegate externo
- **Rules:** comando template por agente (`{bin} -p {prompt}` claude-style; override `extagents.cmd.{id}`); stdout cap 50KB; exit≠0 → erro com stderr resumido.

## 5. API Contract

**`GET /api/v1/external-agents/detect`** → `[{id,name,installed,version,skillInstalled}]`.
**`PUT /api/v1/external-agents/{id}/selected`** `{enabled}` → instala/remove skill conforme detecção.
**Errors:** `404` agente desconhecido; `409` enable sem binário detectado.

## 6. Acceptance Criteria

- [x] **Given** `claude` no PATH **when** `/detect` **then** `installed:true`; sem binário → `false`.
- [x] **Given** toggle enable+detectado **when** PUT **then** `~/.claude/skills/openwebui-cli/SKILL.md` existe; disable remove.
- [x] **Given** `delegate_task agent:"ext:claude"` **when** aprovado **then** executa bin headless e devolve stdout.
- [x] **Given** binário some após detecção **when** delegate **then** erro legível `agente externo indisponível`.
- [x] **Given** PATH vazio **when** detect **then** todos `installed:false`, sem exceção.

## 7. Task Plan

- [x] **T1 —** Registry + `ExternalAgentService` (detect PATH + cache).
- [x] **T2 —** Skill ponte: template SKILL.md + install/remove + endpoints.
- [x] **T3 —** `delegate_task` `ext:{id}` → shell headless confinado.
- [x] **T4 —** Testes: PATH fake, skill install/remove, delegate com bin fake (script shell).

**7.1 Validation strategy:** unit tests com PATH injetável e binários stub (shell scripts); integration no endpoint.

## 8. Organization Guardrails

- Nunca spawnar o binário na detecção — só verificação de existência/permissão.
- Delegate externo = mesma aprovação de `shell_exec` (Execute tier); command template sanitizado (prompt via argv, não shell string).
- SKILL.md não contém segredos.

## 9. Definition of Done

- [x] RF-001..RF-005 verdes; build+test limpos; spec-status ok.

## Delivered

| Item | Link |
| --- | --- |
| PR/commit | #419 (cadeia #421) · 7a2bb4265 |
| Issue | https://github.com/afonsoft/OpenWebUiCode/issues/412 |
