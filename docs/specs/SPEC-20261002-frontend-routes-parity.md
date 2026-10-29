# SPEC-20261002-frontend-routes-parity

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `frontend-routes-parity` |
| Type | `Frontend` |
| Stack | `Blazor WASM` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261002-frontend-routes` |
| Ticket | Issue #49 |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** as rotas de página dedicadas do upstream (create/edit/[id] por entidade)
**So that** a navegação e URLs batam com o original e deep-links funcionem.

**Problem context:**
Upstream tem 47 rotas; nosso cliente tem ~24. Tudo existe como abas/modais, mas faltam URLs dedicadas: `/workspace/{models,prompts,tools,knowledge}/create|edit|[id]`, `/notes/[id]|/notes/new`, `/automations/[id]`, `/folders/[folderId]`, `/playground/{completions,images}`, `/admin/{users,evaluations,settings}/[tab]`, `/admin/functions`, `/watch`.

## 2. Scope

**In scope:**
- Rotas dedicadas para cada entidade de workspace: `create`, `[id]`/`edit` — mesmos formulários das abas, apenas roteáveis.
- `/notes/[id]` e `/notes/new` (editor dedicado — hoje inline).
- `/automations/[id]` detalhe (runs do item).
- `/folders/[folderId]` (lista de chats da pasta, compartilhável).
- `/playground/completions` e `/playground/images` como rotas (mesma UI com tab inicial).
- `/admin/.../[tab]` deep-link para aba.
- `/watch` — página de eventos/atividade (se upstream usar para channels; caso contrário omitir — verificar).
- Redirects: `/` → `/home`? upstream usa `(app)/home` — alinhar.

**Out of scope:**
- `/admin/functions`, `/workspace/{functions,skills}/*` — dependem do SPEC plugin-ecosystem.

## 3. Technical Context

**Where:** `src/OpenWebUI.Client/Pages/*.razor` (novos arquivos por rota), `App.razor` router, `Sidebar`/`Workspace` links → navegação real.

**Files to read:**
- `src/OpenWebUI.Client/Pages/{Workspace,Notes,Automations,Playground,Admin}.razor`
- `src/OpenWebUI.Client/App.razor`, `Components/Sidebar.razor`

**Files to create/modify:**
```text
src/OpenWebUI.Client/Pages/WorkspaceItem.razor (rota genérica /workspace/{kind}/{id}?)
src/OpenWebUI.Client/Pages/{NoteDetail,NoteNew,AutomationDetail,FolderPage}.razor
src/OpenWebUI.Client/Pages/Playground.razor (sub-rotas)
src/OpenWebUI.Client/Pages/Admin.razor (route param tab)
```

## 4. Requirements

### RF-001: Rotas CRUD de workspace
- **Description:** `/workspace/{kind}/create` e `/workspace/{kind}/{id}` abrem o formulário correspondente para `models|prompts|tools|knowledge`.
- **Rules:** URLs canônicas do upstream; back-button funciona.
- **Input → Output:** rota → form pré-carregado

### RF-002: Rotas de entidade
- **Description:** `/notes/{id}`, `/notes/new`, `/automations/{id}`, `/folders/{id}`.
- **Rules:** ids inexistentes → NotFound existente.

### RF-003: Deep-link admin tabs
- **Description:** `/admin/{users,evaluations,settings,analytics}/{tab?}` ativa a aba via route param.

### RF-004: Playground sub-rotas
- **Description:** `/playground/{completions,images}` com tab inicial correspondente.

**Invariants:** nenhuma rota nova quebra o fallback SPA; rotas i18n ok.

## 5. API Contract

Sem novos endpoints — roteamento apenas.

## 6. Critérios de Aceite

- [ ] Cada rota upstream listada resolve no Blazor (sem 404).
- [ ] Links internos usam as novas rotas (não só abas).
- [ ] Back/forward do browser alterna abas corretamente.
- [ ] Testes de roteamento (bUnit ou endpoints + revisão manual).

## 7. Notas

Estratégia: reutilizar componentes existentes extraindo form de `Workspace.razor` para componentes roteáveis — evita duplicar lógica de save.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/64 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/49
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/48
