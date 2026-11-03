# SPEC-20261103-compactor-v3-delta

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `compactor-v3-delta` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/Devin-20261103-compactor-v3-delta` |
| Ticket | `#642` |
| Status | `Done` |

## 1. User Story

**As a** usuário de sessões longas
**I want** o resumo de compactação com seções de erros e próximo passo (citação verbatim) e gatilho por acúmulo de attachments
**So that** o contexto compactado não perca os erros aprendidos nem a direção exata do trabalho, e imagens acumuladas não estourem a janela sem disparar compactação.

**Problem context:**
A REA do Claude Code (§4) mostra o summarizer de 9 seções — o nosso v2
(SPEC-20261102, mergeada) já cobre `summary/state/breadcrumbs/files/
decisions/pending` + records + todos. Dois deltas ainda ausentes:

1. **Seções `Errors and fixes` e `Optional Next Step`** — Claude exige
   "errors encountered and how they were fixed" e um next step "DIRECTLY
   in line with the user's most recent explicit requests" **com citação
   verbatim** de onde parou ("verbatim to ensure there's no drift").
2. **Gatilho por attachments** — "Accumulated images and attachments…
   pushed the request over the limit. Remove older images or compact":
   overflow por conteúdo binário dispara compactação, não só tokens de
   texto.

## 2. Scope

**In scope:**
- **Summarizer-v3**: transporte JSON ganha `errors[]` e `next_step {text, quote}`; `FormatSummary` renderiza `## Errors and fixes` e `## Optional Next Step` (com `> "{quote}"`).
- **Attachment-overflow trigger**: `MaybeSpawnAsync`/`WaitPendingAsync` também disparam quando `attachment_bytes` acumulados na janela excederem `compaction:attachment_threshold_bytes` (default 8 MB) — independente do token-count.
- **Compat**: campos novos nullable; digest antigo desserializa.

**Out of scope:**
- Mudanças no 3-tier/scheduler/hard-wait (SPEC-20261102 entregue).
- Remoção de imagens antigas (só o disparo da compactação).
- `PostCompact` markers de UI.

## 3. Technical Context

**Where the change happens:**
`ContextCompactionService` — o summarizer prompt ganha as 2 seções e o parser/`FormatSummary` as renderizam; `ThresholdsAsync`/spawn-check passa a considerar `attachment_bytes` da janela (soma de bytes dos attachments persistidos nas mensagens do trecho).

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs`
- `src/OpenWebUI.Infrastructure/Context/AsyncCompactionScheduler.cs`
- `tests/OpenWebUI.Api.Tests/ContextCompactionV2Tests.cs`

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs
tests/OpenWebUI.Api.Tests/ContextCompactionV2Tests.cs   (+testes)
```

## 4. Requirements

### RF-001: `Seções Errors e Next Step`
- **Description:** Summarizer-v3 exige `errors[]` ("errors encountered and how they were fixed") e `next_step {text, quote}` ("verbatim to ensure there's no drift"); `FormatSummary` renderiza ambas quando presentes.
- **Input → Output:** JSON do summarizer → digest renderizado com as 2 seções.

### RF-002: `Attachment-overflow trigger`
- **Description:** Spawn-check também dispara quando `sum(attachment_bytes)` do trecho > `compaction:attachment_threshold_bytes` (default 8 MB), mesmo abaixo do spawn-ratio.

### RF-003: `Compat`
- **Description:** `errors`/`next_step` nullable; digest v2 desserializa como v3; `HasDigest` aceita qualquer seção não-vazia.

### RF-004: `Citação verbatim obrigatória`
- **Description:** `next_step.quote` é exigida quando `next_step.text` presente; ausente → seção descartada com `note compaction_next_step_no_quote` (sem quebrar o apply).

### RF-005: `Flags`
- **Description:** `compaction:summarizer_v3` (default true) volta o prompt/seções ao v2 quando off; `compaction:attachment_threshold_bytes` configurável.

### RF-006: `Eventos`
- **Description:** `compaction_spawned` ganha `trigger ∈ {"tokens","attachments"}`.

## 6. Acceptance Criteria

- [ ] **Given** summarizer retornando `errors` e `next_step{quote}` **when** `FormatSummary` **then** as seções aparecem com a citação verbatim.
- [ ] **Given** `next_step` sem `quote` **when** formata **then** seção descartada + nota emitida, apply não quebra.
- [ ] **Given** janela com 10 MB de attachments e tokens abaixo do spawn-ratio **when** `MaybeSpawnAsync` **then** spawna com `trigger:"attachments"`.
- [ ] **Given** digest v2 (sem `errors`/`next_step`) **when** desserializa **then** funciona como antes.
- [ ] **Given** `compaction:summarizer_v3=false` **when** sumariza **then** prompt/seções v2.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| `errors:[]` | vazio | seção omitida |
| attachment sem `bytes` | metadado ausente | estimativa por chars×0.75, sem falha |
| ambos triggers | tokens+attachments | `trigger:"attachments"` (maior severidade) |

## 7. Task Plan

- [ ] **T1 — Discovery:** parser/`FormatSummary`/`HasDigest` atuais; contagem de bytes dos attachments.
- [ ] **T2 — Summarizer-v3:** prompt + parser + render das seções.
- [ ] **T3 — Attachment trigger:** soma + threshold + `trigger` no evento.
- [ ] **T4 — Tests:** critérios da seção 6.
- [ ] **T5 — DoD + PR.**

## 8. Organization Guardrails

- **Branches:** `feature/Devin-20261103-compactor-v3-delta` — nunca `main`.
- **Workflows:** não modificar `.github/workflows/`.
- **Security:** não logar conteúdo de attachments; apenas bytes/count.
- **Scope:** delta apenas — não reimplementar o 3-tier.

## 9. Definition of Done

- [ ] Todos os RFs implementados.
- [ ] Critérios da seção 6 cobertos por testes.
- [ ] Edge cases tratados.
- [ ] Build + suíte passando; cobertura ≥ baseline.
- [ ] Guardrails respeitados.

**Next action after DoD:** `Status = Done` e PR na branch da feature.

## Open Questions / Pending Ambiguity

- `attachment_bytes` por metadado real ou estimativa por chars? → **recomendo real quando presente, estimativa `chars×0.75` senão**.
