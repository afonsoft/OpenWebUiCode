# SPEC-20261019-terminal-color-palette

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `terminal-color-palette` |
| Type | `Feature` |
| Stack | `.NET / Blazor WASM` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261019-terminal-color-palette` |
| Ticket | `[A DEFINIR — criada via create-issues após aprovação]` |
| Status | `Delivered` |
| Fonte | pedido do usuário (2026-10-19): "arrumar o terminal para ficar com mais cores, parecido com o terminal do agent-harness" |

## 1. User Story

**As a** usuário do terminal web
**I want** saída ANSI colorida e vívida (prompt, `ls`, git, logs)
**So that** a experiência se aproxime do terminal do agent-harness e de um terminal local real.

**Problem context:**
`wwwroot/js/terminal.js:67-71` define apenas 3 cores do tema xterm (`background #0d1117`, `foreground #e6edf3`, `cursor #58a6ff`) e deixa as 16 cores ANSI no default do xterm — desbotadas sobre o fundo escuro. O agent-harness (`/home/ubuntu/repos/agent-harness/src/Taskboard.Client/wwwroot/js/terminal.js`) usa o mesmo tema mínimo, **mas** seu `PtySession.cs:116-117` exporta `HOME` — o bash de login carrega o `.bashrc` real do usuário (prompt colorido, `ls --color=auto`, `grep --color`). O `PtySession.cs` do open-webui só define `TERM=xterm-256color` (linha 114); dentro do container Debian, o shell root não carrega aliases de cor e o resultado é um terminal majoritariamente monocromático.

## 2. Scope

**In scope:**
- RF-001: paleta ANSI completa no tema xterm do `terminal.js` — 16 cores + `selection`, na linha GitHub Dark (combina com `#0d1117`), cores vivas e legíveis.
- RF-002: env do PTY — `COLORTERM=truecolor` e `HOME` (paridade agent-harness; bash `-l` carrega rc do usuário).
- RF-003: fallback de cores quando o shell não tem rc configurado — exportar defaults no comando interno do PTY (`LS_COLORS` mínimo + `PS1` colorida somente quando o rc não definir uma; avaliação via `[ -z "$PS1" ]`/teste de prompt padrão, não sobrescrever configuração do usuário).

**Out of scope:**
- Tema claro do terminal (terminal sempre dark `#0d1117` hoje).
- Tabs/keybar/features do terminal agent-harness (paste virtual key, watchScroll pill) — outra SPEC se desejado.
- Mudança no protocolo WS `/ws/terminal/{id}` ou no `xterm` lib.
- Powerline/Nerd Fonts.

## 3. Technical Context

**Where the change happens:**
- `src/OpenWebUI.Client/wwwroot/js/terminal.js` — objeto `theme` em `open()` (linhas 62-72).
- `src/OpenWebUI.Infrastructure/Terminal/PtySession.cs` — `Start()`: `startInfo.Environment` (linha 114) e `InnerCommand()`/`script -c` payload (linha 112, 130-134).
- Referência de paridade: `/home/ubuntu/repos/agent-harness/src/Taskboard.Integrations/Terminal/PtySession.cs` (env `TERM`+`HOME`).

**Files to read before implementing:**
- `CLAUDE.md`
- `src/OpenWebUI.Client/wwwroot/js/terminal.js`
- `src/OpenWebUI.Infrastructure/Terminal/PtySession.cs` (linhas 86-140)
- `tests/OpenWebUI.Api.Tests/LocalTerminalSpawnerTests.cs` e `TerminalPtyTests.cs` (padrão de teste do PTY)

**Files to create or modify:**
```text
src/OpenWebUI.Client/wwwroot/js/terminal.js          # theme ANSI completo
src/OpenWebUI.Infrastructure/Terminal/PtySession.cs  # env COLORTERM/HOME + defaults de cor
tests/OpenWebUI.Api.Tests/TerminalPtyTests.cs        # asserts de env (se suportado pelo harness atual)
```

## 4. Requirements

### RF-001: Paleta ANSI no tema xterm
- **Description:** estender `theme` com as 16 cores ANSI + `selectionBackground` + `cursorAccent`, base GitHub Dark: `black #484f58`, `red #ff7b72`, `green #7ee787`, `yellow #f2cc60`, `blue #79c0ff`, `magenta #d2a8ff`, `cyan #39c5cf`, `white #b1bac4` e bright variants (`brightBlack #6e7681`, `brightRed #ffa198`, `brightGreen #56d364`, `brightYellow #e3b341`, `brightBlue #79c0ff`, `brightMagenta #d2a8ff`, `brightCyan #56d4dd`, `brightWhite #f0f6fc`).
- **Rules:** manter `background #0d1117`, `foreground #e6edf3`, `cursor #58a6ff`; aplicar em todas as instâncias criadas por `open()` (sessões interativas); sem alterar a lib xterm.
- **Input → Output:** saída ANSI `\x1b[31m…`/`[92m…` renderiza nas cores da paleta definida.

### RF-002: Env `COLORTERM` e `HOME`
- **Description:** `PtySession.Start()` passa a exportar `COLORTERM=truecolor` e garantir `HOME` definido (fallback: `Environment.GetFolderPath(UserProfile)` quando ausente do ambiente herdado).
- **Rules:** `_env` do caller continua com precedência (loop de override já existente roda depois); não regredir `TERM=xterm-256color`.
- **Input → Output:** processo filho do `script` enxerga `COLORTERM`/`HOME` — bash `-l` carrega o rc do usuário.

### RF-003: Defaults de cor no shell
- **Description:** quando o shell interno for o `bash -l` default (sem `_command` custom), o comando `stty …; exec bash -l` passa a definir defaults **não destrutivos**: `LS_COLORS` de uma paleta escura razoável somente se vazio, e `PS1` colorida somente quando o rc deixar a PS1 padrão do sistema — implementado como wrapper `bash --rcfile <(printf …)` ou arquivo temporário, preservando `exec`.
- **Rules:** nunca sobrescrever `LS_COLORS`/`PS1` já configurados pelo usuário (testar `-z`); `_env`/comando custom não recebem o shim; quoting via `ShellQuote` existente.
- **Input → Output:** terminal novo no container mostra prompt colorido e `ls` com cores mesmo sem `.bashrc` de cor.

## 5. API Contract

N/A — sem mudança de contrato WS/HTTP.

## 6. Acceptance Criteria

- [ ] **Given** uma sessão de terminal **when** o usuário roda `printf '\e[31mred \e[92mbright-green\e[0m'` **then** as cores correspondem à paleta RF-001 (não ao default desbotado do xterm).
- [ ] **Given** sessão nova no container **when** `ls --color=auto`/`git status`/`grep --color` executam **then** saída colorida visível.
- [ ] **Given** usuário com `.bashrc` colorido (HOME) **when** sessão inicia **then** prompt/aliases do usuário prevalecem (shim não sobrescreve).
- [ ] **Given** `_env` ou `command` custom no PTY **when** sessão inicia **then** nenhum shim de cor é injetado.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| HOME ausente no ambiente | `startInfo.Environment` sem HOME | fallback para o profile do processo, sem crash |
| `LS_COLORS` via `_env` | caller passa LS_COLORS | valor do caller prevalece (override depois do default) |
| Windows host | PTY já não suportado hoje | comportamento inalterado (feature gate `Terminal:Enabled`) |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** ler `terminal.js`, `PtySession.cs`, `LocalTerminalSpawnerTests.cs`/`TerminalPtyTests.cs`; confirmar onde `InnerCommand()` é testado.
- [ ] **T2 — Paleta:** estender `theme` em `terminal.js` com as 16 cores + selection/cursorAccent.
- [ ] **T3 — Env:** `COLORTERM`/`HOME` em `PtySession.Start()`; shim de `LS_COLORS`/`PS1` no comando default com testes de não-destruição.
- [ ] **T4 — Testes:** ajustar/adicionar asserts no padrão dos testes PTY existentes (env presente no ProcessStartInfo ou no InnerCommand); `dotnet test tests/OpenWebUI.Api.Tests`.
- [ ] **T5 — Manual:** subir app, abrir `/terminal`, rodar `ls`, `git status`, `printf` de cores — evidência visual.
- [ ] **T6 — Done + PR:** `Status = Done`, mover para `docs/specs/`, PR na branch.

**7.1 Validation strategy**

| Item | Evidence |
| --- | --- |
| Build | `dotnet build OpenWebUI.slnx -c Release` |
| Testes | `dotnet test tests/OpenWebUI.Api.Tests` verde |
| Manual | screenshot terminal com `ls`/`git`/ANSI colorido |

## 8. Organization Guardrails

- Não modificar `.github/workflows/`.
- Sem commit em `main`; branch `feature/devin-20261019-terminal-color-palette`.
- Não quebrar sessões de terminal existentes nem o protocolo WS (mudança só em tema/env/comando default).
- PTY segue atrás de `Terminal:Enabled`/`TERMINAL_ENABLED` — nenhum caminho novo de bypass.

## 9. Definition of Done

- [ ] Paleta ANSI aplicada e visível.
- [ ] `COLORTERM`/`HOME`/defaults de cor implementados sem sobrescrever config do usuário.
- [ ] Testes PTY existentes passam; novos asserts de env incluídos quando o harness permitir.
- [ ] SPEC arquivada em `docs/specs/` com `Status: Done` + `Delivered` (PR).
