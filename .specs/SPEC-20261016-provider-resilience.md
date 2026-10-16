# SPEC-20261016-provider-resilience

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `provider-resilience` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261016-provider-resilience` |
| Ticket | [#400](https://github.com/afonsoft/open-webui/issues/400) · Epic [#398](https://github.com/afonsoft/open-webui/issues/398) |
| Status | `Approved` |
| Fonte | análise opencode v2.0.31 (`session/runner/retry.ts`, `runner/llm.ts:273-330`, `compaction.ts`) |

## 1. User Story

**As a** usuário com runs de agente longas
**I want** retries automáticos em falhas transitórias do provider, continuação quando a resposta é truncada por `length` e compactação+retry quando o contexto estoura
**So that** uma instabilidade de rede/rate-limit ou janela cheia não mate a run no meio do trabalho.

**Problem context:**
Hoje `ProviderService` captura `HttpRequestException`/`TaskCanceledException` e devolve erro — sem nenhuma tentativa de retry. `RunToolLoopAsync` não trata `finish_reason=length` (resposta trunca em silêncio) nem erro de context-overflow (provider rejeita por prompt grande → run falha). O opencode resolve com engine de retry (exponencial 2s→10s, jitter, `Retry-After` capado em 15min, timeouts com cap próprio), continuação sintética em `length` (até 3x) e overflow→compact→retry.

## 2. Scope

**In scope:**
- RF-001: política de retry nas chamadas de completion (stream + buffered + tool loop): classificação retryable (429, 5xx, timeout de conexão, stream incompleto antes de output), backoff exponencial 2s→cap 10s, jitter, máx. ~6 tentativas (≈2min) por chamada, `Retry-After` honrado até 15min.
- RF-002: fase `retrying` no SSE da run com tentativa/atraso/motivo (o usuário vê a run "esperando" em vez de muda).
- RF-003: `finish_reason=length` → injeta mensagem sintética de continuação ("continue de onde parou sem repetir") e segue o loop; até 3 continuações consecutivas → erro `output-limit`.
- RF-004: erro de context-overflow do provider (HTTP 400 com assinatura de prompt-too-long por provider) → `ContextCompactionService` compacta com alvo progressivo (70%/50%/35% do tamanho rejeitado) e retenta até 3x.
- RF-005: timeouts consecutivos limitados a 3 (provider morto não segura a run por 10min).
- RF-006: stream quebrado DEPOIS de output iniciado → continuação "a resposta anterior foi interrompida, continue" (padrão `CONTINUE_AFTER_INCOMPLETE_STREAM`).

**Out of scope:**
- Failover/round-robin entre providers/connections (separado, futuro).
- Retry de chamadas não-completion (listagem de modelos, embeddings).
- Resume pós-restart de processo — SPEC run-recovery.

## 3. Technical Context

- `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` — `StreamCompletionAsync`, `CompleteWithToolsAsync`, `CompleteWithToolsStreamingAsync` (≈2500 linhas; parse SSE/DSML próprio).
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs` — `RunToolLoopAsync`, `CompleteRoundAsync`, `BumpMaxTokens` (padrão existente de nudge em round vazio).
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` — `PublishPhaseAsync`, `FinishAsync`/`FailAsync`.
- `src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs` — `MaybeCompactAsync`/`ApplyAsync` já existentes.
- Novo: `ProviderRetryPolicy` (Infrastructure/Services) — classificação + schedule + eventos; testável isolado.

## 4. Requirements

### RF-001: ProviderRetryPolicy
- **Description:** serviço com `ShouldRetry(error, attempt, outputStarted)` e `DelayFor(error, attempt)` — exponencial base 2s, cap 10s, jitter ±25%, máx 6 tentativas; `Retry-After` floor até 900s; timeout puro capado a 3 tentativas.
- **Rules:** nunca retry quando já saiu delta de content visível (exceto caminho de continuação do RF-006); 4xx não-retryable exceto 408/409/429; `OperationCanceledException` do usuário nunca retenta.
- **Input → Output:** exceção/resposta → `{retry, delay, reason}`.

### RF-002: Telemetria de retry
- **Description:** fase `retrying` publicada via `PublishPhaseAsync` com `{attempt, delayMs, reason}`; contador no log da run.

### RF-003: Continuação em `length`
- **Description:** quando `finish_reason=="length"` (ou truncamento detectado no parser), o loop adiciona mensagem user sintética de continuação e chama de novo; contador reseta em input do usuário; 3 seguidas → `StepFailed` com `output-limit`.
- **Input → Output:** round truncado → novo round automático.

### RF-004: Overflow → compact → retry
- **Description:** detecção de context-overflow por provider (429/400 com patterns: `context_length`, `max.*tokens`, `too many tokens`, `prompt is too long`, `context window`); na 1ª ocorrência compacta para ~70% do est. rejeitado; 2ª → 50%; 3ª → 35%; ainda falha → erro final `context-overflow`.

### RF-005: Cap de timeouts
- **Description:** 3 timeouts consecutivos encerram com erro `provider-timeout` (não consome o budget geral de retries).

### RF-006: Stream incompleto pós-output
- **Description:** falha de transporte depois do primeiro delta → continua com mensagem sintética "resposta anterior interrompida; continue sem repetir conteúdo" (máx. 1 por round, conta no budget de retry).

## 5. API Contract

Sem endpoints novos — comportamento interno do pipeline + evento SSE `phase: retrying` já suportado pelo broadcaster.

## 6. Acceptance Criteria

- [ ] **Given** provider responde 429 com `Retry-After: 2` **when** a run chama completion **then** retenta após ~2s e publica `retrying`; sucesso na 2ª tentativa → run segue normal.
- [ ] **Given** 429 persistente **when** 6 tentativas esgotam **then** run falha com erro `rate-limit` (não trava indefinidamente).
- [ ] **Given** resposta `finish_reason=length` **when** o round termina **then** continuação automática acontece e o texto final concatena sem repetição; 3 truncamentos seguidos → erro `output-limit`.
- [ ] **Given** provider rejeita com `context_length_exceeded` **when** o round executa **then** compactação reduz e retenta; após 3 falhas → erro `context-overflow`.
- [ ] **Given** stream morre após 3 deltas de content **when** o erro chega **then** nova chamada pede continuação (não reinicia do zero).
- [ ] **Given** usuário cancela a run **when** em backoff de retry **then** cancelamento é imediato (não espera o delay).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| `Retry-After: 3600` | 429 | delay capado a 15min mas retry budget pode esgotar antes |
| 400 genérico (não-overflow) | body sem pattern | não retenta; erro direto |
| overflow sem compaction possível | histórico mínimo | falha `context-overflow` na 1ª tentativa |
| retry durante pause (`WaitIfPaused`) | run pausada | backoff respeita pause (não dispara chamada pausado) |

## 7. Task Plan

- [ ] **T1 —** `ProviderRetryPolicy` + `RetryReason` classification + testes de schedule.
- [ ] **T2 —** Wire no `CompleteRoundAsync`/streaming path (retry antes de propagar) + fase `retrying`.
- [ ] **T3 —** `finish=length` continuation no `RunToolLoopAsync` (+ parser expõe finish reason — verificar `ProviderCompletion`).
- [ ] **T4 —** Detecção de overflow por provider + loop compact→retry.
- [ ] **T5 —** Testes: mock provider com 429→ok, length×3, overflow→compact, timeout cap, cancel durante backoff.
- [ ] **T6 —** build+test; DoD; PR.

**7.1 Validation strategy:** .NET — unit tests do policy + integration do loop com `HttpMessageHandler` fake; ≥80%.

## 8. Organization Guardrails

- Branch `feature/devin-20261016-provider-resilience`.
- Não logar bodies de erro do provider além de 500 chars (podem conter prompt) — `SecretScrubber` nos logs.
- Delays de retry respeitam `CancellationToken` e `ChatRunPauses`.
- Mensagens sintéticas de continuação marcadas (não contam como input do usuário para título/stats).

## 9. Definition of Done

- [ ] RF-001..RF-006 implementados e testados.
- [ ] Critérios da seção 6 verdes.
- [ ] `dotnet build` 0 erros; `dotnet test` verde.
- [ ] Logs sem prompt/secret; fases SSE documentadas no front.

## Open Questions / Pending Ambiguity

- `ProviderCompletion` hoje expõe `finish_reason`? Se não, adicionar campo — confirmar no T3.
