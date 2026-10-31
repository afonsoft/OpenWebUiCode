# SPEC-20261031-agent-prompt-pack

## 0. Metadata

| Field | Value |
|-------|-------|
| Feature | Pacote de prompts do agente — delegação, verify-once, instant_context, unseen_messages, tool_result_footer, extend_context |
| Type | Feature |
| Stack | .NET 10 / ASP.NET Core |
| Repository | afonsoft/OpenWebUiCode |
| Branch | feature/devin-20261031-agent-prompt-pack |
| Ticket | [A DEFINIR] |
| Status | Approved |

## 1. User Story

**As a** mantenedor do agent-harness
**I want** portar as práticas de prompt e de loop de controle destiladas do Devin CLI (delegação disciplinada, verify-once, contexto sob demanda, steering de mensagens não-vistas, footer de paralelismo, extensão de contexto, stall watch)
**So that** o comportamento do agente melhore sem depender de features pesadas — só com engenharia de prompt + pequenos mecanismos no loop.

**Problem context:**
Do binário do Devin (`docs/DEVIN-CLI-REA-ANALYSIS.md` §7, §8, §9, §10) saíram práticas portáveis e baratas:

- **`tool_result_footer`**: após cada tool result, um lembrete "First privately list what you need next; then request every item that doesn't depend on another's result in this one response" — reforça paralelismo no meio da run.
- **`unseen_messages`**: tool `unseen_messages` injeta prompts do usuário chegados durante a run (steering real-time).
- **`instant_context`**: sub-agente de coleta com `restricted_exec` (`rg`/`readfile`/`tree` schema-enforced, N cmds/turn, M turns) que devolve **XML estrito** `<ANSWER><file path><range>a-b/></ANSWER>` — contexto inicial cirúrgico file+linha.
- **`extend_context`**: tool que o modelo chama para pedir extensão de janela (grant/limit/at_ceiling).
- **`stall_watch`** + **`cache_keepalive`**: watchdog de stall e pings de prompt-cache no control loop.
- **Disciplina de delegação** (do prompt lead): failure patterns explícitos — premature implementation briefing, promover hipótese delegada a conclusão, verificar cedo demais/vezes demais, re-rodar checks por reassurance.
- **Verify-once**: um passe consolidado no fim, comando mais estreito que cubra a mudança, não re-rodar check que passou sem mudança relevante.
- **`<ref_file>`/`<ref_snippet>`**: citações clicáveis de arquivo na resposta (já existe suporte parcial?).

O OpenWebUiCode já tem blocos de system prompt (`SystemPromptContributors`) e loop (`ChatPipeline`), mas não tem esses mecanismos de loop-control nem a disciplina de delegação/verificação explícita.

## 2. Scope

**In scope:**

- **Tool result footer**: após cada tool result na run, injetar uma linha fixa de lembrete de paralelismo (configurável `AgentLoop:ToolResultFooter`).
- **`unseen_messages`**: builtin tool que retorna mensagens do usuário chegadas desde a última iteração; o loop chama automaticamente quando há mensagens pendentes (steering).
- **`instant_context`**: builtin tool que dispara um mini-agente de coleta com `restricted_exec` (subcomandos `rg`, `readfile`, `tree` — schema-enforced, N cmds/turn, M turns) e retorna o `<ANSWER>` XML com file+line ranges; integrado na primeira iteração quando a tarefa é exploratória.
- **`extend_context`**: builtin tool que pede extensão de janela (`granted|at_ceiling|limit`); integra com `ContextCompactionService`.
- **`stall_watch`**: watchdog no control loop que detecta N iterações sem progresso (mesma tool+args repetidas, sem mudança de estado) e injeta um nudge ou escala.
- **`cache_keepalive`**: durante esperas longas (aprovação do usuário, subagent), pings no provider para manter prompt-cache quente (configurável, provider-aware).
- **Disciplina de delegação + verify-once**: blocos no system prompt (contributors) com os failure patterns e a regra de verificação única consolidada.
- **`<ref_file>`/`<ref_snippet>`**: já suportado parcialmente — garantir rendering consistente na resposta final.

**Out of scope:**

- Refatoração do `ChatPipeline` (os mecanismos entram como contribuições/serviços adjacentes).
- Sidekick/lead completo (SPEC-20261031-sidekick-orchestration cobre).
- Compactor v2 (notas-por-mensagem, async file compactor) — fica como follow-up.

## 3. Technical Context

**Where the change happens:**

`src/OpenWebUI.Api/Completions/Contributors/SystemPromptContributors.cs` (blocos de disciplina), `src/OpenWebUI.Infrastructure/ChatTools/Tools/` (novas tools `unseen_messages`, `extend_context`, `instant_context`), `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` (footer pós-tool, stall watch, unseen auto-call), `ChatPipeline.cs` (cache keepalive), `ContextCompactionService` (extend_context hook).

**Files to read before implementing:**

- `src/OpenWebUI.Api/Completions/Contributors/SystemPromptContributors.cs`
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs`
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs`
- `src/OpenWebUI.Infrastructure/ChatTools/BuiltinToolRegistry.cs`
- `docs/DEVIN-CLI-REA-ANALYSIS.md` §7, §8, §9
- `src/OpenWebUI.Infrastructure/ContextCompactionService.cs`

**Files to create or modify:**

```text
src/OpenWebUI.Infrastructure/ChatTools/Tools/UnseenMessagesBuiltinTool.cs   (novo)
src/OpenWebUI.Infrastructure/ChatTools/Tools/ExtendContextBuiltinTool.cs    (novo)
src/OpenWebUI.Infrastructure/ChatTools/Tools/InstantContextBuiltinTool.cs   (novo)
src/OpenWebUI.Infrastructure/Context/RestrictedExecRunner.cs                (novo — rg/readfile/tree)
src/OpenWebUI.Infrastructure/Context/StallWatchdog.cs                       (novo)
src/OpenWebUI.Api/Runs/ChatRunExecutor.cs                                   (footer, unseen auto-call, stall)
src/OpenWebUI.Api/Completions/ChatPipeline.cs                               (cache keepalive)
src/OpenWebUI.Api/Completions/Contributors/SystemPromptContributors.cs      (disciplina)
tests/OpenWebUI.Api.Tests/AgentLoopMechanismsTests.cs                       (novo)
```

## 4. Requirements

### RF-001: Tool result footer
- **Description:** Após cada tool result, uma linha fixa é anexada lembrando paralelismo ("First privately list what you need next; then request every item that doesn't depend on another's result in this one response").
- **Rules:** configurável (`AgentLoop:ToolResultFooter`, vazio desliga); não aparece no último result antes da resposta final; sanitizada (sem markdown).

### RF-002: `unseen_messages`
- **Description:** Tool que devolve mensagens do usuário chegadas durante a run; o executor a chama automaticamente quando há `ChatRunPauses`/mensagens pendentes antes da próxima iteração.
- **Rules:** retorna em ordem com timestamps; marcar como "vistas" na sessão; steering — o modelo deve reagir antes de continuar.

### RF-003: `instant_context`
- **Description:** Dispara coleta estruturada: `restricted_exec` aceita só `rg <args>`, `readfile <path> <a> <b>`, `tree <path> <depth>` (schema-enforced), máx N comandos/turn e M turns; o coletor devolve **somente** `<ANSWER><file path><range>a-b/>...</ANSWER>` com os trechos relevantes.
- **Rules:** comandos fora da allowlist → erro; ranges obrigatórios; sem prosa fora do XML; resultado alimenta o contexto da run-pai.

### RF-004: `extend_context`
- **Description:** Tool que sinaliza necessidade de mais janela; resposta `{status: granted|limit|at_ceiling, newLimit?}`; integra com `ContextCompactionService` (tenta estender antes de compactar).
- **Rules:** at_ceiling → orienta a compactar/resumir; limit → reporta o teto.

### RF-005: `stall_watch`
- **Description:** Watchdog no loop que detecta stall (K iterações consecutivas com mesma tool+args-hash e sem side-effect novo) e injeta um nudge ("You appear to be repeating <tool> — reassess") ou escala após M.
- **Rules:** hash de tool+args normalizados; side-effect = file/fs/estado alterado; configurável `AgentLoop:StallThreshold`.

### RF-006: `cache_keepalive`
- **Description:** Durante esperas > T (aprovação pendente, subagent síncrono), ping leve ao provider para manter prompt-cache quente.
- **Rules:** provider-aware (só providers com prompt-cache); intervalo e liga/desliga configuráveis; nunca interfere na run.

### RF-007: Disciplina de delegação + verify-once no prompt
- **Description:** Blocos no system prompt com: failure patterns de delegação (premature briefing, hipótese→conclusão, verificar cedo/vezes demais, re-rodar por reassurance) e regra verify-once (passe consolidado no fim, comando mais estreito).
- **Rules:** em pt-BR nos contributors, coerente com os blocos existentes.

## 5. API Contract

Sem API nova (todas builtin tools + mecanismos internos do loop).

## 6. Acceptance Criteria

- [ ] **Given** uma tool result, **when** o executor processa, **then** o footer é anexado (quando configurado).
- [ ] **Given** mensagem do usuário durante a run, **when** a próxima iteração chega, **then** `unseen_messages` é chamada e a mensagem entra no contexto.
- [ ] **Given** `instant_context` com tarefa exploratória, **when** executada, **then** retorna `<ANSWER>` com file+ranges válidos.
- [ ] **Given** loop preso repetindo a mesma tool, **when** K iterações, **then** stall_watch injeta nudge.
- [ ] **Given** provider com prompt-cache e aprovação pendente > T, **when** o tempo passa, **then** keepalive é enviado.
- [ ] **Given** `extend_context` perto do teto, **when** chamada, **then** responde `at_ceiling` e sugere compactar.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| `restricted_exec` comando fora da lista | `rm -rf` | erro "comando não permitido" |
| `unseen_messages` sem pendências | nenhuma | "no new messages" |
| Provider sem prompt-cache | keepalive | no-op silencioso |
| Stall com side-effect novo | mesmo tool, resultado diferente | não conta como stall |

## 7. Task Plan

- [ ] **T1 — Discovery:** ler contributors/executor/pipeline/compaction; mapear pontos de injeção.
- [ ] **T2 — Footer + unseen:** footer pós-tool + `unseen_messages` com auto-call.
- [ ] **T3 — `instant_context`:** `RestrictedExecRunner` + coletor + parse XML.
- [ ] **T4 — `extend_context` + stall + keepalive:** tool, watchdog, pings.
- [ ] **T5 — Prompts:** blocos de disciplina nos contributors.
- [ ] **T6 — Testes:** unitários (footer, unseen, restricted-exec, stall hash, extend) + integração (loop).
- [ ] **T7 — Done + PR.**

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **.NET** | Unit tests (cada mecanismo); integration tests (loop com footer/unseen/stall); 80% coverage. |

## 8. Organization Guardrails

- **Branches:** `feature/devin-20261031-agent-prompt-pack`.
- **Security:** `restricted_exec` é allowlist estrita; keepalive não vaza dados; unseen respeita sessão.
- **Scope:** mecanismos leves de loop + prompts; sem sidekick/compactor v2.
- **Architecture:** tools builtin + serviços de loop adjacentes ao executor.

## 9. Definition of Done

- [ ] RFs implementados; aceite coberto por testes.
- [ ] Edge cases tratados; `dotnet test` verde; `check-spec-status.py` ok.
- [ ] Guardrails respeitados; logs sem PII.

## Open Questions / Pending Ambiguity

- `instant_context` deve rodar automaticamente na 1ª iteração ou só quando o modelo chama? (recomendo opt-in pelo modelo + config `instantContext.auto` para tarefas exploratórias).
- Stall nudge vs. hard-stop? (recomendo nudge primeiro, escala para o usuário após M stalls seguidos).
