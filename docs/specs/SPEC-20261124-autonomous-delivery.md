# SPEC-20261124 — Entrega autônoma: branch → PR sem intervenção

| Campo | Valor |
|-------|-------|
| Status | `Delivered` |

> Objetivo: o agente interno executa o ciclo completo de entrega sozinho
> quando recebe uma tarefa — cria branch de trabalho se estiver numa
> protegida, implementa, testa, valida, commita, faz push e abre o PR,
> passando para a próxima tarefa da fila sem pausar.

## Contexto

O bloco BUILD do system prompt já ordena implement→test→commit→PR, mas
três lacunas impedem a entrega autônoma de verdade:

1. **Nada impede commit na `main`** — o guard de segredos
   (`SecretPathGuard`) cobre conteúdo, não ref. Um `git commit` na branch
   protegida do repo clonado vaza para `git push origin main` e contorna
   o fluxo de PR.
2. **`dev_pipeline` commita na branch corrente** — `CommitAsync` faz
   `add -A` + commit + push cegamente; se o workdir estiver na `main`,
   empurra direto pra ela.
3. **PR manual** — nem o prompt nem o pipeline criam o PR; o agente
   precisa saber que `gh` + `GH_TOKEN` (env gerenciada,
   `AgentSecretsEnvService`) já estão disponíveis.

## Requisitos

### RF-001 — Guard de branch protegida no `shell_exec`

`ProtectedBranchGuard` (public static, `ChatTools`): dado o comando e a
branch corrente, devolve violação quando uma operação mutante mirar uma
branch protegida:

- `git commit`, `git merge`, `git rebase`, `git cherry-pick`, `git am`
  → bloqueados quando a branch **corrente** é protegida.
- `git push` → bloqueado quando a branch corrente é protegida **ou**
  quando um refspec/argumento explícito resolve pra protegida
  (`push origin main`, `push origin HEAD:main`, `push origin :main`).
- Lista protegida: kv `git.protectedBranches`, default `"main,master"`
  (CSV, case-insensitive).
- Recusa orienta: *"crie uma branch de trabalho:
  `git checkout -b feature/{slug}`"* — o bloqueio é a correção, não o
  beco sem saída.
- Checkout/switch/branch -b **não** são bloqueados (são a saída).
- Roda no `ExecuteAsync` do `shell_exec`, ao lado do
  `GitSecretStagingCheckAsync` — cobre foreground e background
  (`wait_for`/`run_in_background` passam pelo mesmo ponto).
- Reusa o tokenizer do classifier (SplitSegments → internal) — comandos
  encadeados (`cd x && git commit`) são avaliados por segmento.

### RF-002 — `dev_pipeline`: auto-branch + PR opcional

`CommitAsync` no `DevPipelineService`:

- Antes do `add -A`: `EnsureWorkBranchAsync` — se HEAD está em branch
  protegida → `git checkout -b feature/agent-{yyyyMMdd}-{slug}` (slug
  derivado da tarefa). A pipeline nunca commita na protegida.
- Após push com sucesso: se `gh` existir no PATH, `GH_TOKEN` resolvível
  via `AgentSecretsEnvService` e o remote `origin` for GitHub →
  `gh pr create --fill --head <branch>`; a URL entra no relatório
  `<pipeline_result>` como `pr_url`. Sem gh/token/remoto → nota
  explicativa em vez de erro (PR não é fatal).
- Nunca força: `gh` ausente, token ausente ou remoto não-GitHub → pula
  com razão registrada.

### RF-003 — System prompts refletem o ciclo completo

- `AgentModeContributor` (bloco BUILD): o fechamento vira
  *"branch de trabalho (feature/… — nunca commit na main) → commit
  Conventional → `push -u origin HEAD` → `gh pr create` (GH_TOKEN já
  está na env)"*.
- `SddWorkflowContributor` (modo execução): ciclo por SPEC =
  *"branch → fatias TDD → validação → commit+push+PR → próxima SPEC
  aprovada da fila, sem pausar"*.

## Critérios de aceite

- **AC-1**: `git commit` num repo na `main` via `shell_exec` → recusa
  com hint de branch; após `checkout -b feature/x` → executa.
- **AC-2**: `git push origin main` / `push origin HEAD:main` → recusa
  mesmo estando noutra branch.
- **AC-3**: `git checkout -b`, `git status`, `git add` na `main` →
  permitidos (não-mutantes de ref ou preparação).
- **AC-4**: pipeline em workdir na `main` → commit cai em
  `feature/agent-*`; HEAD final não é protegida.
- **AC-5**: `kv git.protectedBranches="trunk"` → `trunk` protegida,
  `main` liberada.
- **AC-6**: prompts contêm as diretivas de branch+PR (assert de texto
  nos testes de contributors existentes).

## Não-escopo

- Merge do PR (sempre humano — já é regra do prompt).
- Force-push / deleção de refs remotas (já Dangerous no classifier).
- Guard em `ChatJobService` direto — o ponto de entrada coberto é o
  `shell_exec`; chamadas internas do pipeline têm RF-002 próprio.
