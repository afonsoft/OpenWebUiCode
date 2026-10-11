# SPEC-20261011-ide-workbench — Chrome do workbench estilo VS Code (Fase A)

**Status**: Implemented (devin)
**Escopo**: transformar o layout de 3 colunas da `/ide` no workbench VS Code —
activity bar, sidebar com views, panel inferior, status bar, breadcrumbs,
sashes e atalhos globais. **Sem plugins** e sem mudança de backend.
Proposta-mãe: `/ide` VS Code-like (aprovação do usuário na sessão de 2026-10-11).

## Requisitos funcionais

- RF-001 Activity Bar: rail de ícones 44px à esquerda alternando a view da
  sidebar primária (`explorer` | `scm`), badge de arquivos modificados no
  ícone SCM e botão de Zen Mode no rodapé da rail.
- RF-002 Sidebar primária: `explorer` (tree existente) ou `scm` (nova
  `IdeScmView` — checkpoints + lista de arquivos + diff + botão Run tests,
  extraído da antiga aba Changes do painel direito).
- RF-003 Panel inferior (`IdeBottomPanel`): abas Problems | Terminal | Jobs |
  Preview, com as mesmas feature-flags (`terminal.enabled`,
  `preview.enabled`); ✕ fecha; aba Problems lista diagnostics LSP e clica→goto.
- RF-004 Status Bar (`IdeStatusBar`): branch + `*N` sujo à esquerda; contagem
  de erros/avisos LSP, linguagem do arquivo ativo (por extensão), UTF-8 e LF
  à direita; itens clicáveis abrem SCM/Problems/toggle do panel.
- RF-005 Atalhos globais (`js/ide-layout.js` + `[JSInvokable] OnLayoutKey`):
  Ctrl+B sidebar, Ctrl+J panel inferior, Ctrl+\` panel na aba Terminal,
  chord Ctrl+K → Z zen mode.
- RF-006 Sashes arrastáveis (`data-ide-sash`/`data-ide-target`): larguras de
  side/aux e altura do panel persistidos em `localStorage` (`ide:layout:*`)
  e restaurados no primeiro render.
- RF-007 Breadcrumbs acima do editor com os segmentos do caminho ativo.
- RF-008 Zen Mode esconde sidebar, panel inferior e secondary sidebar.
- RF-009 Painel direito passa a secondary sidebar: abas Chat | Pulls | Tests
  (Changes → view SCM; Terminal/Jobs/Preview → panel inferior).
- RF-010 A11y/i18n: roles/aria nas novas regiões, chaves `ide.*` nos 8
  locales; drift guards (`check-css-classes`, `check-form-a11y`,
  `check-i18n-parity`) verdes.

## Não-escopo (fases seguintes da proposta aprovada)

Quick Open/Command Palette/minimap/tabs VS Code (B), busca global,
decorations git, diff editor (C), IntelliSense completo (D).

## Arquivos

- `src/OpenWebUI.Client/Pages/Ide.razor` — layout workbench + sashes + keys.
- `Components/IdeActivityBar.razor`, `IdeStatusBar.razor`, `IdeBottomPanel.razor`,
  `IdeScmView.razor` — novos.
- `Components/IdeEditor.razor` — eventos `OnActiveFileChanged`/
  `OnProblemsChanged` + `GotoLineAsync` público + `Problems` exposto.
- `Components/IdeRightPanel.razor` — abas Chat|Pulls|Tests.
- `wwwroot/js/ide-layout.js` — atalhos + sashes (carregado em `index.html`).
- `tailwind.input.css` (+css gerado), `i18n/*.json` (×8), `index.html`.
