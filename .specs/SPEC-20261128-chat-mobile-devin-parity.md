# SPEC-20261128-chat-mobile-devin-parity — chat estilo Devin web (mobile-first)

**Status**: Approved — plano de melhorias aprovado 2026-11-28

## Motivação

A tela de chat do OpenWebUiCode deve se aproximar do Devin web,
principalmente no mobile: status persistente do agente, tool calls
agrupadas e colapsáveis, indicador de progresso das tarefas e thinking
não-intrusivo.

## Design

### 1. Status line persistente (`RunStatusBar` no `ChatView`)

Barra fixa no topo do stream quando há run ativa:

- Conteúdo: ícone animado + "Trabalhando… etapa {N}" + tarefa atual
  (primeiro item `in_progress` da lista de tarefas, se houver) +
  progresso `{done}/{total}`.
- Mobile: sempre visível (hoje o chip do header é `hidden sm:`); toque
  abre o drawer de workspace na aba Tarefas.
- Desktop: posição atual do chip mantida, mesma info consolidada.

### 2. Agrupamento de tool calls (`ToolCallGroup` no `MessageBubble`)

Tool calls consecutivas do mesmo step vira um card colapsável único:

- Header: "Trabalho do agente · {N} ações" + resumo dos verbos
  (read ×3, grep ×2, edit ×1) + spinner enquanto step aberto.
- Expandido: lista compacta de cada tool (ícone + título 1 linha +
  status), com drill no card individual existente.
- No mobile: colapsado por padrão; no desktop: expandido até N>3 itens.
- Persistir estado expanded/collapsed por mensagem na sessão.

### 3. ThinkingSection + bloco de trabalho fluido (pedido 2026-11-28)

- Mobile (`<sm`): thinking inicia colapsado, mostrando só a 1ª linha +
  "Ver raciocínio"; desktop mantém comportamento atual.
- Durante streaming: "Pensando…" com dots (já existe) — manter.
- **Bloco "trabalhando" fluido estilo Devin web**: enquanto a run está
  ativa, o processo em andamento (steps/tool calls/thinking) fica num
  bloco único vivo com header colapsável (minimizar/maximizar) —
  substitui a leitura seca de "passos"/"pensando" por um feed contínuo:
  linha atual da ação (ex.: "Lendo src/Foo.cs…", "Rodando dotnet test…"),
  contador de ações e o thinking inline. Colapsado mostra o resumo em
  1 linha; expandido o feed scrollável. Persistir collapsed por chat na
  sessão.

### 4. Aba Tarefas — badge de progresso no mobile

- Botão flutuante no canto inferior direito do stream (acima do
  composer, `safe-area` aware) visível só quando a run tem lista:
  `{done}/{total}` + anel de progresso; toque abre o drawer na aba
  Tarefas.
- Drawer mobile já existe (`ChatWorkspacePanel` drawer) — reutilizar.

### 5. Composer mobile

- Viewport: garantir `100dvh` + `env(safe-area-inset-bottom)` no
  container do stream e composer (verificar keyboard-open).
- Esconder chips secundários do header já coberto; revisar densidade
  vertical (padding do stream, separadores) para ganhar ~2 linhas de
  mensagem.

### Tailwind

- Regenerar `tailwind.css` ao final (mesma versão pinned do CI).

## Testes

- Unit: agrupamento de tool calls (boundary por step, resumo de verbos).
- i18n paridade nos 8 locales para as novas chaves.
- E2E mobile viewport (openwebui-dotnet-e2e skill): status bar visível,
  badge progresso abre drawer, card doc renderiza.

## Critérios de aceite

1. Run ativa → status line sempre visível no mobile com etapa e tarefa.
2. ≥2 tool calls consecutivas renderizam como grupo colapsável.
3. Botão de progresso abre a aba Tarefas no mobile.
4. Thinking colapsado por padrão no mobile.
5. `dotnet build` 0 warnings; `check-css-classes`/`check-i18n-parity`
   verdes.
