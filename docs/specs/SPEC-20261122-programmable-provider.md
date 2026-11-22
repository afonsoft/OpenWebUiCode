# SPEC-20261122-programmable-provider — Fixture de provider programável

**Status**: Completed

## Problema

A suíte NUnit não exercita a maquinaria de orquestração (`DevPipelineService`
fases LLM/fan-out/workflow) ponta-a-ponta porque os testes não têm como
programar as respostas do provider. Resultado: ~0.5% de dívida de cobertura
e nenhum e2e do pipeline.

## Objetivo

Mock de provider **scriptável** no fixture de integração: cada request
`/api/chat` (Ollama) / `/chat/completions` (OpenAI) consulta uma fila de
handlers que decide a resposta por inspeção do body — permitindo dirigir
runs reais do `ChatRunExecutor` dentro do `WebApplicationFactory`.

## Design

- `ScriptedLlmMock` (HttpListener): fila de
  `Func<string body, Response?>` — primeiro handler não-nulo responde;
  fallback responde completion vazio + `done:true`.
- `Response`: `(status, bodyNdjson, delayMs)` — body em formato Ollama
  NDJSON (um objeto JSON por linha já é NDJSON válido; serve stream e
  non-stream).
- Builders: `Text(conteúdo)`, `ToolCalls(nome,args)`, `Fail(status,body)`,
  `BillingNotice()` (aviso forte Pollinations), `Sequence(...)`.
- `Bodies`: ConcurrentBag para assertions (inspeção de prompts).
- Roteamento: `/api/chat` → Ollama; `/chat/completions` → OpenAI;
  `/api/tags`, `/api/show`, `/v1/models` → listagem mínima.

## Casos e2e cobertos (primeira entrega)

1. `run_workflow` com step `llm` completando via `<pipeline_result>`
   real — `RunStepsAsync`, `RunLlmPhaseAsync`, `ParsePhaseResultAsync`,
   `WaitRunAsync`, `ReportAsync`.
2. Step `gate` + `dev_pipeline op=answer` ponta-a-ponta.
3. Board `pipeline_board` post/read dentro de run real.

## Fora de escopo

- Refatorar mocks existentes (`AgentLoopAutoChainTests` etc.) — o novo
  mock convive; migração gradual depois.
- Worktree real do fan-out (git) — segue coberto por testes unitários.

## Delivered

- PR [#770](https://github.com/afonsoft/OpenWebUiCode/pull/770) — merge commit `c751a30`.
