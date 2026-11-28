# SPEC-20261128-memory-reflect — `builtin memory_reflect`

**Status**: Approved — plano de melhorias aprovado 2026-11-28 (plano item P1.16, memory tools omp `memory_reflect`).

## Objetivo

Síntese sobre muitas memórias: digest determinístico do banco de
memórias do usuário para o agente refletir antes de agir ou consolidar
duplicatas — sem chamada de LLM (o agrupamento é por máquina e o modelo
reflete sobre o digest).

## Escopo

- `memory_reflect` (`ApprovalTier.Read`), mesmo filtro de escopo do
  `memory_search` (`default`/`global`/`repo`/`all`):
  - contagens por escopo/repo + título da entrada mais recente por
    grupo (cap 25 grupos);
  - candidatas a staleness: entradas sem update há >30 dias (cap 10).
- Registro em `Program.cs`, `PlanAllowed`, rótulo
  `chat.activity.memory_reflect` nos 8 locales.
- Testes: digest com grupo + stale; escopo vazio.

## Não-escopo

- Consolidação/merge de memórias (ação do agente via `memory_save`
  upsert) e `retain` proativo — instrução já existe no TrackingPrompt.
