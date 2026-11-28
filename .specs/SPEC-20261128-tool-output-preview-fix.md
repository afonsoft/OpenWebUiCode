# SPEC-20261128-tool-output-preview-fix — regressões do preview de output

**Status**: Approved — plano de melhorias aprovado 2026-11-28 (correção da validação E2E do item P2.25).

## Objetivo

Corrigir duas regressões encontradas na validação E2E do `ToolCallCard`:
placeholders literais no cabeçalho resumido e linhas de diff concatenadas.

## Requisitos

- RF-001: os recursos `chat.tool_output_summary` usam placeholders no formato
  suportado pelo `LocalizationService.T` (`{{nome}}`) nos 8 locales.
- RF-002: o diff renderizado em `<pre>` preserva quebras de linha reais entre
  os spans coloridos, sem depender de whitespace de markup Razor.
- RF-003: testes bUnit cobrem interpolação com locale real/carregado ou a
  string de recurso, e separação de linhas do diff.
- RF-004: regenerar Tailwind se classes mudarem; drift guards verdes.

## Não-escopo

- Redesenhar o preview ou cobrir novos tipos de output.
