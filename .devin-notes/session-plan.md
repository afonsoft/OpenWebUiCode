# Session plan — 2026-10-11 (open-webui)

Aprovado pelo Afonso: corrigir 2 bugs reportados + implementar TODAS as fases de melhoria da /ide (uma branch/PR por spec, em sequência).

## Bug 1 — "Análise e Build do Repositório: falhou — element of type 'Object'...'String'"
Run falhou com InvalidOperationException de System.Text.Json.
Causa provável: `src/OpenWebUI.Infrastructure/ChatTools/BuiltinToolRegistry.cs` ExecuteAsync (linha ~122-133):
`args = JsonDocument.Parse(argumentsJson).RootElement` — se `argumentsJson` vem double-encoded (string com JSON dentro, comum no Gemini), RootElement é ValueKind=String → `builtin.ExecuteAsync(args)` → `args.TryGetProperty(...)` explode "requires an element of type 'Object', but the target element has type 'String'".
Mesmos pontos vulneráveis: `PermissionRuleset.cs:~161` e `ToolCallRiskClassifier.cs:~114,~130` (TryGetProperty em RootElement sem guardar ValueKind).
FIX: helper que normaliza args — se root é String e contém JSON, re-parse; se ainda não for Object, retornar erro claro da tool em vez de throw. Aplicar nos 3 arquivos + testes NUnit.

## Bug 2 — fila de tarefas não avança sozinha
`mode=queue` em `ChatRunSteers` só promove via `PollSteerAsync(queuedOnly)` dentro da run viva. Run finaliza (completed/failed/stopped) com steers pendentes → órfãos, usuário precisa mandar "Continue".
FIX: quando a run terminar, se houver steers pending mode=queue no chat → criar nova ChatRun com a próxima mensagem (promover para ChatMessage user + marcar steer promoted), dispatcher.Enqueue. Repetir até fila zerar (cada nova run repete o drain). Continuar também após falha? SIM — fila é work-queue; tarefa falha não bloqueia a próxima (documentar). Arquivos: `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` (FinishAsync/drain), `ChatRunDispatcher.cs`, `ChatRunEndpoints.cs`.

## IDE — proposta aprovada (doc: /home/ubuntu/ide-vscode-proposta.md anexado à sessão)
Fase A (workbench, só frontend): Activity Bar ícones, Panel inferior (terminal/problems/jobs/preview) arrastável, Status Bar (branch+dirty, erros, Ln:Col, indent, encoding, EOL, lang), breadcrumbs, sashes resizable persistidos (BrowserStorage), zen mode (Ctrl+K Z), Ctrl+B/J/` toggles, welcome tab c/ atalhos.
Fase B (editor): Quick Open Ctrl+P (precisa endpoint flat GET /files — tree hoje depth≤3), Command Palette Ctrl+Shift+P/F1, Go to Line Ctrl+G, Go to Symbol Ctrl+O (parser naive), minimap @replit/codemirror-minimap vendored, tabs VS Code (preview itálico/pin double-click, middle-click close, context menu Close Others/Right/All/Copy Path/Reveal, drag reorder), multi-cursor Alt+click (rectangularSelection/crosshairCursor no bundle CM), editor settings popover (tab size, word wrap Alt+Z, whitespace, font, minimap on/off), autosave (off/afterDelay/onFocusChange), split editor Ctrl+\.
Fase C (search/SCM): Global Search Ctrl+Shift+F (novo endpoint GET /workspace/repo/search?q= via jail ResolveInsideFinal + CollapsedDirs), explorer context menu right-click, git decorations na tree (M/A/D/? colors + dir badges), SCM view w/ commit box (decisão: git write ops — flag), diff editor @codemirror/merge, gutter diff markers.
Fase D (IntelliSense, backend): completion Ctrl+Space, go to def F12, references Shift+F12, rename F2, format Shift+Alt+F, outline (documentSymbol) na sidebar + breadcrumbs símbolo.

## Constraints do repo
- JS 100% vendored (sem CDN). CodeMirror 6 mantido (Monaco vetado). bundle em src/OpenWebUI.Client/wwwroot/js/ide-editor.bundle.js — precisa rebuild para novos módulos CM.
- i18n nos 8 locales (src/OpenWebUI.Client/wwwroot/i18n/*.json), drift guards: python3 tools/check-css-classes.py + check-form-a11y.py + check-i18n-parity.py.
- Tailwind: regenerar wwwroot/css/tailwind.css após novas classes (tailwindcss -i ... -o ... --minify).
- Testes: dotnet test tests/OpenWebUI.Api.Tests e tests/OpenWebUI.Client.Tests; bUnit pra componentes.
- Branch por spec; commits conventional (en); docs/comentários pt-BR; NUNCA push direto em main.
- Layout atual /ide: Pages/Ide.razor (header + 3 colunas), Components/Ide{Explorer,Editor,TreeNode,RightPanel,TestsPanel,PreviewPanel}.razor, Services/IdeTestRunService.cs, Endpoints/IdeEndpoints.cs (flag ide.enabled), WorkspaceFileEndpoints (/tree /file PUT+GET /mkdir /rename /delete /git), LspEndpoints (/status /doc /diagnostics /hover), TerminalView (PTY /ws/terminal).
- Toast "X: falhou — err" vem de ChatNotificationsService.HandleRunCompleted (toast sobre run.failed).
