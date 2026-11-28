# SPEC-20261128-auto-parallel — `delegate_task` em paralelo

**Status**: Approved — plano de melhorias aprovado 2026-11-28 (plano item P1.15, omp `auto-parallel`).

## Objetivo

O loop já executa grupos contíguos de calls `SafeInParallel` em
concorrência (cap `BuiltinTools:MaxParallel`=4, SPEC-20261016-parallel-tool-calls).
Faltava o `delegate_task` poder entrar nesses grupos: marcar a tool como
`SafeInParallel` permite ao modelo disparar N subtarefas independentes
num único round (auto-parallel por paths/módulos, estilo omp/Devin).

## Escopo

- `DelegateTaskBuiltinTool.SafeInParallel => true`.
- Teste: `IsSafeInParallel("builtin:delegate_task")` retorna true.

## Não-escopo

- Detector automático de unidades independentes (a decisão de
  parallelizar já é do modelo via emissão de calls contíguas — o
  mecanismo de merge/ordenação do loop permanece).
