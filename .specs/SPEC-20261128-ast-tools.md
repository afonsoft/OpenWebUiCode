# SPEC-20261128-ast-tools — decisão sobre `ast_grep`/`ast_edit`

**Status**: Approved — plano de melhorias aprovado 2026-11-28 (plano item P1.17, tools estruturais).

## Objetivo

Avaliar custo/benefício de busca/edição estrutural de código estilo
`ast-grep` antes de incorporar uma dependência pesada ao backend.

## Decisão

**Não implementar `ast_grep`/`ast_edit` neste batch.**

## Evidências

- A opção .NET mais completa encontrada, `TreeSitter.DotNet` 1.3.0, é um
  pacote de ~53,4 MB porque embute o runtime nativo do tree-sitter e 28+
  gramáticas para Windows/Linux/macOS. Isso inflaria restore, imagem Docker
  e superficie de supply-chain do backend para uma capability ainda sem
  caso de uso concreto no agent loop.
- `ast_edit` exige mais do que parsing: mapear queries por linguagem,
  validar substituições estruturais, reescrever ranges de bytes preservando
  formatação e cobrir gramáticas heterogêneas com testes. O custo de
  correção é alto comparado às tools já disponíveis (`file_grep`,
  `file_edit`, `apply_patch`, `code_search` e LSP).
- O ecossistema tree-sitter em .NET ainda é fragmentado/em maturação;
  alternativas exigem builds nativos ou pacotes nightly/alpha, contrariando
  a preferência do repositório por dependências battle-tested.

## Consequência

- Nenhuma dependência nem tool de produção é adicionada nesta entrega.
- O item fica documentado como decisão aprovada para não reabrir por impulso
  em futuros batches.

## Critério para revisitar

Reavaliar quando houver (1) caso concreto de refatoração estrutural que as
tools atuais não cubram e (2) pacote .NET estável com gramáticas opt-in por
linguagem ou tamanho de payload significativamente menor.
