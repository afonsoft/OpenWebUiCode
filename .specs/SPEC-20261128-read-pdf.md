# SPEC-20261128-read-pdf — builtin `read_pdf`

**Status**: Approved — plano de melhorias aprovado 2026-11-28 (plano item P1.17, tools de leitura ricas).

## Objetivo

O agente extrai texto de PDFs do workspace direto do loop, sem converter o
arquivo manualmente — paridade com oh-my-pi (`read_pdf`) e Devin.

## Requisitos

- RF-001: `read_pdf` (`ApprovalTier.Read`, mesmo sandbox do `file_read`)
  recebe `path` obrigatório e retorna texto extraído das páginas do PDF.
- RF-002: limites determinísticos: máximo de 20 páginas por chamada e
  64 KiB de texto; `startPage` (1-based) permite paginar; a resposta indica
  truncamento/páginas restantes.
- RF-003: PDF inválido, arquivo ausente ou path fora do sandbox retornam
  erro estruturado de tool, nunca exceção na run.
- RF-004: registro em `Program.cs`, `PlanAllowed`, `SafeInParallel`,
  `ToolActivityMap` (`chat.activity.read_pdf`) e chave nos 8 locales.
- RF-005: testes NUnit cobrem extração, paginação, truncamento, sandbox,
  PDF inválido e metadados/permissões.

## Dependência

Adicionar `PdfPig` 0.1.16 (Apache-2.0, `netstandard2.0`/`net6.0`, pacote
estável do projeto UglyToad/PdfPig). É dependência nova — não há migração
de uso existente. A quebra histórica relevante do projeto (`decimal` →
`double` em 0.1.9) não afeta este consumo novo; usamos apenas `PdfDocument.Open`
e `page.Text`, API estável de leitura.

## Não-escopo

- OCR de PDFs escaneados/imagens.
- Escrita/edição de PDF.
- `ast_grep`/`ast_edit` e demais `gh_*` (itens seguintes do P1.17).
