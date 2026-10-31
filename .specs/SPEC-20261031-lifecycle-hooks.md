# SPEC-20261031-lifecycle-hooks

## 0. Metadata

| Field | Value |
|-------|-------|
| Feature | Lifecycle hooks do agente (PreToolUse/PostToolUse/PermissionRequest/etc.) |
| Type | Feature |
| Stack | .NET 10 / ASP.NET Core / Blazor WASM |
| Repository | afonsoft/OpenWebUiCode |
| Branch | feature/devin-20261031-lifecycle-hooks |
| Ticket | [A DEFINIR] |
| Status | Approved |

## 1. User Story

**As a** administrador de um workspace OpenWebUI
**I want** hooks de ciclo de vida configuráveis que interceptem chamadas de tool, prompts do usuário e eventos de sessão
**So that** eu possa impor políticas, injetar contexto e auditar o comportamento do agente sem recompilar o sistema.

**Problem context:**
A engenharia reversa do Devin CLI (`docs/DEVIN-CLI-REA-ANALYSIS.md` §4) revelou um sistema de hooks com **8 eventos** (`PreToolUse, PostToolUse, PermissionRequest, UserPromptSubmit, Stop, PostCompaction, SessionStart, SessionEnd`), dois tipos de hook (`command` com JSON stdin/stdout + exit codes; `prompt` avaliado por LLM), reescrita de input (`updatedInput`), injeção de contexto (`additionalContext`), matcher por regex sobre `tool_name`, correlação `session_id`/`prompt_id` e env `DEVIN_PROJECT_DIR`. Configuração em `.devin/hooks.v1.json`, chave `hooks` do `config.json`, e compat com `.claude/settings*.json`. O OpenWebUiCode hoje só tem **automation webhooks inbound** (`AutomationHookEndpoints` dispara runs via token) — não há hooks de ciclo de vida que interceptem tools ou eventos dentro de uma run.

## 2. Scope

**In scope:**

- Modelo de dados `HookDefinition { event, matcher?, type: command|prompt, command|prompt, timeout }` persistido por workspace/repo e usuário.
- Eventos MVP: `PreToolUse`, `PostToolUse`, `UserPromptSubmit`, `Stop`, `SessionStart`, `SessionEnd` (PermissionRequest e PostCompaction entram quando existirem os pontos correspondentes; compactor já existe).
- Executor `command`: JSON do evento no stdin, JSON no stdout, exit code `0` ok / `2` block / demais não-bloqueante; timeout mata o processo.
- Executor `prompt`: avalia por modelo rápido (advisor) retornando `approve|block|ask` com `reason` e `add_context`.
- Semântica de decisão: `approve` segue; `block` devolve a reason ao modelo como resultado da tool (não aborta a run); `ask` roteia p/ gate de aprovação; `updatedInput` substitui args JSON da tool; `additionalContext` injeta mensagem de sistema efêmera.
- `matcher` regex case-insensitive sobre nome da tool (`builtin_`, `mcp__*`).
- Carregamento de config: `.openwebui/hooks.json`, `.devin/hooks.v1.json` e `.claude/settings*.json` (chave `hooks`) do repo vinculado + `~/.config/openwebui/hooks.json` do usuário; ordem repo > user.
- Correlação `session_id` (run/chat id) e `prompt_id` (mensagem) + env `OPENWEBUI_PROJECT_DIR` no processo do hook.
- Endpoint REST `GET/PUT /api/v1/workspace/repo/hooks` (admin) e telemetria por evento (contagem, latência, blocks).

**Out of scope:**

- Marketplace/compartilhamento de hooks (fica p/ plugins futuros).
- Sandbox dos hooks (rodam no usuário do servidor — documentado como poderoso).
- `PermissionRequest`/`PostCompaction` completos se os pontos de integração ainda não existirem (fase 2).

## 3. Technical Context

**Where the change happens** (architecture, layers, integrations):

`src/OpenWebUI.Infrastructure/ChatTools/` (novo `LifecycleHookService` + `HookDefinition`), integração no `ChatRunExecutor` antes/depois de cada execução de tool (junto ao gate `PermissionRuleset`), no ponto de submit do usuário e no início/fim da run. Compat `config-importers` reaproveita o loader de skills/rules existente.

**Files to read before implementing:**

- `CLAUDE.md`, `.claude/rules/`
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` (gate de tools, início/fim da run)
- `src/OpenWebUI.Infrastructure/ChatTools/PermissionRuleset.cs`
- `src/OpenWebUI.Infrastructure/ChatTools/BuiltinToolRegistry.cs`
- `docs/DEVIN-CLI-REA-ANALYSIS.md` §4
- `.claude/skills/write-specs/references/spec-sdd-template.md`

**Files to create or modify:**

```text
src/OpenWebUI.Infrastructure/ChatTools/HookDefinition.cs            (novo)
src/OpenWebUI.Infrastructure/ChatTools/LifecycleHookService.cs      (novo)
src/OpenWebUI.Infrastructure/ChatTools/CommandHookRunner.cs         (novo)
src/OpenWebUI.Infrastructure/ChatTools/PromptHookEvaluator.cs       (novo)
src/OpenWebUI.Infrastructure/ChatTools/HookConfigLoader.cs          (novo)
src/OpenWebUI.Api/Runs/ChatRunExecutor.cs                           (integração)
src/OpenWebUI.Api/Endpoints/HookEndpoints.cs                        (novo)
tests/OpenWebUI.Api.Tests/LifecycleHookTests.cs                     (novo)
```

## 4. Requirements

### RF-001: Hook `command` com JSON stdin/stdout
- **Description:** O sistema executa hooks `command` com o payload do evento no stdin e interpreta stdout/exit code.
- **Rules:** exit `0` + stdout JSON válido aplica decisão; exit `2` = block com stderr como reason; demais códigos/JSON inválido/timeout = erro não-bloqueante logado; timeout padrão 60s configurável por hook.
- **Input → Output:** `{session_id, prompt_id, cwd, permission_mode, hook_event_name, tool_name, tool_input|tool_response|prompt|stop_reason}` → `{"decision":"approve|block|ask","reason"?,"hookSpecificOutput":{"additionalContext"?,"updatedInput"?,"permissionDecision"?}}`.

### RF-002: Hook `prompt` avaliado por LLM
- **Description:** Hook `prompt` envia o evento a um modelo rápido (advisor/role) com instrução fixa e interpreta `approve|block|ask`, `true/false` ou `{"pass":bool}`.
- **Rules:** modelo configurável (`hooks.promptModel`), resposta não-parseável = não-bloqueante; nunca expõe o prompt do hook ao modelo principal.

### RF-003: `PreToolUse` com `updatedInput` e `additionalContext`
- **Description:** Antes de cada tool call, hooks com `matcher` que bate `tool_name` rodam em ordem; `updatedInput` (JSON object) substitui `call.ArgumentsJson`; `additionalContext` vira mensagem de sistema visível ao modelo.
- **Rules:** `updatedInput` só em `PreToolUse`; `block` retorna reason como conteúdo da tool (`"Blocked by hook <name>: <reason>"`) sem abortar a run; matcher ausente = todos os tools.

### RF-004: `PostToolUse` com observação e feedback
- **Description:** Após a execução, hooks recebem `tool_input`+`tool_response`; `block` devolve feedback ao modelo (o efeito da tool já aconteceu — documentado); `additionalContext` injeta contexto.

### RF-005: `UserPromptSubmit` e `Stop`
- **Description:** `UserPromptSubmit` roda antes da mensagem entrar na fila (`block` rejeita a mensagem com reason ao usuário; `additionalContext` injeta). `Stop` roda quando o agente tenta parar (`block` injeta instrução e continua a run).

### RF-006: `SessionStart`/`SessionEnd` com contexto
- **Description:** Disparados no início/fim da run (e em resume); `additionalContext` de `SessionStart` vira contexto inicial.

### RF-007: Configuração multi-fonte com compat
- **Description:** Carrega `.openwebui/hooks.json` (formato nativo), `.devin/hooks.v1.json` (arquivo inteiro = definição), chave `hooks` de `.devin/config.json`/`.claude/settings*.json` do repo vinculado, e `~/.config/openwebui/hooks.json`; precedência repo > usuário, nativo > compat.
- **Rules:** `hooks.v1.json` schema `{ "hooks": { "<Event>": [{ "matcher": "...", "hooks": [{"type":"command|prompt","command|prompt": "...", "timeout": N}] }] } }`; chaves desconhecidas = warning não fatal.

### RF-008: Auditoria e telemetria
- **Description:** Toda decisão de hook é persistida no audit trail da run (evento, hook, decisão, latência); métricas por evento/hook.

**Business rules / invariants:**
- Hooks nunca veem secrets das credenciais; env só recebe `OPENWEBUI_PROJECT_DIR`, `OPENWEBUI_SESSION_ID`, `OPENWEBUI_PROMPT_ID`.
- `block` de `PreToolUse` nunca aborta a run — devolve reason como resultado da tool.
- Falha de hook (timeout, crash, JSON inválido) = não-bloqueante, logado.
- Hooks `command` rodam com o usuário do servidor e cwd = workspace da run.

## 5. API Contract (if applicable)

**Endpoint:** `GET /api/v1/workspace/repo/hooks` — lista hooks resolvidos (repo+user, origem por hook). `PUT` no mesmo path grava `.openwebui/hooks.json` do repo (admin). `POST /api/v1/workspace/repo/hooks/test` executa um hook de teste com payload fornecido.
**Auth:** JWT admin; PUT exige workspace vinculado.

## 6. Acceptance Criteria

- [ ] **Given** hook `PreToolUse` com matcher `shell_exec` retornando `{"decision":"block","reason":"no rm"}`, **when** o modelo chama `shell_exec`, **then** a tool devolve `"Blocked by hook <name>: no rm"` e a run continua.
- [ ] **Given** `PreToolUse` com `hookSpecificOutput.updatedInput`, **when** a tool roda, **then** executa com os args substituídos.
- [ ] **Given** hook com exit `2`, **when** executado, **then** é tratado como block com stderr como reason.
- [ ] **Given** `UserPromptSubmit` block, **when** o usuário envia mensagem, **then** ela é rejeitada com a reason visível.
- [ ] **Given** `Stop` block, **when** o agente emite resposta final, **then** recebe instrução de continuação e a run segue.
- [ ] **Given** `.devin/hooks.v1.json` válido no repo, **when** a run inicia, **then** os hooks são carregados com a mesma semântica.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Timeout | hook demora > timeout | não-bloqueante, logado |
| JSON inválido | stdout lixo | não-bloqueante, logado |
| Matcher regex inválido | `[` | hook desabilitado com warning |
| `updatedInput` com tipo errado | string no lugar de object | ignorado, hook segue |
| Hooks múltiplos | 2 hooks no mesmo evento | rodam em ordem; primeiro `block` vence |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** ler `ChatRunExecutor`/`PermissionRuleset`/`BuiltinToolRegistry` e os pontos de lifecycle; estudar `.claude/settings` loader existente.
- [ ] **T2 — Modelo+loader:** `HookDefinition`, `HookConfigLoader` (nativo + compat devin/claude), precedência repo>user.
- [ ] **T3 — Runners:** `CommandHookRunner` (stdin/stdout/exit/timeout/env) e `PromptHookEvaluator` (modelo advisor, parse de decisão).
- [ ] **T4 — Integração:** chamadas nos pontos PreToolUse/PostToolUse/UserPromptSubmit/Stop/SessionStart/SessionEnd do `ChatRunExecutor` + `additionalContext`/`updatedInput`.
- [ ] **T5 — Endpoints:** `HookEndpoints` GET/PUT/teste + auditoria.
- [ ] **T6 — Testes:** unitários do runner/loader/decisão + integração end-to-end com hook de arquivo.
- [ ] **T7 — Done + PR.**

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **.NET** | Unit tests (runner, parser, matcher, precedence); integration tests (hook command real via arquivo temp, eventos na run); 80% coverage. |

## 8. Organization Guardrails

- **Branches:** `feature/devin-20261031-lifecycle-hooks` — nunca em `main`.
- **Security:** hooks rodam com poder do servidor — documentar risco; env não carrega secrets; `OPENWEBUI_*` mínimos.
- **Scope:** sem marketplace/plugins ainda.
- **Architecture:** decisão de hook em serviço de infraestrutura; `ChatRunExecutor` só invoca.

## 9. Definition of Done

- [ ] Todos os RFs implementados.
- [ ] Todos os critérios de aceite cobertos por testes.
- [ ] Edge cases tratados.
- [ ] `dotnet build` + `dotnet test` passam; `python3 tools/check-spec-status.py` ok.
- [ ] Guardrails respeitados; logs sem PII.
- [ ] `docs/DEVIN-CLI-REA-ANALYSIS.md` atualizado com status.

## Open Questions / Pending Ambiguity

- `PermissionRequest`/`PostCompaction` entram no MVP ou fase 2? (depende dos pontos de integração — recomendo fase 2).
- Hooks `prompt` usam o modelo `advisor` ou role dedicada? (recomendo `advisor`, configurável via `hooks.promptModel`).
