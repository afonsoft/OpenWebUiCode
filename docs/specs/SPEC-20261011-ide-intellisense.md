# SPEC-20261011-ide-intellisense — LSP outline + format (Fase D)

Status: Completed

> Slice D da proposta "IDE ≈ VS Code Web". Continua A (#334), B (#335), C (#336).

## Objetivo

IntelliSense do LspService no workbench — dentro do que o bundle CodeMirror
vendored permite.

## Escopo

### Backend (`/api/v1/workspace/lsp`)
- `GET /symbols?path=` → `textDocument/documentSymbol` → lista plana
  `{name, detail, kind, line, col, endLine, endCol, depth}` (SymbolKind
  → id numérico; hierarquia achatada com `depth`). Degrade: server off
  → `{available:false, symbols:[]}` (200, nunca erro de provider enlatado
  sem marcador).
- `POST /format {path}` → `textDocument/formatting` → aplica TextEdits no
  conteúdo e devolve `{available, text, changed}` — o editor faz setDoc e
  salva pelo fluxo PUT /file já existente (format NÃO escreve disco direto).

### Frontend
- `IdeOutlineView` — view "outline" na sidebar (activity bar): árvore de
  símbolos do arquivo ativo via LSP; clique → goto linha. Degrade:
  `GetSymbols()` regex local (Fase B) quando `available:false`.
- `Ide.razor` — `Format` command na palette + Shift+Alt+F global:
  POST /format do arquivo ativo → `owuiIde.setDoc` + marca dirty.
- i18n ×8.

## Fora de escopo (bundle-bound)

- F12 definition, Shift+F12 references, F2 rename, completion/hover
  inline: precisam da posição do cursor e de keymaps/extensions do
  CodeMirror — o bundle `ide-editor.bundle.js` não expõe `getCursor` nem
  tem fonte no repo pra rebuild. Hover já tem endpoint GET /lsp/hover
  para quando o bundle crescer.

## Requisitos

- RF-1: symbols com hierarquia (depth) + click-goto; degrade sem server.
- RF-2: format devolve texto novo; `changed:false` quando já formatado.
- RF-3: mesma barra BoundWorkdirAsync/jail dos demais endpoints.
- RF-4: i18n paridade, guards verdes, testes de degrade (sem server →
  available:false) + shape do contract.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/337 |
