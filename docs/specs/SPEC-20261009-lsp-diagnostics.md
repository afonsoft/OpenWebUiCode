# SPEC-20261009-lsp-diagnostics

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `lsp-diagnostics` (série web-ide, fatia S8 — **fase 2**, maior custo) |
| Type | `Feature` |
| Stack | `.NET 10` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-{YYYYMMDD}-lsp` |
| Ticket | `GAP-impl-lsp` |
| Status | `Completed` |
| Priority | `low` (deferível) — mas é o pré-requisito de squiggles/go-to-def no editor |
| Depends on | `SPEC-20261009-web-ide-surface` (S2) |

## 1. User Story

**As a** usuário/agente
**I want** diagnostics, hover, go-to-definition e símbolos via Language Server Protocol no workdir
**So that** o editor mostra erros reais e o agente navega código por semântica, não só grep — como `src/lsp/` + `src/tool/lsp.ts` do opencode.

**Problem context:** o editor (S2) é só texto; o agente navega por `file_grep`/`file_glob` sem noção de símbolo. LSP fecha essa lacuna — mas é a fatia mais cara (spawn por linguagem + JSON-RPC + lifecycle).

## 2. Scope

**In scope:**
- `LspService` (Infrastructure): spawna language server por workspace/linguagem via stdio JSON-RPC (`StreamJsonRpc` ou implementação manual de `Content-Length` framing); lifecycle: `initialize(rootUri=workdir)` → `initialized` → requests → `shutdown`; restart com backoff; timeout por request; kill tree on dispose.
- Mapa de servidores por config (`Lsp:Servers`): `csharp` → `csharp-ls`/`OmniSharp`, `typescript`/`javascript` → `typescript-language-server --stdio`, `python` → `pylsp`, `json` → `vscode-json-languageserver`. Servidor ausente no host → linguagem fica `unavailable` (não quebra nada).
- Tools do agente (quando repo vinculado + linguagem disponível): `lsp_diagnostics {path?}` (erros/warnings do workspace), `lsp_symbols {path}` (documentSymbol), `lsp_workspace_symbols {query}`, `lsp_definition`, `lsp_references`, `lsp_hover` — confinados ao jail; respostas compactas (cap por lista).
- Editor (S2): consome `lsp_diagnostics`/`hover` por arquivo aberto — squiggles + tooltip; sem server → UI não mostra nada (degrada limpo).
- `didOpen`/`didChange`/`didSave` sincronizados com o editor e com tools `file_*` (servidor sempre vê o conteúdo real — importante: agente edita por `file_edit`, não pelo editor).

**Out of scope:**
- Code actions/refactors, formatting via LSP (S9 hooks cobrem format).
- Completion provider (editor digita sem IA completion).
- Debug (DAP).

## 3. Technical Context

**AS-IS:** nada de LSP; processos gerenciados por `ChatJobService`/`LocalTerminalSpawner` (padrão de spawn+kill tree existe); ferramenta builtin pattern (`BuiltinToolRegistry`).

**TO-BE:** LSP por workdir com lifecycle gerenciado.

**Constraints:**
- Um server por (workdir, linguagem); cap 2 servers por workdir / `[A DEFINIR]` global.
- Todo path passa `WorkspaceFiles.ResolveInside` → `file://` URI correto (normalização de path/URI é o bug clássico — testar espaços/Unicode).
- Sem server instalado → linguagem `unavailable`, tools respondem erro amigável; NUNCA instalar server em runtime (instalação é do ambiente/blueprint).

## 4. Requirements

### RF-001: Lifecycle
Spawn → `initialize` com rootUri do workdir → pronto; falha de spawn/init → estado `unavailable` + log; `shutdown`+kill no dispose; restart após crash (máx 3, backoff).

### RF-002: Tools
As 6 tools acima; cada uma com permission `lsp` (Low — read-only); respostas resumidas (path:line, severity, message; cap 50 itens).

### RF-003: Sync
`didOpen` ao abrir no editor; `didChange` incremental ou full-sync (full na v1 — mais simples, ok até ~200KB); `didSave` no PUT; `file_*` tools notificam via `didChange` pós-escrita.

### RF-004: UI
Editor: diagnostics como squiggles + painel "Problems" (lista); hover tooltip. Sem LSP → nada renderiza.

### RF-005: Config
`Lsp:Enabled` (default on se server presente), `Lsp:Servers:{lang}:{command,args}`, `Lsp:RequestTimeoutSeconds` (30).

## 5. Acceptance Criteria

- [ ] Repo C# com `csharp-ls` instalado → diagnostics aparecem no editor e `lsp_diagnostics` lista para o agente.
- [ ] Linguagem sem server → degradação limpa.
- [ ] Edição via `file_edit` do agente atualiza diagnostics (didChange path).
- [ ] Dispose mata o processo (sem zumbi).

## 6. Tests

Api.Tests: JSON-RPC framing, init/shutdown com fake server (script python que responde `initialize`), cap de resposta, restart. Path↔URI normalization com espaços/unicode.

## 7. Rollout

Flag `Lsp:Enabled`; docs `docs/{en,pt}/` listam servidores suportados e instalação.

## 8. Risks

- **Dependência de binário externo por linguagem** — mitigação: `unavailable` elegante + docs de instalação; blueprint instala csharp-ls quando decidirmos ligar em produção.
- **Perf em repos grandes** — rootUri + `workspace_folders` limitado ao workdir já mitiga; cap de diagnostics.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/273 |
