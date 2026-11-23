# SPEC-20261123 — dev_pipeline fan-out (implement paralelo por item)

## 0. Metadata

| Campo | Valor |
|-------|-------|
| Owner | afonsoft |
| Status | `Delivered` |
| Origem | aprovado pelo usuário — inspirado no swarm/board do Antigravity |
| Depende de | SPEC-20261121-dev-pipeline (plan, modelos por fase, stall) |

## 1. User Story

A fase `plan` já decompõe a tarefa em itens. Quando ≥2 itens são
**independentes** (arquivos disjuntos, sem ordem), quero que o
`implement` rode **em paralelo** — cada item numa run/chat própria com
worktree isolado — e depois faça merge de tudo no chat filho antes de
seguir para test/validate/commit, em vez de implementar em série.

## 2. Design

- `plan` passa a emitir `items` no `<pipeline_result>`:
  `[{id,title,files[],independent}]` — `independent` quando o item não
  divide arquivo nem depende de outro item.
- `PhaseResult.Items` parseia a lista; `state.EnableFanout` (arg
  `fanout`, default true) habilita o caminho.
- Elegível quando: `EnableFanout` + `EnablePlan` + ≥2 itens
  `independent` + **binding de repo no filho** (scratch não tem git →
  cai no implement sequencial de sempre).
- Cada item elegível (cap `Agent:DevPipeline:MaxFanout`, default 4)
  ganha um **chat irmão** (`pipeline:{title} [{id}]`) herdando o
  binding (`InheritChatBinding` → worktree próprio do mesmo repo) e uma
  run `implement:{id}` com o modelo worker — paralelo real.
- `ParentRunId` da run irmã aponta o plan/main run → `sameChatParent`
  falso → `force:true` → worktree por run garantido; o prompt carrega
  item + plano (histórico da irmã é vazio = contexto focado do worker).
- Wait-all em paralelo; stall por item usa retry **por runKey**
  (`StallRetriesByKey`) — uma run travada não consome o budget das
  outras; substituta rotaciona a chain (RF-008).
- Merge sequencial item a item: `MergeAsync(mainWorkdir,
  runWorktree)` — conflitos (diff não aplica) viram `issues` para a
  fase `fix` (resolve no workdir principal); nunca forçados.
- Itens `independent:false` ou além do cap → run `implement-rest`
  sequencial no chat filho cobrindo o restante (se houver).
- Sem itens parseáveis → comportamento atual (implement único).

## 3. Acceptance Criteria

- AC-1: `plan` pede `items` com `independent`; parser extrai para
  `PhaseResult.Items`.
- AC-2: `FanoutItems` seleciona só `independent`, respeita
  `EnableFanout`, cap `MaxFanout` e exige ≥2.
- AC-3: fan-out cria run por item em chat irmão com binding herdado;
  `Runs["implement:{id}"]` registrados; merges aplicam diffs no workdir
  do filho e conflitos alimentam `fix`.
- AC-4: stall por item consome retry próprio (`{key}-stall{n}`); id
  simples de modelo não rotaciona.
- AC-5: sem items / <2 independentes / sem binding → fluxo sequencial
  inalterado.
