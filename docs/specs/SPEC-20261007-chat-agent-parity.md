# SPEC-20261007-chat-agent-parity

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-agent-parity` (série chat-harness, rodada 3 — tools & autonomia) |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `devin/1791380000-chat-agent-parity` |
| Ticket | `GAP-chat-agent-parity` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** que o agente tenha tools de workspace de verdade (todo, arquivos, perguntas, subagentes, browser) e um painel de contexto mostrando o trabalho em andamento
**So that** o chat alcance a autonomia de Devin Web / OpenHands / opencode — executando no host, com approvals por risco como fronteira de segurança.

**Problem context:**
Matriz de tools comparada entre 4 referências:

| Capability | Devin Web | OpenHands | opencode TUI | Open-WebUI (hoje) |
| --- | --- | --- | --- | --- |
| Shell/exec | Shell tab | `terminal` | `shell` | `shell_exec` + `job_*` ✅ |
| Arquivos | Files tab + IDE | `file_editor` | `edit`/`write`/`read`/`grep`/`glob` | ❌ só `fetch_url`/`web_search` |
| Todo/plano | interno | `task_tracker` + TaskList tab | `todo` + sidebar | ❌ sem tool de todo |
| Pergunta ao usuário | texto | — | `question` (opções+livre) | ❌ |
| Subagente | child sessions | `delegate` | `task` | ❌ (ChatJob é base) |
| Browser | Browser/Computer | `browser_use` + tab | — | ❌ |
| Imagem | — | — | — | `generate_image` ✅ |
| Code exec | — | — | `python` via shell | `code_interpreter` ✅ |
| Workspace panel | tabs direita | Agent Canvas | sidebar | ❌ (P7 é só status) |
| Risk approvals | — | security analyzer LOW/MED/HIGH | allow/always/reject-msg | preset por conversa (P3) ✅ parcial |
| Pause/resume | wake/sleep | AgentState machine | — | ❌ só stop |

Execução no **host** (sandbox = fase posterior, decisão do dono) — a fronteira de segurança é o gate de aprovação + jail do workdir.

## 2. Scope

**In scope:**

- **P10 — `todo_write` + aba Tasks:** tool builtin que persiste lista de tarefas da run (`{todos: [{content, status, priority?}]}`); estado no `ChatRun.Meta` ou tabela própria; renderiza como checklist no painel lateral (P7 estendido) e chip "N/M tarefas" no header durante a run.
- **P11 — File tools no workspace:** `file_read`, `file_write`, `file_edit` (search/replace ou unified diff), `grep`, `glob`, `ls` — todos confinados ao workdir do usuário (`WorkdirService`); escrita/edição passam pelo gate e o card mostra **diff real** (linhas +/-) em vez de JSON.
- **P12 — `ask_user`:** já no SPEC-agent-ux (P8) — confirmar implementação aqui ou lá.
- **P13 — Risk-tiered approvals:** classificador estático por chamada (LOW: read-only/fs-safe → auto; MED: escrita em workdir, fetch → notice; HIGH: shell destrutivo, rede, fora do jail → ask) + preset `auto` no chat. Sem LLM-judge.
- **P14 — Pause/resume + state chip:** `POST /runs/{id}/pause` e `/resume` na seam do dispatcher; chip de estado no header (generating · running_tool · awaiting_approval · paused · interrupted).
- **P15 — Workspace panel completo:** estende o painel lateral (P7) com tabs: **Tasks** (todo), **Changes** (diff unificado dos file_* da run), **Jobs**, **MCPs**, **Info** (preset, modelo, workdir). Drawer no mobile.
- **P16 — `delegate`:** tool `delegate_task` cria ChatJob (ou sub-run) com prompt próprio + contexto isolado; card no transcript vira link pro run filho; resultado agrega quando o filho termina (poll ou callback).
- **P17 — `browser_use` fase 1 (screenshot):** tool `browser_screenshot {url}` via headless Chromium (Playwright ou Chrome CLI); resultado renderiza a imagem no transcript. Interativo (click/type) fica para depois.
- **P18 — Git bar (quando workdir é repo):** chips branch/diff-count no header; `GET /runs/{id}/diff` retorna unified diff do workdir vs base (se git disponível); aba Changes do P15 consome isso.
- **P19 — `generate_video`:** mesma seam do `generate_image` — engine de vídeo configurável (`ImageEngine` generalizado para `MediaEngine`; ComfyUI/OmniRoute/provider), geração assíncrona via ChatJob, resultado inline no transcript (`<video>` no renderer).
- **P20 — Integração n8n:** settings admin (URL + API key); tools `n8n_list_workflows` e `n8n_trigger {workflowId, payload}`; inbound: `POST /api/v1/hooks/n8n/{token}` cria mensagem/run num chat vinculado (runs desacopladas já existem — o webhook só enfileira).
- **P21 — Webhooks genéricos de automação:** `POST /api/v1/hooks/{hookToken}` — qualquer sistema externo (n8n, cron, CI) dispara um prompt num chat; resposta retorna no corpo ou vai pro chat via notificação existente.

**Out of scope:**
- Sandbox isolado (decisão: host; fase posterior).
- Browser interativo (click-through, element picker → composer) — fase posterior.
- Tokens/custo por mensagem — providers não devolvem usage consistente.
- Voice input, ⌘K, suggestions pós-run — polish (SPEC separado se aprovado).

## 3. Technical Context

**Where the change happens:**
`Infrastructure/Services` (novos `BuiltinFileTools`, `BuiltinTodoTool`, `BuiltinDelegateTool`, `BuiltinBrowserTool`; `RiskClassifier`), `Api/Runs` (`ChatRunDispatcher` pause/resume; `ChatRun.Meta` para todo/diff), `Api/Endpoints` (`ChatRunEndpoints`, `ChatJobEndpoints`), `Client` (`RunContextPanel` com tabs, `DiffView`, `TodoChecklist`, `GitBar`).

**Constraints:**
- File tools **sempre** dentro do workdir (`Path.GetFullPath` + prefix check; `..` e symlinks rejeitados) — mesma disciplina do `shell_exec`/`code_interpreter`.
- `file_edit` gera unified diff persistido no tool_result (`{"diff": "..."}`) — o card renderiza; a aba Changes concatena.
- Risk classifier é **estático e determinístico** (tabela de regras), nunca LLM.
- `delegate` usa o pipeline existente com histórico isolado; resultado do filho vira tool_result no pai (máx 5min, senão retorna "em andamento" + link).
- `browser_screenshot` precisa de Chromium no host — feature-flag `tools.browser` (off default) como `terminal.enabled`.

## 4. Requirements

### RF-010: todo_write + Tasks
- `todo_write` substitui a lista da run; itens `{id, content, status: pending|in_progress|done|cancelled, priority?}`.
- Painel mostra checklist com toggle manual (PATCH local, não volta pro agente).
- Header do chat mostra "2/5 tarefas" enquanto a run tem todos.

### RF-011: File tools + diff
- `file_read` (linhas offset/limit), `file_write` (sobrescreve, gate), `file_edit` (unified-diff apply ou search/replace, gate), `grep` (rg-like no workdir), `glob`, `ls`.
- Tool_result de escrita/edição carrega `diff` (unified); `ToolCallCard` renderiza com +/- colorido.
- Nenhum caminho resolve fora do workdir (400 + tool_result de erro).

### RF-013: Risk approvals
- `RiskClassifier.Classify(tool, args)` → `Low|Medium|High`; tabela de regras por tool + args (paths, comandos, hosts).
- Preset `auto`: LOW auto-aprova (log só), MED executa com notice no card, HIGH vai pro gate.
- Presets existentes mantêm semântica (`always-allow` pula tudo; `approve-mutations` = comportamento atual).

### RF-014: Pause/resume
- `POST /api/v1/chats/{id}/runs/{runId}/pause` → run entra em `paused` no próximo boundary do loop (entre iterações/tool calls); `/resume` retoma.
- Chip de estado no header reflete `queued|generating|running_tool|awaiting_approval|paused|stopping|interrupted`.

### RF-015: Workspace panel
- Painel ≥lg / drawer mobile com tabs **Tasks | Changes | Jobs | MCPs | Info**; Changes lista arquivos com `+a/-d` e expande pro diff unificado.

### RF-016: delegate
- `delegate_task {prompt, context?}` cria run filha no mesmo chat (pai continua com "delegado"); resultado do filho vira tool_result ao concluir; card linka pro filho.

### RF-017: browser_screenshot
- `browser_screenshot {url, width?, height?, fullPage?}` → PNG em `/api/v1/files/{id}/content`; flag `tools.browser` admin; sem interação nesta fase.

### RF-018: Git bar
- Se `workdir/.git` existe: header mostra branch + `+a/-d` agregado da run; aba Changes usa `git diff` do workdir.

## 5. API Contract

| Endpoint | Método | Descrição |
| --- | --- | --- |
| `/api/v1/chats/{id}/runs/{runId}/pause` | POST | pausa no próximo boundary |
| `/api/v1/chats/{id}/runs/{runId}/resume` | POST | retoma |
| `/api/v1/chats/{id}/runs/{runId}/todos` | GET/PUT | estado do checklist |
| `/api/v1/chats/{id}/runs/{runId}/diff` | GET | unified diff agregado (git ou file_*) |
| `/api/v1/hooks/{hookToken}` | POST | webhook genérico → enfileira run |
| `/api/v1/n8n/workflows` | GET | lista workflows do n8n (admin/config) |

Novas tools builtin: `builtin:todo_write`, `builtin:file_read`, `builtin:file_write`, `builtin:file_edit`, `builtin:grep`, `builtin:glob`, `builtin:ls`, `builtin:delegate_task`, `builtin:browser_screenshot`, `builtin:generate_video`, `builtin:n8n_list_workflows`, `builtin:n8n_trigger`.

## 6. Critérios de Aceite

- [ ] `todo_write` persiste e o painel reflete em tempo real durante a run.
- [ ] `file_edit`/`file_write` pedem aprovação e mostram diff no card; nada escapa do workdir.
- [ ] Preset `auto` auto-aprova leitura e pergunta escrita destrutiva.
- [ ] Pause interrompe entre iterações e resume continua sem perder contexto.
- [ ] Painel com 5 tabs funciona em desktop e drawer mobile.
- [ ] `delegate_task` cria run filha e o resultado volta ao transcript do pai.
- [ ] `browser_screenshot` retorna imagem inline (feature-flag off por default).
- [ ] `generate_video` produz arquivo e renderiza `<video>` no transcript via ChatJob.
- [ ] `n8n_trigger` dispara workflow e devolve resultado como tool_result; webhook inbound cria run no chat vinculado.
- [ ] i18n nos 8 locales; cobertura não regride (≥90% line / ≥73% branch).

## Reconciliation

_Reconciliado em 2026-10-08 (SPEC-20261008-spec-status-reconciliation, Issue #203)._

| RF | Veredito | Evidência |
| --- | --- | --- |
| RF-010 todo_write + Tasks | delivered | `Infrastructure/ChatTools/Tools/TodoWriteBuiltinTool.cs`; aba Tasks em `Client/Components/ChatWorkspacePanel.razor:37-52` com toggle manual |
| RF-011 File tools + diff | delivered | `FileBuiltinTools.cs` (`file_list/file_read/file_grep/file_glob/file_write/file_edit`) confinados via `WorkspaceFiles.ResolveInside`; tool_result com diff unified; `ToolCallCard.razor:95,162-173` renderiza +/- colorido; `ChatToolsEdgeTests` cobre a matriz |
| RF-013 Risk approvals | delivered | `Infrastructure/ChatTools/ToolCallRiskClassifier.cs` — `Low|Medium|High` por tool+args; preset `auto`/`smart` (`:7-23,51`); integrado ao gate de aprovação do executor |
| RF-014 Pause/resume | delivered | `POST .../runs/{runId}/pause|resume` (`Api/Endpoints/ChatRunEndpoints.cs:30-31`) + `Api/Runs/ChatRunPauses.cs`; chip de estado reflete `paused`; `chat.pause/resume` no ChatView |
| RF-015 Workspace panel | delivered | `ChatWorkspacePanel.razor` — tabs `Tasks|Changes|Jobs|MCPs|Info` (`:19-34`), painel ≥lg + drawer mobile |
| RF-016 delegate | delivered | `DelegateTaskBuiltinTool.cs` — cria run filha em chat próprio do usuário (histórico isolado, sem recursão `:14-16`); resultado volta como tool_result |
| RF-017 browser_screenshot | delivered | `BrowserScreenshotBuiltinTool.cs` — flag `browser.enabled`/`BrowserTools:Enabled` (off por padrão, admin); PNG em `/api/v1/files/{id}/content` (`:110`) |
| RF-018 Git bar | delivered | `ChatWorkspacePanel.razor:76-90` — quando `workdir/.git` existe: branch + `+a/−d` agregado e diff por arquivo |

