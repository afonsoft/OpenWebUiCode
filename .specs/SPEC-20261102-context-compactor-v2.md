# SPEC-20261102-context-compactor-v2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `context-compactor-v2` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/Devin-20261102-context-compactor-v2` |
| Ticket | `#634` |
| Status | `Approved` |

## 1. User Story

**As a** operador de runs longas do agente
**I want** compactação antecipada em background e preservação fiel (pedidos verbatim do usuário, tarefas, breadcrumbs pesquisáveis)
**So that** o turno nunca pague o resumo síncrono no limiar, e o contexto compactado não perca o que o usuário realmente pediu nem o estado do plano.

**Problem context:**
A REA do Devin CLI (`docs/DEVIN-CLI-REA-ANALYSIS-V2.md` §2) mostrou uma
máquina de compactação 3-tier: `spawn` dispara o resumo **em background** a
~70% da janela, `apply` o aplica quando pronto, `hard` bloqueia só no
teto. O nosso `ContextCompactionService` compacta **sincronamente** num
limiar único (`compaction:threshold_tokens`, default 224k) — o turno paga
a chamada do summarizer — e o resumo não preserva verbatim as mensagens do
usuário, não carrega o estado do `builtin_todo_write` (que hoje só vive em
evento SSE, não persistido) e não ensina o modelo a citar `history://`
como âncora. Itens já cobertos e fora desta SPEC: `builtin:extend_context`
(orçamento com teto, SPEC-20261031-agent-prompt-pack RF-004), `new_context`
(compactação pedida pelo agente), `StallWatchdog`, poda mecânica
(`ContextPruner`), `history://` (`HistoryResourceService`).

## 2. Scope

**In scope:**
- **3-tier thresholds**: `spawn` (ratio do hard), `apply` (fronteira de rodada), `hard` (espera limitada do resumo em voo) — com overrides por modelo.
- **Summarizer-v2**: prompt com seções Overview / Key Details & Breadcrumbs / Current State, exclusão explícita de rules, breadcrumbs citando `history://{chatId}/msg:{id}` ou `[pos]`; transporte JSON estendido.
- **`user_message_records`**: extração mecânica (determinística) das mensagens `role=user` do trecho compactado, injetadas verbatim.
- **Todo preservation**: último payload `tasks` de `builtin_todo_write` no trecho compactado injetado como seção própria.
- **`<context_usage>` por rodada**: nota de sistema com tokens estimados da janela.
- Telemetria dos estágios (spawn/apply/hard-wait/falha).

**Out of scope:**
- Blind-call de tool deferida ("schema loaded — re-issue") — SPEC separada pequena.
- `continue-on-output-truncation`, injeção de `<user_actions>` diffs — SPECs separadas.
- `memory_guard` cgroup, attribution check.
- Mudanças no `INativeContextCompactor` (caminho nativo inalterado — v2 cobre o summarizer interno).
- Mudanças na poda mecânica (`ContextPruner`), em `history://` ou na gramática de boundary (`ContextBoundaryService`).
- Nova UI; nova API externa.

## 3. Technical Context

**Where the change happens:**
`ContextCompactionService` (Infrastructure) ganha scheduler de resumo em
background single-flight por chat + thresholds 3-tier; o bloco injetado
(`FormatSummary`/`ApplyAsync`) passa a renderizar seções novas (records,
tarefas, breadcrumbs). `ChatRunExecutor` consulta o scheduler nas mesmas
fronteiras de hoje (linhas ~622–633 início da run, ~1032 overflow de
contexto) e injeta a nota `<context_usage>` na janela por rodada.
`LlmContextSummarizer` recebe o prompt v2 e o parser tolerante ganha os
campos `state` e `breadcrumbs`. `CompactionState` (kv `chat:{id}:compaction`)
ganha campos opcionais — leitura de estado antigo sem eles continua válida.

**Files to read before implementing:**
- `CLAUDE.md` · `.claude/rules/global-rules.md`
- `src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs`
- `src/OpenWebUI.Infrastructure/Services/ContextPruner.cs`
- `src/OpenWebUI.Infrastructure/Services/ContextTokens.cs`
- `src/OpenWebUI.Infrastructure/Services/HistoryResourceService.cs`
- `src/OpenWebUI.Infrastructure/Context/ExtendContextBudget.cs`
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` (linhas ~600–640, ~1018–1044, ~1489–1530)
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/TodoWriteBuiltinTool.cs`
- `docs/DEVIN-CLI-REA-ANALYSIS-V2.md` §2 (evidência)
- `tests/OpenWebUI.Api.Tests/` (padrões de fixture de compaction existentes)

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs      # 3-tier + scheduler + records/todo + FormatSummary seções
src/OpenWebUI.Infrastructure/Context/AsyncCompactionScheduler.cs       # NOVO — single-flight por chat, cancel-aware
src/OpenWebUI.Infrastructure/Services/ContextTokens.cs                 # Estimate já existe — reusar p/ context_usage
src/OpenWebUI.Api/Runs/ChatRunExecutor.cs                              # wiring apply/hard + injeção <context_usage>
tests/OpenWebUI.Api.Tests/ContextCompactionV2Tests.cs                  # NOVO — suíte da SPEC
```

## 4. Requirements

### RF-001: Thresholds 3-tier
- **Description:** spawn/apply/hard por chat. `hard` = `compaction:threshold_tokens` (hoje, clamp 32k–256k). `spawn` = `round(hard × compaction:spawn_ratio)`, ratio default `0.70`, clamp `0.30–0.95`, inválido → default. `apply` = qualquer fronteira de rodada com resumo pronto.
- **Rules:** `spawn < hard` sempre; `spawn` nunca abaixo de 32k tokens efetivos; ratio fora do clamp cai no default com log uma vez por chat.
- **Input → Output:** estimativa de tokens da janela → decisão `none|spawn|apply|hard_wait|sync`.

### RF-002: Spawn assíncrono single-flight
- **Description:** estimativa ≥ spawn e < hard → dispara `AsyncCompactionScheduler` (Task em background) que roda `IContextSummarizer` com o transcript do head podado (mesma construção de `BuildTranscriptAsync`). Uma task por chat; segundo spawn enquanto corre → no-op.
- **Rules:** task respeita CancellationToken da aplicação (não da run — sobrevive ao fim da run para o apply posterior); falha → log + descarte (a run segue; hard cairá no caminho síncrono); conclusão grava em estado pendente `chat:{id}:compaction:pending` (não no estado aplicado).
- **Input → Output:** `(chatId, history, model, userId)` → task em voo registrada; resumo pronto persistido como pendente.

### RF-003: Apply na fronteira
- **Description:** nas fronteiras existentes (início da run e pós-overflow), se há resumo pendente para o chat → move para o estado aplicado (`CompactionState` kv) e `ApplyAsync` já devolve `[resumo]+tail` no mesmo fluxo atual.
- **Rules:** apply só se o pendente cobrir `cutoff ≥` marcador atual; resumo pendente de chat errado nunca aplica; apply publica fase `compacting` (já existe) + evento `compaction.applied` com `spawned_at`, `applied_at`.

### RF-004: Hard cap com espera limitada
- **Description:** estimativa ≥ hard → se task em voo, aguarda até `compaction:hard_wait_ms` (default 45_000, clamp 5_000–120_000) pela conclusão e aplica; se expirar ou não houver task → caminho síncrono atual (`MaybeCompactAsync`), inalterado.
- **Rules:** a espera emite `compaction.hard_wait` com `waited_ms`; timeout nunca falha a run — cai no síncrono; `extend_context` consumido continua pulando a compactação de emergência (interação existente preservada).
- **Input → Output:** estouro do hard → resumo aplicado via task (preferido) ou síncrono (fallback).

### RF-005: Summarizer-v2 (prompt + parse)
- **Description:** novo prompt: responda JSON `{"summary": "<## Overview…1-2 frases>", "state": "<Current State — tarefa imediata, próximos passos, bloqueios>", "breadcrumbs": ["history://{chatId}/msg:{id}" | "[pos] termo"], "files": [...], "decisions": [...], "pending": [...]}`. Instrução explícita: **não recitar rules/instruções verbatim** (serão re-injetadas); breadcrumbs = âncoras de busca para recuperar detalhe. Parser tolerante: campos novos opcionais; falha de parse → prosa em `Text` (comportamento atual preservado).
- **Rules:** `breadcrumbs` limitado a 20 itens; ids citados devem existir no head (sanity: descarta ids fora do range — best-effort); `state` vazio → omitido na injeção.
- **Input → Output:** transcript podado + resumo anterior → `ContextSummary` estendido.

### RF-006: `user_message_records` mecânico
- **Description:** na compactação (sync ou via task), extrai do trecho compactado as últimas `compaction:user_records` mensagens `role=user` (default 20, teto `compaction:user_records_chars` = 4_000), verbatim, ordem cronológica; corta no início com marcador `[…{n} anteriores omitidas]` quando estoura.
- **Rules:** extração determinística — não passa pelo LLM; ignora mensagens sintéticas (prefixos `SummaryPrefix`, `TurnPrefixHeader`, notas de sistema); injeta como seção `Pedidos do usuário (verbatim):` no bloco.
- **Input → Output:** head compactado → `IReadOnlyList<string>` persistida em `CompactionState.UserMessages`.

### RF-007: Preservação de tarefas (todo)
- **Description:** varre o trecho compactado pelo último tool-result de `builtin_todo_write` contendo array `tasks` → injeta como seção `Tarefas:` (JSON verbatim das tasks, teto 4k chars); ausência → omite a seção.
- **Rules:** última ocorrência vence; result podado pelo `ContextPruner` não serve — a extração roda no head **pré-poda**; falha de parse → omite (nunca falha a run).
- **Input → Output:** head compactado → `string? TodosJson` em `CompactionState`.

### RF-008: Injeção `<context_usage>` por rodada
- **Description:** quando `compaction:context_usage` (default true), a janela enviada ao modelo ganha uma nota de sistema final: `<context_usage>\n{n}k tokens used. Ignore this: at the limit your context is automatically summarized and you keep working with your memory of the task intact; only the full outputs of earlier tool calls are hidden. Never tell the user.\n</context_usage>` com `n` = `ContextTokens.Estimate` da janela pós-poda ÷ 1000.
- **Rules:** injetada depois da poda (mede a janela real); flag false → não injeta; playground/run sem chat persistido → não injeta.
- **Input → Output:** janela final → janela + nota de orçamento.

### RF-009: Estado persistido compatível
- **Description:** `CompactionState` ganha `UserMessages`, `Todos`, `Breadcrumbs`, `State` (todos nullable/omit-on-null no JSON do kv). Estado antigo sem os campos continua desserializando; `FormatSummary` renderiza seções novas só quando presentes; resumo pendente vive em kv separada `chat:{id}:compaction:pending` com `SpawnedAt` e `Cutoff`.
- **Rules:** sem migração de dados; `compaction` marker message no chat mantém o texto do resumo (comportamento atual).

### RF-010: Telemetria e observabilidade
- **Description:** eventos SSE/nota: `compaction.spawned` `{cutoff, tokens}`, `compaction.applied` `{cutoff, waited_ms?}`, `compaction.hard_wait` `{waited_ms, timed_out}`, `compaction.failed` `{stage}`. Fases `preparing_context`/`compacting` existentes reutilizadas.
- **Rules:** logs Warning apenas em falha; sem PII/conteúdo de mensagens nos eventos (só contagens e ids de chat/run).

**Business rules / invariants:**
- Falha do background **nunca** derruba ou atrasa a run além de `hard_wait_ms`.
- Par `tool_call`/`tool_result` nunca parte (garantia existente de `SplitIndex`/`ComputeTurnSplit` preservada).
- Resumo nunca inventa estrutura: parse falhou → prosa; extrações mecânicas são verbatim.
- `extend_context` e `new_context` continuam funcionais nos mesmos caminhos.
- Compactação de um chat nunca escreve estado de outro chat (chaves por `chatId`).

## 5. API Contract

N/A — sem endpoints novos nem alteração de contrato externo. Configs novas:
`compaction:spawn_ratio` (double, 0.70), `compaction:hard_wait_ms` (int, 45000),
`compaction:user_records` (int, 20), `compaction:user_records_chars` (int, 4000),
`compaction:context_usage` (bool, true), e overrides por modelo
`compaction:{model}:threshold_tokens` / `compaction:{model}:spawn_ratio`.

## 6. Acceptance Criteria

- [ ] **Given** histórico com estimativa ≥70% do hard e <hard **when** a run chega à fronteira **then** dispara spawn em background e a janela segue inalterada neste turno (sem esperar o summarizer).
- [ ] **Given** resumo pendente gravado pela task **when** a próxima fronteira chega **then** a janela sai `[resumo+records+tarefas]+tail` e `compaction.applied` é emitido.
- [ ] **Given** estimativa ≥hard com task em voo **when** estoura **then** aguarda até `hard_wait_ms`, aplica o resumo da task e emite `compaction.hard_wait` com `timed_out=false`.
- [ ] **Given** estimativa ≥hard sem task (ou task falhou/expirou) **when** estoura **then** cai no caminho síncrono atual e a run continua.
- [ ] **Given** head com mensagens do usuário "faça X sem Y" **when** compacta **then** o bloco injetado contém `Pedidos do usuário (verbatim):` com "faça X sem Y".
- [ ] **Given** head com `builtin_todo_write` result `{"tasks":[a,b]}` **when** compacta **then** bloco contém `Tarefas:` com as 2 tasks.
- [ ] **Given** summarizer respondendo prosa pura **when** compacta **then** resumo injetado normalmente (degradação tolerante).
- [ ] **Given** `compaction:context_usage` true **when** a janela sai **then** contém `<context_usage>` com estimativa em k; flag false **then** ausente.
- [ ] **Given** `compaction:gpt-4o:threshold_tokens=128000` e global 224k **when** run em `gpt-4o` **then** hard efetivo é 128k; em outro modelo, 224k.
- [ ] **Given** dois spawns concorrentes para o mesmo chat **when** disparam **then** apenas uma task corre (single-flight).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| `spawn_ratio` inválido | `compaction:spawn_ratio="abc"` ou 1.5 | default 0.70 + log |
| `spawn ≥ hard` | ratio 0.95 num hard 32k (floor) | spawn efetivo `hard-1` garantindo `spawn<hard` |
| Task em voo + chat revert/branch | resumo de cutoff antigo | apply só se `cutoff ≥` estado atual; senão descarta |
| `hard_wait_ms` estourado | task lenta | síncrono assume; task órfã completa em background e grava pendente (pode aplicar depois se válida) |
| Head sem `role=user` | conversa só de sistema/assistant | seção de records omitida |
| Mensagem sintética do usuário | nota `<system_note>`/`TurnPrefixHeader` | não entra nos records |
| Todo podado | tool result elidido por `ContextPruner` | extração usa head pré-poda → tasks presentes |
| `context_usage` em playground | run sem `chatId` | não injeta |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** reler `ContextCompactionService`, `ChatRunExecutor` (fronteiras), `ExtendContextBudget`, `TodoWriteBuiltinTool`, fixtures de teste de compaction; mapear pontos de chamada exatos.
- [ ] **T2 — Thresholds + scheduler:** `AsyncCompactionScheduler` (single-flight, pending kv), `Thresholds` 3-tier + overrides por modelo; testes RED→GREEN.
- [ ] **T3 — Summarizer-v2:** prompt novo + `ContextSummary` estendido (`State`, `Breadcrumbs`) + parser tolerante + sanity de ids; testes.
- [ ] **T4 — Records + todo:** extração mecânica no trecho compactado (pré-poda p/ todo) + `CompactionState` campos + `FormatSummary` seções; testes.
- [ ] **T5 — `<context_usage>`:** injeção pós-poda na janela + flag; testes.
- [ ] **T6 — Executor wiring:** consulta spawn/apply nas fronteiras + hard-wait bounded + eventos; testes de integração.
- [ ] **T7 — Validação:** `dotnet test` suíte afetada + build 0 warnings + drift guards se `.razor`/i18n tocados (não previsto).
- [ ] **T8 — QA + PR:** gate qa-analyst, Issue → in_review/in_pullrequest, PR `closes #TICKET`.

**7.1 Validation strategy:** .NET — unit tests para regras (thresholds, parser, extrações), integration tests para spawn→apply e hard-wait com summarizer fake; cobertura ≥80% nos arquivos novos/alterados; suíte completa verde.

## 8. Organization Guardrails

- **Branches:** nunca commitar em `main` — `feature/Devin-20261102-context-compactor-v2`.
- **Workflows:** não modificar `.github/workflows/`.
- **Security:** sem PII/tokens em logs/eventos; resumo/transcript nunca em log (só contagens).
- **Scope:** sem features fora da lista; ambiguidade → parar e perguntar.
- **Architecture:** lógica em Infrastructure; executor só orquestra nas fronteiras; contratos públicos com XML doc.
- **Paridade:** comportamento default novo (spawn async) ativo por padrão; flags permitem desligar `<context_usage>` e ajustar ratio/hard_wait sem deploy.

## 9. Definition of Done

- [ ] Todos os RFs implementados.
- [ ] Todos os acceptance criteria cobertos por testes passando.
- [ ] Edge cases tratados.
- [ ] `dotnet build` 0 warnings + `dotnet test` verde + cobertura dos arquivos novos ≥80%.
- [ ] Guardrails respeitados; logs sem PII.
- [ ] `docs/DEVIN-CLI-REA-ANALYSIS-V2.md` gap table marcada como entregue (itens 1,4,5,6,7 + context_usage).

**Next action after DoD:** `Status = Done` + PR `closes #TICKET`.
