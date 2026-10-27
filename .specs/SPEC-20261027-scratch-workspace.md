# SPEC-20261027-scratch-workspace

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `scratch-workspace` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` (github.com/afonsoft/open-webui) |
| Branch | `feature/scratch-workspace` |
| Ticket | #547 — https://github.com/afonsoft/open-webui/issues/547 |
| Status | `In implementation` |

## 1. User Story

**As a** usuário do Open WebUI .NET
**I want** que todo chat (modo chat, plan ou build) sem repositório vinculado trabalhe numa pasta própria efêmera, com opção de publicar o resultado num repo GitHub ao final
**So that** o trabalho de cada conversa fique isolado, versionado localmente e publicável — em vez de cair na raiz compartilhada `workspaces/{userId}` ou ser bloqueado por falta de repo.

**Problem context:**
Hoje, sem repo vinculado, `ResolveWorkdirAsync` devolve `workspaces/{userId}` — raiz compartilhada entre TODOS os chats sem repo do usuário (arquivos vazam entre conversas). Além disso, `PermissionRuleset` nega todas as `WorkspaceTools` em modo chat e `PatchChatAsync` rejeita `mode=build` sem binding — ou seja, sem repo não há como trabalhar com arquivos. O modelo worktree (SPEC-20261008/20261010/SPEC-15) já resolve isolamento e lifecycle para chats COM repo; falta o análogo para chats SEM repo.

## 2. Scope

**In scope:**
- Pasta scratch por chat: `data/workspaces/{userId}/scratch/{chatId}`, criada **lazy** (primeiro resolve de workdir do chat sem binding), com `git init` na materialização.
- Resolução de workdir sem binding passa a devolver o scratch do chat (não mais a raiz do usuário).
- Desbloqueio das workspace tools sem repo: com scratch disponível, `WorkspaceTools` deixam de ser negadas estaticamente e seguem o preset de aprovação (Ask por default). `mode=build` passa a ser permitido sem repo (o PATCH deixa de exigir binding).
- Subagentes sem repo: workdir do filho em `scratch/{parentChatId}/agents/{childChatId}` — dentro da árvore do pai, para o publish incluir o trabalho dos filhos.
- Lifecycle: delete e archive do chat removem o scratch (divergência consciente do modelo worktree-com-repo, que preserva binding no archive — scratch é rascunho descartável). ArchiveAll/DeleteAll varrem scratches também.
- Publish: `POST /api/v1/chats/{id}/workspace/publish` aceitando `{ "repo": "owner/name" }`, `{ "repo": "name" }` (cria sob a conta do PAT), `{ "remoteUrl": "https://x:TOKEN@github.com/o/r.git" }` ou `{ "repo": "name", "token": "..." }`. Fluxo: commit de mudanças pendentes → repo novo criado via GitHub API quando necessário → push (`main` em repo novo; branch `chat/{chatId}` quando o repo já existir, retornando URL de compare/PR).
- UI mínima: botão "Publicar" no chat quando há scratch com conteúdo e sem repo + dialog (repo novo / URL com token / token ad-hoc). i18n nos 8 locales.
- `GitHubService.CreateRepoAsync` (POST /user/repos) — novo método.

**Out of scope:**
- Polish visual além do botão/dialog mínimo (badge, diff viewer, histórico de commits na UI).
- Publish para hosts não-GitHub (GitLab/Bitbucket) — `remoteUrl` genérica já cobre o transporte, mas criação de repo é só GitHub.
- Download da pasta como ZIP (alternativa ao push) — fase posterior.
- Sweep de scratches órfãos no startup (folders cujo chat já não existe) — cleanup acontece via delete/archive; orphan sweep é hardening posterior.
- Mudar comportamento de archive/unarchive para chats COM repo (worktree recriado sob demanda — inalterado).

## 3. Technical Context

**Where the change happens:**
`WorkspaceRepoService.ResolveWorkdirAsync` é o ponto único de resolução — todas as tools (`file_*`, `shell_exec`, terminal PTY, LSP, `delegate_task`) e `ChatPipeline` (`WorkdirFactory`) passam por ele. `ReleaseChatWorkspaceAsync` é o ponto único de cleanup, já chamado por delete/archive/archive-all/delete-all em `ChatEndpoints`. `PermissionRuleset.Evaluate` controla o gate por modo. `PatchChatAsync` (ChatEndpoints ~L724) hoje rejeita `mode=build` sem binding. `GitHubService` já guarda PAT por usuário (`kv`), valida e chama a REST API — falta `CreateRepoAsync`. Clone/push autenticado usa `http.extraheader` (token nunca persiste no remote — `CloneAsync` já sanitiza).

**Files to read before implementing:**
- `CLAUDE.md` · `.claude/rules/`
- `src/OpenWebUI.Infrastructure/Services/WorkspaceRepoService.cs` (binding, workdir, worktree, release)
- `src/OpenWebUI.Infrastructure/Services/WorkspaceGitService.cs` (helpers git)
- `src/OpenWebUI.Infrastructure/Services/GitHubService.cs` (PAT, REST API)
- `src/OpenWebUI.Infrastructure/ChatTools/PermissionRuleset.cs` (gate por modo)
- `src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs` (patch mode, delete/archive, workspace-repo, pipeline binding)
- `src/OpenWebUI.Api/Endpoints/WorkspaceEndpoints.cs` + `WorkspaceFileEndpoints.cs` + `TerminalPtyEndpoints.cs` (consumidores do workdir)
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/DelegateTaskBuiltinTool.cs` (spawn de filhos)
- `tests/OpenWebUI.Api.Tests/GitHubWorkspaceTests.cs` (padrão de testes workspace)

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/WorkspaceRepoService.cs   # scratch ensure/resolve/release
src/OpenWebUI.Infrastructure/Services/WorkspaceGitService.cs    # init/commit/push helpers (se não existir)
src/OpenWebUI.Infrastructure/Services/GitHubService.cs          # + CreateRepoAsync
src/OpenWebUI.Infrastructure/ChatTools/PermissionRuleset.cs     # desbloqueio WorkspaceTools com scratch
src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs                    # publish endpoint + PATCH build sem repo
src/OpenWebUI.Application/Contracts/*.cs                        # PublishRequest/Response
src/OpenWebUI.Client/Components/… (header do chat)              # botão Publicar + dialog
src/OpenWebUI.Client/wwwroot/i18n/*.json                        # ×8 locales
tests/OpenWebUI.Api.Tests/ScratchWorkspaceTests.cs              # novo
tests/OpenWebUI.Api.Tests/GitHubWorkspaceTests.cs               # regressão
```

## 4. Requirements

### RF-001: Scratch workspace por chat
- **Description:** Todo chat sem binding de repo tem pasta de trabalho exclusiva `data/workspaces/{userId}/scratch/{chatId}`.
- **Rules:** Materialização lazy — `Directory.CreateDirectory` + `git init` somente no primeiro `ResolveWorkdirAsync` com `chatId` e sem binding; `chatId` sanitizado contra path traversal; idempotente (re-resolve não recria). Chats que nunca usam tools não geram pasta.
- **Input → Output:** `ResolveWorkdirAsync(userId, chatId, ct)` sem binding → path absoluto do scratch materializado.

### RF-002: Resolução de workdir
- **Description:** Sem binding + `chatId` → scratch do chat. Sem `chatId` (uso global do usuário) → `WorkspaceRoot` (inalterado). Com binding → worktree/checkout (inalterado).
- **Rules:** Binding por chat continua estrito (SPEC-20261010); scratch nunca é devolvido quando binding existe.

### RF-003: Desbloqueio de workspace tools sem repo
- **Description:** A ausência de repo deixa de bloquear as `WorkspaceTools` — o scratch provê o alvo de trabalho.
- **Rules:** Em modo `chat`, tools de `WorkspaceTools` passam de `Deny` para `Ask` (preset decide) — mesma postura das demais tools. Modo `plan` inalterado (só `PlanAllowed`). `PATCH /chats/{id}` aceita `mode=build` sem binding (remove a checagem de repo). Modo build sem repo = trabalho no scratch.
- **Input → Output:** `PermissionRuleset.Evaluate("chat", "file_write")` sem repo → `Ask` (era `Deny`).

### RF-004: Subagentes sem repo
- **Description:** `delegate_task` utilizável sem repo; workdir do filho = subdir isolado dentro do scratch do pai.
- **Rules:** Filho resolve `scratch/{parentChatId}/agents/{childChatId}`; conteúdo do filho permanece na árvore do pai → publish do pai inclui trabalho dos filhos. Com repo, modelo worktree inalterado.
- **Input → Output:** run filho sem repo → `ls` do filho vê pasta própria vazia; artefatos visíveis no scratch do pai.

### RF-005: Lifecycle — delete e archive removem o scratch
- **Description:** Delete OU archive do chat remove `scratch/{chatId}` do disco.
- **Rules:** Delete: scratch + binding + repo-base-se-órfão (fluxo existente) + scratch. Archive: scratch removido — unarchive recria pasta vazia no próximo uso (trabalho anterior descartado — decisão consciente). `ArchiveAll`/`DeleteAll`/`DeleteAllChats` idem. Falha de remoção não bloqueia a operação do chat (best-effort com log).

### RF-006: Publish para GitHub
- **Description:** Publica o conteúdo do scratch num repositório GitHub.
- **Rules:** `git add -A` + commit ("work from chat {chatId}" — identidade do usuário) → resolve remote:
  - `repo="owner/name"` existente → push branch `chat/{chatId}` → resposta com `compareUrl` para PR.
  - `repo="name"` ou inexistente → `POST /user/repos` (PAT salvo ou `token` ad-hoc) → push `main`.
  - `remoteUrl` com credencial embutida → push direto; remote sanitizado após o uso (mesmo padrão de `CloneAsync`).
  - Token nunca persistido em `.git/config` nem logado. Pasta vazia → 400 "nada a publicar". Sem PAT e sem token/url → 400 "configure um token GitHub".
- **Input → Output:** `POST …/publish {"repo":"meu-projeto"}` → `{ repoUrl, branch, commitSha, compareUrl? }`.

### RF-007: UI mínima de publish
- **Description:** Botão "Publicar no GitHub" visível quando o chat tem scratch com conteúdo e nenhum binding de repo; dialog com três opções (nome do repo / URL remota com token / token ad-hoc).
- **Rules:** Estados de loading/erro/sucesso com link do repo; i18n nos 8 locales; seguir padrões de a11y do repo (`check-form-a11y.py`).

### RF-008: Segurança e confinamento
- **Description:** O scratch permanece dentro do jail `workspaces/{userId}`; nenhum caminho fora do jail pode ser materializado; credenciais não vazam para disco/log.
- **Rules:** `chatId` sanitizado (`Sanitize` existente); `remoteUrl` validada (http(s) ou file:// apenas); tokens só via `http.extraheader`; sem PII em logs.

**Business rules / invariants:**
- Todo chat é isolado por pasta — nenhum chat sem repo escreve na raiz `workspaces/{userId}` nem no scratch de outro chat.
- O publish nunca altera o binding do chat (scratch segue scratch; bind de repo é operação separada já existente).
- Archive descarta scratch — não há "restore" de conteúdo.

## 5. API Contract

**Endpoint:** `POST /api/v1/chats/{id}/workspace/publish`
**Auth:** JWT Bearer (usuário dono do chat)

**Request:**
```json
{ "repo": "owner/name" }
```
ou
```json
{ "repo": "nome-novo", "token": "ghp_…" }
```
ou
```json
{ "remoteUrl": "https://x:TOKEN@github.com/owner/repo.git" }
```

**Response (success):** `200`
```json
{
  "repoUrl": "https://github.com/owner/name",
  "branch": "chat/6f1b…",
  "commitSha": "a1b2c3…",
  "compareUrl": "https://github.com/owner/name/compare/main...chat/6f1b…"
}
```

**Expected errors:** `400` (validação: repo inválido, url inválida, pasta vazia, nenhum token disponível) · `401` (sem auth) · `404` (chat inexistente ou sem scratch) · `502` (GitHub indisponível/push recusado) — formato genérico `{ "error": "…" }`, sem PII/token.

## 6. Acceptance Criteria

- [ ] **Given** um chat novo sem repo **when** uma tool de escrita (`file_write`/`shell_exec`) resolve o workdir **then** existe `data/workspaces/{u}/scratch/{chatId}` com `.git` inicializado.
- [ ] **Given** um chat que só troca mensagens (nenhuma tool de workspace) **when** a conversa termina **then** nenhuma pasta scratch foi criada.
- [ ] **Given** chat sem repo em modo `chat` **when** o modelo chama `file_write` **then** a tool passa pelo gate do preset (Ask) e escreve no scratch — não é negada por falta de repo.
- [ ] **Given** chat sem repo **when** `PATCH /chats/{id}` com `mode=build` **then** retorna 200 (não mais 400 "modo build exige repositório").
- [ ] **Given** chat com repo vinculado **when** tools rodam **then** workdir continua sendo o worktree `worktrees/{owner}__{repo}/{chatId}` (regressão — comportamento SPEC-15 inalterado).
- [ ] **Given** chat sem repo com `delegate_task` **when** o filho spawna **then** seu workdir é `scratch/{pai}/agents/{filho}` e arquivos escritos aparecem na árvore do pai.
- [ ] **Given** chat com scratch populado **when** o chat é deletado **then** a pasta é removida do disco.
- [ ] **Given** chat com scratch populado **when** o chat é arquivado **then** a pasta é removida do disco; ao desarquivar e usar tools, um scratch novo e vazio é materializado.
- [ ] **Given** scratch com arquivos e PAT configurado **when** `publish {"repo":"novo-nome"}` **then** repo é criado na conta do PAT, push em `main`, resposta contém `repoUrl` e `commitSha`.
- [ ] **Given** scratch com arquivos e repo existente `{"repo":"owner/existe"}` **when** publish **then** push na branch `chat/{chatId}` e resposta contém `compareUrl`.
- [ ] **Given** scratch vazio (só `.git`) **when** publish **then** 400 "nada a publicar".
- [ ] **Given** nenhum PAT configurado **when** `publish {"repo":"x"}` sem token **then** 400 "configure um token GitHub".

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| `chatId` com caracteres hostis | `../..` | sanitizado — pasta sempre dentro do jail |
| `.git` do scratch corrompido/apagado | pasta existe sem `.git` | re-init idempotente no próximo resolve |
| Publish duplo | segundo publish na mesma branch | commit novo + push — não falha |
| Repo name inválido | `"repo":"//"` | 400 com mensagem clara |
| Token inválido/expirado | `token:"bad"` | 400/502 genérico, sem vazar o token |
| `remoteUrl` não-http | `ftp://…` | 400 validação |
| Chat deletado com run ativa no scratch | delete durante execução | best-effort: run falha ou termina, pasta removida |
| `remoteUrl` com token | push ok | remote final sanitizado (sem credencial em `.git/config`) |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** ler arquivos da seção 3; mapear todos os chamadores de `ResolveWorkdirAsync`/`ReleaseChatWorkspaceAsync`.
- [ ] **T2 — Scratch core:** `EnsureScratchAsync` (mkdir + `git init` + sanitize) + alterar `ResolveWorkdirAsync` (sem binding + chatId → scratch) + `ReleaseChatWorkspaceAsync` remove scratch (sempre, inclusive archive).
- [ ] **T3 — Gate:** `PermissionRuleset` — `WorkspaceTools` em modo chat → `Ask` quando há scratch; remover exigência de repo no PATCH `mode=build`.
- [ ] **T4 — Subagentes:** override de workdir do filho sem repo → `agents/{childChatId}` no scratch do pai.
- [ ] **T5 — Publish:** `GitHubService.CreateRepoAsync` + helpers git (commit/push/remote sanitize) + endpoint `POST /chats/{id}/workspace/publish` + contratos.
- [ ] **T6 — UI:** botão Publicar + dialog + i18n ×8.
- [ ] **T7 — Testes:** `ScratchWorkspaceTests` cobrindo cada acceptance criterion + regressão worktree; drift guards (`check-css-classes`, `check-form-a11y`, `check-i18n-parity`, `check-spec-status`).
- [ ] **T8 — Validation:** `dotnet build` + `dotnet test` completo; DoD (seção 9).
- [ ] **T9 — Done + PR:** `Status = Done` + PR `feature/scratch-workspace` referenciando a issue.

## 8. Organization Guardrails

- **Branches:** nunca commitar em `main` — usar `feature/scratch-workspace`.
- **Workflows:** não modificar `.github/workflows/` sem revisão humana.
- **Security:** token GitHub nunca persistido em `.git/config` nem em logs; sem secrets em commit; `chatId` sanitizado contra path traversal; workdir sempre dentro do jail `workspaces/{userId}`.
- **Scope:** não implementar ZIP-download, hosts não-GitHub, orphan sweep — fora de escopo.
- **Architecture:** lógica de git/publish em `Infrastructure` (services), endpoint fino em `Api`, contratos em `Application` — sem lógica de negócio em endpoints.

## 9. Definition of Done

- [ ] All requirements (section 4) implemented.
- [ ] All acceptance criteria (section 6) covered by passing tests or equivalent validation evidence for the stack (section 7.1).
- [ ] Edge cases handled.
- [ ] Build, lint and tests pass locally; coverage gate (`.ci/coverage-*.txt`) não regride.
- [ ] Guardrails in section 8 respected.
- [ ] Logs contain no PII/tokens; errors use the generic `{ "error": "…" }` format.

**Next action after DoD is complete:** `Status = Done` + PR `feature/scratch-workspace` → `Closes #<issue>`.

## Open Questions / Pending Ambiguity

- Nenhuma — decisões de design fechadas com o usuário em 2026-10-27 (pasta por `chatId`, archive deleta scratch, publish com 3 formas de credencial, criação lazy, `git init` na materialização, subagentes em subdir do pai, UI mínima).
