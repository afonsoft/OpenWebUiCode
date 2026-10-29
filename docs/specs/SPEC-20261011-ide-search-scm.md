# SPEC-20261011-ide-search-scm — Busca global + SCM write (Fase C)

Status: Completed

> Slice C da proposta "IDE ≈ VS Code Web" (`/home/ubuntu/ide-vscode-proposta.md`,
> aprovada 2026-10-11). Continua A (#334) e B (#335).

## Objetivo

Aproximar o workbench `/ide` do VS Code Web em busca e source control:
busca global estilo Ctrl+Shift+F, decorations de git na tree, context menu
completo do explorer e commit pela SCM view — gated por flag.

## Escopo

### Backend
- `GET /api/v1/workspace/repo/search?q=&path=&chatId=` — literal
  case-insensitive por linha; jail `ResolveInsideFinal`; pula
  `CollapsedDirs`, symlinks, exts binárias e arquivos >512KB; cap 200
  matches (`Truncated`). `WorkspaceSearchResponse(Matches, Truncated)`.
- `POST /api/v1/workspace/repo/commit` — `git add -A + commit -m`
  (`WorkspaceGitService.CommitAsync`, autor "OpenWebUI IDE"). Flag
  `ide.git_write`: `Ide:GitWrite`/`IDE_GIT_WRITE` → kv `ide.git_write` →
  **default off**; off → 403.
- `GET /git` ganha `GitWrite` no response (UI esconde a caixa de commit
  quando off).

### Frontend
- `IdeSearchView` — view de busca na sidebar (activity bar 🔍): input
  com debounce 400ms, resultados agrupados por arquivo, clique abre o
  arquivo na linha:col; Enter abre o 1º match. Ctrl+Shift+F foca a view.
- `IdeExplorer`/`IdeTreeNode` — decoration de status git (M âmbar,
  A/?? verde, D riscado) + context menu de botão direito (New File,
  New Folder, Rename, Copy Path, Delete) — superset do par inline.
- `IdeScmView` — caixa de mensagem + botão Commit só quando
  `Git.GitWrite`; feedback do sha curto; Enter commita.

### Fora de escopo (bundle-bound)
- Editor de diff lado-a-lado (`@codemirror/merge`) e gutter markers:
  dependem de rebuild do `ide-editor.bundle.js` vendored sem fonte no
  repo — mesmo motivo do defer do minimap na Fase B.

## Requisitos

- RF-1: busca literal case-insensitive devolve Path/Line(0-based)/Col/Text.
- RF-2: endpoints no jail; CollapsedDirs e binários fora da varredura.
- RF-3: commit default-off; 403 quando off; 400 mensagem vazia.
- RF-4: decorations no nome do nó + letra do status.
- RF-5: i18n paridade (8 locales), guards de drift verdes.

## Testes

- `/search`: match linha/col, `.git` ignorado, q vazio, jail 400.
- `/git`: `GitWrite=false` default; `/commit`: 403 off; `Ide:GitWrite=true`
  → Ok + sha + `.git/HEAD` existe.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/336 |
