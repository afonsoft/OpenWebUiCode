# SPEC-20261021-harness-seams

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `harness-seams` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261021-harness-seams` |
| Ticket | [#486](https://github.com/afonsoft/open-webui/issues/486) |
| Status | Completed |
| Parent | `README.md` → "deepseek-harness: architecture deltas & ideas to adopt" (benchmark [deepseek-ai/deepseek-harness](https://github.com/deepseek-ai/deepseek-harness)) |

## 1. User Story

**As a** mantenedor desta plataforma de agente
**I want** os seams de extensibilidade inspirados no deepseek-harness — eventos
internos com interceptors, system prompt por contribuidores, superfícies
headless/SDK/ACP, webhooks provider-aware e agent teams sobre a hierarquia de
chats
**So that** novas capacidades se acoplam a pontos documentados sem alterar o
loop central, e o produto passa a ser dirigível por outros clientes (CLI,
editores, sistemas externos).

**Problem context:**
Hoje o loop de run (`ChatRunExecutor`, ~2.2k linhas) e a montagem de system
prompt (`ChatPipeline.EnrichRequestAsync`, ~10 blocos fixos em
`systemParts`) são pontos únicos e hardcoded: telemetria, guardrails ou
mutação de prompt exigem editar o executor. A única superfície é a web
(REST+SSE); não existe modo one-shot nem protocolo para editores. Webhooks
existem (`POST /api/v1/hooks/{token}`) mas são genéricos — sem verificação de
assinatura, filtro de evento ou template. `delegate_task` cria filhos
one-shot: sem roster, sem task list compartilhada, sem inbox entre irmãos.

## 2. Scope

**In scope:**

- **A. Event seams** — barramento interno: eventos de observação read-only
  (`run.*`, `step.*`, `tool.*`) + interceptors waterfall mutáveis em
  `pre-step` e `tool.call`, ordenados, fail-closed, com timeout.
- **B. Prompt contributors** — `ISystemPromptContributor` ordenado via DI;
  blocos existentes do `EnrichRequestAsync` viram contribuidores sem mudança
  de comportamento.
- **C. Superfícies** — modo headless one-shot (`--headless`), endpoint
  JSON-RPC 2.0 estável e host ACP (stdio) mapeando sessões ACP → chats/runs.
- **D. Webhooks v2** — `AutomationHook` ganha `Secret` (HMAC-SHA256
  timing-safe), `Provider` (generic/github), `EventsJson` (filtro),
  `PromptTemplate`, `RepoSlug`/`PersonaId`/`AgentMode`; entidade
  `AutomationHookDelivery` (idempotência por delivery id + auditoria).
- **E. Agent teams** — `Chat.TeamId` (id do chat raiz) + `TeamRole`; tabela
  `TeamTask`; tools `team_task`/`team_message`/`team_status`; inbox =
  steer/queue no endpoint existente; `delegate_task` ganha `team` e
  `resume_chat_id`.

**Out of scope:**

- Seam de provider de execução remota (sandbox/SSH para `shell_exec`/`lsp_*`)
  — item adiado na análise, SPEC futura.
- Plugin loading/unloading dinâmico (AssemblyLoadContext) ou HMR de
  serviços — o modelo Cordis completo fica fora; seams são compile-time DI.
- Plugin market, desktop app, migrações de formato de log versionadas.
- MCP server-side (expor nossas tools como MCP server) — já coberto em
  `SPEC-20261018-mcp-oauth` apenas no lado cliente.

## 3. Technical Context

**Where the change happens:**

- A: `ChatRunExecutor` emite eventos e consulta interceptors nos pontos já
  existentes (publicação de fase, step, tool call/result via
  `RunEventWriter`); novos contratos em `OpenWebUI.Application/Interfaces`
  (`IAgentRunObserver`, `IRunInterceptor`).
- B: `ChatPipeline.EnrichRequestAsync` — `systemParts` passa a ser
  preenchida por `IEnumerable<ISystemPromptContributor>` ordenado; cada
  bloco atual (base, always-allow, custom model, skills, files, web search,
  memories, repo binding, project_instructions, agent_memory, mentions,
  plan/chat mode) vira um contributor preservando a ordem atual.
- C: `Program.cs` ganha saída antecipada `--headless`; novo
  `Endpoints/RpcEndpoints.cs` (JSON-RPC); novo host `src/OpenWebUI.Acp/`
  (console stdio) reusando `IChatRunDispatcher`/`ChatRunBroadcaster`.
- D: `AutomationHookEndpoints` + `AutomationHook`/`AutomationHookDelivery`
  em `Entities.cs`; verificação HMAC no `FireAsync` antes do parse.
- E: `Entities.cs` (`Chat.TeamId`, `TeamRole`, `TeamTask`),
  `DelegateTaskBuiltinTool` (params `team`, `resume_chat_id`), novos tools
  em `ChatTools/Tools/TeamBuiltinTools.cs`, reuse de
  `ChatRunEndpoints.SteerRunAsync` para inbox.

**Files to read before implementing:**

- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` · `Runs/ChatRunDispatcher.cs`
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs`
- `src/OpenWebUI.Infrastructure/Services/RunEventWriter.cs` · `Services/ToolExecutor.cs`
- `src/OpenWebUI.Infrastructure/ChatTools/BuiltinToolRegistry.cs` · `IBuiltinChatTool.cs`
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/DelegateTaskBuiltinTool.cs` · `TodoWriteBuiltinTool.cs`
- `src/OpenWebUI.Api/Endpoints/AutomationHookEndpoints.cs` · `ChatRunEndpoints.cs` (steer)
- `src/OpenWebUI.Domain/Entities.cs` (`Chat`, `ChatRun`, `AutomationHook`)
- `src/OpenWebUI.Api/Program.cs` (bootstrap/DI)
- ACP: especificação `https://agentclientprotocol.com` (initialize, session/new,
  session/prompt, session/cancel, session/update).

**Files to create or modify:**

```text
src/OpenWebUI.Application/Interfaces/IAgentRunObserver.cs        # NOVO: run.*/step.*/tool.* read-only
src/OpenWebUI.Application/Interfaces/IRunInterceptor.cs          # NOVO: OnPreStepAsync/OnToolCallAsync waterfall
src/OpenWebUI.Application/Interfaces/ISystemPromptContributor.cs # NOVO: Order + ContributeAsync
src/OpenWebUI.Infrastructure/Runs/AgentRunEventBus.cs            # NOVO: dispatch ordenado + timeout fail-closed
src/OpenWebUI.Infrastructure/Runs/Interceptors/                  # NOVO: TelemetryObserver, RepeatToolGuard
src/OpenWebUI.Api/Completions/Contributors/*.cs                  # NOVO: ~11 contributors (blocos atuais)
src/OpenWebUI.Api/Completions/ChatPipeline.cs                    # systemParts ← contributors ordenados
src/OpenWebUI.Api/Runs/ChatRunExecutor.cs                        # publica eventos + invoca interceptors
src/OpenWebUI.Api/Program.cs                                   # --headless early-exit; DI registrations
src/OpenWebUI.Api/Headless/HeadlessRunner.cs                     # NOVO: run one-shot → stdout, exit code
src/OpenWebUI.Api/Endpoints/RpcEndpoints.cs                      # NOVO: POST /api/v1/rpc (JSON-RPC 2.0)
src/OpenWebUI.Acp/                                             # NOVO projeto: host stdio ACP → chats/runs
src/OpenWebUI.Api/Endpoints/AutomationHookEndpoints.cs           # HMAC, provider, filtro, template, delivery log
src/OpenWebUI.Domain/Entities.cs                               # AutomationHook+Delivery; Chat.TeamId/TeamRole; TeamTask
src/OpenWebUI.Infrastructure/ChatTools/Tools/TeamBuiltinTools.cs # NOVO: team_task/team_message/team_status
src/OpenWebUI.Infrastructure/ChatTools/Tools/DelegateTaskBuiltinTool.cs # params team, resume_chat_id
tests/OpenWebUI.Api.Tests/*                                    # cobertura por capability (A–E)
```

## 4. Requirements

> Agrupados por capability (A–E). Migrações EF via
> `dotnet ef migrations add` conforme soft rule 2.

### A — Event seams

#### RF-A1: `IAgentRunObserver` (observação)
- **Description:** Interface read-only com callbacks `OnRunEventAsync(RunEvent e)`
  recebendo `run.started`, `run.step_started`, `run.tool_call`,
  `run.tool_result`, `run.step_finished`, `run.completed`, `run.failed`,
  `run.steer`, `run.phase`. Registrada via DI (`IEnumerable<>`), invocada
  pelo `ChatRunExecutor` após o `RunEventWriter.AppendAsync` correspondente.
- **Rules:** observer lento/falho nunca aborta a run — exceção vira
  `LogWarning`; eventos carregam `RunId`, `ChatId`, `UserId`, `Type`,
  `PayloadJson` (mesmo payload do `RunEventWriter`).

#### RF-A2: `IRunInterceptor` (waterfall)
- **Description:** Hooks `OnPreStepAsync(PreStepContext)` e
  `OnToolCallAsync(ToolCallContext)` executados em ordem crescente de
  `Order`; cada um pode: passar (`next`), mutar mensagens/args, ou rejeitar
  (rejeição encerra o step com motivo logado + evento `interceptor.veto`
  durável).
- **Rules:** fail-closed — timeout (default 5s, config
  `interceptors.timeout_ms`) ou exceção ⇒ veto conservador (pre-step =
  passa inalterado e loga; tool.call = nega execução); interceptors nunca
  veem secrets (payload já scrubbed pelo `SecretScrubber`).

#### RF-A3: implementações iniciais
- **Description:** `TelemetryObserver` (observer: contadores/latência por
  run/tool via `ILogger` estruturado) e `RepeatToolGuard` (interceptor:
  N≥3 chamadas idênticas consecutivas da mesma tool+args ⇒ veto com
  instrução ao modelo, análogo ao `guard/repeat-tool-reminder` do dsh).

### B — Prompt contributors

#### RF-B1: `ISystemPromptContributor`
- **Description:** `int Order { get; }` +
  `IAsyncEnumerable<string>`/`Task<string?> ContributeAsync(PromptContext
  ctx)`; `PromptContext` carrega request, user, chatId, agentMode,
  repoBinding, db, services resolvidos. `EnrichRequestAsync` itera
  `contributors.OrderBy(Order)` e agrega as partes.
- **Rules:** ordem efetiva idêntica à atual (comparar prompt montado antes/
  depois num teste golden); contributor que lança exceção é pulado com
  `LogWarning` (mesmo comportamento tolerante dos blocos atuais).

#### RF-B2: migração dos blocos existentes
- **Description:** cada bloco de `systemParts` vira um contributor próprio
  (`BaseAgentContributor`, `AlwaysAllowContributor`, `CustomModelContributor`,
  `ModelSkillsContributor`, `FileContextContributor`, `WebSearchContributor`,
  `MemoriesContributor`, `RepoBindingContributor`, `ProjectInstructionsContributor`,
  `AgentMemoryContributor`, `MentionsContributor`, `AgentModeContributor`).

### C — Superfícies

#### RF-C1: headless one-shot
- **Description:** `dotnet run --project src/OpenWebUI.Api -- --headless
  --prompt "..." [--model M] [--repo owner/repo] [--mode chat|plan|build]`
  executa uma run única sem bind de portas, imprime eventos/resultado em
  stdout (JSON lines), exit code 0 sucesso / 2 falha / 3 timeout
  (`--timeout-seconds`, default 600). Auth: exige `--user-email` de usuário
  existente ou `HEADLESS_USER` env.
- **Rules:** reutiliza `ChatRunExecutor` via escopo DI; sem
  `WebApplication` listener; log vai para stderr (stdout é só protocolo).

#### RF-C2: JSON-RPC 2.0
- **Description:** `POST /api/v1/rpc` autenticado (JWT/`sk-*`). Métodos:
  `run.create` {chatId?, prompt, model?, mode?} → {runId, chatId};
  `run.steer` {runId, message, mode?}; `run.status` {runId};
  `run.events` {runId, sinceEventId?} → página de eventos duráveis;
  `chat.create`; `team.status` (E). Batch e `id` echo conforme JSON-RPC 2.0;
  erros `-32601`/`-32602`/`-32000`.
- **Rules:** sem streaming — eventos via polling de `run.events` (SSE já
  existe para o browser); rate limit do `ratelimit` config aplica.

#### RF-C3: ACP host (stdio)
- **Description:** projeto `OpenWebUI.Acp` (console) falando JSON-RPC stdio:
  `initialize`, `session/new` → cria Chat (+ repo binding opcional via
  `cwd`), `session/prompt` → enfileira run e emite `session/update`
  notifications (chunks de texto, tool_call/tool_result, plan updates de
  todo), `session/cancel` → cancela run. Autenticação via env
  `OPENWEBUI_ACP_TOKEN` (`sk-*` de serviço) ou primeiro-user local quando
  `--acp-local`.
- **Rules:** um processo por sessão de editor; graceful shutdown em
  `session/close`; publicável como ferramenta (`dotnet tool` opcional) —
  distribuição detalhada fica `[A DEFINIR]` para follow-up.

### D — Webhooks v2

#### RF-D1: assinatura e providers
- **Description:** `AutomationHook` ganha `Secret`, `Provider`
  (`generic`|`github`), `EventsJson`, `PromptTemplate`, `RepoSlug`,
  `PersonaId`, `AgentMode`. `FireAsync`: se `Secret` setado, exige
  `X-Hub-Signature-256` = `sha256=` + HMAC-SHA256(secret, rawBody) com
  comparação timing-safe — ausência/mismatch ⇒ 401; hooks sem secret
  seguem funcionando (deprecação documentada).
- **Rules:** provider `github` valida `X-GitHub-Event` contra `EventsJson`
  e dedup por `X-GitHub-Delivery`; provider `generic` aceita `X-Event-Type`
  opcional.

#### RF-D2: template + binding + delivery log
- **Description:** `PromptTemplate` com placeholders `{{field.path}}`
  resolvidos do payload (máx. 4KB interpolado); `RepoSlug`/`PersonaId`/
  `AgentMode` aplicados à run criada (chat herda binding). Nova entidade
  `AutomationHookDelivery` {Id, HookId, DeliveryKey, EventType,
  ReceivedAt, Status(accepted|filtered|rejected|failed), RunId?, Error?};
  `GET /api/v1/hooks/{id}/deliveries` autenticado.
- **Rules:** idempotência — mesmo `DeliveryKey` ⇒ retorna a primeira run;
  payload máx. 256KB; erro de template ⇒ 422 com campo faltante (sem PII).

### E — Agent teams

#### RF-E1: roster sobre a hierarquia
- **Description:** `Chat.TeamId` (id do chat raiz — filhos herdam no spawn;
  raiz = próprio id) + `Chat.TeamRole` (`leader`|`member`, default leader
  na raiz). `delegate_task` ganha `team: bool` (filho entra no team do
  chamador) e `resume_chat_id` (nova run em chat filho existente =
  subagente continuável). `GET /api/v1/chats/{id}/team` retorna roster:
  membros, roles, runs vivas, status.
- **Rules:** TeamId imutável após spawn; resolver team = subir ParentChatId
  até a raiz (coluna existe para query direta); sem tabela Team — identidade
  é o chat raiz.

#### RF-E2: task list + inbox
- **Description:** entidade `TeamTask` {Id, TeamId, Title, Status
  (pending|in_progress|done), AssigneeChatId?, CreatedByChatId, Result?,
  timestamps}. Tools: `team_task` (list|create|claim|update|complete),
  `team_message` (to: member chatId — vivo ⇒ `steer`; idle ⇒ mensagem
  durável de role `user` com prefixo `[team:{fromChatId}]`, que vira input
  no próximo resume), `team_status` (roster + tasks + runs). UI: badge de
  team no chat e aba "Tasks" do workspace ganha filtro por team.
- **Rules:** tools só operam dentro do TeamId do chamador (fail-closed
  fora); `team_message` para run terminal grava mensagem + notificação
  `automation.reminder` no chat alvo.

## 5. API Contract

**Endpoint:** `POST /api/v1/rpc` · **Auth:** JWT ou `Bearer sk-*`

**Request:**
```json
{ "jsonrpc": "2.0", "id": 1, "method": "run.create",
  "params": { "prompt": "…", "chatId": "…?", "model": "…?", "mode": "build" } }
```
**Response (success):**
```json
{ "jsonrpc": "2.0", "id": 1, "result": { "runId": "…", "chatId": "…" } }
```

**Endpoint:** `POST /api/v1/hooks/{token}` (existente, estendido)
**Auth:** token GUID na URL + `X-Hub-Signature-256` quando `Secret` setado
**Expected errors:** `400 corpo vazio` · `401 assinatura` · `404 hook` ·
`422 template/modelo` · `429 rate limit` — formato `{ "detail": "…" }`.

**Endpoint:** `GET /api/v1/chats/{id}/team` · **Auth:** JWT/`sk-*` →
`{ teamId, members: [{chatId, role, title, activeRunId?}], tasks: [...] }`

## 6. Acceptance Criteria

- [ ] **Given** um observer lançando exceção **when** a run emite eventos
  **then** a run completa normalmente e o erro sai em `LogWarning`.
- [ ] **Given** `RepeatToolGuard` ativo **when** a mesma tool é chamada 3×
  com args idênticos **then** a 3ª é vetada com `interceptor.veto` durável e
  o modelo recebe instrução de mudar de abordagem.
- [ ] **Given** contributors migrados **when** `EnrichRequestAsync` roda
  sobre fixtures conhecidos **then** o system prompt montado é byte-identico
  à implementação anterior (teste golden).
- [ ] **Given** `--headless --prompt "2+2"` **when** o processo roda
  **then** stdout emite JSON-lines dos eventos e termina com exit 0 sem
  abrir porta.
- [ ] **Given** client JSON-RPC **when** `run.create` + polling de
  `run.events` **then** a run executa e eventos chegam ordenados por id.
- [ ] **Given** hook com `Secret`+`github` **when** chega
  `X-Hub-Signature-256` inválida **then** 401 e nenhuma run; **when** o
  mesmo `X-GitHub-Delivery` repete **then** retorna a run original.
- [ ] **Given** `delegate_task` com `team:true` **when** dois filhos rodam
  **then** `GET …/team` lista ambos e `team_message` entre irmãos entrega
  via steer/queue.
- [ ] **Given** ACP `session/new`+`session/prompt` **when** stdio envia o
  prompt **then** `session/update` carrega chunks e tool calls até
  `stop_reason=end_turn`.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Interceptor timeout | hook >5s | veto conservador + log + evento |
| Contributor falha | exceção no contributor | seção omitida, run segue |
| Headless sem user | `--headless` sem `--user-email` | exit 2 + mensagem em stderr |
| Hook sem secret | POST sem assinatura | aceito (legado) + `SecurityWarning` na delivery |
| Team message p/ run morta | alvo terminal | mensagem durável + notificação |
| ACP sem token | spawn sem env | erro `auth required` no `initialize` |

## 7. Task Plan

- [x] **T1 — Discovery:** ler arquivos da seção 3 + spec ACP; baseline de
  testes atual.
- [x] **T2 — A+B:** contratos `IAgentRunObserver`/`IRunInterceptor`/
  `ISystemPromptContributor`, bus, interceptors iniciais e migração dos 12
  contributors com teste golden.
- [x] **T3 — E:** schema (`TeamId`/`TeamRole`/`TeamTask`), `delegate_task`
  params, tools de team, endpoint roster.
- [x] **T4 — D:** campos do hook, HMAC, filtro/template, delivery log,
  migração EF.
- [x] **T5 — C:** headless runner, `RpcEndpoints`, projeto ACP + adapter de
  eventos → `session/update`.
- [x] **T6 — Validação:** suíte completa + drift guards
  (`tools/check-*.py`) + cobertura por capability.
- [ ] **T7 — Done + PR:** DoD → `Status = Done` → PR na branch
  `feature/Devin-20261021-harness-seams`.

**7.1 Validation strategy (.NET):** unit tests nas regras (HMAC, waterfall
ordering/fail-closed, template resolver, team ACL) + integration tests nos
endpoints + golden test do prompt; gate de cobertura ratchet existente
(≥80% nos novos serviços).

## 8. Organization Guardrails

- Branch dedicada `feature/Devin-20261021-harness-seams`; nunca commit em
  `main`; não tocar `.github/workflows/`.
- Sem secrets em log/código; `Secret` do hook armazenado com a mesma
  proteção das API keys (`UserSecretsService`/criptografia em repouso).
- Nenhum comportamento existente muda sem teste de paridade (B2 golden,
  hooks legados sem secret).
- Mudança de schema ⇒ `dotnet ef migrations add` + baseline legado do
  `DatabaseMigrator` preservado.
- CSS: se novas classes em `.razor` (badge/aba Tasks), regenerar
  `tailwind.css` via skill `tailwind-css`.

## 9. Definition of Done

- [ ] Todos os RFs (A1–E2) implementados e critérios de aceitação verdes.
- [ ] `dotnet build OpenWebUI.slnx --configuration Release` +
  `dotnet test tests/OpenWebUI.Api.Tests` passando.
- [ ] `python3 tools/check-css-classes.py`, `check-form-a11y.py`,
  `check-i18n-parity.py`, `check-spec-status.py` limpos (se UI tocada).
- [ ] Migrações EF criadas e testadas em base legada.
- [ ] README/docs atualizados (seção agent features + webhook docs).
- [ ] Sem PII/tokens em logs; erros no formato `{detail}`.

## Open Questions / Pending Ambiguity

- Distribuição do host ACP (`dotnet tool` vs script no repo vs binário
  self-contained) — `[A DEFINIR]` na implementação, não bloqueia o contrato.
- Providers de webhook além de `generic`/`github` (gitlab, linear) — fase 2.
- `PromptTemplate` engine: placeholder simples `{{path}}` (recomendado) vs
  engine completa (scriban/liquid) — decidir no T4.
