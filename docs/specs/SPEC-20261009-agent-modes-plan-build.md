# SPEC-20261009-agent-modes-plan-build

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `agent-modes-plan-build` (série web-ide, fatia S5) |
| Type | `Feature` |
| Stack | `.NET 10 / Blazor WASM` |
| Repository | `afonsoft/open-webui` |
| Branch | `feature/devin-{YYYYMMDD}-agent-modes` |
| Ticket | `GAP-impl-agent-modes` (+ absorve `GAP-arch-permission-rulesets`) |
| Status | `Completed` |
| Priority | `medium` |
| Depends on | — (recomendado após S1 para o `plan_exit` escrever no workdir) |

## 1. User Story

**As a** usuário
**I want** alternar o agente entre modo **Plan** (só lê — produz plano e pede aprovação) e **Build** (tools completas)
**So that** eu controle quando o agente pode escrever/executar, como no opencode (`agent plan` ↔ `build`, `src/agent/agent.ts:141-157`, `src/tool/plan.ts`).

**Problem context:** hoje toda run tem o mesmo toolset; o gate de risco (`ToolCallRiskClassifier`/`CommandRiskClassifier`) é global e por chamada — não há "modo". O preset `auto`/`smart` existe mas não é um modo de agente com prompt/ruleset próprios.

## 2. Scope

**In scope:**
- `mode` no chat (ou por run): `build` (default) e `plan`; persistido em `Chat`/chat settings; UI = toggle no composer (Tab ou dropdown) + chip de modo visível durante a run.
- `PermissionRuleset` por modo: `plan` = write/shell-write/network-deny (tools permitidas: read/list/grep/glob/web/fetch/ask/todo; `file_write`/`file_edit`/`shell_exec`/`code_interpreter` → bloqueado ou sempre-ask). Reuso/estende `ToolCallRiskClassifier` → `Ruleset(mode, tool, args)` com wildcard matching no estilo `permission/evaluate.ts` do opencode.
- Tool `plan_exit` (só no modo plan): grava o plano em `.openwebui/plans/{runId}.md` no workdir (quando vinculado; senão em memória), chama `ask_user` ("Executar este plano?") → confirmação promove o chat pra `build` e injeta o plano como instrução.
- System prompt do modo plan: instrução "não modifique nada; produza plano" (template pt/en).
- "always" answer: respostas de aprovação podem marcar `always` para a (tool, pattern) naquela sessão — memória de sessão, não persiste além do chat (`[A DEFINIR]` se persiste por usuário).

**Out of scope:**
- Agentes customizados por config de arquivo (opencode `agent.*` do opencode.json) — fase posterior.
- Mode `subagent` (delegate já existe sem modos).

## 3. Technical Context

**AS-IS:** `ToolCallRiskClassifier` estático; gate no `ChatRunExecutor`; `PermissionPromptCard` com Allow/Deny; preset auto/smart; `AskUserBuiltinTool` + `QuestionPromptCard`; `Chat.Meta`/settings por chat existem.

**TO-BE:** ruleset por modo + transição plan→build via tool.

## 4. Requirements

### RF-001: Mode no modelo de dados
`Chat` ganha `Mode` (`build`|`plan`, default `build`); `PUT` via chat settings; runs herdam o modo corrente.

### RF-002: Ruleset
`PermissionRuleset.Evaluate(mode, toolName, argsSummary)` → `allow|ask|deny`; `plan` nega `file_write|file_edit|shell_exec|code_interpreter|n8n_trigger|job_kill` (deny = tool nem é anunciada ao provider; se chamada, erro estruturado).

### RF-003: `plan_exit`
Disponível só em modo plan; `BuiltinTool` que escreve `plan.md` + `ask_user`; no "sim" → `Mode=build` + mensagem de sistema "Plano aprovado — execute" + rerun da última instrução (ou continua com instrução "execute o plano").

### RF-004: UI
Toggle plan/build no composer (ícone+label, `aria-pressed`), chip de modo no header durante run, badge "Modo plano" quando ativo. i18n nos 8 locales.

### RF-005: Always-permission de sessão
Permission card ganha opção "Sempre nesta sessão" → `(tool, pattern)` vai pra allowlist in-memory da sessão; nunca persiste entre logins.

## 5. Acceptance Criteria

- [ ] Modo plan bloqueia `file_write`/`shell_exec` (deny) e anuncia só tools read.
- [ ] `plan_exit` grava plano e promove a build após confirmação.
- [ ] Toggle troca de modo; chip reflete; run anterior não muda de modo.
- [ ] "Sempre nesta sessão" supre o re-ask da mesma tool+pattern.

## 6. Tests

Api.Tests: ruleset matrix (plan/build × tools), `plan_exit` fluxo completo, always-scope. Client.Tests: toggle, chip, card com opção always.

## 7. Rollout

Sem flag — `build` é o default e comportamento atual.

## 8. Risks

- **Plano que promete escrita no plano:** o `plan_exit` grava só `.md`; a promoção passa pelo `ask_user` — não há bypass.
- **Confusão de escopo do "always":** sempre por sessão, nunca global — documentado no card.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/250 |
