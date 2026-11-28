# SPEC-20261128-wait-tool — builtin `wait`

**Status**: Approved — plano de melhorias aprovado 2026-11-28 (plano item P1.18, wait/yield).

## Objetivo

Tool `builtin wait` para o agente aguardar um processo/condição sem
sair do loop — metade "processo" do wait/yield do plano de melhorias
(comparação com opencode/oh-my-pi/Devin). Ceder o turno ao usuário sem
terminar a run (metade "yield") já é coberto por `ask_user`/`plan_exit`.

## Escopo

- `wait` (`ApprovalTier.Read`):
  - `seconds` (1–60, default 60): aguarda a duração.
  - `job_id` opcional: faz poll do `ChatJobService` até o job sair de
    `running` ou o prazo de `seconds` expirar, retornando o status
    terminal (`completed`/`failed`/`killed`) ou "ainda em execução".
- Registro em `Program.cs`, `PlanAllowed` (plan mode), rótulo de
  atividade `chat.activity.wait` nos 8 locales.
- Testes: aguarda N segundos; job inexistente; job já terminado.

## Não-escopo

- Yield ao usuário (coberto por `ask_user`).
- Auto-parallel, memory_reflect, read_* tools (PRs seguintes).
