# SPEC-20261016-commands-v2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `commands-v2` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261016-commands-v2` |
| Ticket | [#403](https://github.com/afonsoft/open-webui/issues/403) · Epic [#398](https://github.com/afonsoft/open-webui/issues/398) |
| Status | `Approved` |
| Fonte | análise opencode v2.0.31 (docs `/commands` + `command.ts` — frontmatter agent/model/subagent, `$ARGUMENTS`/`$N`, `!`shell`` expansion) |

## 1. User Story

**As a** usuário com workflows repetitivos
**I want** commands markdown que selecionam agente/modelo, rodam em subagente e expandem com args posicionais (e opcionalmente saída de shell)
**So that** `/review src/auth.ts` vira um prompt estruturado executado com o contexto certo — não só texto colado no input.

**Problem context:**
Hoje commands do repo são interpolados no cliente (`ChatView.razor:1512` — `RepoSlashCommands.Interpolate` com `$N`) e submetidos como prompt comum: sem seleção de agent/mode, sem modelo dedicado, sem execução em subagente e sem expansão de shell. O opencode trata o command como um template rico avaliado no servidor.

## 2. Scope

**In scope:**
- RF-001: frontmatter de command: `description`, `agent` (mode/agent definition), `model` (`provider/model` ou id local), `subagent` (`true` → run filha via `delegate_task`), `mode` (`plan|build|chat` atalho para mode do chat).
- RF-002: template com `$ARGUMENTS`, `$1..$9` (último captura resto), quotes agrupando — portar a semântica do opencode.
- RF-003: expansão server-side no submit (`POST /api/v1/runs` ou endpoint de expansão) — o client envia `command:` + args crus; o servidor expande e persiste o prompt final na mensagem (transparência: a mensagem mostra o texto expandido).
- RF-004: `agent:`/`mode:` do command aplica o modo da run; `model:` troca o modelo só daquela run (override request).
- RF-005: `subagent:true` → cria run filha (reutiliza `DelegateTaskBuiltinTool`/`ChatRun` com `ParentRunId`) e devolve link; a run-mãe recebe a notificação de término.
- RF-006: `!`shell`` expansion — **opt-in por config** (`RepoCommands:AllowShellExpansion`, default **off**). Quando on: executa no workdir com timeout 30s, saída limitada a 8KB, marcada como `<shell-output cmd="...">` — comando nunca inclui args do usuário não-quotados. Off → bloco vira literal `[shell block omitted: disabled]`.

**Out of scope:**
- Hot-reload de commands (cache TTL já cobre).
- Commands aninhados (command chamando command).
- Compatibilidade com `subtask` legado.

## 3. Technical Context

- `src/OpenWebUI.Infrastructure/Services/SkillDiscoveryService.cs` — já lê `.md` de commands (precisa parse de frontmatter YAML simples).
- `src/OpenWebUI.Client/Components/ChatView.razor` + `RepoSlashCommands` — picker + interpolação atual (migrar para endpoint server-side).
- `src/OpenWebUI.Api/Endpoints/RepoSkillEndpoints.cs` — `GET /commands/{name}` (adicionar campos do frontmatter ao payload).
- `src/OpenWebUI.Api/Endpoints/ChatRunEndpoints.cs` / `Runs/ChatRunExecutor.cs` — override de mode/model + run filha.
- `ChatRun` entidade — existe `ParentRunId`? (delegate_task já cria runs filhas — reutilizar).

## 4. Requirements

### RF-001: Parse de frontmatter
- **Description:** `CommandDefinition` = `{name, description?, agent?, model?, subagent?, mode?, template(body), scope}`; frontmatter YAML simples (campos string/bool), tolerante a ausência.

### RF-002: Expansão de args
- **Input → Output:** `template` + `args string` → prompt final; `$ARGUMENTS` = linha inteira; `$1..$9` posicionais com quotes; último placeholder numérico absorve resto (semântica opencode).

### RF-003: Expansão no servidor
- **Description:** `POST /api/v1/workspace/repo/commands/{name}/expand` `{args}` → `{prompt, agent?, model?, subagent?}` — o composer continua colando o prompt no input quando não há agent/model; quando há, submete via payload `command:{name}:{args}` que o servidor expande na criação da run (mensagem persistida = texto expandido).

### RF-004: Overrides de run
- **Description:** `agent`/`mode` → `EffectiveModeAsync` do chat daquela run; `model` → `request.Model` override validado contra os modelos do usuário.

### RF-005: Subagent run
- **Description:** `subagent:true` → run criada como filha (`ParentRunId`); feed da mãe recebe evento `subagent_completed` com resumo.

### RF-006: Shell expansion opt-in
- **Description:** blocos `` !`...` `` avaliados só se `RepoCommands:AllowShellExpansion=true`; executa com `ChatProcessRunner` no workdir, 30s, 8KB, sem network? (permitir — é comando do dono do repo; documentar o risco como opencode faz).

## 5. API Contract

**Endpoint:** `POST /api/v1/workspace/repo/commands/{name}/expand`
**Auth:** Bearer JWT / `sk-*`

**Request:**
```json
{ "args": "src/auth.ts strict" }
```
**Response:**
```json
{ "prompt": "Review src/auth.ts strict for bugs…", "agent": "plan", "model": null, "subagent": false }
```
**Expected errors:** `404 command` · `400 args` · `401 auth`.

## 6. Acceptance Criteria

- [ ] **Given** `review.md` com `agent: plan` e corpo `Review $ARGUMENTS.` **when** `/review src/auth.ts` **then** a run entra em modo plan com o prompt expandido.
- [ ] **Given** `compare.md` `Compare $1 with $2.` **when** `/compare a "b c" d` **then** prompt = `Compare a with b c d.` (último absorve resto).
- [ ] **Given** `audit.md` com `subagent: true` **when** `/audit` **then** run filha criada e mãe notificada ao fim.
- [ ] **Given** `` !`git diff --stat` `` e flag off **when** expande **then** literal `[shell block omitted: disabled]`; flag on → saída embutida com tag.
- [ ] **Given** `model: inexistente` **when** submete **then** `400` com erro claro (sem fallback silencioso).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Command sem frontmatter | body puro | funciona como hoje |
| `$0` ou `$10` | template | `$0`=literal; >9 literal |
| Args em command sem placeholder | `/cmd x` | args anexados após linha em branco (fallback opencode) |
| `agent` desconhecido | `agent: foo` | `400` na expansão |

## 7. Task Plan

- [ ] **T1 —** `CommandDefinition` + parser frontmatter no `SkillDiscoveryService`.
- [ ] **T2 —** Expansão `$ARGUMENTS`/`$N` + fallback; endpoint `/expand`.
- [ ] **T3 —** Payload `command:` na criação de run + overrides mode/model.
- [ ] **T4 —** `subagent:true` → run filha + evento de retorno.
- [ ] **T5 —** `` !`shell` `` opt-in com limites.
- [ ] **T6 —** Composer migra p/ expand endpoint (mantém interpolação local como fallback offline?).
- [ ] **T7 —** Testes + build; DoD; PR.

**7.1 Validation strategy:** unit tests parser/expansão/flags; integration endpoint + run filha; ≥80%.

## 8. Organization Guardrails

- Branch `feature/devin-20261016-commands-v2`.
- Shell expansion **default off** — doc no `.env.exemplo` com aviso (mesmo aviso do opencode: só fontes confiáveis).
- Command content NUNCA executa código por si só (exceto bloco opt-in).
- `model`/`agent` overrides validados contra permissões do usuário.

## 9. Definition of Done

- [ ] RF-001..RF-006 implementados e testados.
- [ ] Critérios da seção 6 verdes.
- [ ] build/test verdes; docs do formato em `docs/`.

## Open Questions / Pending Ambiguity

- `agent:` refere-se a `agents/*.agent.md` (AgentDefinitionService) ou mode? Decisão: aceita ambos — nome de agent definition resolve, senão cai em mode se `plan|build|chat`.
