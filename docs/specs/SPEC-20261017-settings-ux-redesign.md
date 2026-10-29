# SPEC-20261017-settings-ux-redesign

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `settings-ux-redesign` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261017-settings-ux-redesign` |
| Ticket | [#437](https://github.com/afonsoft/OpenWebUiCode/issues/437) |
| Status | `Delivered` |
| Fonte | pedido do usuário: melhorar telas de configurações (admin + usuário), usabilidade mobile, botões salvar, categorização e cobertura das features implementadas |

## 1. User Story

**As a** usuário (e admin) do Open WebUI
**I want** telas de configurações organizadas em categorias, com ação de salvar clara e consistente, e acesso às features novas (agents, combos, automations, personas, external agents, maintenance, MCP…)
**So that** eu encontro cada opção onde espero, salvo com confiança e consigo usar tudo no celular sem rolar/cacear.

**Problem context:**
- `SettingsModal.razor` (~2000 linhas): 9–11 tabs flat (`general, account, memories, notifications, github, models, tools, [connections, admin], about`) — campos empilhados sem agrupamento; cada seção tem seu próprio botão salvar com estilos inconsistentes (`.auth-submit` vs `.msg-action` inline).
- `Admin.razor` (/admin): 6 tabs (`users, chats, analytics, evaluations, leaderboard, config`) — a aba `config` é uma lista longa sem categorias.
- Mobile: rail de tabs é uma fileira de botões que estoura a largura; tap targets < 44px; modal não ocupa a tela inteira; feedback de save é um `.field-note` fácil de perder.
- Features entregues sem superfície em settings: `agents` (+ opt-in workspace), `agent-personas`/`channels`, `combos`, `external-agents`, `automations`, `maintenance`, `mcp`, `hooks`, `jobs`, `skills` do repo, `notes`, `knowledge`…

## 2. Scope

**In scope:**
- RF-001: categorias com headers fixos dentro de cada tab (usuário e admin) — ex.: General → Aparência · Áudio · Idioma; Account → Perfil · Segurança · API keys.
- RF-002: save affordance unificado — barra sticky no rodapé da tab ativa com estado (pristine/dirty/saving/saved+toast); remove os `.msg-action` avulsos de salvar.
- RF-003: mobile — rail de tabs vira chips scrolláveis horizontalmente (ou dropdown), tap targets ≥44px, modal full-height em viewport < 640px, inputs 100% width.
- RF-004: feature coverage — novas tabs/seções para: **Agents** (definições + opt-in `agents.workspace.enabled`), **Combos** (CRUD de model combos), **Automations**, **External agents**, **Personas/Channels**, **Maintenance**, **Integrations** (github + mcp + hooks juntos), **Skills** (catálogo read-only + instruções).
- RF-005: admin parity — aba `config` do /admin ganha as mesmas categorias + toggles globais das features (flags de runtime já existentes: `RepoCommands:AllowShellExpansion`, `ChatRuns:MaxConcurrent`, etc. quando expostos via configs).
- RF-006: a11y — `aria-live` no feedback de save, foco visível, ordem de tab correta, `role=tablist` já existe (manter).

**Out of scope:**
- Backend novo — só consumir endpoints existentes (`/api/v1/agents`, `/combos`, `/automations`, `/external-agents`, `/agent-personas`, `/maintenance`, `/mcp`, `/skills`, `/configs`…). Toggles globais ainda não expostos ficam documentados como follow-up, não implementados nesta fatia.
- Rebranding visual/tema.
- Refatorar o estado interno de cada formulário (só o container/save bar).

## 3. Technical Context

- `src/OpenWebUI.Client/Components/SettingsModal.razor` — shell do modal (`modal-shell`/`modal-tabs`/`modal-body`), 24 blocos `_tab == "x"`, saves dispersos (`SaveProfileAsync`, `SaveConnectionsAsync`, `SaveGitHubTokenAsync`, `SaveAdminAsync`, `SaveMemSyncAsync`, `SaveWebhookAsync`, `SaveAdminWebhookAsync`, `SaveAdminGitHubAsync`, `SaveByokAsync`…).
- `src/OpenWebUI.Client/Pages/Admin.razor` — rail + 6 seções; `config` é flat.
- `src/OpenWebUI.Client/Services/ApiService.cs` — métodos REST existentes; adicionar wrappers apenas onde faltar (`GetCombosAsync`, `GetAgentsCatalogAsync`, `GetAutomationsAsync`, `GetExternalAgentsAsync`, `GetPersonasAsync`, `GetMaintenanceAsync`, `GetMcpServersAsync`, `GetSkillsCatalogAsync`).
- CSS: `wwwroot/css/tailwind.css` é gerado (não editar manual); classes novas → regerar com `tailwindcss -i … --minify` + rodar `tools/check-css-classes.py`.
- i18n: novas chaves nos 8 locales — `tools/check-i18n-parity.py` guarda drift.
- Componente novo sugerido: `SettingsSection.razor` (header + content) e `SettingsSaveBar.razor` (sticky footer + estado dirty/saving/saved) — reutilizados pelas duas telas.

## 4. Requirements

### RF-001: Categorias
- Cada tab vira lista de `SettingsSection` (header sticky + descrição curta + campos). Ordem estável, id para âncora (`#general-aparencia`).
- Aceite: toda tab tem ≥1 seção nomeada; nenhum campo órfão fora de seção.

### RF-002: Save bar unificada
- Componente `SettingsSaveBar` por tab: `Salvar` primário + `Descartar` (quando há dirty) + feedback (`Salvo ✓` / erro) via `aria-live`.
- Dirty tracking mínimo: comparar estado inicial vs atual por formulário (já existem objetos de settings por tab — reusar).
- Aceite: nenhum `.msg-action`/`auth-submit` salta solto dentro de seções; todos os saves passam pela barra.

### RF-003: Mobile
- `modal-tabs` → chips com `overflow-x:auto` + `scroll-snap`, min-height 44px; em ≤640px o modal vira full-screen sheet (`inset:0`, sem margin/radius excessivo).
- Campos: `display:block; width:100%`; selects/inputs min-height 44px.
- Aceite: axe mobile viewport 375px sem violação nova (CI já tem o job `axe-core mobile 375px`).

### RF-004: Feature coverage (read/edit onde o endpoint permite)
- **Agents**: lista defs (builtin/global/admin/user/workspace + scope badge) + toggle `agents.workspace.enabled` + link "gerenciar" se houver CRUD.
- **Combos**: lista + criar/editar (name, strategy, models[]) — endpoints `/api/v1/combos` existem.
- **Automations**: lista + toggle enabled + executar agora.
- **External agents**: lista + toggle enabled + testar conexão.
- **Personas**: lista + criar + editar básico.
- **Maintenance**: status + executar (dry-run vs apply).
- **Integrations**: github token (move de `github` tab) + `mcp` (lista servers do `.agents/mcp.json`, read-only + status) + `hooks` (lista/toggle).
- **Skills**: catálogo read-only (nome/desc/escopo) + toggle project-instructions se houver config.
- **About** permanece; `connections`/`admin` (existentes) viram seções categorizadas sem mudança funcional.

### RF-005: Admin /admin config
- Mesmas categorias; incluir toggles globais disponíveis hoje via `/api/v1/configs` (webhook, default role, signup enabled, api keys) + seção **Features** com os switches globais que já existirem (não criar novos endpoints).
- Aceite: paridade de categorização com o modal do usuário.

### RF-006: A11y
- `role=tablist`/`tab`/`tabpanel` já existem — manter; `aria-live="polite"` na save bar; foco não se perde após save.

## 5. Acceptance Criteria

- [ ] Usuário acha qualquer setting em ≤2 níveis (tab → seção) sem rolagem infinita.
- [ ] Em 375px: tabs viram chips scrolláveis, modal full-height, todo controle ≥44px, nenhum conteúdo cortado.
- [ ] Toda tab tem exatamente 1 ação de save consistente (barra sticky) com estado dirty→saving→saved visível.
- [ ] Agents/Combos/Automations/External agents/Personas/Maintenance/Integrations(MCP+hooks)/Skills aparecem e funcionam via endpoints existentes.
- [ ] CI verde: `check-css-classes`, `check-i18n-parity`, `axe mobile`, `dotnet test`.

## 6. Task Plan

- [ ] T1 — `SettingsSection` + `SettingsSaveBar` + refator das tabs existentes em categorias (usuário).
- [ ] T2 — mobile polish (chips rail, full-sheet, tap targets).
- [ ] T3 — novas tabs de features (Agents, Combos, Automations, External, Personas, Maintenance, Integrations, Skills) via endpoints existentes.
- [ ] T4 — `/admin` config: categorias + seção Features (só toggles existentes).
- [ ] T5 — i18n nos 8 locales + regen tailwind.css + testes (Bunit dos componentes novos + endpoints já cobertos).
- [ ] T6 — docs (`docs/features/settings*`) + screenshots de smoke no CI existente.
