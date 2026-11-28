# SPEC-20261128-gh-tools — builtin `gh_pulls`

**Status**: Approved — plano de melhorias aprovado 2026-11-28 (item P1.17, inspirado nas `gh_*` do omp).

## Escopo

- `GhPullsBuiltinTool` (`builtin:gh_pulls`): lista os PRs abertos do repo
  vinculado ao chat — número, título, autor, head ref e rollup de CI —
  reusando `GitHubService.ListPullRequestsAsync` (PAT do usuário + cache
  60s) e `WorkspaceRepoService.ResolveBindingAsync`. Read-only,
  `ApprovalTier.Read`, permitido no plan mode.
- Mensagens de erro explícitas: sem repo vinculado, sem PAT
  (`NeedsToken`) ou GitHub inacessível — nunca dados enlatados.
- `PlanAllowed` + `ToolActivityMap` (`gh_pulls` → `chat.activity.gh_pulls`)
  + chave i18n nos 8 locales.

## Fora de escopo

- `gh_view`/`gh_diff`/`gh_comment` (precisam de novos métodos no
  `GitHubService`) — follow-up.

## Adição (esta branch)

- `GhViewBuiltinTool` (`builtin:gh_view`): detalhe de um PR por número —
  metadados, corpo (truncado em 4000 chars), rollup de CI, +/-/arquivos e
  `mergeable`. Novo `GitHubService.GetPullDetailAsync` (sem cache — número
  é explícito e frescor importa).
