# SPEC-20261010-chat-repo-binding — Repo binding por chat

Status: Completed
Owner: Afonso

## Contexto
Hoje o binding workspace↔repo é global por usuário: `config.SetAsync("u:{uid}:workspace.repo", WorkspaceRepoBinding)` (ver `WorkspaceRepoService`, `GitHubEndpoints`, `WorkspaceFileEndpoints`). Todos os chats de um usuário compartilham o mesmo repo/workdir — impossível trabalhar em 2 repos simultaneamente em chats diferentes (estilo opencode-web, uma session por projeto).

## Objetivo
Binding de repo **por chat**: cada chat pode ter seu repo/workspace próprio; ausência de binding por chat cai no binding global do usuário (backward compat total).

## Escopo
1. **Resolução**: `WorkspaceRepoService.ResolveWorkdirAsync` (e todo chamador) passa a receber `chatId`/`runId` quando disponível. Ordem: binding `chat:{chatId}:workspace.repo` → fallback `u:{uid}:workspace.repo` → fallback `{DATA_ROOT}/workspaces/{uid}` atual.
2. **Config keys**: nova chave `chat:{chatId}:workspace.repo` (mesmo shape `WorkspaceRepoBinding`). Helper no service para `Get/Set/Clear` por chat.
3. **Endpoints**: `GET /api/v1/chats/{chatId}/workspace-repo` e `PUT /api/v1/chats/{chatId}/workspace-repo` (body = binding existente ou `null` para limpar e voltar ao global). Owner-only. Resposta inclui `source: "chat"|"user"|"none"` pra UI saber a origem.
4. **Propagação**: `BuiltinToolContext`/run deve carregar o `ChatId` (já existe `context.ChatId`) — tools que hoje resolvem workdir pelo user binding passam a consultar o chat binding primeiro. Endpoints de workspace (files, test-run, checkpoint, repo-skills, git) que operam "no contexto do chat" devem aceitar/resolver por `chatId` quando vierem de uma run.
5. **UI**: na página do chat (header ou menu de contexto do chat na sidebar), mostrar repo bound + permitir trocar/limpar (reusa lista de repos do `GitHubEndpoints`). Indicador visual quando o chat usa binding global vs próprio. i18n flat nos 8 locales (`chat.repo_*` ou chaves flat equivalentes seguindo convenção existente).
6. **delegate_task**: o chat filho passa a herdar o binding **por chat** do pai (copia `chat:{parent}:workspace.repo` → `chat:{child}:workspace.repo` quando existir; senão nada — filho usa global).

## Fora de escopo
Multi-repo simultâneo dentro do mesmo chat (1 repo por chat); worktree por run; UI de comparação.

## Acceptance Criteria
- `PUT chats/{id}/workspace-repo` persiste binding por chat; `GET` retorna `source:"chat"`.
- Sem binding por chat, `GET` retorna o binding global (`source:"user"`); após `PUT null`, volta ao global.
- Duas runs em chats diferentes com bindings diferentes executam `shell_exec`/`file_*` em workdirs distintos (teste: cada um cria arquivo no seu workspace).
- `delegate_task` herda binding do pai quando existe.
- Tests: resolução por chat, fallback, endpoints (401/404/owner), herança no delegate.
- Suíte Release completa 0 falhas; drift guards se tocar .razor/i18n; tailwind regenerado se classes novas.

## SPEC file
Commit `.specs/SPEC-20261010-chat-repo-binding.md` (este documento, Status: Completed→Completed no PR) junto da implementação.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/295 |
