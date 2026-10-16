# SPEC-20261011-ide-editor-core — Core do editor estilo VS Code (Fase B)

**Status**: Completed
**Escopo**: Quick Open, Command Palette, Go to Line/Symbol, comportamentos
de aba VS Code e autosave. Continuação da SPEC-20261011-ide-workbench (A).

## Requisitos funcionais

- RF-001 Quick Open (`IdeQuickOpen.razor`): modal no topo com fuzzy match
  (subsequência, bônus de basename/consecutivo/token) sobre a lista flat de
  arquivos do workdir (walk recursivo da tree API, cap 4000, lazy).
- RF-002 Prefixos do input: vazio → arquivos; `>` → Command Palette;
  `:` → Go to Line; `@` → Go to Symbol do documento ativo.
- RF-003 Navegação: ↑/↓ cicla seleção, Enter executa, Esc fecha; click-out
  fecha; foco automático no input.
- RF-004 Command Palette (Ctrl+Shift+P): Save, Save All, Run Tests,
  Toggle Sidebar/Panel/Secondary/Zen, Go to Line, Close All Tabs,
  Toggle Auto Save — labels localizadas.
- RF-005 Go to Line (Ctrl+G) e Go to Symbol (Ctrl+O): symbols por regex
  leve sobre o doc ativo (C#, JS/TS, Python, markdown headers).
- RF-006 Tabs VS Code: abertura via explorer/quick-open cria aba *preview*
  (itálico) substituída pela próxima — dblclick na aba ou qualquer edição
  "pinha"; middle-click fecha; menu de contexto (Close, Close Others,
  Close to the Right, Close All, Keep Open, Copy Path).
- RF-007 Autosave: `File: Toggle Auto Save` persiste `ide:layout:autosave`
  em localStorage; didChange agenda save debounced 900ms por aba.
- RF-008 Atalhos globais novos em `ide-layout.js`: Ctrl+P, Ctrl+Shift+P,
  Ctrl+G, Ctrl+O (preventDefault + `.NET` dispatch).

## Não-escopo (bundle-bound)

Minimap (`@replit/codemirror-minimap`), multi-cursor (Alt+click) e split
editor exigem rebuild do bundle `ide-editor.bundle.js` vendored — sem fonte
de build no repo. Itens registrados para SPEC futura quando o bundle tiver
pipeline (Fase D pode trazer junto com LSP completion).

## Arquivos

- `Components/IdeQuickOpen.razor` — modal + fuzzy + 4 modos.
- `Utils/IdeQuickOpenFuzzy.cs` — scoring (NUnit puro).
- `Components/IdeEditor.razor` — preview/pin/middle/menu/autosave/save-all/
  GetSymbols.
- `Pages/Ide.razor` — wiring (OnLayoutKey → quickopen/palette/gotoline/
  symbol, walk de arquivos, dispatch da paleta).
- `wwwroot/js/ide-layout.js` — teclas novas + copyText/autosave helpers.
- `tailwind.input.css` (+css gerado) — `.ide-menu-item`.
- `i18n/*.json` (×8) — 18 chaves novas `ide.*`.
- `tests/OpenWebUI.Client.Tests/IdeQuickOpenFuzzyTests.cs`.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/335 |
