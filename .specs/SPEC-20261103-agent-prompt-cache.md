# SPEC-20261103-agent-prompt-cache

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `agent-prompt-cache` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/Devin-20261103-agent-prompt-cache` |
| Ticket | `#639` |
| Status | `In implementation` |

## 1. User Story

**As a** operador do agente
**I want** o system prompt dividido em bloco estático/dinâmico com `cache_control` nos providers que suportam
**So that** cada rodada não repague o prompt inteiro (custo e latência) quando só o contexto dinâmico mudou.

**Problem context:**
A REA do Claude Code (`docs/CLAUDE-CODE-REA-ANALYSIS.md` §1) mostra a
assembly com `__SYSTEM_PROMPT_STATIC_BREAKPOINT__` separando o bloco
estável (identidade + seções `# System`/`# Tone and style`/`# Using your
tools`/`# Doing tasks`/`# Delivering work`/`# Corrections`) do dinâmico
(superfície, modos, reminders), com telemetria de hash por bloco
(`tengu_sysprompt_block`) e eventos "system prompt changed". O nosso
pipeline monta o system prompt inteiro por contribuidor a cada chamada do
provider (`ChatPipeline`) sem demarcação: providers com prompt-cache
(Anthropic `cache_control`, OpenAI cache automático por prefixo) não
reaproveitam nada e não medimos quando o prefixo muda.

## 2. Scope

**In scope:**
- Split estático/dinâmico: contribuidores declaram `CacheTier {Static, Dynamic}`; o executor emite o bloco estático como prefixo estável (ordem determinística) e o dinâmico ao final.
- `cache_control` por provider: Anthropic → `{"type":"ephemeral"}` no último bloco estático; OpenAI → apenas prefixo estável; demais → no-op.
- Telemetria: `sha256` do bloco estático por run + evento `prompt_block_changed` quando divergir dentro da run; captura de `cache_read_input_tokens`/`cache_creation_input_tokens` no ledger.
- Flags: `prompt_cache:enabled` (default true), `prompt_cache:min_static_chars` (default 1024).

**Out of scope:**
- Reordenação do conteúdo das seções (só a demarcação).
- Cache de definições de tools (fase 2).
- Providers sem prompt-cache exposto (Ollama, OpenAI-compat genérico) — degradar silenciosamente.
- Mudança no `<context_usage>` (já efêmera, fora do bloco estático).

## 3. Technical Context

**Where the change happens:**
`ChatPipeline` monta o request por rodada (`makeRequest`/`CompleteResilientAsync`); `ChatRunExecutor` emite eventos; `RunUsageLedgerService` captura tokens. A interface dos contribuidores de prompt ganha `CacheTier` (default `Static` para identidade/regras; `Dynamic` para modo, datas, reminders, `<context_usage>`).

**Files to read before implementing:**
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs`
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs`
- `src/OpenWebUI.Infrastructure/Services/RunUsageLedgerService.cs`
- contribuidores de prompt (`*PromptContributor*`)

**Files to create or modify:**
```text
src/OpenWebUI.Api/Completions/ChatPipeline.cs          (split + cache_control)
src/OpenWebUI.Api/Runs/ChatRunExecutor.cs              (evento prompt_block_changed)
src/OpenWebUI.Infrastructure/Services/RunUsageLedgerService.cs (cache_* tokens)
tests/OpenWebUI.Api.Tests/                             (fixture nova)
```

## 4. Requirements

### RF-001: `Split estático/dinâmico`
- **Description:** O executor monta o system prompt em dois blocos — `static` (contribuidores `Static`, ordem estável) seguido de `dynamic` (resto). Conteúdo interno inalterado.
- **Input → Output:** contribuidores + flags → request com system prefixo estável.

### RF-002: `cache_control por provider`
- **Description:** Anthropic → `cache_control:{"type":"ephemeral"}` no último bloco estático quando `len(static) ≥ min_static_chars`; OpenAI → nenhum marcador (prefixo estável basta); demais → ignoram.

### RF-003: `Telemetria de bloco`
- **Description:** `sha256(static)` no início da run; mudança entre rodadas → `note prompt_block_changed {tier, prev, next}`; idem `dynamic` (debug).

### RF-004: `Métricas de cache`
- **Description:** `RunUsageLedger` persiste `cache_read_input_tokens`/`cache_creation_input_tokens` (e `prompt_tokens_details.cached_tokens` OpenAI) quando presentes no `usage`; admin usage soma.

### RF-005: `Flags`
- **Description:** `prompt_cache:enabled=false` desliga marcação (prompt idêntico ao atual); default `true`.

### RF-006: `context_usage fora do estático`
- **Description:** A nota `<context_usage>` permanece sempre no bloco dinâmico, nunca cacheada.

## 6. Acceptance Criteria

- [ ] **Given** dois builds com mesmos contribuidores **when** comparo `sha256(static)` **then** são idênticos.
- [ ] **Given** provider Anthropic com static ≥ threshold **when** monta o request **then** último bloco estático carrega `cache_control`; OpenAI não carrega.
- [ ] **Given** static < threshold **when** monta **then** nenhum `cache_control` emitido.
- [ ] **Given** contribuidor dinâmico muda conteúdo entre rodadas **when** a rodada seguinte monta **then** evento `prompt_block_changed` emitido.
- [ ] **Given** `<context_usage>` **when** o request é montado **then** ela está fora do bloco estático.
- [ ] **Given** `prompt_cache:enabled=false` **when** monta **then** body byte-idêntico ao de hoje.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Provider sem cache | Ollama | request inalterado, sem marcador |
| static vazio | nenhum contribuidor Static | sem breakpoint, sem erro |
| usage sem cache tokens | provider não reporta | campos ausentes no ledger, sem falha |

## 7. Task Plan

- [ ] **T1 — Discovery:** mapear contribuidores e o ponto de montagem do system no `ChatPipeline`.
- [ ] **T2 — Split + tier:** interface `CacheTier` + montagem de dois blocos.
- [ ] **T3 — cache_control + telemetria:** marcação Anthropic, hash por run, evento, ledger.
- [ ] **T4 — Tests:** critérios da seção 6.
- [ ] **T5 — DoD + PR.**

## 8. Organization Guardrails

- **Branches:** `feature/Devin-20261103-agent-prompt-cache` — nunca `main`.
- **Workflows:** não modificar `.github/workflows/`.
- **Security:** não logar conteúdo do prompt; hash apenas.
- **Scope:** não inventar requisitos; parar e perguntar em ambiguidade.
- **Architecture:** lógica no `ChatPipeline`/infra; sem regra em endpoints.

## 9. Definition of Done

- [ ] Todos os RFs implementados.
- [ ] Critérios da seção 6 cobertos por testes.
- [ ] Edge cases tratados.
- [ ] Build + suíte passando; cobertura ≥ baseline.
- [ ] Guardrails respeitados.

**Next action after DoD:** `Status = Done` e PR na branch da feature.

## Open Questions / Pending Ambiguity

- Anthropic aceita até 4 breakpoints — usar só 1 (system) nesta fase? → **recomendo sim**.
