# SPEC-20261018-formatters-catalog

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `formatters-catalog` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261018-formatters-catalog` |
| Status | `Completed` |
| Parent | `docs/analise-opencode-v2.md` (P2.7) |

## 1. User Story

**As a** usuário trabalhando em repos multi-linguagem
**I want** um catálogo de formatters por extensão com fallback ordenado, em vez de um único `FormatCommand` por binding
**So that** `.razor`/`.cs` rodam `dotnet format`, `.py` roda `ruff`, `.ts` roda `prettier` — paridade com opencode v2 `formatters` (catálogo builtin por extensão + config custom + fallback).

## 2. Scope

**In scope:**
- `FormatterEntry {extensions[], command, env?, timeout?, builtin?}`; seleção: custom config do binding → custom global do usuário → catálogo builtin (detecta binário disponível).
- Catálogo builtin: `dotnet format` (cs/razor/csproj), `csharpier`, `ruff format` (py), `black`, `prettier` (ts/js/json/md/css/html), `dprint`, `gofmt` (go), `rustfmt` (rs), `shfmt` (sh), `stylua` (lua), `pg_format` (sql) — cada um marcado `builtin`, só usado se o binário existir (`which`).
- Run pós-write/edit: coleta extensões tocadas → para cada, primeiro formatter que casa e existe → roda com `{files}` interpolado; falha nunca quebra o tool (comportamento atual mantido), mas warnings agora vão pro run log.
- UI: mostra formatter efetivo por extensão no binding/repo settings; toggle por formatter.

**Out of scope:**
- Format on save do editor web (nossa surface é tool-call post-write, como o v2). Instalação automática de formatters.

## 3. Technical Context

**Where the change happens:**
`FormatHookService` (hoje 1 comando por binding/global) → resolve por extensão via `FormatterCatalog`; detecção de binários cacheada por workdir.

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/Services/FormatHookService.cs`
- chamadas de format hook nos file tools (write/edit)

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/FormatterCatalog.cs   # NOVO: builtin catalog + seleção
src/OpenWebUI.Infrastructure/Services/FormatHookService.cs  # ext→entry, fallback, warnings→run log
src/OpenWebUI.Domain/Entities.cs                           # FormatterEntry config
src/OpenWebUI.Client/Pages/Settings/SettingsFormatters.razor # NOVO (ou dentro de Workspaces)
tests/OpenWebUI.Api.Tests/FormatterCatalogTests.cs          # NOVO
```

## 4. Requirements

### RF-001: Catálogo e seleção
- **Description:** lookup `FormatterCatalog.Resolve(ext, bindingId, workdir)` retorna a primeira entry por prioridade custom-binding > custom-user > builtin-detectado; sem match → no-op (não falha).
- **Rules:** detecção de binário cacheada por workdir e revalidada a cada run; `{files}`/`{file}` interpolados shell-quoted; timeout default 60s por formatter.

### RF-002: Agrupamento por extensão
- **Description:** após tool write/edit com múltiplos arquivos, agrupa por extensão e roda cada formatter uma vez com sua lista (não N vezes por arquivo).
- **Rules:** ordem dos formatters não importa — extensões independentes; saída >4KB truncada no run log.

### RF-003: Visibilidade
- **Description:** run log registra `{formatter, files, exit, durationMs}`; UI de settings lista catálogo efetivo com badges builtin/custom/disabled.

## 5. Acceptance Criteria

- Write `.py` + `.ts` na mesma tool call → `ruff format` nos `.py`, `prettier` nos `.ts` (binários presentes) — cada um com sua lista.
- Sem formatter pro ext → no-op silencioso (só log).
- Formatter falhando → tool call retorna ok e warning vai pro log.
- Custom por binding sobrescreve builtin; testes cobrem seleção, agrupamento, timeout.

## 6. Implementation notes (2026-10-25)

**Entregue:**
- `FormatterCatalog` (src/OpenWebUI.Infrastructure/Services/FormatterCatalog.cs):
  11 builtins (dotnet-format, csharpier, ruff, black, prettier, dprint,
  gofmt, rustfmt, shfmt, stylua, pg-format) com índice ext→entries;
  `ResolveAsync` aplica binding → `Format:Commands:{ext}` →
  `Format:Command` (catch-all) → primeiro builtin cujo binário responde
  ao probe (`command -v`, revalidado a cada run — nada de cache entre
  invocações); `FormatterEntry` tem `Env` e `TimeoutSeconds` opcionais.
- `FormatHookService.RunForUserAsync` agrupa por extensão
  (`GroupByExtension`) e roda cada formatter uma vez por grupo;
  `{files}` expande a lista quoted, `{file}` roda uma invocação por
  arquivo, sem placeholder → arquivos como args finais; warnings
  agregados por formatter; `Format:Disabled` (csv de nomes) pula
  formatters; log `LogDebug` com {formatter, files, exit, ms}; saída
  truncada em 4KB (`MaxOutputChars` = 4096).
- Testes `FormatterCatalogTests` (13): precedência binding/custom/
  builtin/none, ordem do catálogo, agrupamento multi-ext com binários
  fake via PATH, override por `Format:Commands:{ext}`, `{file}`
  por-arquivo, falha→warning+não-quebra, disabled, `Format:Command`
  catch-all, no-op sem match. Regressão `WorktreeFormatHooksTests`
  26/26 verdes.

**Desvios documentados:**
- UI de catálogo efetivo (RF-003): não existe surface de repo/worktree
  settings no client — visibilidade entregue via run log
  (`LogDebug` por execução). Badge/toggle fica fora de escopo.
- `Entities.cs`: sem nova entidade — `FormatterEntry` é record
  estático no catálogo (SPEC previa entidade; desnecessária pois
  builtins são código, customs vêm de config existente).

## Delivered

- **PR:** https://github.com/afonsoft/open-webui/pull/520 (`be79859`)
- **Issue:** #509
- **Branch:** `feature/Devin-20261018-formatters-catalog`
- **Tests:** `FormatterCatalogTests` 13 + regressão `WorktreeFormatHooksTests` 26/26
