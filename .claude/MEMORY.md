# MEMORY.md — Estado Cross-Session

> Nunca armazenar PII, secrets ou credenciais. Verificar fatos contra o código antes de usar.

## Decisões Técnicas

| Data | Decisão | Racional | Alternativas descartadas |
|---|---|---|---|
| 2026-10-01 | Tailwind v4 estático commitado | mesma paleta/tema do upstream sem Node no build | Node no pipeline .NET |
| 2026-10-01 | Clean Architecture em 5 projetos | separação Domain/App/Infra/Api/Client | monolito único |
| 2026-10-01 | `MapStaticAssets` para WASM | resolve `#[.{fingerprint}]` | UseStaticFiles + fallback |
| 2026-10-13 | Binding de repo estrito por chat + worktree por conversa (SPEC-15) | conversa nova sem repo; build só com repo; isola escrita entre chats | fallback ao binding global, checkout compartilhado |

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

## Handoff 2026-10-26 (contexto esgotado)

- **#513 themes-custom**: WIP completo commitado na branch remota `feature/themes-custom-wip` (UserTheme+migration, ThemeEndpoints, ThemeContracts, SettingsAppearance, presets, i18n×8, bootstrap index.html/app.js). Falta: merge com main, build, acceptance da `.specs/SPEC-20261018-themes-custom.md`, drift guards, PR `Closes #513`.
- **#515 perf load-test**: gate "⚡ Performance Load Test" falhando (run 40177494422, último 26/10 22:20). Diagnosticar `gh run view --log-failed`, corrigir, PR `Closes #515`.
- **#506 épico**: fechar quando #513 mergear (6/7 slices já entregues; checkboxes atualizados).
- Worktree `pipe_10a0f6bb` segue checked-out na branch do WIP — não apagar até confirmar o merge.
