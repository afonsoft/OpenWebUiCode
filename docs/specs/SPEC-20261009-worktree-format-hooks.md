# SPEC-20261009-worktree-format-hooks

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `worktree-format-hooks` (série web-ide, fatia S9 — opcional) |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/open-webui` |
| Branch | `feature/devin-{YYYYMMDD}-worktree-hooks` |
| Ticket | `GAP-impl-worktree` + `GAP-impl-apply-patch` |
| Status | `Completed` |
| Priority | `low` (opcional) |
| Depends on | `SPEC-20261009-agent-modes-plan-build` (S5), `SPEC-20261009-checkpoints-revert` (S6) |

## 1. User Story

**As a** usuário com runs paralelos no mesmo repo
**I want** que cada run possa trabalhar num `git worktree` isolado e que edições passem por um formatter configurável
**So that** runs paralelos não colidem e o código sai formatado — como `src/worktree/` e `src/format/` do opencode.

**Problem context:** workdir único por usuário → duas runs escrevendo no mesmo repo se atropelam. E `file_edit` é search/replace — falta `apply_patch` multi-arquivo + hook de format pós-edição.

## 2. Scope

**In scope:**
- `WorktreeService`: `git worktree add data/worktrees/{userId}/{runId} <branch>` por run quando `Workspace:RunIsolation=worktree`; expõe `Workdir` da run; cleanup `git worktree remove` ao final (ou merge manual via aba Changes). Default `shared` (comportamento atual) — opt-in por chat/config.
- Mudança no pipeline: `BuiltinToolContext.WorkspacePath` passa a apontar pro worktree da run quando isolado.
- `apply_patch` builtin: patch multi-arquivo (formato opencode/openai apply_patch ou unified diff) — cria/edita/deleta num patch só; gera diff persistido igual ao `file_edit`.
- Format hook: config `Format:Command` (por repo ou global, ex.: `dotnet format --include {files}`, `prettier --write {files}`); após `file_write`/`file_edit`/`apply_patch` executa o formatter nos arquivos tocados, dentro do jail da run; falha do formatter = warning, não erro.
- UI: chip "worktree" na run quando isolada; Changes da run lê o worktree; botão "Merge into workspace" (merge/apply do diff do worktree → workdir principal) com confirmação.

**Out of scope:**
- Rebase/PR automático do worktree.
- Múltiplos formatters encadeados.

## 3. Technical Context

**AS-IS:** `BuiltinToolContext.WorkspacePath` já é por-run (o jail já é parametrizado); `WorkspaceGitService.RunGitAsync`; `file_edit` com diff persistido.

**TO-BE:** isolamento opcional por run + patch multi-file + format hook.

## 4. Requirements

### RF-001: Worktree por run
`RunIsolation=worktree` → workdir da run = `data/worktrees/{userId}/{runId}`; jobs/terminal da run apontam pra lá; cleanup: worktree é mantido até o merge manual (botão na aba Changes) ou o prune de órfãos por TTL — DECIDIDO: manter p/ merge manual; órfãos (run inexistente ou terminada há `Workspace:WorktreeTtlHours`, default 168h) são removidos no boot pelo `ChatRunDispatcher`.

### RF-002: Merge
`POST /api/v1/workspace/repo/merge-worktree {runId}` → aplica diff do worktree no workdir principal; conflitos listados, nunca forçado às cegas.

### RF-003: `apply_patch`
Spec OpenAI-compatible (`*** Begin Patch`/`Add|Update|Delete File`); valida paths no jail antes de aplicar; resultado com diff unified.

### RF-004: Format hook
Após escrita: `Format:Command` roda com `{files}` interpolado (só paths jailed); stdout/stderr no log da run; timeout 60s; falha → warning no tool_result, não bloqueia.

### RF-005: Isolamento de lixo
Worktrees órfãos (run morta) limpos na manutenção (`WorkspaceRepoService`/`DatabaseMigrator`-adjacent cleanup) com TTL.

## 5. Acceptance Criteria

- [ ] Duas runs isoladas editam o mesmo arquivo sem colidir; merge manual reporta conflito quando real.
- [ ] `apply_patch` cria+edita+deleta num patch; diff visível na aba Changes.
- [ ] Formatter configurado roda após edição; falha vira warning.
- [ ] Órfãos são limpos.

## 6. Tests

Api.Tests: worktree create/cleanup/merge-conflict; apply_patch edge cases; hook interpolation (só jailed paths); timeout.

## 7. Rollout

`Workspace:RunIsolation` (`shared` default) + `Format:Command` opt-in — zero mudança para quem não ligar.

## 8. Risks

- **Worktree + repo sem git** → fallback `shared` com aviso.
- **Disk growth** → prune por TTL no cleanup.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/262 |
