# SPEC-20261007-chat-agent-ux

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-agent-ux` (série chat-harness, rodada 2 — Devin Web × OpenHands × opencode TUI) |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `devin/1791375000-chat-tool-ux` |
| Ticket | `GAP-chat-agent-ux` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** ver o que cada tool fez de forma legível (comando, saída, URL, código), decidir aprovações com contexto e corrigir o agente sem negar cegamente, e acompanhar o estado da run num painel lateral
**So that** o chat funcione como um workbench de agente (estilo Devin Web / OpenHands / opencode TUI) e não só uma conversa.

**Problem context:**
Referências analisadas: Devin Web (chat + workspace com tabs Shell/IDE/Browser/Files/Preview), OpenHands (painel TaskList/Planner/Changes/Terminal/VSCode, máquina de estados visível, security analyzer LOW/MED/HIGH), opencode TUI (`packages/tui`: sidebar com Context/MCP/Todo/Modified Files, renderers por tool, permissão em 3 estágios com reject-com-mensagem, question prompt estruturado). Nosso chat já tem runs desacopladas (P1), notificações (P2), tool streaming + gate (P3), builtin tools (P4a) e PTY (P4b) — mas os cards de tool são genéricos (JSON truncado), a negação não permite instruir o modelo, e não há painel de contexto da sessão. Execução no **host** (sandbox = fase posterior, decisão do dono).

## 2. Scope

**In scope (esta rodada):**

- **P5 — Tool cards ricos:** renderer por tool builtin no `ToolCallCard`:
  - `shell_exec` → linha `$ {command}` + saída colapsada (~10 linhas) com expand;
  - `code_interpreter` → bloco de código + saída;
  - `fetch_url`/`web_search` → URL/query no cabeçalho do card;
  - `job_list`/`job_output`/`job_kill` → id do job + resumo;
  - `generate_image` → prompt + imagem inline (já existe).
  Resumo extraído do `argsPreview` de forma tolerante (JSON pode vir truncado).
- **P6 — Aprovação v2:** `PermissionPromptCard` ganha terceira opção **"Negar com instrução"** — textarea opcional cujo texto vira o resultado da tool (`Erro: execução negada pelo usuário: {msg}`), fazendo o modelo corrigir a rota (padrão opencode `RejectPrompt`). Corpo rico por tool no prompt: `shell_exec` mostra o comando, `fetch_url` a URL, `code_interpreter` o código. `RunApprovalDecisionRequest` ganha `Message` opcional.
- **P7 — Painel lateral da run** (desktop ≥lg; vira drawer no mobile): estado da run (generating/running_tool/awaiting_approval), MCPs conectados com dot, jobs ativos (`GET /api/v1/jobs`), preset de aprovação do chat.
- **P8 — Tool `ask_user`:** builtin que pausa a run (mesma seam do approval) com perguntas estruturadas (opções + resposta livre); resposta volta como resultado da tool.
- **P9 — Composer:** `↑` histórico de prompts por chat, rascunho persistido em `localStorage` por chat, palette `/` básica.

**Out of scope:**
- Sandbox isolado (decisão: host; fase posterior).
- Tokens/custo por mensagem (providers não devolvem usage consistente).
- Diff de arquivos / Modified Files — depende de tools de edição de arquivo (avaliar junto com `file_edit` em fase posterior).
- Subagente navegável (`task` do opencode) — exige delegação dentro do chat.
- `browser_use` — precisa de browser headless no host.

## 3. Technical Context

**Where the change happens:**
`Client` (`ToolCallCard`, `PermissionPromptCard`, `ChatView`, novo `RunContextPanel`), `Api` (`ChatRunEndpoints` — `Message` no decision request), `Runs` (`ChatRunApprovals` resolve carrega a mensagem), `Completions` (`ChatPipeline` — `GateAsync` retorna decisão+mensagem).

**Files to read before implementing:**
- `src/OpenWebUI.Client/Components/{ToolCallCard,PermissionPromptCard,ChatView}.razor`
- `src/OpenWebUI.Api/Runs/{ChatRunApprovals,ChatRunExecutor}.cs`
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs` (ToolLoopCallbacks.GateAsync)
- Referências: `repos/opencode/packages/tui/src/routes/session/{permission,question,index}.tsx`, `feature-plugins/sidebar/*`

## 4. Requirements

### RF-001: Resumo por tool no card
- `ToolCallCard` extrai do `argsPreview` (JSON tolerante a truncamento: `JsonDocument` com fallback regex `"key"\s*:\s*"..."`) um resumo de uma linha por tool conhecida, exibido no cabeçalho após o nome.
- `shell_exec` renderiza `$ comando` e a saída com colapso em ~10 linhas (expand on click); `generate_image` segue inline; demais builtins mostram o campo-chave (url/query/code/prompt/jobId).

### RF-002: Negar com instrução
- `POST .../runs/{id}/approvals/{callId}` aceita `message` (opcional, ≤2KB, scrubbed).
- Decisão `deny` com `message` injeta `Erro: execução negada pelo usuário: {message}` como resultado da tool; sem `message` mantém o texto atual.
- `remember` só se aplica a `approve` (inalterado).

### RF-003: Corpo rico no prompt de aprovação
- `PermissionPromptCard` mostra o campo-chave da tool (comando/URL/código) extraído do `argsPreview`, além do JSON truncado.
- Estado de UI: `deny` abre o campo de instrução (esc/cancelar volta).

### RF-004: Painel de contexto da run
- Painel lateral no chat (≥lg) com: fase atual da run, lista de MCPs com status, jobs em execução (poll leve enquanto run ativa), preset vigente.
- Mobile: mesmo conteúdo num bottom-sheet/drawer.

### RF-005: ask_user
- Tool builtin `ask_user` (sempre `RequiresApproval`-like: pausa a run) com perguntas `{question, options[], multiple?}`; UI renderiza `QuestionPromptCard`; resposta volta como tool result. Timeout 5min → "sem resposta".

### RF-006: Composer
- `↑` no composer vazio navega histórico de prompts do chat (localStorage); rascunho salvo por chat e restaurado ao trocar; `/` abre palette mínima (new chat, stop).

## 5. API Contract

**Endpoint:** `POST /api/v1/chats/{id}/runs/{runId}/approvals/{callId}`
**Auth:** `Bearer` (dono do chat)

**Request:** `{decision: "approve"|"deny", remember?: bool, message?: string}`
**Response:** `200 {status:true}` · `404` call não pendente · `410` run finalizada

## 6. Critérios de Aceite

- [ ] `shell_exec` mostra `$ comando` + saída colapsada expansível no transcript.
- [ ] Negar com instrução entrega o texto ao modelo (visível na resposta seguinte).
- [ ] Painel lateral mostra fase da run + MCPs + jobs; funciona como drawer no mobile.
- [ ] `ask_user` pausa a run, renderiza opções, resposta alimenta o loop.
- [ ] Histórico `↑` e rascunho por chat funcionam ao trocar de conversa.
- [ ] i18n nos 8 locales; testes cobrindo decision-com-message, ask_user e painel.

## Reconciliation

_Reconciliado em 2026-10-08 (SPEC-20261008-spec-status-reconciliation, Issue #203)._

| RF | Veredito | Evidência |
| --- | --- | --- |
| RF-001 Resumo por tool | delivered | `Client/Services/ToolArgsPreview.cs` — resumo de 1 linha por tool (JSON tolerante a truncamento); `ToolCallCard` exibe `shell_exec` como `$ comando` com colapso ~10 linhas |
| RF-002 Negar com instrução | delivered | `Api/Runs/ChatRunApprovals.cs:83,97` — `message` opcional (≤2KB, scrubbed); deny com mensagem injeta `Erro: execução negada pelo usuário: {message}` como tool_result |
| RF-003 Corpo rico no prompt | delivered | `PermissionPromptCard.razor` — campo-chave extraído do `argsPreview` + JSON truncado; `deny` abre campo de instrução |
| RF-004 Painel de contexto | delivered | `ChatWorkspacePanel.razor` — fase da run, MCPs com status, jobs (poll leve), preset vigente; bottom-sheet/drawer no mobile |
| RF-005 ask_user | delivered | `Tools/AskUserBuiltinTool.cs` + `QuestionPromptCard.razor` (`ChatView.razor:216,1775`) — perguntas `{question, options[], multiple?}`, resposta volta como tool result |
| RF-006 Composer | delivered | `ChatView.razor:1028-1039` `↑` navega histórico por chat; `:1146` `/` abre prompt suggestions (palette de comandos) |

