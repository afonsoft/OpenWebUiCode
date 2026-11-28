# SPEC-20261128-advisor-v2 — Severidades no revisor de run

**Status**: Approved — plano de melhorias aprovado 2026-11-28

## Objetivo

Advisor v2: cada conselho carrega uma severidade (`nit` | `concern` |
`blocker`), inspirada no oh-my-pi, permitindo ao usuário (e ao agente)
calibrar a atenção: blocker = agir já, concern = atenção, nit = dica.

## Requisitos

1. `RunAdvisorEvent` ganha `Severity` (string?, opcional — retro-compatível).
2. Formato de saída do advisor: primeira linha `severity: <nível>`,
   depois o conselho; `OK` continua significando "nada a dizer".
   Parse tolerante: sem a linha → Severity=null (conselho comum).
3. Prompt `advisor-system.md` atualizado com o formato e os critérios
   de cada severidade.
4. Executor emite a severidade no evento SSE e a inclui na nota system
   `[advisor:<severity>]` injetada no próximo round.
5. Client exibe a severidade no feed de atividade (preview da linha
   `advisor` no RunActivityFeed) e no texto detalhado.
6. Testes NUnit para o parser de conselho (OK, sem severidade,
   cada nível, case-insensitive, texto longo capado).

## Aceite

- Conselho sem header → Severity null, comportamento atual preservado.
- Severidade inválida → null; conselho mantido.
- Build 0 warnings; testes novos verdes.
