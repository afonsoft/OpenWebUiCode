# SPEC-20261009-ide-mentions-tests

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `ide-mentions-tests` (série web-ide, fatia S7) |
| Type | `Feature` |
| Stack | `.NET 10 / Blazor WASM` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-{YYYYMMDD}-mentions-tests` |
| Ticket | `GAP-impl-at-mentions` + `GAP-impl-test-runner` |
| Status | `Completed` |
| Priority | `medium` |
| Depends on | `SPEC-20261009-workspace-file-api` (S1), `SPEC-20261009-web-ide-surface` (S2) |

## 1. User Story

**As a** usuário no chat/IDE
**I want** mencionar arquivos com `@path` para injetá-los no contexto e rodar a suite de testes do repo com um clique
**So that** o loop codar→testar→conversar é completo sem sair da UI.

**Problem context:** composer só conhece `/prompts`; rodar testes hoje = pedir pro agente usar `shell_exec` opaco. O mention `@file` do opencode (`packages/app`) e o fluxo de teste precisam de UX nativa.

## 2. Scope

**In scope:**
- `@` no composer: autocomplete de paths do workdir (debounced, fuzzy por nome) via `GET /workspace/repo/tree`; selecionar insere chip `path` no input; no submit, cada chip vira um bloco `<file path="...">…conteúdo (cap por arquivo e total)…</file>` no prompt da run — o usuário vê o chip, não o dump.
- **Run tests:** endpoint `POST /api/v1/workspace/repo/test-run` que detecta o comando por manifesto (`.slnx/.sln/.csproj`→`dotnet test`, `package.json`→`npm test`/`bun test`, `pyproject.toml`→`pytest`, `go.mod`→`go test ./...`; override por config `TestCommand` no repo binding) e executa como ChatJob no workdir → `GET /test-run/{id}` com saída parseada (linhas `failed/passed` conhecidas por runner) → badge ✓/✗ + aba "Tests" no `ChatWorkspacePanel`/IDE com log linkado.
- O job de teste passa pelo `CommandRiskClassifier` (WorkspaceWrite) → approval normal; comando mostrado antes de aprovar.
- Botão "Run tests" no `/ide` e na aba Changes; chip de resultado (✓ N / ✗ N) no header.

**Out of scope:**
- Run/debug com breakpoints (DAP) — fora de escopo.
- Watch mode/contínuo.
- Cobertura visual (badge de % vem depois, se pedirem — parse de cobertura é opcional `[A DEFINIR]`).

## 3. Technical Context

**AS-IS:** `_promptSuggestions`/`SelectPrompt` (`ChatView.razor`) — mesmo seam para `@`; `ChatJobService` spawna processos no workdir com output capturado; `CommandRiskClassifier` já decide gate.

**TO-BE:** `@mention` chips + test runner nativo.

## 4. Requirements

### RF-001: `@` autocomplete
`@` seguido de ≥1 char → dropdown de paths (top 20, dirs primeiro); Esc fecha; chip renderiza `📄 path` no input; remove como unidade (backspace).

### RF-002: Injeção de contexto
No submit, chips viram `<file>` blocks com cap 16KB/arquivo, 64KB total; binário/`>cap` → placeholder com aviso; path fora do workdir → rejeitado silenciosamente no server.

### RF-003: Test run
`POST .../test-run` retorna `{jobId, command}`; `GET .../test-run/{jobId}` → `{state, summary: {passed, failed, skipped, durationMs}, tail}`. Parser por runner (dotnet TRX-less text, jest/pytest text). Sem manifesto conhecido → `422` com `{detail, suggested:"configure TestCommand"}`.

### RF-004: UI
Botão Run tests (IDE + aba Changes) → approval card se necessário → aba Tests com spinner→✓/✗ + log; chip no header durante run de teste. i18n + a11y nos 8 locales.

## 5. Acceptance Criteria

- [ ] `@README` insere chip; run recebe conteúdo do arquivo.
- [ ] Repo .NET vinculado → Run tests executa `dotnet test` e badge mostra ✓/✗ com contagens.
- [ ] Repo sem manifesto → mensagem orientando configurar comando.
- [ ] Chips inválidos/fora do jail não vazam conteúdo.

## 6. Tests

Api.Tests: mention injection (cap, binary, jail), manifesto→comando matrix, parser de saída, 422 sem manifesto. Client.Tests: dropdown `@`, chips, aba Tests.

## 7. Rollout

Sem flag. Runner cobre os 4 manifestos acima — extensível por config.

## 8. Risks

- **Testes destrutivos/lentos:** passam pelo classifier + timeout do ChatJob; usuário vê comando antes (approval).
- **Parser frágil:** badge cai pra "concluído/saída" genérica se o parse falha — nunca inventa contagens.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/256 |
