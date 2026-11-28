# SPEC-20261128-gh-diff-comment — builtins `gh_diff` + `gh_comment`

**Status**: Approved — plano de melhorias aprovado 2026-11-28 (item P1.17, restante das `gh_*` inspiradas no omp).

## Objetivo

O agente lê o diff de um PR e, mediante aprovação, comenta no PR sem sair
do loop — paridade com as `gh_*` do oh-my-pi/Devin.

## Requisitos

- RF-001: `gh_diff` (`ApprovalTier.Read`, permitido no plan mode) recebe
  `number` e retorna o diff do PR do repo vinculado, truncado de forma
  explícita. Implementar via `GET /repos/{owner}/{repo}/pulls/{n}/files`,
  montando patch por arquivo; cap de 16 KiB e indicador quando o GitHub
  paginar/omitir patch.
- RF-002: `gh_comment` (`ApprovalTier.Write`) recebe `number` + `body` e
  cria comentário em `/repos/{owner}/{repo}/issues/{n}/comments` usando o
  token efetivo de `GitHubService.GetTokenAsync`. Por ser write, fica fora
  do `PlanAllowed` e passa pelo fluxo de aprovação existente antes do
  primeiro uso.
- RF-003: erros explícitos de tool para sem repo vinculado, token ausente,
  PR inexistente, GitHub inacessível e args malformados — nunca exceção na
  run nem dados enlatados.
- RF-004: registro em `Program.cs`, `PlanAllowed` apenas para `gh_diff`,
  rótulos de atividade nos 8 locales.
- RF-005: testes NUnit de metadados/permissões e do `GitHubService` com
  handler stub: diff montado/truncado, comentário POSTado, ausência de token
  e auth recusada.

## Não-escopo

- Review comments inline, reviews formais, edição/exclusão de comentários.
- Paginação além da primeira página de 100 arquivos (a resposta marca o
  truncamento).
