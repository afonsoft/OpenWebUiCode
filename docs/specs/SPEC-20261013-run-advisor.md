# SPEC-20261013-run-advisor

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `run-advisor` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261013-run-advisor` |
| Ticket | [#352](https://github.com/afonsoft/open-webui/issues/352) |
| Status | `Completed` |
| Depends on | `SPEC-20261013-model-roles` (papel `advisor`; sem ele usa o modelo do chat) |

## 1. User Story

**As a** usuário do chat-agente
**I want** um modelo revisor secundário observando a run em tempo real e injetando conselhos curtos quando detecta desvio, risco ou oportunidade
**So that** erros de rumo sejam pegos cedo — antes de custarem turnos inteiros de tools erradas.

**Problem context:**
oh-my-pi tem o subsistema advisor/watchdog: um modelo secundário lê o transcript, pode inspecionar o workspace com tools read-only próprias e injeta avisos na sessão principal — sem poder de aprovação nem mutação. Nosso agente executa a run inteira sem segunda opinião; o usuário só percebe desvios no fim (ou nunca).

## 2. Scope

**In scope:**
- `RunAdvisorService`: durante a run, a cada gatilho (mensagem do usuário processada, N tool calls, run pausada por aprovação), envia o transcript tail + estado ao modelo do papel `advisor` e publica conselho.
- Conselho entra na run como nota de contexto (mensagem system curta no próximo turno do pipeline) **e** como evento SSE `advisor` visível ao usuário (chip discreto no transcript/feed).
- Advisor pode consultar o workspace com subconjunto read-only (`file_read`, `file_grep`, `file_glob`, `lsp_*`) via chamadas internas — nunca tools mutáveis, nunca aprovação.
- Toggle por chat (`Chat.AdvisorEnabled`) + default admin; advisor desligado por default.
- Cadência e custo limitados: máx. 1 conselho por gatilho, cap de N por run, timeout curto, falha silenciosa (log Debug, run nunca bloqueia).

**Out of scope:**
- Advisor com tools mutáveis (`WATCHDOG.yml` do omp permite — aqui não).
- Advisor em modo ACP/hosts externos.
- Streaming token-a-token do conselho (conselho é curto, não-streaming).
- Múltiplos advisors simultâneos (roster) — v1 é um papel `advisor` único.
- Advisor alterando/aprovando tool calls (só aconselha).

## 3. Technical Context

**Where the change happens:**
`ChatRunExecutor` publica gatilhos (hook points já existentes: loop de tools, pause). `RunAdvisorService` (novo, Infrastructure) monta prompt compacto (system do advisor + transcript tail + task atual) → `ProviderService.CompleteAsync` com modelo do papel `advisor` (via `ModelRoleResolver`, fallback = modelo da run) → resultado vai para (a) `ChatRunBroadcaster.Publish` evento `advisor`, (b) fila consumida pelo pipeline que vira nota system no próximo turno. Read-only workspace access via `BuiltinToolRegistry` restrito (subset hardcoded). Client: `ChatStreamService`/`ChatView` consome evento `advisor` → chip no feed de atividade + tooltip com o conselho.

**Files to read before implementing:**
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` (loop de tools, `GateToolCallAsync`, broadcaster calls)
- `src/OpenWebUI.Api/Runs/ChatRunDispatcher.cs` (ciclo de vida da run)
- `src/OpenWebUI.Infrastructure/Services/ChatRunBroadcaster.cs` (SSE events)
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/FileBuiltinTools.cs`, `LspBuiltinTools.cs` (subset read-only)
- `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` (`CompleteAsync`)
- `src/OpenWebUI.Client/Services/ChatStreamService.cs` + `RunActivityFeed.razor` (eventos)
- `src/OpenWebUI.Domain/Entities.cs` (`Chat`, `ChatRun`)

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities.cs                        # Chat.AdvisorEnabled (bool, default false)
src/OpenWebUI.Application/Contracts/ChatRunContracts.cs # RunAdvisorEvent
src/OpenWebUI.Application/Contracts/ConfigContracts.cs  # admin default
src/OpenWebUI.Infrastructure/Services/RunAdvisorService.cs  # NOVO
src/OpenWebUI.Infrastructure/Services/ModelRoleResolver.cs  # (da SPEC model-roles; senão resolver local)
src/OpenWebUI.Infrastructure/ChatTools/AdvisorReadTools.cs  # NOVO subset read-only
src/OpenWebUI.Infrastructure/prompts/advisor-system.md      # NOVO — prompt do advisor como asset .md
src/OpenWebUI.Api/Runs/ChatRunExecutor.cs                   # gatilhos + injeção da nota
src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs                # PATCH advisorEnabled por chat
src/OpenWebUI.Infrastructure/Migrations/*_AdvisorEnabled*   # migration
src/OpenWebUI.Client/Services/ChatStreamService.cs          # evento advisor
src/OpenWebUI.Client/Components/RunActivityFeed.razor       # chip de conselho
src/OpenWebUI.Client/Components/ChatView.razor              # toggle por chat
src/OpenWebUI.Client/wwwroot/i18n/*.json
tests/OpenWebUI.Api.Tests/                                  # gatilhos, cadência, read-only, falha silenciosa
```

## 4. Requirements

### RF-001: Gatilhos de revisão
- **Description:** O advisor é consultado (a) após a mensagem do usuário entrar na run, (b) a cada N=5 tool calls, (c) quando a run pausa por aprovação. Cada gatilho produz no máximo um conselho.
- **Rules:** cap de 10 conselhos por run; cooldown de 30s entre chamadas; run cancelada/finalizada → advisor não roda; transcript tail truncado (últimas ~4k palavras + tool calls resumidas).

### RF-002: Execução isolada read-only
- **Description:** A chamada do advisor pode executar tools do subset read-only (`file_read`, `file_grep`, `file_glob`, `lsp_*`) via chamadas internas ao registry — ToolSession própria, sem tools mutáveis, sem gate de aprovação (read-only não muta).
- **Rules:** tool fora do subset devolve erro ao advisor; loops internos capados (máx 3 tool calls por revisão); sem acesso a secrets/env.

### RF-003: Conselho como contexto + evento
- **Description:** Conselho não-vazio vira (a) evento SSE `advisor` `{text, trigger}` no feed e (b) nota system `[advisor]` anexada ao próximo turno do pipeline (sem virar mensagem do usuário).
- **Rules:** conselho vazio/whitespace → descartado; texto capado a ~500 chars; nunca altera mensagens persistidas.

### RF-004: Falha silenciosa e custo
- **Description:** Timeout (20s), provider offline, modelo do papel ausente → run segue sem advisor, log `Debug`/`Information` sem stack de usuário.
- **Rules:** advisor nunca bloqueia o turno; exceção não propaga; desligar mid-run para na próxima fronteira.

### RF-005: Toggle e i18n
- **Description:** `Chat.AdvisorEnabled` default off; admin pode definir default e desligar globalmente; toggle na UI do chat; chip de conselho e tooltips i18n nos 8 locales; prompt do advisor em `.md` asset (prática do omp: prompts nunca inline).

**Business rules / invariants:**
- Advisor é consultivo: zero poder de aprovação, zero mutação, zero escrita em mensagens do usuário.
- Custo contido: caps de cadência/conselho/run são invariantes não-configuráveis em v1.
- Prompt do advisor vive em `prompts/advisor-system.md` — não inline em C#.

## 5. API Contract

**`PATCH /api/v1/chats/{id}`** (existente `ChatPatchRequest`) ganha `advisorEnabled: bool?`.

**SSE novo:** `event: advisor` — `{"text": "...", "trigger": "user_message|tool_burst|pause"}`.

**Expected errors:** `401`; `404` chat alheio — formatos atuais.

## 6. Acceptance Criteria

- [ ] **Dado** chat com advisor ligado **quando** envio mensagem **então** a run emite evento `advisor` (ou nenhum se o conselho for vazio) e a nota entra no turno seguinte.
- [ ] **Dado** run com ≥5 tool calls **quando** o burst completa **então** uma revisão é disparada (no máx. 1 por burst, cap 10/run).
- [ ] **Dado** modelo advisor offline **quando** um gatilho dispara **então** a run continua normal e o log registra a falha sem erro ao usuário.
- [ ] **Dado** advisor tentando `file_write` **quando** o subset é imposto **então** a tool é negada à sessão do advisor.
- [ ] **Dado** toggle desligado mid-run **quando** próximo gatilho chega **então** nenhuma revisão nova é feita.
- [ ] **Dado** papel `advisor` configurado (SPEC model-roles) **quando** revisa **então** usa o modelo do papel; sem papel → modelo da run.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Conselho gigante | 5000 chars | truncado a ~500 no evento e na nota |
| Run termina durante revisão | completar mid-call | resultado descartado; sem evento |
| Subagent/run filha | advisor no filho | herdado só se chat filho tiver flag (default off) |
| Transcript vazio | run sem mensagens | nenhum gatilho dispara |

## 7. Task Plan

- [ ] **T1 — Discovery:** ler arquivos da seção 3; mapear pontos de hook no executor (tool loop, pause).
- [ ] **T2 — Serviço:** `RunAdvisorService` (prompt, truncate, cadência, cap) + prompt asset `.md` + subset read-only.
- [ ] **T3 — Wiring:** gatilhos no `ChatRunExecutor`, nota no pipeline, evento SSE.
- [ ] **T4 — Contratos+migration:** `Chat.AdvisorEnabled`, `RunAdvisorEvent`, PATCH chat, admin default.
- [ ] **T5 — Client:** evento no ChatStreamService, chip no RunActivityFeed, toggle no chat, i18n.
- [ ] **T6 — Testes:** gatilho/cadência/cap, read-only imposto, falha silenciosa, conselho→contexto.
- [ ] **T7 — Validation + DoD + PR.**

**7.1 Validation:** NUnit unit (cadência, subset, truncate) + integration (SSE advisor no stream); ≥80%.

## 8. Organization Guardrails

- Branch `feature/Devin-20261013-run-advisor`.
- Advisor nunca muta workspace, mensagens ou aprovações — invariante de segurança.
- Prompt como asset `.md` (alinha prática omp; nosso padrão atual é inline — SPEC introduz o asset e CLAUDE.md pode registrar a convenção).
- Custo: caps fixos em v1; métricas de uso do advisor fora de escopo.

## 9. Definition of Done

- [ ] RF-001..RF-005 implementados/testados.
- [ ] Zero bloqueio da run pelo advisor provado por teste (timeout/offline).
- [ ] build/test/drift guards verdes; migration aplica em base legada.
- [ ] Depende de model-roles: se merged sem ela, fallback documentado (modelo da run).

**Next action after DoD:** `Status = Done` + PR.

## Open Questions / Pending Ambiguity

- Conselho visível também como bolha no transcript ou só chip de atividade? Palpite: chip de atividade (não polui o chat).
- Advisor em runs filhas (delegate): herdar flag do chat filho (default off) — confirmar.

## Delivered

| Item | Link |
| --- | --- |
| Commit | https://github.com/afonsoft/open-webui/commit/9e88803e7 |
| Issue | https://github.com/afonsoft/open-webui/issues/352 |
