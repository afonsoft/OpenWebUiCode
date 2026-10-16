# SPEC-20261009-checkpoints-revert

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `checkpoints-revert` (série web-ide, fatia S6) |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/open-webui` |
| Branch | `feature/devin-{YYYYMMDD}-checkpoints` |
| Ticket | `GAP-impl-checkpoints` |
| Status | `Completed` |
| Priority | `medium` |
| Depends on | `SPEC-20261009-workspace-file-api` (S1) — precisa de repo git vinculado |

## 1. User Story

**As a** usuário
**I want** que cada turno da run crie um checkpoint do workdir e eu possa reverter
**So that** edições do agente são desfazíveis com um clique — como `snapshot`/`revert` do opencode (`src/snapshot/index.ts`, `session/revert.ts`).

**Problem context:** a aba Changes mostra diff, mas desfazer exige git manual no terminal. Sem safety net, modo Build livre (S5) é arriscado.

## 2. Scope

**In scope:**
- `CheckpointService` (Infrastructure): por turno (início de cada run e após cada tool de escrita) cria snapshot do workdir quando é repo git.
  - Implementação opencode-style: commit em ref oculto (`refs/openwebui/checkpoints`) via `git commit-tree`+`write-tree`/`read-tree` — **não** suja a working branch do usuário; ou `git stash create` (hash sem stash entry) — `[A DEFINIR]` qual; ambos sem checkout.
  - Alternativa sem git: snapshot de manifesto `{path→hash}` + cópia dos arquivos alterados em `data/checkpoints/{runId}/` — fallback quando workdir não é repo.
- `POST /api/v1/workspace/repo/checkpoints/{hash}/revert` — restaura arquivos do patch do snapshot (só os arquivos que o snapshot cobre; nunca `git reset` cego).
- SSE: evento `checkpoint {hash, files[]}` por turno; UI: botão **Reverter** no card da run / na aba Changes (por checkpoint) + confirm modal mostrando o diff que será desfeito.
- Prune: snapshots >7 dias ou >2MB agregados são limpos no `cleanup` (padrão opencode: `prune = "7.days"`, `limit = 2MB`).

**Out of scope:**
- Diff/checkpoint de arquivos fora do repo.
- Time-travel de mensagens (revert de conversa) — só filesystem.
- Fork de sessão (opencode tem; separado).

## 3. Technical Context

**AS-IS:** `WorkspaceGitService` já roda `git` no workdir (`RunGitAsync`, `GitWorkspaceInfo`); a aba Changes consome diff unificado. Reuso direto.

**TO-BE:** snapshot imutável por turno + revert granular.

**Constraints:**
- Nunca `git checkout`/`reset` na branch do usuário — o snapshot é objeto solto; revert = apply do patch inverso nos arquivos cobertos.
- Lock por workdir (`SemaphoreSlim` por path — padrão opencode `locks: Map<string, Semaphore>`).
- Checkout em meio a run ativa é proibido (revert só com run pausada/terminada, ou pausa automática).

## 4. Requirements

### RF-001: Snapshot por turno
Antes da run e após cada tool `file_write`/`file_edit`/`apply_patch`: cria checkpoint `{hash, runId, turn, files[]}`; SSE `checkpoint` emitido. Sem git → manifesto+payload.

### RF-002: Revert
`POST .../revert` aplica o patch inverso do checkpoint; retorna `{reverted: paths[], conflicts: paths[]}`; conflito (arquivo mudou depois) → listado, não sobrescrito às cegas (force flag opcional via confirm extra).

### RF-003: UI
Card de run / aba Changes lista checkpoints com botão Reverter + mini-diff; toast de resultado. i18n.

### RF-004: Prune
Job de manutenção remove checkpoints antigos por TTL/tamanho (config `Checkpoints:MaxAgeDays`, `MaxBytes`).

### RF-005: Isolamento
Snapshots não interferem no histórico git do usuário (`git log`/`git status` limpos — ref oculto ou diretório de dados fora do repo).

## 5. Acceptance Criteria

- [x] Run que edita 3 arquivos → 3+ checkpoints revertíveis individualmente.
- [x] Revert com arquivo modificado depois → conflito reportado, não sobrescrito.
- [x] `git log`/`git status` do workdir não mostram commits/entradas de checkpoint.
- [x] Sem repo git → fallback manifesto funciona.

## 6. Tests

Api.Tests: snapshot→edit→revert round-trip, conflito, prune, lock por workdir, fallback sem git.

## 7. Rollout

On por padrão quando há repo vinculado; `Checkpoints:Enabled=false` desliga.

## 8. Risks

- **Repo sem identidade git configurada:** commit-tree com `-c user.email=openwebui@local` (opencode usa config própria — `-c core.quotepath=false` etc.).
- **Repos enormes:** snapshot por hash/ítem é barato; a cópia-fallback respeita `MaxFileBytes`.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/257 |
