# SPEC-20261016-ide-visual-validation

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `ide-visual-validation` |
| Type | `Tests` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Status | `Approved` |
| Ticket | [#395](https://github.com/afonsoft/open-webui/issues/395) · Epic [#392](https://github.com/afonsoft/open-webui/issues/392) |
| Fonte | `gap-analysis` run 2026-10-16 — gap `GAP-tests-ide-visual` (único item pendente do SPEC-20261011-ide-bundle) |

## 1. User Story

**As a** mantenedor do fork
**I want** a validação visual da superfície `/ide` que ficou pendente no SPEC-20261011-ide-bundle
**So that** a Fase E do IDE seja declarada encerrada com evidência e o SPEC possa ser arquivado como Completed.

**Problem context:**
`.specs/SPEC-20261011-ide-bundle.md` marca como entregues: build 0 erros, 88/88 testes LSP, 91/91 testes Client, drift guards OK. Único checkbox aberto: *"Validação visual `/ide` (screenshots) — pedida pelo usuário nesta sessão."* Sem isso, o SPEC fica "Em execução" indefinidamente.

## 2. Scope

**In scope:**
- Subir a app localmente (`dotnet run --project src/OpenWebUI.Api`), login admin seeded, abrir `/ide` com um workspace repo bound.
- Screenshots desktop + mobile: activity bar, explorer, editor (CodeMirror + LSP), search, SCM, bottom panel (terminal/tests), preview, status bar.
- Registrar evidência no SPEC `ide-bundle` (ou neste) e marcar o item.

**Out of scope:**
- Novas features do IDE; correções de issues visuais encontrados (viram follow-ups separados).

## 3. Technical Context

- Página: `src/OpenWebUI.Client/Pages/Ide.razor` (`@page "/ide"`); entrada via ícone no `ChatView.razor:90` e `Workspace.razor:39`.
- Feature flag `ide.enabled` (default on) — `IdeEndpoints.cs`.
- Skill local `e2e-test-dotnet-chat-apps` documenta como rodar esse app (:8080, credenciais seeded, quirks Blazor) — usar como guia.
- Browser preview tooling disponível no ambiente.

## 4. Requirements

- **RF-001:** `/ide` renderiza sem erro de console; todos os painéis presentes.
- **RF-002:** screenshots anexados ao SPEC/arquivados em docs (sem PII).
- **RF-003:** checkbox do `ide-bundle` marcado + status atualizado.

## 5. Acceptance Criteria

- [x] Evidência visual existe e referencia commit — `docs/screenshots/ide-visual-20261016/` (8 PNGs), app `dotnet run` :8080, usuário e2e, repo `afonsoft/open-webui@main` bound.
- [x] `ide-bundle` sai de "Em execução" → Completed → `docs/specs/`.
- [x] Issues visuais encontrados são reportados como pendências separadas — nenhum encontrado.

## 6. Task Plan

- [x] T1 — app :8080 + usuário e2e (signup) + repo bound via `POST /api/v1/workspace/repo/open`.
- [x] T2 — painéis desktop/mobile capturados via Playwright/Chromium headless.
- [x] T3 — evidência registrada + SPECs atualizados.

## Delivered

- Navegação `http://localhost:8080/ide` autenticada: **0 erros de console, 0 pageerror, 0 requestfailed**.
- Activity bar: Explorer, Busca, Controle de Código-Fonte, Outline, Modo Zen — todas as views clicáveis e renderizando.
- Explorer: árvore do repo bound `afonsoft/open-webui@main` (header correto). SCM: "Árvore de trabalho limpa" + "Rodar testes". Painel direito: abas Chat/PRs/Testes. Status bar: branch, problemas, UTF-8, LF. Editor CodeMirror (`.cm-editor`) montado no DOM. Mobile 390×844: layout responsivo (hamburger + activity bar colapsada).
- Screenshots: `docs/screenshots/ide-visual-20261016/{ide-desktop,ide-editor,ide-mobile,ide-view-*}.png`.
- Issue #395; SPEC `ide-bundle` arquivado como Completed em `docs/specs/`.
