# SPEC-20261017-agent-secrets-env

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `agent-secrets-env` |
| Type | `Feature` + `Bugfix` |
| Stack | ASP.NET Core Minimal APIs, kv `ConfigService`, process spawn (`ProcessStartInfo`), Blazor WASM Settings, git, `gh` CLI |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/agent-20261017-secrets-env` |
| Status | `Completed` |
| Ticket | [#445](https://github.com/afonsoft/open-webui/issues/445) |
| Origem | análise de sessão 2026-10-17 — agente falhou em `git push`/`gh pr create` ("Could not read Username", "gh: not found", `GITHUB_TOKEN` ausente) apesar do PAT configurado. Usuário pediu: **seção de Secrets nas configurações** (chave→valor, ex.: `SONAR_TOKEN`) repassada ao shell e terminal — **com nível global (admin) e override por usuário**, e o `GITHUB_TOKEN` da integração já entra por padrão. |

## 1. User Story

**As a** usuário do chat/agente
**I want** cadastrar segredos (nome → valor) no meu Settings — herdando os defaults globais definidos pelo admin e podendo sobrescrevê-los — e tê-los injetados como variáveis de ambiente nos processos das tools (`shell_exec`, jobs, terminal)
**So that** o agente use CLIs autenticadas (`git push`, `gh`, `sonar-scanner`, etc.) sem que tokens sejam colados no chat ou no repositório.

**Problem context:** `GitHubService.GetTokenAsync` resolve o PAT do GitHub (kv `u:{userId}:github.token` → fallback global `github.token`), mas é usado **só server-side** (clone/fetch/pulls via `http.extraheader` — `WorkspaceRepoService.RunAsync` linhas 516-523). Já `ChatProcessRunner.RunAsync` spawna `/bin/sh -c` **sem nenhuma env injetada** (`ChatProcessRunner.cs:38-48`), e o mesmo vale para jobs e PTY. Não existe mecanismo de secrets do usuário nem globais — só o PAT GitHub fixo.

## 2. Scope

**In scope:**
- Store de secrets **por usuário** no kv: `u:{userId}:secrets` → `Dictionary<string,string>` (name→value).
- Store de secrets **globais** (admin) no kv: `secrets` → `Dictionary<string,string>` — defaults para todos os usuários.
- **Resolução com override:** `env = globalSecrets ∪ userSecrets` — nome igual no usuário **vence** o global (mesmo padrão do PAT: user → global).
- API REST: user (`/api/v1/secrets`) + admin (`/api/v1/configs/secrets`); listagens expõem **só nomes + timestamps + origem** — nunca valores.
- UI: `SettingsSection` "Secrets" na tab **integrations** do `SettingsModal.razor` (com badge de origem `user`/`global` como GitHub/MCP) + seção "Secrets (global)" na área de admin defaults.
- Injeção no env dos processos: `shell_exec` (foreground + `background` via `ChatJobService`) e terminal PTY (`LocalTerminalSpawner`).
- Credencial GitHub gerenciada **por padrão**: PAT resolvido (user → global) injeta `GITHUB_TOKEN`/`GH_TOKEN` + `GIT_TERMINAL_PROMPT=0` + `GIT_ASKPASS` (script que só ecoa `$GITHUB_TOKEN` — segredo nunca em disco nem em `.git/config`). As vars gerenciadas **sóbrepõem** secrets de mesmo nome — que são bloqueadas no cadastro (RF-002).
- Scrub de valores literais nos outputs das tools (regexes do `SecretScrubber` + literais dos secrets resolvidos).

**Out of scope:**
- Endpoint que devolva valores — **proibido**; listagens mostram só nome/createdAt/updatedAt/source.
- Criptografia at-rest do kv (hoje `github.token` já fica plaintext na base — hardening é SPEC futura).
- Scrub do stream do terminal PTY (saída visível só ao dono; garantia de scrub é nas saídas que vão ao modelo — `shell_exec`/jobs).
- Editar `.github/workflows/`; portar secrets para MCP servers externos.

## 3. Technical Context

- `src/OpenWebUI.Infrastructure/Services/GitHubService.cs` — `GetTokenAsync(userId)` (54-56); chaves kv do PAT + padrão user→global.
- `src/OpenWebUI.Infrastructure/Services/ConfigService.cs` — kv `SetAsync`/`GetAsync`/`ListByPrefixAsync` (tabela `ConfigEntries`).
- `src/OpenWebUI.Api/Endpoints/ConfigEndpoints.cs` — padrão admin `GET/PUT/DELETE /api/v1/configs/github` com `IsAdmin` (linhas 403-446) — espelho para `/api/v1/configs/secrets`.
- `src/OpenWebUI.Infrastructure/ChatTools/ChatProcessRunner.cs` — `RunAsync` precisa de `env` + `secretsToScrub` opcionais.
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/ShellExecBuiltinTool.cs` — injeção no foreground; `RunInBackgroundAsync` → `ChatJobService`.
- `src/OpenWebUI.Infrastructure/ChatTools/ChatJobService.cs`, `src/OpenWebUI.Infrastructure/Services/LocalTerminalSpawner.cs` — mesma env em jobs e PTY.
- `src/OpenWebUI.Infrastructure/ChatTools/SecretScrubber.cs` — regexes cobrem `ghp_*`/`github_pat_*`/`sk-*`/AWS/Bearer/PEM; secrets arbitrários (`sqp_*`, tokens genéricos) exigem **scrub literal** por valor resolvido.
- `src/OpenWebUI.Client/Components/SettingsModal.razor` — tab `integrations` (253+), `SettingsSection`/`SettingsSaveBar`, `_ghToken` write-only (1320-1364), badges `settings.cfg_source_user|global` (256-267), seção admin defaults (~1078) para o PAT global.
- `src/OpenWebUI.Client/Services/ApiService.cs` — `Get/Set/ClearGitHubTokenAsync` + `SetGlobalGitHubTokenAsync` como padrão dos métodos de secrets.
- `BuiltinToolContext` (`ChatTools/BuiltinToolContext.cs`) — `UserId`/`ChatId`/`RunId`; o resolvedor de env usa `UserId`.

## 4. Requirements

- **RF-001 — Store em 2 níveis:** `UserSecretsService` (Infrastructure/Services) sobre `ConfigService`:
  - usuário: chave `u:{userId}:secrets` (`Dictionary<string,string>`);
  - global (admin): chave `secrets` (`Dictionary<string,string>`).
  Operações: `ListResolvedAsync(userId)` (merge global→user com `Source`), `SetAsync`, `DeleteAsync` por nível. Máx. 50 secrets por nível; valor ≤ 8KB.
- **RF-002 — Validação de nome (ambos os níveis):** regex `^[A-Za-z_][A-Za-z0-9_]{0,63}$`. **Blocklist gerenciada/perigosa:** `GITHUB_TOKEN`, `GH_TOKEN`, prefixos `GIT_`, `LD_`, `DYLD_`, e nomes `PATH`, `HOME`, `SHELL`, `NODE_OPTIONS`, `BASH_ENV`, `ENV` → 400 ("gerenciado pela integração GitHub" / "nome reservado").
- **RF-003 — API:**
  - User: `GET /api/v1/secrets` → `[{name, createdAt, updatedAt, source: "user"|"global"}]` (merge já aplicado, sem valores); `PUT /api/v1/secrets/{name}` `{value}` → upsert **user** (criar com nome igual a um global = override); `DELETE /api/v1/secrets/{name}` → remove só o do usuário (o global volta a valer).
  - Admin: `GET/PUT/DELETE /api/v1/configs/secrets[/{name}]` — mesmo shape, nível global, gate `IsAdmin` (padrão `configs/github`).
- **RF-004 — UI Secrets:**
  - Tab **integrations**: `SettingsSection` "Secrets" — lista de nomes com badge `user`/`global` (reuso `settings.cfg_source_*`), timestamps, remover (só `source=user`; global mostra hint "herdado — cadastre mesmo nome para sobrescrever"); form Nome+Valor (`type=password`, `autocomplete=off`), validação client-side.
  - Área admin (admin defaults): `SettingsSection` "Secrets (global)" — mesmo componente, nível global.
  - Valor **nunca** reexibido após salvar.
- **RF-005 — Env do agente:** `AgentSecretsEnvService` devolve `IReadOnlyDictionary<string,string>` = `globalSecrets ∪ userSecrets ∪ managed`. Gerenciadas (só quando PAT resolve): `GITHUB_TOKEN`, `GH_TOKEN`, `GIT_TERMINAL_PROMPT=0`, `GIT_ASKPASS` → `data/credentials/{userId}/git-askpass.sh` (`echo` de `x-access-token`/`$GITHUB_TOKEN` — segredo só em env; dir/script `0700`). Sem nada resolvido → env vazia, zero mudança.
- **RF-006 — Wiring:** `ChatProcessRunner.RunAsync(…, env?, secretsToScrub?)` aplica `psi.Environment[k]=v`; `ShellExecBuiltinTool` resolve via provider; `ChatJobService` e `LocalTerminalSpawner` recebem a mesma env.
- **RF-007 — Scrub literal:** outputs de `shell_exec`/jobs passam `secretsToScrub` (valores com length ≥ 4) — após os regexes do `SecretScrubber`, cada literal vira `[REDACTED_SECRET]`. Segredo nunca em logs, `BuiltinToolResult`, args da tool ou resposta de API.
- **RF-008 — `gh`/git:** com `GH_TOKEN`/`GITHUB_TOKEN`+`GIT_ASKPASS`, `gh` autentica sem `gh auth login` e `git push/fetch/pull` em `https://github.com/…` funcionam sem interação. Binário ausente → `MissingCommandHint` existente.

## 5. API Contract

```http
GET    /api/v1/secrets                    → 200 [{ "name":"SONAR_TOKEN", "source":"user", "createdAt":…, "updatedAt":… }]
PUT    /api/v1/secrets/{name}             → 200 { "name":"SONAR_TOKEN" } | 400 {detail} (inválido/reservado/limite)
DELETE /api/v1/secrets/{name}             → 200 { "status": true }  # só nível user

GET    /api/v1/configs/secrets            → 200 [{ "name":…, "createdAt":…, "updatedAt":… }]        (admin)
PUT    /api/v1/configs/secrets/{name}     → 200 { "name":… }                                        (admin)
DELETE /api/v1/configs/secrets/{name}     → 200 { "status": true }                                  (admin)
```

`GET /api/v1/github/config` pode opcionalmente expor `agentEnv: bool` (PAT disponível às tools) — sem valor.

## 6. Acceptance Criteria

- **Cenário 1 (override):** dado `SONAR_TOKEN` global = `sqp_global` e o usuário cadastra `SONAR_TOKEN=sqp_user`, então o `shell_exec` do usuário vê `sqp_user` (mascarado no output); outro usuário sem override vê `sqp_global`.
- **Cenário 2:** usuário remove seu `SONAR_TOKEN` → próximo processo volta a ver o global.
- **Cenário 3 (GitHub default):** PAT válido → `git push` no repo vinculado autentica via askpass; `gh auth status` ok — sem o usuário cadastrar nada.
- **Cenário 4:** `PUT /api/v1/secrets/GITHUB_TOKEN` ou `PATH` → 400 reservado; nome `1BAD` → 400.
- **Cenário 5:** `env`/`printenv` num comando permitido → todos os valores de secrets saem `[REDACTED_SECRET]`.
- **Cenário 6:** `GET /api/v1/secrets` não vaza nomes entre usuários; `PUT`/`DELETE` em `/configs/secrets` sem admin → 403.

| Edge case | Entrada | Esperado |
| --- | --- | --- |
| Override de gerenciada | `PUT GITHUB_TOKEN` | 400 — bloqueado antes do merge |
| Valor curto (<4 chars) | `A=x` | injeta mas não entra no scrub literal — documentado |
| Secret editado | re-`PUT` mesmo nome | `updatedAt` renova; próxima execução usa novo valor |
| Admin remove global | `DELETE /configs/secrets/X` | usuários sem override perdem `X`; com override mantêm o seu |
| Usuário remove PAT | `DELETE /github/config` | vars gerenciadas saem do env; askpass removido |
| 51º secret | PUT | 400 "limite de 50" |

## 7. Task Plan

- [x] T1 — `UserSecretsService` (kv user+global, merge com `Source`, validação + blocklist) + contratos; testes NUnit (override, nome/limite/reservados).
- [x] T2 — endpoints user `/api/v1/secrets` + admin `/api/v1/configs/secrets`; testes de isolamento e gate admin.
- [x] T3 — `AgentSecretsEnvService` (merge ∪ managed + askpass) + `env`/`secretsToScrub` em `ChatProcessRunner`; wiring `ShellExec`/`ChatJobService`/`LocalTerminalSpawner`.
- [x] T4 — `SecretScrubber.Scrub(output, literals)` sobrecarga + teste `echo $SONAR_TOKEN → [REDACTED_SECRET]`; limpeza askpass no `ClearTokenAsync`/`ClearGlobalTokenAsync`.
- [x] T5 — UI: seção Secrets (integrations) + seção global (admin) + métodos `ApiService` + i18n 8 locales; drift guards (`check-i18n-parity`, `check-form-a11y`, `check-css-classes`) + `tailwind.css` se preciso.
- [x] T6 — `dotnet test`, `dotnet build -c Release`, PR.

## 8. Organization Guardrails

- Valores **write-only**: API nunca devolve `value`; UI nunca reexibe.
- Nenhum segredo em `.git/config`, remote, arquivo do workspace, logs ou transcript — só env de processo e script askpass que referencia a env.
- Script askpass e artefatos ficam em `data/credentials/` — fora do workspace editável pelo agente (jail das tools).
- Prefixo `GIT_`/envs críticas bloqueados no cadastro (ambos os níveis) para não quebrar o runner nem o askpass gerenciado.

## 9. Definition of Done

- [x] Secrets globais (admin) e de usuário cadastráveis em Settings, com override user→global funcionando no env.
- [x] `GITHUB_TOKEN`/`GH_TOKEN`/`GIT_ASKPASS` injetados por padrão quando há PAT.
- [x] Saídas sempre scrubbed; testes + build + guards verdes; PR aberto.

## 10. Open Questions

- Expirar/rotacionar secrets? ➡️ fora — delete+recreate manual basta nesta fase.
- Auditoria de uso (qual run consumiu qual secret)? ➡️ follow-up — hoje só log do lado do processo.

## Delivered

| Item | Link |
| --- | --- |
| Issue | https://github.com/afonsoft/open-webui/issues/445 |
| Branch | `feature/agent-20261017-chat-workspace-secrets-browser` |
