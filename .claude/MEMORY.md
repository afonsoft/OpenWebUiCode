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

## Handoff 2026-10-27 (scratch-workspace entregue)

- **#547 scratch-workspace**: entregue via PR #548 (merge `b2889d6`), issue fechada, SPEC arquivada em `docs/specs/`. Deploy local feito (container `openwebui`, :3032, healthy, HTTP 200, endpoints `/workspace/scratch` + `/workspace/publish` respondendo 401 sem auth).
- Ciclo de CI do #548 ensinou: provider real em modo chat entra em loop de tools se o announce incluir tools de build (`worktree_*`, `lsp_*`, `specs`) ou se o prompt convidar exploração — `BuildOnlyTools` no `PermissionRuleset.ShouldAnnounce` + prompt condicional resolveram.
- Coverage ratchet: baseline linha 92.22% / branch 79.73% — quedas >0.05pp reprovam; cobrir endpoints + services novos desde o primeiro commit.
- `.specs/SPEC-20261027-context-gen-wizard.md` permanece deferida (não implementar).
