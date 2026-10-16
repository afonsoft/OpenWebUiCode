# SPEC-20261011-ide-bundle — Fase E: bundle CodeMirror com fonte + IntelliSense LSP

Status: Completed — fases A–E mergeadas em #334–#338; validação visual da `/ide` executada em 2026-10-16 (SPEC-20261016-ide-visual-validation / issue #395, evidência em `docs/screenshots/ide-visual-20261016/`).

## Contexto

O `wwwroot/js/ide-editor.bundle.js` era um blob minificado sem fonte — impossível
adicionar APIs de cursor/seleção (bloqueava completion/F12/F2/rename/minimap/
split/diff). Esta fase recria o bundle a partir de fonte própria e destrava os
recursos pendentes.

## Escopo

### 1. Fonte do bundle (`tools/ide-bundle`)

- `src/index.js`: mesmas extensions/languages do bundle original (basicSetup sem
  lint; langs js/ts/tsx/jsx, py, css, html, json, md, yaml, c/cs/cpp/cxx/h;
  Mod-s→`OnSaveRequested`, Mod-f→search) + novidades:
  - **Cursor/seleção**: `getCursor/setCursor/getSelection/setSelection/
    replaceRange/wordAt` (posições 0-based UTF-16 = LSP).
  - **Multi-cursor**: Alt+click adiciona cursor (`allowMultipleSelections` +
    `drawSelection`, já no basicSetup).
  - **LSP keybinds**: F12→`OnDefinitionRequested`, Shift+F12→
    `OnReferencesRequested`, F2→`OnRenameRequested` (callbacks dotnet).
  - **Completion**: `autocompletion({override})` chama
    `OnLspCompletionRequested(path,line,col)` → promise de itens CM.
  - **Minimap**: `@replit/codemirror-minimap` (`showMinimap.compute`,
    compartment `mmC` toggleável).
  - **Split**: `createSplit(elId,srcId)` — 2º `EditorView` na mesma doc, sync
    bidirecional echo-guarded via compartment `linkC`; Ctrl+\.
  - **Diff**: `createDiff(elId,optsA,optsB)` — `MergeView` (@codemirror/merge)
    lado A=HEAD readonly, lado B=editável; `getDiffDoc/destroyDiff`.
- Build: `npm run build` (esbuild → iife minificado → `wwwroot/js/
  ide-editor.bundle.js`, commitado — mesma política vendored, sem CDN).

### 2. Endpoints (`LspEndpoints.cs` + `WorkspaceFileEndpoints.cs`)

- `POST /lsp/completion` — textDocument/completion → itens mapeados pro CM
  (`LspCompletionResponse{Available,From,Items}`; kind→tipo CM).
- `GET /lsp/definition` / `GET /lsp/references` — Location|Location[]|
  LocationLink[] → `LspLocationsResponse` (path relativo ao workdir).
- `POST /lsp/rename` — WorkspaceEdit (changes + documentChanges): edits do
  arquivo ativo voltam pro client (`replaceRange`, preserva dirty); demais
  arquivos escritos no disco (mesmo jail `ResolveInside`).
- `GET /repo/file-head?path=` — `git show HEAD:path` (guard `IsRepoRootAsync`)
  pro diff; `WorkspaceGitService.ShowHeadAsync`.
- Degrade uniforme: sem servidor/arquivo → `Available:false`/`locations:[]`,
  nunca 5xx; fora do jail → 400; arquivo inexistente → 404 (format/file-head).

### 3. Client (`IdeEditor.razor`, `Ide.razor`, `ide-editor.js`, `ide-layout.js`)

- JSInvokable: `OnLspCompletionRequested` (retorna itens), `OnDefinitionRequested`
  (1 loc → goto; N → peek list), `OnReferencesRequested` (peek list),
  `OnRenameRequested` (abre barra rename com `wordAt`).
- UI: barra de rename (input + Enter/Esc), peek list flutuante
  (references/definitions), diff overlay `#ide-diff-host` + botão fechar,
  host-2 do split (`#ide-cm-host-2`).
- Teclas/paleta: Ctrl+\ split, Shift+Alt+F format (Fase D), commands
  `split`, `minimap`, `diff_head`.
- Rename aplica edits do ativo via `replaceRange`; tabs limpas de arquivos
  escritos no disco → reload (não-dirty apenas).

## Fora de escopo

- `@codemirror/lint` real (squiggles continuam na camada DOM da facade).
- Peek view inline estilo VS Code (overlay simples por ora).
- Signature help, code lens, inlay hints.

## Verificação

- [x] `dotnet build` Api + Client — 0 erros
- [x] `dotnet test tests/OpenWebUI.Api.Tests --filter Lsp` — 88/88
- [x] `dotnet test tests/OpenWebUI.Client.Tests` — 91/91
- [x] `check-css-classes.py`, `check-form-a11y.py`, `check-i18n-parity.py` — OK (937 chaves)
- [x] Validação visual `/ide` (screenshots) — `docs/screenshots/ide-visual-20261016/`, 0 erros de console (SPEC-20261016-ide-visual-validation).

## Delivered

- Fases A–E mergeadas em #334–#338; bundle CodeMirror com fonte em `tools/ide-bundle`, endpoints LSP (`LspEndpoints.cs`, `WorkspaceFileEndpoints.cs`), client (`IdeEditor.razor`, `ide-editor.js`, `ide-layout.js`).
- Verificação completa: build 0 erros, 88/88 testes LSP, 91/91 testes Client, drift guards OK (937 chaves), validação visual `/ide` em 2026-10-16 — `docs/screenshots/ide-visual-20261016/` (SPEC-20261016-ide-visual-validation, issue #395).
