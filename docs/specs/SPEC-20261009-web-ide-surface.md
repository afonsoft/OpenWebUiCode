# SPEC-20261009-web-ide-surface

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `web-ide-surface` (série web-ide, fatia S2 — o "IDE" visível) |
| Type | `Feature` |
| Stack | `.NET 10 / Blazor WASM` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-{YYYYMMDD}-web-ide-surface` |
| Ticket | `GAP-impl-ide-surface` |
| Status | `Completed` |
| Priority | `high` |
| Depends on | `SPEC-20261009-workspace-file-api` (S1) |

## 1. User Story

**As a** usuário
**I want** uma página `/ide` com file explorer, editor de código, abas, diff, terminal e o chat ao lado
**So that** eu possa inspecionar/editar o repo vinculado e acompanhar o agente — como na web app do opencode — sem sair do Open WebUI.

**Problem context:** não existe superfície de edição — o workdir é invisível na UI (só o agente toca via tools). Referência: `packages/app` do opencode (tree + tabs + diff + terminal). Trava WASM: Monaco é ~3MB e exige workers — **default: CodeMirror 6** via JS interop (`[A DEFINIR]` se Monaco for requisito).

## 2. Scope

**In scope:**
- Rota `/ide` (auth; admin ou dono do workspace) + card de entrada no `Workspace.razor` e no chat header quando repo vinculado.
- Layout 3 colunas (responsivo → painéis colapsáveis no mobile): **Explorer** (tree lazy via S1; new file/folder/rename/delete com confirm) · **Editor** (abas, dirty-dot, save `Ctrl+S`/`Cmd+S`, ETag conflict dialog, modo read-only para binary/>cap) · **Direita** (abas: Chat contextual ao repo | Changes (git diff) | Terminal embutido | Jobs).
- Editor: CodeMirror 6 via módulo JS vendored em `wwwroot/js/cm/` (syntax: csharp/js/ts/json/css/html/md/python/yaml); tema claro/escuro seguindo o app; minimap off; search in-file.
- Changes: reuso `WorkspaceGitService.GetInfoAsync` — lista changed files + diff render (mesmo renderer do `ChatWorkspacePanel`).
- Terminal: embed `TerminalView.razor` existente (flag `terminal.enabled` respeitada — esconde aba se off).
- Estado vazio: sem repo vinculado → CTA "Vincular repositório" abrindo `RepoBindingPanel`.
- i18n pt/en/es + demais locales (paridade) — novas chaves `ide.*`.

**Out of scope:**
- LSP/diagnostics no editor (S8).
- Edição colaborativa/multi-cursor.
- Extensões/plugins de editor.

## 3. Technical Context

**AS-IS:** `TerminalView.razor` (PTY via `/ws/terminal/{id}`), `WorkspaceGitService` (branch/numstat/diff), `RepoBindingPanel.razor` (bind), `/workspace` hub. JS estático em `wwwroot/js/` (padrão `boot.js`/interop já existente).

**TO-BE:** `/ide` = superfície única de código.

**Constraints:**
- Editor roda 100% client-side; servidor só persiste via S1.
- Nenhuma dependência JS por CDN — vendored/commitado (offline-friendly, alinhado ao app self-hosted).
- Não editar `tailwind.css` à mão — regenerar se classes novas aparecerem.

## 4. Requirements

### RF-001: Explorer
Tree lazy (expand ao clicar via `tree?path=`), context menu: New file, New folder, Rename, Delete (confirm modal). Ícone por extensão. Mostra branch+dirty badge no topo (de `WorkspaceGitService`).

### RF-002: Editor
Abrir arquivo → aba; dirty tracking; `Ctrl+S` salva via `PUT file` com etag; conflito 409 → dialog (Reload / Overwrite / Copy to new). Arquivo >cap ou binary → view read-only com aviso. Fecha aba com dirty → confirm.

### RF-003: Changes
Aba lista arquivos alterados (status A/M/D + numstat) e diff unificado por arquivo (reuso do renderer `DiffLines`). Refresh manual + refresh automático após save.

### RF-004: Terminal + Jobs
`TerminalView` embutido no painel direito; aba Jobs lista `GET /api/v1/jobs` do workspace (reuse `JobBuiltinTools`/endpoints) com kill.

### RF-005: Chat integrado
Aba Chat do painel direito = `ChatView` embutido num contexto de chat "IDE" criado/sob demanda por usuário (flag `chatContext: "ide"`), ou link "discutir este arquivo" que injeta `@<path>` no composer (S7 faz o mention real — aqui o link pré-preenche o input).

### RF-006: A11y + i18n
Tabs com `role=tablist`, roving tabindex (reuso `openwebui.rovingFocus`), labels via `L[...]`; novas chaves nos 8 locales; passa `check-form-a11y.py`/`check-i18n-parity.py`/`check-css-classes.py`.

## 5. Acceptance Criteria

- [ ] `/ide` com repo vinculado: navega tree, abre/edita/salva arquivo, vê Changes atualizar.
- [ ] Sem repo: estado vazio + CTA de bind funcional.
- [ ] Conflito etag → dialog; binary → read-only.
- [ ] Terminal abre sessão PTY; Jobs lista/mata.
- [ ] Mobile ≤375px: explorer vira drawer; editor usável.
- [ ] Drift guards verdes; `dotnet build`/`Client.Tests` passam.

## 6. Tests

`OpenWebUI.Client.Tests` (bUnit): render do explorer com tree mockada, dirty/save flow, conflict dialog, estado vazio. E2E manual: editar arquivo → rodar `dotnet build` via terminal embutido → Changes mostra diff.

## 7. Rollout

Flag `features.ide` (`ConfigService` + env `IDE_ENABLED`, default on em dev / off em prod até S7?). `[A DEFINIR]` se default-on já na entrega.

## 8. Risks

- **Bundle WASM + CM6:** vendored ~150KB gz minificado — medir no build e citar no PR.
- **Edição concorrente agente↔usuário:** etag 409 cobre; checkpoint/revert (S6) reduz a perda.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/247 |
