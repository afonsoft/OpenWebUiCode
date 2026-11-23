# SPEC-20261123-lint-diagnostics-feedback — injeção de diagnósticos de build pós-edição

**Status**: Completed

## Motivação

Portado do Devin CLI (binary `devin 3000.14.30821`, `agent-ext/src/lints/diagnostics_handler.rs`):
após edições, o handler injeta no contexto do agente os **erros de lint/build
relacionados aos arquivos editados** — *"the following lint errors may be
related to your recent edits… fix them if you can, but exercise prudence"* —
com cap de injeção (`injection cap reached, skipping`).

Hoje o nosso loop não tem feedback proativo: o agente edita e só descobre o
erro quando decide rodar `dotnet build` manualmente — um round-trip inteiro
de LLM entre "editou errado" e "soube que errou".

## Design

- Novo `DiagnosticsExtension` no pipeline do `ChatRunExecutor`: após cada
  step que contenha calls de edição (`edit`, `write`, `patch`, `notebook_edit`),
  dispara uma coleta **assíncrona e com debounce** (agrupa edições do mesmo
  step — uma build por step, não por call).
- A coleta roda `dotnet build --no-restore` **scoped aos projetos afetados**
  (mapeia path editado → `.csproj` dono; projeto sem mapeamento → slnx toda).
  Timeout configurável (`diagnostics:timeout_sec`, default 120s).
- Resultado injetado como evento `lint_diagnostics` no stream do run —
  somente erros (warnings ignorados por default,
  `diagnostics:include_warnings` opcional), **cap de 20 diagnósticos** e
  dedup por (arquivo, linha, código) contra o snapshot anterior para não
  repetir erros pré-existentes.
- Texto injetado espelha o padrão Devin: erros + instrução explícita de que
  podem ser pré-existentes ("may be related to your recent edits… exercise
  prudence") — evita o modelo perseguir warnings alheios.
- Desliga sozinho quando: workspace não tem `.sln/.slnx`, build falha por
  motivo ambiental (restore/SDK ausente), ou `diagnostics:enabled=false`.
- Custo controlado: uma build incremental .NET pós-edição ≈ segundos;
  step sem edição → zero custo.

## Testes

- `Diagnostics_StepComEdicao_InjetaErros` — edição que quebra compilação →
  evento `lint_diagnostics` com arquivo:linha no próximo turno.
- `Diagnostics_StepSemEdicao_NaoDispara` — step só de leitura não gasta build.
- `Diagnostics_Debounce` — 3 edições no mesmo step → 1 build só.
- `Diagnostics_ErroPreExistente_Dedup` — erro que já existia antes do run
  não é reportado como novo.
- `Diagnostics_CapEDesligamento` — >20 erros trunca; `enabled=false` no-op.

## Delivered

Implementado em feature/agent-20261123-devin-parity — ver commit da
branch e testes dedicados em tests/OpenWebUI.Api.Tests.
