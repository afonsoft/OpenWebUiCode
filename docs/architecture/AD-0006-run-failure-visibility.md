# AD-0006 — Falhas de run visíveis: SSE `{error}`, mensagem persistida e detalhe upstream real

## Context

Incidente de produção (2026-11-07): runs cujo provedor falhava (pool do gateway
esgotado, 4xx/5xx upstream, resposta mascarada de billing) terminavam em
`silêncio` — a UI mostrava "Pensando" indefinidamente porque:

1. O `{error}` emitido no SSE da run era tratado como exceção de transporte
   pelo parser tipado e engolido;
2. O polling que encontrava a run `failed` apenas encerrava o stream;
3. `run.Error` nunca era persistido como mensagem — F5 apagava qualquer traço;
4. A mensagem de falha era genérica ("falhou após N tentativa(s)") — o motivo
   real do upstream (quota, modelo inválido, pool vazio) ficava só no corpo
   da `ProviderHttpException`, sem chegar ao usuário.

## Decision

Tornar a falha de run um cidadão de primeira classe em três camadas:

- **Stream**: `{error}` do SSE vira evento tipado `ChatStreamEvent.Error`;
  a UI exibe `_error` e encerra — e o polling fallback cobre o reattach
  (`HandleEmptyAttachAsync` quando a run já morreu).
- **Persistência**: `FinishAsync`/`FailAsync` gravam `> ⚠️ {run.Error}` como
  mensagem do assistant — o erro sobrevive a F5 e entra no histórico.
- **Detalhe upstream**: `ProviderErrorDetail` anexa
  ` Último erro: <status> — <error.message|body sanitizado>` à mensagem de
  falha — tanto no embrulho do retry loop (`server-error:`) quanto na
  exceção não-retryable direta no catch-all do executor.

Alternativas rejeitadas: exibir só no console de runs (erro invisível após
F5); relançar a exceção sem decoração (mensagem técnica sem contexto de
provider); varrer o corpo por regex de billing (frágil — o JSON envelope
`error.message` já carrega o motivo).

## Consequences

- Positive: zero falha silenciosa — todo desfecho `failed` mostra o motivo
  real na UI e no histórico; o detalhe upstream permite diagnosticar
  exaustão de pool no gateway sem abrir logs; reattach de run morta também
  exibe o erro.
- Trade-off: o snippet do corpo upstream (cap 300 chars, linha única) vai
  para o banco — mitigado por `ProviderHttpException.MaxBodyChars` (1200)
  e pela preferência pelo campo `error.message` em vez do corpo bruto.

## Related SPEC

- Incidência investigada no banco de produção `openwebui.db` (runs
  `c0d240ea`, `1199dd6b`, `66a0956c`, `2d118742`, `2f2573c4`) e no gateway
  OmniRoute (`call_logs` — pools `auto/deepseek`, `auto/glm`,
  `auto/claude-haiku`, `auto/gemini` com upstreams esgotados).
