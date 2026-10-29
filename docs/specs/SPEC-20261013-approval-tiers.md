# SPEC-20261013-approval-tiers

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `approval-tiers` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261013-approval-tiers` |
| Ticket | [#351](https://github.com/afonsoft/OpenWebUiCode/issues/351) |
| Status | `Completed` |

## 1. User Story

**As a** usuário do chat-agente
**I want** granularidade por ferramenta na aprovação — cada tool declara seu tier (leitura/escrita/execução) e eu posso fixar por tool `allow`/`deny`/`prompt`
**So that** eu rode `file_read`/`grep` livre, seja sempre consultado em `shell_exec`, e nunca permita `delegate_task` — sem depender só do preset global do chat.

**Problem context:**
Hoje o gate é `RequiresApproval` (bool) + preset por chat (`allow-readonly|approve-mutations|smart|always-allow|auto`) + `ToolCallRiskClassifier` (só no `auto`) + `ArgPattern` ("sempre nesta sessão"). oh-my-pi tem modelo de 3 inputs: tier declarado na tool (`read|write|exec`, default fail-closed `exec`), política da própria tool por argumento, e override do usuário por tool — que **não pode** enfraquecer um deny da tool. Nosso modelo é mais grosso: `RequiresApproval` é binário e o override por tool não existe (só por padrão de args e só na sessão).

## 2. Scope

**In scope:**
- `ApprovalTier` (`Read|Write|Exec`) declarado por `IBuiltinChatTool` — substitui o bool `RequiresApproval` (migração mecânica: `true`→`Exec`/`Write`, `false`→`Read`).
- Tools externas: MCP=`Write` default, HTTP/python custom=`Exec` (fail-closed, espelha omp).
- Override persistente por usuário `tools.approval.<name> = allow|deny|prompt` (kv/settings); aplica-se em todos os presets; não pode anular `Deny` do `PermissionRuleset` (plan/chat modes) nem deny da própria tool.
- Presets reinterpretados sobre tiers: `allow-readonly`→só Read livre; `approve-mutations`→Read livre, Write/Exec perguntam; `smart`/`auto`→Read livre, Write pelo classificador existente, Exec pergunta; `always-allow`→tudo livre (exceto denies duros).
- UI: no prompt de aprovação, além de "sempre nesta sessão", opção "sempre para esta tool (persistente)"; lista de overrides editável em Settings.

**Out of scope:**
- Políticas por-argumento declaradas pela tool (`policy:` com `policyKey` estilo omp) — `ArgPattern` atual cobre o caso comum; expandir depois se precisar.
- Overrides por projeto/workspace (só por usuário).
- Mudança de preset existente / novos presets.

## 3. Technical Context

**Where the change happens:**
`IBuiltinChatTool` ganha `ApprovalTier` (default `Exec` para tools novas — fail-closed); cada builtin em `ChatTools/Tools/` declara o tier correto; `ChatRunExecutor.GateToolCallAsync` consulta: (1) `PermissionRuleset` (modo) → deny duro, (2) override do usuário por tool, (3) tier×preset, (4) classificador no `auto`/`smart`. `ToolExecutor.IsMutable` passa a derivar de tier != Read. Overrides persistem em kv `user:{id}:tool-approvals` via `ConfigService`. Prompt de aprovação (`PermissionPromptCard`/`ChatView`) ganha a 3ª opção persistente.

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/ChatTools/IBuiltinChatTool.cs` (`RequiresApproval`)
- `src/OpenWebUI.Infrastructure/ChatTools/PermissionRuleset.cs` (modes + `ArgPattern`)
- `src/OpenWebUI.Infrastructure/ChatTools/ToolCallRiskClassifier.cs` + `CommandRiskClassifier.cs`
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` (`GateToolCallAsync` ~L532, allowlist de sessão)
- `src/OpenWebUI.Infrastructure/Services/ToolExecutor.cs` (`IsMutable`)
- `src/OpenWebUI.Client/Components/PermissionPromptCard.razor` + ChatView (approval flow)
- `src/OpenWebUI.Infrastructure/Services/ConfigService.cs` (kv de settings)

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/ChatTools/IBuiltinChatTool.cs        # ApprovalTier substitui RequiresApproval
src/OpenWebUI.Infrastructure/ChatTools/Tools/*.cs                 # tier por tool (21 arquivos)
src/OpenWebUI.Infrastructure/ChatTools/ToolApprovalPolicy.cs      # NOVO: resolve (modo, override, tier, preset) → decisão
src/OpenWebUI.Infrastructure/Services/ToolExecutor.cs             # IsMutable ← tier
src/OpenWebUI.Api/Runs/ChatRunExecutor.cs                         # gate usa ToolApprovalPolicy
src/OpenWebUI.Api/Endpoints/ConfigEndpoints.cs                    # GET/PUT overrides do usuário
src/OpenWebUI.Client/Components/PermissionPromptCard.razor        # opção "sempre p/ esta tool"
src/OpenWebUI.Client/Components/SettingsModal.razor               # lista/edição de overrides
src/OpenWebUI.Client/Services/ApiService.cs                       # Get/PutToolApprovals
src/OpenWebUI.Client/wwwroot/i18n/*.json
tests/OpenWebUI.Api.Tests/                                      # matriz tier×preset×override
```

## 4. Requirements

### RF-001: Tier por tool
- **Description:** `IBuiltinChatTool.ApprovalTier` ∈ `Read|Write|Exec`; ausente/não-classificado = `Exec` (fail-closed). Tabela de tiers: Read=`file_*` leitura, `lsp_*`, `web_search`, `fetch_url`, `memory_search`, `todo_write`, `ask_user`, `job_list/output`, `skill`, `run_result`; Write=`file_write`, `file_edit`, `apply_patch`, `memory_save`, `worktree_merge`, `generate_image/video`, `routine`, `reminder`; Exec=`shell_exec`, `code_interpreter`, `delegate_task`, `job_kill`, `browser_screenshot`(? decidir: screenshot é leitura de browser→Read; browsing actions se houver→Exec). MCP=`Write`; HTTP/python=`Exec`.
- **Rules:** `RequiresApproval` removido; `ToolExecutor.IsMutable(t)=tier!=Read`; tier é propriedade do tipo, não do call.

### RF-002: Override persistente por usuário
- **Description:** `tools.approval.<toolName>` ∈ `allow|deny|prompt` persistido por usuário; precede o preset; `deny` bloqueia com erro estruturado ao modelo; `prompt` força pergunta mesmo em `always-allow`; `allow` auto-aprova exceto quando o modo (`plan`/`chat`) ou a própria tool negam.
- **Rules:** ordem de decisão = modo(Deny?) → override → preset×tier → classificador (`auto`/`smart`); override nunca reabilita deny de modo; sem override → comportamento atual preservado.

### RF-003: Presets × tiers
- **Description:** Tabela preset→auto-approve: `allow-readonly`{Read}; `approve-mutations`{Read}; `smart`{Read + Write se classificador OK}; `auto`{Read + MEDIUM do classificador}; `always-allow`{Read+Write+Exec menos denies}.
- **Rules:** comportamento observável dos presets não pode regredir — tiers só adicionam precisão por tool.

### RF-004: UI de override
- **Description:** `PermissionPromptCard` ganha "Sempre para esta tool" (persistente, além de "sempre nesta sessão" por padrão de args); SettingsModal lista overrides com remover; i18n ×8.

**Business rules / invariants:**
- Fail-closed: tool sem tier → Exec; decisão inválida → prompt.
- Deny do modo (plan/chat) é absoluto — nem override `allow` reabilita.
- Overrides são do usuário dono da sessão; subagents herdam os do dono.

## 5. API Contract

**`GET /api/v1/configs/user/tool-approvals`**
```json
{ "approvals": { "shell_exec": "prompt", "file_read": "allow", "delegate_task": "deny" } }
```
**`PUT /api/v1/configs/user/tool-approvals`** — merge por chave; valor `null`/ausente no patch remove o override; nome de tool inválido → 400.

**Expected errors:** `401`; `400` tool/tier inválido — `{detail}`.

## 6. Acceptance Criteria

- [ ] **Dado** override `file_read=allow` **quando** run em `approve-mutations` chama file_read **então** executa sem prompt.
- [ ] **Dado** override `shell_exec=prompt` **quando** run em `always-allow` chama shell_exec **então** ainda pergunta.
- [ ] **Dado** override `file_write=allow` em modo `plan` **quando** call escapa **então** continua negado (modo vence override).
- [ ] **Dado** tool sem tier declarado **quando** chamada em `approve-mutations` **então** pergunta (Exec default).
- [ ] **Dado** MCP tool sem declaração **quando** em `approve-mutations` **então** pergunta (Write pergunta nesse preset).
- [ ] **Dado** "sempre para esta tool" clicado **quando** recarrego e repito a call **então** auto-aprova sem prompt e o override aparece em Settings.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Override de tool removida | nome órfão | ignorado na decisão; listado como inativo em Settings |
| deny + run subagent | filho delegado | herda overrides do dono |
| `deny` em ask_user | override deny | call devolve erro estruturado ao modelo, não prompt |

## 7. Task Plan

- [ ] **T1 — Discovery:** mapear todos os builtins + tabela de tiers (validar browser_screenshot e afins).
- [ ] **T2 — Modelo:** `ApprovalTier`, tier em cada `IBuiltinChatTool`, `IsMutable` por tier, `ToolApprovalPolicy` com ordem de decisão.
- [ ] **T3 — Persistência + endpoint:** kv do usuário + GET/PUT.
- [ ] **T4 — Gate:** `GateToolCallAsync` consome `ToolApprovalPolicy`; SSE de deny explicando origem (modo/override).
- [ ] **T5 — Client:** opção persistente no PermissionPromptCard + lista em SettingsModal + i18n.
- [ ] **T6 — Testes:** matriz tier×preset×override×modo; regressão dos presets atuais.
- [ ] **T7 — Validation + DoD + PR.**

**7.1 Validation:** NUnit unit (matriz de decisão) + integration (PUT/GET); ≥80% no tocado.

## 8. Organization Guardrails

- Branch `feature/Devin-20261013-approval-tiers`.
- Fail-closed em qualquer caminho novo; deny de modo é absoluto.
- Sem PII em logs — logar tool name e decisão, nunca args com secrets (reuso do scrubber).
- Não mudar presets existentes nem criar novos.

## 9. Definition of Done

- [ ] RF-001..RF-004 implementados; matriz de decisão testada.
- [ ] Nenhuma regressão observável nos presets (teste de regressão).
- [ ] build/test verde; drift guards OK.
- [ ] Docs do CLAUDE.md/RULES atualizadas se necessário.

**Next action after DoD:** `Status = Done` + PR.

## Open Questions / Pending Ambiguity

- `browser_screenshot` tier final (Read vs Exec) — decidir na implementação e registrar.
- Vale expor overrides por chat (além de por usuário)? Palpite: não em v1.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/357 |
| Issue | https://github.com/afonsoft/OpenWebUiCode/issues/351 |
