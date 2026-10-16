# SPEC-20261010-workspace-chatid-scope — Escopo de chat nos endpoints de workspace

Status: Completed

## Contexto

SPEC-20261010-chat-repo-binding (PR #295) introduziu binding de repo **por chat** (`chat:{id}:workspace.repo`, precedência chat → user → none). As runs e as tools builtin já resolvem o workdir pelo chat, mas os endpoints REST de workspace usados pelo painel/IDE (`tree`, `file`, `git`, `test-run`, `test-command`, `skills`, `commands`, `checkpoints`, `pulls`, `lsp`, run-diff) continuam resolvendo **somente pelo binding global do usuário** — num chat vinculado ao repo B com global = repo A, a run opera em B mas a UI mostra A (gap identificado na revalidação pós-#295).

## Objetivo

Endpoints de workspace aceitam `?chatId=` opcional: quando presente (e o chat pertence ao usuário), resolvem o binding/workdir pela precedência chat → user → none. Ausente, comportamento atual (global) — backward compat total.

## Escopo

1. `WorkspaceFileEndpoints` (`tree`, `file` GET/PUT, `mkdir`, `rename`, `delete`, `git`): `string? chatId` em todos os handlers; `BoundWorkdirAsync` valida ownership (`chatId` ≠ chat do usuário → 404) e usa `ResolveBindingAsync`/`ResolveWorkdirAsync(userId, chatId)`.
2. `WorkspaceTestRunEndpoints`: `POST /test-run` e `PUT /test-command` aceitam `chatId`; `test-command` escreve de volta na chave resolvida (chat quando `source=="chat"`, senão user).
3. `RepoSkillEndpoints` (`skills`, `skills/{name}`, `commands`, `commands/{name}`), `CheckpointEndpoints` (`checkpoints*`), `GitHubEndpoints` (`pulls`), `LspEndpoints` (`status`, `doc`, `diagnostics`, `hover`): mesmo padrão `?chatId=`.
4. `ChatRunEndpoints` run-diff: `ResolveWorkdirAsync` passa a usar o `chatId` da própria run.
5. Client `ApiService`: parâmetro opcional `chatId` nos métodos acima (append `?chatId=`); `ChatView` passa `_loadedChatId` nas chamadas de contexto de chat (tree, skills, commands); link `/ide` leva `?chatId=`; `Ide.razor` honra `[SupplyParameterFromQuery] ChatId` repassando a explorer/editor/pulls/test-run/lsp/checkpoints.
6. Testes: `?chatId=` honra binding do chat; chat alheio → 404; sem chatId → global (retrocompat); `test-command` escopo de chat.

## Fora de escopo

`GET/POST/DELETE /api/v1/workspace/repo/` (gerencia o binding global — segue global por design); completions anônimas (`ApiEndpoints`, sem chat); `merge-worktree` (já run-scoped).

## Acceptance Criteria

- `GET /workspace/repo/tree?chatId={c}` retorna a árvore do repo do chat quando `c` tem binding próprio; global caso contrário.
- `chatId` de outro usuário → 404 em todos os endpoints tocados.
- Sem `chatId`, comportamento idêntico ao anterior (testes existentes seguem verdes).
- Client: painel/skills/commands do chat refletem o repo do chat; `/ide?chatId=` reflete o repo do chat.
- Suíte Release completa 0 falhas; drift guards verdes.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/295 |
