# SPEC-20261015-plan-spec-sdd

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `plan-spec-sdd` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/open-webui` |
| Branch | `feature/Devin-20261015-plan-spec-sdd` |
| Ticket | [#381](https://github.com/afonsoft/open-webui/issues/381) |
| Status | `Completed` |

## 1. User Story

**As a** usuário do chat
**I want** que o modo plan e o comando `/plan` analisem o requisito e produzam uma SPEC SDD detalhada em markdown
**So that** o plano exibido tenha estrutura suficiente para outro agente implementar a partir do documento.

**Problem context:**
O modo plan atual (`SPEC-20261009-agent-modes-plan-build`) instrui o modelo a "apresentar o plano em markdown" sem contrato de estrutura — a saída varia de checklist superficial a prosa. `plan_exit` aceita qualquer markdown não-vazio. Não existe slash command `/plan`; o usuário precisa descobrir o `AgentModeToggle`.

## 2. Scope

**In scope:**
- Contrato SPEC SDD canônico (template condensado) injetado no system prompt do modo plan.
- Validação estrutural do plano submetido a `plan_exit` (seções obrigatórias + `RF-\d`); recusa `Refused` com orientação de correção.
- Slash command built-in `/plan` no composer: selecionar/enviar ativa o modo plan e usa o restante do texto como requisito.
- i18n do badge/título do comando built-in nos 8 locales.
- Testes: validação, tool, merge de suggestions, E2E plan→build.

**Out of scope:**
- Mudança no ciclo de aprovação plan→build (já existe).
- Persistência fora de `.openwebui/plans/` ou upload da SPEC para o servidor.
- Formatação especial de renderização do markdown no chat (o renderer atual já exibe markdown).

## 3. Technical Context

**Where the change happens:**
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs` — bloco "Modo PLAN" (3.6) injeta a instrução; passa a concatenar o contrato SDD.
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/PlanExitBuiltinTool.cs` — valida o plano antes de gravar.
- Novo `src/OpenWebUI.Infrastructure/ChatTools/PlanSpecTemplate.cs` — template + `Validate`.
- `src/OpenWebUI.Client/Services/RepoSlashCommands.cs` — `Builtins` + parâmetro no `Merge`.
- `src/OpenWebUI.Client/Components/ChatView.razor` — `SelectPrompt` (source `builtin`), `SendAsync` (intercepta `/plan` digitado), badge `chat.src_builtin`.

**Files to read before implementing:**
- `tests/OpenWebUI.Api.Tests/AgentModeE2ETests.cs` (payload plan_exit mínimo — precisa virar SDD válido)
- `tests/OpenWebUI.Api.Tests/ChatToolsEdgeTests.cs` (harness `Ctx()`/`Args()` para builtin tools)
- `tests/OpenWebUI.Client.Tests/RepoSlashCommandsTests.cs`

## 4. Requirements

### RF-001: Contrato SPEC SDD no prompt do modo plan
- **Description:** em modo plan, o system prompt deve exigir análise do requisito e saída seguindo o template SDD (seções 0–9 + Open Questions), com requisitos `RF-\d`, critérios BDD e plano de tarefas ordenado.
- **Rules:** modo plan permanece read-only; ambiguidades bloqueantes vão para "Open Questions"; nunca começar implementação sem `Status = Approved`.
- **Input → Output:** modo `plan` + requisito do usuário → mensagem em markdown SDD completo + call `builtin_plan_exit`.

### RF-002: Validação estrutural em `plan_exit`
- **Description:** `plan_exit` recusa (`Refused`) plano sem as seções obrigatórias (`Metadata`, `User Story`, `Scope`, `Requirements`, `Acceptance Criteria`, `Task Plan`) ou sem identificador `RF-\d`, devolvendo a lista do que falta para o modelo corrigir.
- **Rules:** matching case-insensitive das seções; plano vazio continua recusado; header `# SPEC-` próprio não é duplicado pelo wrapper de título.
- **Input → Output:** `{plan, title}` → arquivo `.openwebui/plans/{runId}.md` ou `Refused` com causa.

### RF-003: Slash command built-in `/plan`
- **Description:** `/plan` aparece nas sugestões do composer com badge `builtin`; selecionado ou enviado diretamente, ativa o modo plan (`SetAgentModeAsync`) e o texto restante vira o requisito.
- **Rules:** `/plan` sem args só alterna o modo; `/plan <requisito>` envia o requisito já em modo plan; funciona em chat novo (intent persistida no localStorage) e carregado (PATCH).
- **Input → Output:** `/plan foo` → `chat.Mode = "plan"` + mensagem `foo` enfileirada.

### RF-004: i18n dos elementos novos
- **Description:** chaves `chat.src_builtin` e `chat.cmd_plan` presentes e coerentes nos 8 locales.

## 5. API Contract (if applicable)

Sem endpoints novos — reutiliza `PATCH /api/v1/chats/{id}` (`Mode`) e o fluxo de runs existente.

## 6. Acceptance Criteria

- [ ] **Given** chat em modo plan **when** o modelo monta a resposta **then** o system prompt contém o contrato SDD (seções + RF + BDD).
- [ ] **Given** `plan_exit` com plano sem `RF-` **when** executado **then** retorna `Refused` listando o que falta e nada é gravado.
- [ ] **Given** `plan_exit` com SPEC SDD completo **when** executado **then** grava `.openwebui/plans/{runId}.md` sem duplicar o heading `# SPEC-`.
- [ ] **Given** o composer **when** usuário digita `/pl` **then** `/plan` aparece nas sugestões com badge `builtin`.
- [ ] **Given** `/plan adicione login` enviado **when** processado **then** o chat fica em modo plan e a mensagem enviada é `adicione login`.
- [ ] **Given** plano SDD aprovado **when** o usuário confirma "Executar este plano?" **then** o chat promove a `build` (fluxo existente preservado).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| `/plan` sozinho | sem args | só alterna o modo; nenhuma mensagem enviada |
| plano com seções em caixa baixa | `## user story` | aceito (match case-insensitive) |
| plano vazio/whitespace | `"  "` | `Refused` "informe o plano completo" |
| plano rico sem `RF-` | prosa detalhada | `Refused` pedindo requisitos numerados |

## 7. Task Plan (agent execution)

- [x] **T1 — Discovery:** ler pipeline plan, plan_exit, RepoSlashCommands, SelectPrompt/SendAsync, testes.
- [x] **T2 — Implementation:** PlanSpecTemplate + validação no plan_exit + prompt no pipeline + `/plan` built-in + i18n.
- [ ] **T3 — Verification:** ajustar `AgentModeE2ETests` (payload SDD), novos testes de validação/merge, rodar suites tocadas.
- [ ] **T4 — Validation:** `dotnet build` + `dotnet test` + drift guards (i18n).
- [ ] **T5 — Done + PR:** Status = Done, PR para `main`.

## 8. Organization Guardrails

- Branch `feature/Devin-20261015-plan-spec-sdd` — nunca commit em `main`.
- `.github/workflows/` intocado.
- Sem secrets/`.env`/bancos commitados.
- Modo plan continua somente-leitura — nenhuma tool de escrita anunciada.

## 9. Definition of Done

- [ ] RF-001..RF-004 implementados.
- [ ] Critérios da seção 6 cobertos por testes.
- [ ] Build + suites verdes; paridade i18n.
- [ ] Sem PII/tokens em logs.

## Open Questions / Pending Ambiguity

- Título das SPECs geradas: o heading `# SPEC-{date}-{slug}` é responsabilidade do modelo (instruído pelo template); `plan_exit` não reescreve.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/380 |
