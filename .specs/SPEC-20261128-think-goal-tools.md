# SPEC-20261128-think-goal-tools — tools think/goal

**Status**: Approved — plano de melhorias aprovado 2026-11-28

## Objetivo

Duas builtins read-only inspiradas no Devin/opencode: `think` registra
uma nota de raciocínio privada no histórico da run; `goal` declara o
objetivo atual — ambas ficam no transcript, alinhando os próximos
rounds e aparecendo no feed de atividade.

## Requisitos

1. `builtin_think` {thought} — devolve nota registrada + payload
   `{thought}`; cap 2000 chars.
2. `builtin_goal` {goal} — devolve objetivo + payload `{goal}`; cap
   300 chars.
3. Ambas `ApprovalTier.Read`, permitidas no modo plan (`PlanAllowed`).
4. Feed de atividade: labels `chat.activity.think`/`chat.activity.goal`
   nos 8 locales.
5. Testes NUnit: validação de args, caps, payload, tier.

## Aceite

- Build 0 warnings; 6 testes novos verdes.
