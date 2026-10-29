# MEMORY.md — Estado Cross-Session

> Nunca armazenar PII, secrets ou credenciais. Verificar fatos contra o código antes de usar.

## Decisões Técnicas

| Data | Decisão | Racional | Alternativas descartadas |
|---|---|---|---|
| 2026-10-01 | Tailwind v4 estático commitado | mesma paleta/tema do upstream sem Node no build | Node no pipeline .NET |
| 2026-10-01 | Clean Architecture em 5 projetos | separação Domain/App/Infra/Api/Client | monolito único |
| 2026-10-01 | `MapStaticAssets` para WASM | resolve `#[.{fingerprint}]` | UseStaticFiles + fallback |
| 2026-10-13 | Binding de repo estrito por chat + worktree por conversa (SPEC-15) | conversa nova sem repo; build só com repo; isola escrita entre chats | fallback ao binding global, checkout compartilhado |
| 2026-10-27 | Scratch workspace lazy por chat sem repo (SPEC-20261027, PR #548) | todo chat/build/plan sem repo trabalha em `workspaces/{u}/scratch/{chatId}` com git init; publish empurra para URL/remoto sem gravar `origin` (token nunca toca `.git/config`) | pasta na criação do chat (descartada: lazy evita lixo em conversa pura); origin persistido (descartado: vaza credencial) |

## Débito Técnico

| Item | Impacto | Prioridade |
|---|---|---|
| Pendências de paridade (RAG, OAuth, voice, realtime) | ver `docs/MIGRACAO-DOTNET.md` | conforme roadmap |

## Lições Aprendidas

| Contexto | Erro | Como evitar |
|---|---|---|
| Boot WASM | placeholder não resolvido → app muda | smoke test deve validar boot, não só HTML 200 |
| Tarefas LLM | fallback "New Chat" sobrescrevia título | endpoint retorna erro; cliente preserva estado |
| Modo do agente | default "build" sem repo quebrava o gate de workspace | default "chat" + modo efetivo com downgrade quando não há binding |

## Handoff 2026-10-27 (fila do composer + resiliência de stream)

- **Fila de mensagens do composer** (`ChatView.razor`): enquanto `_streaming`, Enter/botão envia para `_messageQueue` (texto + snapshot de anexos/mentions); painel cinza acima do composer com edição inline (lápis), force-send (↵ para a run e prioriza o item via `_forceQueueId`) e remover (×). `DrainMessageQueueAsync` nos finally dos 3 caminhos de stream + `HandleRunCompleted` despacha itens como runs sequenciais; `_drainingQueue` barra reentrância (o finally interno retorna e o while externo continua).
- **Links em nova aba**: `MarkdownService` injeta `target="_blank" rel="noopener noreferrer"` pós-sanitize em `<a href>` não-`#` (raw string com conteúdo terminando em `"` precisa delimitador de 4+ aspas — bug pego por teste).
- **Resiliência**: stall watchdog 60s→35s (2 heartbeats de 15s + margem); `GetActiveRunAsync` na carga do chat ganhou retry único de 1s para falhas transitórias de fetch (proxy CF/nginx em produção); `StopStreaming` do caminho legado (playground) agora cancela `_cts` — antes era no-op.
- **AgentRunEventBus**: eventos terminais (`run.completed/stopped/failed`) usam `observers.timeout_ms` (default 90s) — observers pós-run que chamam LLM (InstinctExtractor) estouravam o timeout de 5s dos interceptors e morriam com TaskCanceledException em todo fim de run.
- Produção (`open-webui.afonsoft.dev`) é Browser → Cloudflare → nginx (aaPanel, `proxy_pass localhost:3032`, timeouts 1200s) → Kestrel; SSE tem flush+heartbeat+`X-Accel-Buffering: no` server-side — travadas residuais vinham de fetch transitório de borda, não de buffering de origem.

## Handoff 2026-10-27 (scratch-workspace entregue)

- **#547 scratch-workspace**: entregue via PR #548 (merge `b2889d6`), issue fechada, SPEC arquivada em `docs/specs/`. Deploy local feito (container `openwebui`, :3032, healthy, HTTP 200, endpoints `/workspace/scratch` + `/workspace/publish` respondendo 401 sem auth).
- Ciclo de CI do #548 ensinou: provider real em modo chat entra em loop de tools se o announce incluir tools de build (`worktree_*`, `lsp_*`, `specs`) ou se o prompt convidar exploração — `BuildOnlyTools` no `PermissionRuleset.ShouldAnnounce` + prompt condicional resolveram.
- Coverage ratchet: baseline linha 92.22% / branch 79.73% — quedas >0.05pp reprovam; cobrir endpoints + services novos desde o primeiro commit.
- `.specs/SPEC-20261027-context-gen-wizard.md` permanece deferida (não implementar).
