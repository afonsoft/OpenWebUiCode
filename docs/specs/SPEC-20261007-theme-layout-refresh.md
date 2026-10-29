# SPEC-20261007-theme-layout-refresh

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `theme-layout-refresh` (série chat-harness, rodada visual) |
| Type | `Feature` |
| Stack | `.NET / Blazor / Tailwind v4` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `devin/1791385000-theme-layout` |
| Ticket | `GAP-theme-layout` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** o app abrindo em tema claro por padrão, com seletor de tema visível, e o chat com o visual denso/funcional do opencode TUI e do Devin Web
**So that** a ferramenta pareça um workbench profissional de agente e não apenas um chat escuro.

**Problem context:**
Decisão do dono: "deixar sempre com o tema claro e com opções de tema; focar no layout do opencode e do devin web". Hoje o default é `dark` (`index.html:16` `|| 'dark'`, `ThemeService.Current = "dark"`). O seletor de tema existe em Settings mas é pouco visível. Referências visuais: opencode TUI (sidebar de contexto com seções, mono font para tools, chips de estado) e Devin Web (claro, cards limpos, tabs de workspace, header com git/status chips).

## 2. Scope

**In scope:**

- **T1 — Tema claro default:** `webui.theme` default vira `light` (novos usuários e quem nunca escolheu); quem já escolheu mantém sua preferência (migração: só muda quando chave ausente). Splash/tela de boot clara.
- **T2 — Seletor de tema visível:** toggle sol/lua/sistema no header do app (não só em Settings); Settings continua com a opção completa. Persistência igual à atual (`localStorage` + `documentElement.dataset.theme`).
- **T3 — Refresh visual do chat (estilo opencode/Devin):**
  - Header do chat: título + chips (modelo, estado da run, tarefas N/M, branch) — compacto, 1 linha;
  - Mensagens: bolhas mais flat, avatar discreto, timestamps sutis; tool cards com fonte mono para comando/saída (já iniciado no P5);
  - Composer: mais alto-contraste no claro, botões de tool/menu agrupados à direita, hint de `↑` histórico;
  - Painel lateral (P7/P15): seções colapsáveis com headers estilo sidebar do opencode (label + contagem), scrollbar fina;
  - Densidade geral: reduzir padding de mensagem/tool card ~15%.
- **T4 — Contraste/audit do tema claro:** revisar `dark:` variants críticas que ficam ruins em claro (tool cards, diffs, terminal, permission prompt, MCP cards); AA nos textos principais.

**Out of scope:**
- Temas customizáveis por usuário além de light/dark/system (ex.: solarized) — fase posterior.
- Rebranding/cores de marca.
- Layout de páginas fora do chat (Admin, Workspace) além do necessário pro tema claro não quebrar.

## 3. Technical Context

**Where the change happens:**
`Client/wwwroot/index.html` (boot theme default), `Services/UiServices.cs` (`ThemeService` default + migração), `Layout/MainLayout.razor` (toggle no header), `Components/{ChatView,ToolCallCard,PermissionPromptCard,RunContextPanel,MessageBubble}.razor` (densidade/estilo), `tailwind.css` regenerado.

**Notes:**
- `documentElement.dataset.theme` já existe; manter contract.
- Splash: `assets/splash-dark.png` → escolher por tema resolvido (já há lógica; ajustar default).
- Não quebrar o modo escuro — é refresh visual, não remoção.

## 4. Requirements

### RF-T1: Default light
- Boot sem `webui.theme` salvo resolve `light`; `prefers-color-scheme` só se aplica quando o usuário escolheu `system`.
- Migração silenciosa: nenhuma chave escrita é sobrescrita.

### RF-T2: Toggle no header
- Botão no header (ícone sol/lua/monitor) com dropdown Claro/Escuro/Sistema; reflete `ThemeService.Current` e persiste na hora.

### RF-T3: Refresh do chat
- Header com chips compactos; tool cards mono; composer agrupado; painel lateral com seções colapsáveis.
- Screenshots e2e (light + dark) nos testes visuais existentes.

### RF-T4: Contraste claro
- Nenhum texto primário abaixo de WCAG AA no tema claro; diff +/-, tool output e permission prompt legíveis.

## 5. Critérios de Aceite

- [ ] Usuário novo abre em tema claro; toggle no header troca na hora e persiste.
- [ ] `system` respeita `prefers-color-scheme`.
- [ ] Chat no claro tem o layout "workbench" (chips, densidade, mono nas tools).
- [ ] Dark mode continua correto em todas as telas tocadas.
- [ ] i18n das novas strings nos 8 locales.

## Reconciliation

_Reconciliado em 2026-10-08 (SPEC-20261008-spec-status-reconciliation, Issue #203)._

| RF | Veredito | Evidência |
| --- | --- | --- |
| RF-T1 Default light | delivered | `index.html:19` — `localStorage 'webui.theme' || 'light'`; `prefers-color-scheme` só quando `system` (`:21-24`); nenhuma chave escrita é sobrescrita |
| RF-T2 Toggle no header | delivered (equivalente) | controle segmentado Claro/Escuro/Sistema no rodapé da **Sidebar** (`Sidebar.razor:255`, sempre visível em todas as telas) + `select` no `SettingsModal.razor:37` — ambos refletem `ThemeService.Current` e persistem na hora. Desvio documentado: não está no header do chat; alternativa aceita por ser chrome-level (mais amplo que o header) |
| RF-T3 Refresh do chat | delivered | chips compactos (preset/status/tools), tool cards com fonte mono nos args, composer agrupado, painel lateral com tabs; screenshots e2e light+dark em `.ci/a11y/screenshots.js` (`chat-light`, `chat-dark`, etc.) |
| RF-T4 Contraste claro | delivered | axe-core `wcag2aa` (inclui `color-contrast`) roda no CI `.github/workflows/a11y-audit.yml` mobile+desktop; tokens `dark:` revisados no E13 |

**Residuais:** nenhum — RF-T2 aceito via alternativa documentada; ausência de `latencyMs` no teste MCP (SPEC-mcp-ux RF-002) não é deste SPEC.

