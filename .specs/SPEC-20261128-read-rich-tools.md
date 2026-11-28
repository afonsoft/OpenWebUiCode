# SPEC-20261128-read-rich-tools — `read_archive` + `read_sqlite`

**Status**: Approved — plano de melhorias aprovado 2026-11-28 (plano item P1.17, tools de leitura ricas).

## Objetivo

O agente inspeciona formatos binários comuns direto do loop, sem baixar/
extrair no workspace — paridade com oh-my-pi (`read_archive`,
`read_sqlite`) e Devin.

## Escopo

- `read_archive` (`ApprovalTier.Read`, mesmo sandbox do `file_read`):
  - sem `member`: lista entradas do `.zip` (nome + tamanho, cap 500).
  - com `member`: extrai o membro como texto (cap 64 KiB, recusa
    binário por presença de NUL).
- `read_sqlite` (`ApprovalTier.Read`):
  - sem `query`: lista tabelas com contagem de linhas (cap 100).
  - com `query`: executa SELECT read-only (cap 200 linhas × 20 colunas),
    conexão `Mode=ReadOnly`; apenas `SELECT` é aceito.
- Registro em `Program.cs`, `PlanAllowed`, rótulos de atividade
  `chat.activity.read_archive`/`read_sqlite` nos 8 locales.
- Testes: listagem/extração de zip, sandbox, lista-tabelas, SELECT,
  recusa de não-SELECT.

## Não-escopo

- `read_pdf`, `ast_grep`/`ast_edit`, `gh_*` (itens seguintes do P1.17).
- Escrita em qualquer formato.
