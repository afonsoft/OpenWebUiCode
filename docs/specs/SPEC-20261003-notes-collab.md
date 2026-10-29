# SPEC-20261003-notes-collab

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `notes-collab` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261003-notes-collab` |
| Ticket | Issue #85 |
| Status | `Completed` |

## 1. User Story

**As a** usuário de notas compartilhadas
**I want** edição colaborativa em tempo real
**So that** notes alcança paridade com o editor colaborativo do upstream (yjs).

**Problem context:**
Upstream usa yjs + websocket para edição simultânea com awareness (cursores). Hoje: editor local com save manual; grants já permitem compartilhar, mas edições concorrentes sobrescrevem.

## 2. Scope

**In scope:**
- Hub SignalR `notes:{id}`: broadcast de ops (intervalo simples: texto + versão; last-write-wins com merge básico por posição) OU avaliar yjs via servidor dedicado — `[A DEFINIR]` no spike: preferir implementação simples nativa se yjs exigir runtime JS.
- Awareness: presença + cursores remotos no editor (cores por usuário).
- Conflito: versão da nota (`UpdatedAt`) rejeita writes defasados → cliente sincroniza.

**Out of scope:**
- Offline-first CRDT completo; undo colaborativo.

## 3. Technical Context

**Files to read:**
- `src/OpenWebUI.Api/Endpoints/WorkspaceEndpoints.cs` (notes)
- `src/OpenWebUI.Api/Hubs/ChatHub.cs` (padrão de grupos)
- `src/OpenWebUI.Client/Pages/Notes.razor` / `NoteEditor.razor`

## 4. Requirements

### RF-001: Sync de edição
- **Description:** digitar em uma sessão reflete na outra <1s; usuários sem write grant não emitem ops.

### RF-002: Awareness
- **Description:** lista de presentes + posição de cursor colorida.

## 5. API Contract

Hub `/ws` grupo `note:{id}`: `note:join`, `note:update` {ops|text,version}, `note:presence`.

## 6. Critérios de Aceite

- [ ] Duas sessões editam a mesma nota com propag. automática.
- [ ] Grant read-only não consegue publicar ops (server rejeita).
- [ ] Testes de hub com 2 conexões SignalR cliente.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/105 |
| Issue | https://github.com/afonsoft/OpenWebUiCode/issues/85 |
| Epic | https://github.com/afonsoft/OpenWebUiCode/issues/79 |

Spike `[A DEFINIR]` (yjs vs nativo): decidido por implementação nativa SignalR com last-write-wins por `UpdatedAt` — yjs exigiria runtime JS no servidor, fora do stack .NET-only.
