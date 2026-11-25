# AUDIT.md — Fase A (read-only) — chat `f666d33c-51ac-4d99-8bff-af55ddcac40d` / run `4c84362c-71d9-48f3-b47d-1d88d3083ecd`

> Auditado em 2026-11-25 ~14:38 UTC contra o banco vivo do container `openwebui` (`/data/openwebui.db`, consultas somente leitura via `sqlite3 -readonly`). Evidências sanitizadas — sem JWT, tokens nem connection strings.

## 1. Reconstrução do chat e das runs

Chat: **"🔌 OmniRoute Migração MCP Hub"**, `userId c494c8e0-…`, repo vinculado **afonsoft/KnowledgeRAG @ main** via `ConfigEntries["chat:f666d33c…:workspace.repo"] = {"repo":"afonsoft/KnowledgeRAG","branch":"main","dir":"worktrees/afonsoft__KnowledgeRAG/f666d33c-…"}`.

| Run | Status | Janela (UTC) | Notas |
|---|---|---|---|
| `17bc78fe` | failed | ~13:47 | provider 402 PaymentRequired (pollinations) |
| `4c84362c` | completed | →14:26:40 | **run auditada** — 37 steps, 345 eventos |
| `a613aa12` | completed | →14:29:14 | teve evento `interceptor_veto` |
| `9e1bbb5a` | stopped | 14:30 | cancelada após 29s — `TaskCanceledException` em `ProviderService.ReadLineWithIdleAsync`; chamou `builtin_run_workflow` (filho: chat `7984fe97`) |
| `850404c5` | running | 14:30:01→ | **viva** — filha de `9e1bbb5a`, heartbeat 15s íntegro (último 14:38:02); não é órfã |

Correção à hipótese inicial: `850404c5` está `running` e saudável — o valor lido antes como heartbeat era `StartedAt`.

### Run `4c84362c` — cronologia
- Input: "Analise esse repo e veja o que falta implementar, analise as specs e veja se todas foram impl[ementadas]…"
- `RequestJson`: modelo adaptativo `auto/gemini,claude-sonnet,glm,gpt`; **106 tools** incl. `builtin:skill`; toolIds = 42 tools MCP projetadas (deepwiki/graph/n8n/settings…) + builtins.
- System prompt efetivo (recuperado de evento snapshot, `/tmp/sys-prompt-run4c.txt`, 225 linhas): "You are the Open WebUI agent — autonomous…" + `<project_instructions>` com AGENTS.md/CLAUDE.md do KnowledgeRAG. **Não contém catálogo de skills** — a palavra "skill" só aparece dentro das instruções do repo alvo.
- Tools usadas: todo_write, instant_context, file_read/write/edit/grep/glob/list, memory_save, run_workflow, `builtin_agent_validate` (timeout 300s em `dotnet test` → FAILED), sem `builtin:skill`.
- Artefatos: edições em `src/KnowledgeHub.Server/Mcp/*.cs` (TavilyToolsProvider, ToolArgs, TemporalGraphToolsProvider, GraphToolsProvider, SettingsToolsProvider, CallerIdentity) e criação de `.specs/SPEC-20261115-omniroute-alignment-gaps.md`.
- Classificação resumida: execução da tarefa **Conforme**; validação **Parcial** (agent_validate falhou por timeout do `dotnet test`, reportado); uso de skills **Não conforme ao objetivo do RF-007** (ver §5).

## 2. Error 1 — label `for` sem `id` (RF-004)

`tools/check-form-a11y.py` passa limpo (76 arquivos). Busca estendida (for→id não-labelable, ids duplicados em runtime e cross-arquivo, for dinâmico) encontrou como fonte provável do warning do console **dois `<label>` sem associação** (Chrome loga "A `<label>` isn't associated with a form control", relatado como "for sem id"):

- `src/OpenWebUI.Client/Components/Sidebar.razor:417` — `<label>` decorativo seguido de `<input readonly aria-label>` (sem `id`, fora do label).
- `src/OpenWebUI.Client/Pages/WorkspaceItemEditor.razor:136` — `<label>` usado como título de seção sem controle associado.

**Causa raiz:** labels sem `for` e sem controle envolvido (o checker cobre `for`→`id`, não label órfão). Correção: `for`+`id` ou reestruturar (label envolvendo o input / `span` estilizado).

## 3. Error 2 — `GET /workspace/repo/tree?path=.agents/memory…` 404 (RF-005)

- Binding do chat existe (acima) → `ResolveBindingAsync` resolve workdir `/data/workspaces/c494c8e0…/worktrees/afonsoft__KnowledgeRAG/f666d33c-…` — **verificado: existe**.
- Dentro do worktree há `.claude/memory/` mas **não há `.agents/`** (verificado via `find` no container).
- `WorkspaceFileEndpoints.TreeAsync` (`Endpoints/WorkspaceFileEndpoints.cs:174-177`): `!Directory.Exists(full)` → `Results.NotFound({ok:false,error:"Caminho não encontrado."})`. Não distingue "diretório inexistente" de "vazio".
- Origem da chamada: `ChatWorkspacePanel.LoadMemoriesAsync` (`ChatWorkspacePanel.razor:919-934`) itera `[".claude/memory",".agents/memory"]` e faz GET em cada um — `.agents/memory` 404 → `SendAsync` retorna null → tratado como "sem memórias". **Funcionalmente graceful; o 404 é ruído de console produzido pela sonda.**

**Causa raiz:** o cliente sonda dois dirs de memória; o segundo não existe no worktree → 404 intencional do endpoint aparece no console. Decisão pendente do gate (SPEC §Perguntas): manter 404 e suprimir o barulho no cliente (consultar tree raiz ou tolerar), ou retornar 200 com coleção vazia + marcador `exists:false` (mudança de contrato — exige teste).

## 4. Error 3 — stream termina em `OperationCanceled` (RF-006)

- Stream = SSE `GET /runs/{id}/stream` (`ChatRunEndpoints.StreamRunAsync`), com heartbeat `: hb` 10s, replay por `Last-Event-ID`, fallback de polling `GetRunEventsAsync`. OCE+IOException no servidor = desconexão normal.
- Cliente: `ChatView.razor` re-anexa com `lastSeq`; stall watchdog com CTS linkado (`FirstEventStallMs` 15s / `StallMs` 45s).
- **Bug em `ChatView.razor:~3853-3861`:** `catch (OperationCanceledException) when (ct.IsCancellationRequested)` re-lança, mas o OCE disparado pelo stall watchdog (ct **não** cancelado) cai no `catch (Exception ex)` → loga `"stream da run {runId} caiu: The operation was canceled"` no console como se fosse falha — cancelamento esperado classificado como erro. A reconexão ainda funciona; é o sintoma reportado.
- Cadeia server-side: run `9e1bbb5a` mostra cancelamento de usuário fluindo até `ProviderService.ReadLineWithIdleAsync` → `TaskCanceledException` → status `stopped` correto. Watchdog do dispatcher (60s × 3 ticks stale → `Interrupted`) não foi violado.

**Causa raiz:** classificação de exceção no cliente — OCE de stall não é filtrado pelo `when`. Correção: distinguir OCE-esperado (watchdog/dispose/navegação) de falha real; não logar como erro; terminal state idempotente já existe no servidor.

## 5. Error 4 — BFCache/WebSocket (RF-006)

- `Program.cs:657` mapeia `MapHub<ChatHub>("/ws")`. Cliente `RealtimeService` usa SignalR `HubConnection` com `withAutomaticReconnect`.
- **Não há handler `pagehide`/`pageshow`/`bfcache` em nenhum `.razor`/`.js` da aplicação** — ao restaurar do BFCache o browser fecha o WS; o SignalR só reconecta se `onclose` disparar. Se a página congelada não recebe o close, o estado fica "conectado" morto até timeout. Correlacionado com o §4: navegação/restore cancela o SSE e o WS; a run continua no servidor (persistida), mas a UI pode não reconciliar.
- Não é a mesma causa do 404 REST (problemas independentes — confirmado).

**Causa raiz:** ausência de tratamento de restore BFCache — falta listener `pageshow(event.persisted)` para forçar reconnect/reanexar stream e reconciliar a run via polling.

## 6. Skills — quatro estados (RF-007/RF-008)

| Estado | Evidência | Resultado |
|---|---|---|
| Descoberta | `SkillDiscoveryService` varre `.claude/skills`→`.agents/skills`→… no workdir; worktree KnowledgeRAG tem `.claude/skills/` | Provável OK |
| Ofertada | `builtin:skill` presente nos toolIds da run (ferramenta de leitura progressive-disclosure). **Mas o system prompt NÃO traz catálogo compacto** (nome/descrição/quando usar) — verificado no prompt persistido | **Falha aqui** |
| Selecionada | run nunca chamou `builtin:skill` (0 ocorrências nos eventos) | — |
| Aplicada | n/a | — |

O menu de UI (`GET /workspace/repo/skills`) mostra skills porque o discovery funciona; o LLM recebeu a *ferramenta* `skill` mas **nenhuma lista do que existe** — sem catálogo no prompt, o modelo não tem como saber o que ler. (As "skills" mencionadas no prompt são texto das instruções do repo KnowledgeRAG, não catálogo executável.) MCP, em contraste, teve tools projetadas nos toolIds (42) — descoberto→ofertado OK.

**Causa raiz:** falta injeção do catálogo compacto de skills no system prompt (ou evento de contexto) quando o workdir tem skills — lacuna entre discovery e o LLM.

## 7. Achados secundários

- `builtin_agent_validate` travou 300s em `dotnet test` (tool_wait 14s→300s) → FAILED por timeout — comportamento esperado do limite, mas o LLM declarou progresso; classificar **Parcial** na validação.
- `interceptor_veto` em `a613aa12` e tool_args malformado em `9e1bbb5a` — registrados, sem impacto na auditoria.
- Run `17bc78fe` failed por 402 do provider — corretamente persistida como falha de provider (não mascarada).

## 8. Plano mínimo proposto (aguardando gate)

| # | Correção | Arquivos prováveis |
|---|---|---|
| F1 | Associar os 2 labels órfãos (`for`+`id` único ou reestruturação) | `Sidebar.razor`, `WorkspaceItemEditor.razor` |
| F2 | 404 do tree: suprimir ruído do probe de memória (preferido: cliente tolera 404 sem log; alternativa: 200 vazio — decisão pendente) | `ChatWorkspacePanel.razor` / `WorkspaceFileEndpoints.cs` |
| F3 | Filtrar OCE-esperado do `catch (Exception)` no loop de stream; não logar como queda | `ChatView.razor` (~3853) |
| F4 | Handler `pageshow`/`pagehide` + reconciliação de run/WS após BFCache | `RealtimeService.cs`, `boot.js`/JS interop, `ChatView.razor` |
| F5 | Catálogo compacto de skills no system prompt quando existirem no workdir (nome+descrição+id) — mantendo `builtin:skill` como leitura sob demanda | `SystemPromptContributors.cs`, possível `SkillDiscoveryService` |

**STOP — Gate Fase A.** Nada foi editado/commitado. Aguardando `SPEC APROVADA — PODE IMPLEMENTAR` (e decisão sobre F2: 404 tolerado vs contrato 200-vazio).

---

## 9. Fase B — implementado (branch `feature/Devin-20261125-audit-agent-skills-runtime`)

| # | Fix | Arquivos | Evidência |
|---|---|---|---|
| F1 | `for="share-url"`+`id` no input do link público; label-ornamento de "Arquivos" virou `div.field-label` | `Sidebar.razor`, `WorkspaceItemEditor.razor` | `check-form-a11y.py` OK (76 arquivos) |
| F2 | `LoadMemoriesAsync` sonda a raiz com `depth=2` e só chama o tree nos dirs presentes — `.agents/memory` ausente não gera mais 404 no console | `ChatWorkspacePanel.razor` | contrato do endpoint preservado (`404` = inexistente; vazio = 200) |
| F3 | `catch (OperationCanceledException)` extra entre o `when (ct.IsCancellationRequested)` e o `catch (Exception)` — stall watchdog re-anexa em silêncio | `ChatView.razor` | OCE esperado não é mais logado como queda |
| F4 | `openwebui.watchBfcache` (pageshow persisted) → `OnBfcacheRestoreAsync` → reconnect SignalR + reconcilia run ativa; `ConnectAsync` não retorna cedo com conexão morta | `app.js`, `ChatView.razor`, `RealtimeService.cs` | `ConnectAsync` agora verifica `HubConnectionState` |
| F5 | `SkillCatalogContributor` (Order 71): catálogo `- nome — descrição` com instrução `builtin:skill{name}`; autoinvoke excluída (corpo já injetado); respeita toggles/admin-deny | `SkillCatalogContributor.cs`, `Program.cs` | 30 testes verdes |
| F6 | `LoadProjectInstructions` lê `.claude/rules`, top-level `.agents/*.md`/`.claude/*.md` e `.agents/memory`/`.claude/memory` (dedup por symlink); cap 32→48KB | `SkillDiscoveryService.cs` | 3 testes novos |

**Decisão F2:** 404 mantido no endpoint (REST correto); ruído eliminado na fonte (cliente não sonda dir ausente).

**Verificação:** `dotnet build` Api+Client 0 erros/0 warnings; 30 testes filtrados verdes; drift guards (form-a11y, css-classes, i18n-parity, spec-status) OK; secret scan no diff limpo.
