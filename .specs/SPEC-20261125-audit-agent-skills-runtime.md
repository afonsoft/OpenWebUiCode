# SPEC-20261125 — Auditoria do agente e correções de runtime

**Status**: In implementation — gate aprovado 2026-11-25; fixes F1–F6 implementados
- **Branch:** `feature/Devin-20261125-audit-agent-skills-runtime`
- **Auditoria:** `AUDIT.md` (Fase A, evidências sanitizadas)

## Escopo aprovado

| # | Fix | Arquivos |
|---|---|---|
| F1 | Associar labels órfãos (`for`+`id` único ou reestruturação) | `Sidebar.razor`, `WorkspaceItemEditor.razor` |
| F2 | Eliminar o 404 do probe de memória — descobrir dirs existentes via tree raiz `depth=2`, buscar só os que existem. Contrato do endpoint preservado (404 = dir inexistente; coleção vazia = 200) | `ChatWorkspacePanel.razor` |
| F3 | OCE-esperado (stall watchdog/dispose/navegação) não é logado como queda de stream | `ChatView.razor` |
| F4 | `pageshow`/`pagehide` → reconectar SignalR `/ws` e reconciliar run após BFCache | `RealtimeService.cs`, JS interop, `ChatView.razor` |
| F5 | Catálogo compacto de skills do workdir no system prompt (nome/descrição/id), `builtin:skill` continua a leitura sob demanda | `SystemPromptContributors.cs` |

## Decisões derivadas da auditoria

- **F2:** `.agents/memory` ausente → manter 404 no endpoint (REST correto para recurso inexistente). O ruído era do cliente sondando dois dirs cegamente; agora a sonda só acontece para dirs confirmados presentes.
- **F3:** cancelamento esperado ≠ falha — filtrar no catch, sem engolir erros reais.
- **F5:** descoberta→ofertada já funcionava no lado da tool; a lacuna era o catálogo no prompt.

## Critérios de aceite

Mapeados em CA-001..012 na SPEC original — ver `AUDIT.md` §8 para mapeamento fix→arquivo.
