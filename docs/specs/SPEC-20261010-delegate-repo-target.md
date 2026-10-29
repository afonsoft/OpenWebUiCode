# SPEC-20261010-delegate-repo-target — delegate_task com repo alvo

Status: Completed

## Contexto

`delegate_task` (SPEC-20261010-async-delegate) cria sub-agents que herdam o binding por chat do pai (SPEC-20261010-chat-repo-binding). Para o fluxo opencode-web "sub-agent noutro repo", o pai precisa poder apontar o repositório da subtarefa sem abrir um chat manual antes.

## Objetivo

`delegate_task` aceita `repo` opcional (`owner/repo`), com `branch` (default `main`) e `clone_url` (default `https://github.com/<repo>.git`): quando presente, o chat filho é vinculado ao repo alvo via `OpenChatAsync` (checkout compartilhado por slug), sobrescrevendo a herança do pai.

## Escopo

- Params novos no JSON schema da tool: `repo`, `branch`, `clone_url`.
- `ExecuteAsync`: após a herança do binding do pai, se `repo` informado → `OpenChatAsync(userId, childChat.Id, repo, branch, cloneUrl, token)` com token do `GitHubService`. Falha → `childRun.Status = failed` + erro no tool_result, sem enfileirar.
- Fora de escopo: mudar repo do próprio chat pai; UI.

## Acceptance Criteria

- `repo`+`clone_url` válidos → `chat:{child}:workspace.repo` aponta pro slug/dir do repo alvo e a run filha é enfileirada.
- Slug inválido → run filha `failed`, dispatcher não chamado, tool_result explica o erro.
- Sem `repo` → comportamento inalterado (herda binding do pai).
- Testes novos passando; suíte Release completa 0 falhas.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/297 |
