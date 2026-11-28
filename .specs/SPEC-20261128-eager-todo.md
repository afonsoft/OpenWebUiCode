# SPEC-20261128-eager-todo — eager todo + few-shots no system prompt

**Status**: Approved — plano de melhorias aprovado 2026-11-28 (plano itens P2.21 few-shots de todo + P2.23 eager todo).

## Objetivo

Pedido claramente multi-step deve abrir com `builtin_todo_write` — plano
antes de qualquer outra tool (padrão opencode `anthropic.txt` e omp
`eager-todo.md`), mais few-shots curtos de disciplina (marca completed na
hora, um in_progress por vez, plano antes de editar).

## Escopo

- `ChatPipeline.TrackingPrompt`: frase "the FIRST tool call must be
  builtin_todo_write" + bloco `Examples:` com três few-shots.
- Testes de prompt (presença das âncoras).

## Cobertura relacionada (sem código novo)

- **P1.19 tool-result eviction**: já implementado por `ContextPruner`
  (elide >4KB → marcador `history://`, supersession de path, acks curtos).
- **P2.22 hindsight/autolearn**: coberto por `builtin_memory_save` +
  `builtin_memory_learn` + instrução existente no `TrackingPrompt`
  ("ALWAYS persist durable knowledge … decisions, conventions, gotchas").

## Não-escopo

- Auto-parallel (P1.15), memory_reflect (P1.16), prompt modular (P2.20),
  polimento de feed (P2.24–26).
