# SPEC-20261010-worktree-review — Review-before-merge do worktree + handoff

Status: Completed

## Contexto
Sub-runs já isolam em git worktree (SPEC-20261010-subagent-worktree), mas nada expõe o diff ao agente pai: a worktree fica "invisível" até o usuário clicar "Merge into workspace" na aba Changes. Referência opencode: orquestrador revisa o diff do worker antes de mergear, com convenção de handoff (HANDOFF.md no worktree).

## Objetivo
O agente pai revê e decide o merge do worktree da run filha pelas próprias tools; auto-merge como opt-in no `delegate_task`.

## Escopo
1. `builtin:worktree_diff` (read-only): `run_id` + `stat_only` → status porcelain + `git diff HEAD --stat` + patch truncado (~5k chars). Owner-scoped.
2. `builtin:worktree_merge` (requer aprovação): `run_id` + `action` (`merge` default | `discard`). Merge aplica o diff no workdir do chat **da run alvo** (`WorkspaceRepoService.ResolveWorkdirAsync(user, run.ChatId)` — respeita binding por chat do filho); conflitos nunca forçados (retornam listados, worktree preservado). Discard remove sem aplicar.
3. `run_result`: quando a run tem worktree com mudanças pendentes, o resultado ganha `hasWorktreeChanges` + hint apontando `worktree_diff`/`worktree_merge`.
4. `delegate_task` param `merge` (bool, default false): com `wait=true`, após o filho completar o pai mergeia o worktree automaticamente via `WorktreeService.MergeAsync`; nota "(merge: N arquivo(s) aplicados)" ou "(merge: PARCIAL — conflitos…)" anexada ao resultado. `wait=false` ignora (o fluxo correto é revisar via worktree_diff).
5. Convenção de handoff: o prompt do delegate pode pedir ao filho que escreva `HANDOFF.md` na raiz do worktree — aparece no `worktree_diff` como qualquer arquivo.

## Fora de escopo
UI nova (a aba Changes + botão Merge já existem); merge de branch (o merge é por `git apply` de diff, não `git merge`); resolução automática de conflitos.

## Acceptance Criteria
- `worktree_diff` mostra stat+patch do worktree da run; `stat_only` omite patch.
- `worktree_merge merge` aplica no workdir do chat da run (respeitando binding por chat); worktree removido em merge completo.
- `worktree_merge discard` remove sem aplicar.
- Conflitos → `merged:false` + lista; worktree preservado.
- `run_result` sinaliza `hasWorktreeChanges` + hint.
- `delegate_task merge=true wait=true` → auto-merge com nota no resultado.
- Tests: diff mostra mudanças; merge aplica; discard descarta; run_result sinaliza.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/304 |
