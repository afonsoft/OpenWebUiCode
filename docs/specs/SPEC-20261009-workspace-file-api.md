# SPEC-20261009-workspace-file-api

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `workspace-file-api` (série web-ide, fatia S1) |
| Type | `Feature` |
| Stack | `.NET 10 / ASP.NET Core Minimal API` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-{YYYYMMDD}-workspace-file-api` |
| Ticket | `GAP-impl-workspace-file-api` |
| Status | `Completed` |
| Priority | `high` — desbloqueia S2 (IDE surface) e S3 (skills do repo) |
| Depends on | — |

## 1. User Story

**As a** usuário com um repositório vinculado ao workspace
**I want** uma API REST para listar, ler, escrever e renomear arquivos do meu workdir
**So that** a UI possa renderizar um file explorer/editor (S2) e a descoberta de skills (S3) possa ler o repo — sem depender de tools de agente.

**Problem context:** hoje os arquivos do workdir só são acessíveis via tools builtin (`file_*` em `ChatTools/Tools/FileBuiltinTools.cs`) dentro de uma run. Não existe endpoint para a UI navegar o repo — `/workspace/files` (`WorkspaceEndpoints.cs`) lista a biblioteca de uploads do RAG, não o workdir.

## 2. Scope

**In scope:**
- `GET /api/v1/workspace/repo/tree?path=&depth=` — árvore paginada (lazy), entradas `{name, path, type: file|dir, size?}`; `.git/` excluído; cap `WorkspaceFiles.MaxEntries`.
- `GET /api/v1/workspace/repo/file?path=` — conteúdo texto paginado (`offset`/`limit` por linhas), binary-guard reutilizando `WorkspaceFiles.LooksBinary` + `MaxFileBytes`, ETag/`Last-Modified` para cache.
- `PUT /api/v1/workspace/repo/file?path=` — escrita (If-Match opcional por Last-Modified), rejeita binary; resposta inclui hash/`Last-Modified` novo.
- `POST /api/v1/workspace/repo/mkdir`, `POST /delete`, `POST /rename` — operações de estrutura.
- Auth: `RequireAuthorization` + apenas o dono do workspace (mesmo guard de `/api/v1/workspace/repo` existente — `GitHubEndpoints.cs:27-29`).
- Quando nenhum repo está vinculado: responde `404` + `{ detail, bound: false }` — a UI mostra estado "vincule um repo".

**Out of scope:**
- Upload/download binário (imagens) — só texto na v1.
- git ops além de leitura de status (já cobertos por `WorkspaceGitService`).
- watching/notificações de mudança externa (fase posterior).

## 3. Technical Context

**AS-IS (evidência):**
- Jail + helpers já existem: `WorkspaceFiles.ResolveInside` (path traversal, `..`, symlink rejeitados), `LooksBinary`, `MaxEntries`, `MaxFileBytes`, `RelativeOf` — `Infrastructure/ChatTools/WorkspaceFiles.cs` (usado por `FileBuiltinTools.cs`).
- Workdir do usuário: `WorkspaceRepoService.ResolveWorkdirAsync(userId)` → `data/workspaces/{userId}` ou clone do repo vinculado (`WorkspaceRepoService.cs:35`).
- Padrão de endpoints: Minimal API groups sob `/api/v1/` (`GitHubEndpoints.cs` como modelo — auth bearer + validação + `Results`).

**TO-BE:** endpoints REST estáveis para o explorer do IDE e para o `SkillDiscoveryService` (S3).

**Constraints:**
- Todo path resolve via `ResolveInside` — zero exceções.
- Respostas paginadas: NUNCA devolver diretório/arquivo inteiro sem paginação.
- Sem cache server-side que sirva conteúdo stale — ETag por mtime+size.

## 4. Requirements

### RF-001: Tree endpoint
`GET /api/v1/workspace/repo/tree?path={rel}&depth={1-3}&cursor=` retorna entradas ordenadas (dirs primeiro), lazy com cursor; raiz `path=""`. `.git`, `bin`, `obj`, `node_modules` colapsados (existem na resposta, `collapsed: true`, sem filhos por default).

### RF-002: Read endpoint
`GET .../file?path=&startLine=&maxLines=` retorna `{path, content, totalLines, truncated, etag}`. Binary → `415`. >`MaxFileBytes` → `413` com `size` no body para a UI decidir.

### RF-003: Write/rename/mkdir/delete
`PUT .../file` cria ou sobrescreve; `If-Match` (etag) divergente → `409` (conflict para a UI oferecer merge/descartar). `POST /mkdir {path}`, `/rename {from,to}`, `/delete {path}`. Todos retornam `{ok, path}` ou `{ok:false, error}` amigável. Nenhuma operação cria path fora do jail.

### RF-004: Sem repo vinculado
Todos os endpoints → `404` + `{detail:"Nenhum repositório vinculado.", bound:false}` quando `GetBindingAsync` é null.

### RF-005: Auditoria
Operações de escrita registram log estruturado (`ILogger`, userId+path+bytes) — trilha de auditoria do IDE.

## 5. Acceptance Criteria

- [ ] Tree/read/write/rename/delete funcionam num workdir com repo vinculado; `..`/symlink/absoluto → rejeitados (teste).
- [ ] Sem repo vinculado → 404 `bound:false` em todos.
- [ ] Arquivo binário e >cap → respostas corretas (415/413).
- [ ] `If-Match` stale → 409.
- [ ] Endpoints não aparecem para usuário não autenticado (401/403).

## 6. Tests

`tests/OpenWebUI.Api.Tests` (NUnit + WebApplicationFactory): fixture com workspace temp + repo fake; casos de jail (`../`, symlink, path absoluto), paginação da tree, etag/conflict, binary-guard, 401 sem auth.

## 7. Rollout

Sem flag — API nova, inativa até a UI (S2) consumir. Documentação em `docs/{en,pt}/` segue na S2.

## 8. Risks

- **Escopo de escrita aberto à UI:** mitigado pelo jail `ResolveInside` + auth por dono + auditoria (RF-005). Sandbox por container é decisão pendente (ADR) — fora desta fatia.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/246 |
