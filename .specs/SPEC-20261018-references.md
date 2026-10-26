# SPEC-20261018-references

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `references` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261018-references` |
| Status | `Approved` |
| Parent | `docs/analise-opencode-v2.md` (P2.6); depends on permissions-v2 (external_directory) |

## 1. User Story

**As a** usuário com vários repos/documentação externa
**I want** registrar referências nomeadas (diretório local ou repo git com branch) que o agente pode ler e anexar
**So that** eu puxe código de outro repo para contexto sem copiar arquivos — paridade com opencode v2 `references` (`{alias: {path|repository, branch, description, hidden}}`, clone com refresh 24h).

## 2. Scope

**In scope:**
- Config de referências por usuário e por repo-binding: `{alias, source: path|git-url, branch?, description?, hidden?}`.
- Git: clone/shallow em `~/.openwebui/references/{hash}` + refresh automático se >24h (fetch+reset) antes do uso.
- Path: validado no momento do attach/leitura (não precisa existir no boot).
- Anexar no composer (`@alias` ou menu) → injeta listing de paths como attachment kind `reference`; `file_read`/`glob`/`grep` aceitam `ref://alias/path`.
- `description` é injetada no system quando a referência está anexada (guia de uso); `hidden:true` oculta do menu mas ainda resolúvel.

**Out of scope:**
- Escrita em references (read-only); auth git privada reusa credenciais do binding quando possível (PAT do repo).

## 3. Technical Context

**Where the change happens:**
Novo `ReferenceService` (resolve alias→path físico, clona/atualiza git); file tools aceitam `ref://`; composer ganha menu `@`; `AttachmentContentService` (attachments-v2) recebe kind `reference` → listing.

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/FileBuiltinTools.cs`
- `src/OpenWebUI.Infrastructure/Services/WorkspaceBindingService.cs` (como paths de binding resolvem)
- `src/OpenWebUI.Client/Components/Chat/ChatComposer.razor`

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/ReferenceService.cs   # NOVO: resolve/clone/refresh
src/OpenWebUI.Domain/Entities.cs                          # ReferenceEntry
src/OpenWebUI.Infrastructure/ChatTools/Tools/FileBuiltinTools.cs # ref:// em read/glob/grep
src/OpenWebUI.Client/Components/Chat/ChatComposer.razor   # menu @references
src/OpenWebUI.Client/Pages/Settings/SettingsReferences.razor # NOVO CRUD
src/OpenWebUI.Api/Endpoints/ReferenceEndpoints.cs         # NOVO
tests/OpenWebUI.Api.Tests/ReferenceServiceTests.cs        # NOVO
```

## 4. Requirements

### RF-001: Resolução e git cache
- **Description:** `ref://alias[/path]` → path físico. git: clone `--depth 1 --branch {branch}` em cache dir; `LastFetched>24h` → `git fetch+reset --hard origin/branch` antes de servir; falha de fetch → serve cache velho com warning.
- **Rules:** refs nunca graváveis via tools; `path` de referência validado dentro do cache (sem `..`).

### RF-002: Anexar e descrever
- **Description:** anexo `reference` injeta `description` (se houver) + listing (cap 2000 paths); o agente navega via tools com `ref://`.
- **Rules:** listing ordenada, relativa à raiz da referência.

### RF-003: Permissões
- **Description:** leitura via `ref://` conta como *dentro* do boundary `external_directory` (SPEC permissions-v2) quando a referência está anexada no chat; sem anexo, pede ask.
- **Rules:** subagent herda refs anexadas do chat pai (como o v2 com sessions).

## 5. Acceptance Criteria

- Reference git clonada e re-utilizada; fetch roda após 24h simuladas.
- `@docs` anexado → listing + descrição no contexto; `file_read ref://docs/README.md` retorna conteúdo.
- Referência sem anexo → ask external_directory; anexada → direto.
- Testes cobrem clone/refresh/path traversal/description injection.

## Progresso (implementado na branch `feature/Devin-20261018-references`)

- `ReferenceEntry` + DbSet + índice único (UserId, Alias) + migration `References`.
- `ReferenceService`: resolve dir local / git (`clone --depth 1`, cache em `{DATA_ROOT}/references/{user}/{alias-hash12}`, fetch+`reset --hard FETCH_HEAD` após 24h via `LastFetchedAt`), cache stale servido com warning em falha de fetch, jail fail-closed via `WorkspaceFiles.ResolveInside`, listing ordenado cap 2000 (sem `.git`).
- `ref://alias[/sub]` em `file_read`/`file_grep`/`file_glob` — gated por `BuiltinToolContext.AttachedRefs`; `file_write`/`file_edit` rejeitam ref:// (somente-leitura).
- Anexo via chip `@alias` → `MentionPaths` com `ref:alias`: `MentionsContributor` emite `<reference alias>` com descrição + listing e popula `PromptContext.AttachedRefs` → request → `ChatRunExecutor` (sub-runs herdam via `RequestJson`).
- `ReferenceEndpoints`: GET/POST/PATCH/DELETE + `/{alias}/tree` + `/{id}/refresh` sob `/api/v1/references`.
- UI: menu `@` oferece `ref:alias` (só visíveis; `visible=true`); tab **Referências** no SettingsModal (list/create/delete/hidden/refresh) + 13 chaves i18n × 8 locales.
- Testes: `ReferenceServiceTests` (14) + `ReferenceEndpointsTests` (4) — 18/18; regressões adjacentes 228/228.

### Desvios
- `external_directory` (RF-003): o sistema de policies ainda não tem essa permissão — acesso `ref://` exige anexo `@alias` (fail-closed); o "ask para não-anexada" fica pendente do permissions-v2.
- Composer/settings ficaram em `ChatView.razor`/`SettingsModal.razor` (não `ChatComposer`/`Pages/Settings` — paths da SPEC não existem).
- Anexo usa `ref:` prefix em `MentionPaths` (reuso do pipeline de menções) em vez de kind `reference` no `AttachmentContentService`.
- Clone com `file://`/URL git; auth de clone privado não coberto (sem creds nas refs v1).
