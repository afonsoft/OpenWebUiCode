# SPEC-20261016-run-recovery

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `run-recovery` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261016-run-recovery` |
| Ticket | [#404](https://github.com/afonsoft/open-webui/issues/404) · Epic [#398](https://github.com/afonsoft/open-webui/issues/398) |
| Status | `Completed` |
| Fonte | análise opencode v2.0.31 (AGENTS.md "Session Core" + `runner/llm.ts:332-385` — eventos duráveis, settle de órfãos, resume no drain) |

## 1. User Story

**As a** usuário com runs longas de codificação
**I want** que uma run sobreviva a restart do servidor — retomando do último step durável
**So that** 20 minutos de trabalho do agente não sejam perdidos por deploy/crash.

**Problem context:**
Hoje runs órfãs de restart são marcadas `interrupted` no boot (`ChatRunDispatcher.cs:74-99`) e todo o progresso além de `PartialContent` (texto) se perde — tool calls executadas, mensagens de tool e o ponto do loop morrem na memória. O opencode persiste cada fato como evento (Bus→projeção→SQLite), e no drain seguinte resume o step e liquida tool calls/compactions órfãs.

## 2. Scope

**In scope:**
- RF-001: `ChatRunEvent` (tabela) — eventos duráveis por run: `step_started`, `content_delta` (agregado), `tool_call`, `tool_result`, `step_finished`, `phase`, `note`. Gravação barata (append-only, sem update).
- RF-002: checkpoint de loop-state: `round`, `messages` tail (tool messages serializadas), `changes`, advisor counters — snapshot JSON a cada fronteira de round (não por delta).
- RF-003: resume no dispatcher: run `running` órfã + eventos presentes → reconstrói `messages` do loop e reentra no round seguinte com nota system `[run resumed after restart — N tool results recovered]`; sem eventos → interrupted (comportamento atual).
- RF-004: settle de tool calls órfãs: calls gravadas sem `tool_result` → result sintético `interrupted` na reconstituição (o modelo vê que a call foi interrompida).
- RF-005: gate de execução (write-ahead claim): run claima execução com `OwnerInstance` + heartbeat; boot de outra instância não resume run de instância viva (preparação para multi-instância — hoje single).
- RF-006: UI indica `resumed` (chip/phase) — SSE publica `phase: resumed` com `{round, toolsRecovered}`.

**Out of scope:**
- Migração do pipeline inteiro para event-sourcing (é incremental: eventos novos, não reescrita total).
- Clustering/multi-instance real de execução (claim prepara, não entrega).
- Replay determinístico de tool calls mutáveis — ferramentas reexecutam? Não: tool results gravados são reutilizados; call sem resultado vira `interrupted`, nunca reexecuta (evita double-apply de mutações).

## 3. Technical Context

- `src/OpenWebUI.Api/Runs/ChatRunDispatcher.cs` — boot-orphan handler atual.
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` — loop; pontos de gravação de eventos.
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs` — `RunToolLoopAsync` (persistência por fronteira).
- `AppDbContext` + migrations EF — `ChatRunEvents`, `ChatRunCheckpoints` (ou 1 tabela de eventos com payload JSON).
- `PartialContent` existente — vira caso particular do evento `content_delta` agregado.

## 4. Requirements

### RF-001: Eventos duráveis
- **Description:** `ChatRunEvent {Id, RunId, Seq, Type, PayloadJson, CreatedAt}`; `Seq` monotônico por run; escrita fire-and-forget segura (falha logada não derruba run, exceto tool_call/tool_result que são mandatórias).

### RF-002: Snapshot de loop
- **Description:** a cada início de round: `{round, messages(tail serializada), changesHash}`; reconstrução usa último snapshot + eventos posteriores.

### RF-003: Resume
- **Description:** no boot, runs `running` cujo heartbeat expirou → `ResumeAsync`: carrega snapshot+eventos → reentra no loop no round seguinte; `PartialContent` continua para texto mid-round (reinicia o round — provider é re-chamado; content parcial do round é descartado para não duplicar).

### RF-004: Settle de órfãos
- **Description:** `tool_call` sem `tool_result` pareado → `tool_result` sintético `{interrupted, "server restarted"}` gravado e entregue ao modelo.

### RF-005: Claim/heartbeat
- **Description:** `ChatRun.OwnerInstance` + `HeartbeatAt` (atualizado a cada 15s durante execução); resume só se heartbeat > 60s stale.

### RF-006: Telemetria
- **Description:** `phase: resumed` + contagem; `run.Meta` marca `resumedCount++`.

## 5. API Contract

Sem endpoints novos; `GET /api/v1/runs/{id}` passa a incluir `resumedCount`/`lastSeq` no meta.

## 6. Acceptance Criteria

- [ ] **Given** run no round 3 com 2 tool results gravados **when** servidor reinicia e sobe **then** run retoma no round 3+ com os results reconstruídos e nota de resume.
- [ ] **Given** `tool_call` sem resultado **when** resume **then** modelo recebe `interrupted` para aquela call e o loop continua.
- [ ] **Given** run sem eventos (legado) **when** boot **then** `interrupted` como hoje.
- [ ] **Given** heartbeat fresco (<60s) de outra instância **when** boot **then** não resume (instância viva é dona).
- [ ] **Given** run resumida completa **when** finaliza **then** mensagem final idêntica a uma run não-interrompida (content + tool messages).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| 2 restarts seguidos | resume→crash→resume | cada resume parte do último snapshot; contador incrementa |
| Snapshot corrompido | JSON inválido | marca interrupted + log (não loop infinito) |
| Run pausada no restart | `ChatRunPauses` | resume respeitando estado pausado |
| Migração: base antiga sem tabela | EF migration | baseline aplica; runs antigas → interrupted |

## 7. Task Plan

- [ ] **T1 —** Entidade `ChatRunEvent` + migration + writer service.
- [ ] **T2 —** Emissão de eventos no executor (call/result/step/delta agregado/snapshot de round).
- [ ] **T3 —** `ResumeAsync` no dispatcher + reconstrução + settle órfãos.
- [ ] **T4 —** Claim/heartbeat.
- [ ] **T5 —** `phase: resumed` + meta.
- [ ] **T6 —** Testes: simular kill mid-run (grava eventos, novo executor, resume); settle; migration baseline.
- [ ] **T7 —** build+test; DoD; PR.

**7.1 Validation strategy:** integration tests com SQLite real simulando restart; ≥80%.

## 8. Organization Guardrails

- Branch `feature/devin-20261016-run-recovery`.
- Migration obrigatória (`dotnet ef migrations add`); `DatabaseMigrator` baselina legados.
- Payloads com `SecretScrubber` antes de gravar.
- Nunca reexecutar tool mutável no resume — result ausente = `interrupted`.
- Append-only: sem update/delete de eventos (retenção futura via job).

## 9. Definition of Done

- [ ] RF-001..RF-006 implementados e testados.
- [ ] Critérios da seção 6 verdes.
- [ ] build/test/migration verdes.

## Open Questions / Pending Ambiguity

- Volume de eventos `content_delta`: agregar por flush 2KB (igual checkpoint atual) — não 1 evento/delta.

## Delivered

| Item | Link |
| --- | --- |
| PR/commit | #434 · cd8940643 |
| Issue | https://github.com/afonsoft/open-webui/issues/404 |
