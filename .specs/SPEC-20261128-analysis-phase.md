# SPEC-20261128-analysis-phase — fase de análise visível

**Status**: Approved — plano de melhorias aprovado 2026-11-28

## Objetivo

Antes do primeiro round do tool loop a run publica uma fase
`analyzing` única — o usuário vê o agente analisar o pedido antes de
executar, como no Devin web.

## Requisitos

1. Executor emite `status {phase:"analyzing"}` uma única vez por run,
   imediatamente antes do primeiro `generating` — sem alterar as demais
   fases (retro-compatível).
2. Client mapeia `analyzing` → `chat.status_analyzing` ("Analisando…")
   no chip/status line.
3. Nova chave i18n nos 8 locales.
4. Teste: `analyzing` aparece uma vez e antes do primeiro `generating`
   no SSE de uma run.

## Aceite

- Runs sem tools também exibem analyzing (é fase de provider, não de tool).
- Build 0 warnings; teste de fases verde.
