# SPEC-20261010-agent-memory — Memória durável do agente (save/search + injeção)

Status: Completed

## Contexto
O agente não lembra nada entre runs/chats: fatos aprendidos ("o comando de teste é X", "deploy vai pro ambiente Y") morrem com o contexto — e a compactação (SPEC-20261010-context-compaction) torna isso mais visível. Referência opencode: `memory_save`/`memory_search` com escopo projeto/global e injeção automática no system prompt.

## Objetivo
Memória durável por usuário em 2 escopos (global, repo), gravável e buscável pelo agente, injetada automaticamente no system prompt.

## Escopo
1. Entidade `AgentMemory` (Id, UserId, Scope global|repo, RepoSlug?, Title, Content, CreatedAt, UpdatedAt) + migration. Upsert por (user, scope, repo, title).
2. `builtin:memory_save` — title+content (+scope). scope=repo resolve o slug via `WorkspaceRepoService.ResolveBindingAsync` do chat (erro se sem binding). Sem aprovação.
3. `builtin:memory_search` — substring em title/content; escopo default = global + repo do chat; `all` varre tudo. Top 10 por UpdatedAt.
4. Injeção no `ChatPipeline.EnrichRequestAsync`: bloco `<agent_memory>` com as 12 memórias mais recentes (global + repo bound do chat), cap ~1800 chars — independente de binding (global sempre injeta).

## Fora de escopo
Auto-sync periódico (SPEC-20261010-memory-autosync); esquecer/retention/expurgo (SPEC-20261010-memory-retention); embeddings.

## Acceptance Criteria
- `memory_save` persiste; mesmo título atualiza (não duplica).
- scope=repo sem binding → erro; com binding → RepoSlug gravado.
- `memory_search` filtra por query e escopo; memória de outro repo não aparece no default.
- Prompt de uma run contém `<agent_memory>` com a memória global; chat bound a outro repo não recebe a memória repo-scoped.
- Tests: save/upsert, repo-scope, search filtros, injeção no pipeline.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/305 |
