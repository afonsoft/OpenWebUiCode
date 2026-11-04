# SPEC-20261104-chat-bang-trigger

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `chat-bang-trigger` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feature/Devin-20261104-customize-hub` (mesmo branch hub) |
| Ticket | `GH#690` — https://github.com/afonsoft/OpenWebUiCode/issues/690 |
| Status | `Approved` |
| Depends on | `SPEC-20261104-customize-hub` (catálogo efetivo de skills `/api/v1/customize/skills`) |

## 1. User Story

**As a** usuário do chat do Open WebUI .NET
**I want** digitar `!` no composer e ver a lista de skills disponíveis (da organização, minhas e do repo) para selecionar uma com um clique — estilo Devin web — e continuar usando `@` para referenciar arquivos do repositório
**So that** invoco procedimentos reutilizáveis sem decorar nomes e sem sair do teclado.

**Problem context:**
- `@query` **já existe** (arquivos/refs, chips, dirs-first — `ChatView.razor:914-2126`) — apenas manter/documentar.
- `/` já mescla prompts+skills+comandos+MCP-prompts (`RepoSlashCommands.Merge`), mas não há um trigger **dedicado a skills** como o `!` do Devin.
- `!` hoje não faz nada no composer (grep zero) — greenfield seguro.

## 2. Scope

**In scope:**
- Trigger `!` no composer: ao digitar `!` (início ou após espaço), dropdown lista o **catálogo efetivo de skills** (mesma fonte de `/api/v1/customize/skills`: DB globais+pessoais habilitadas + repo skills habilitadas), filtro substring por nome/descrição, selecionar (click/Enter/Tab) injeta o corpo da skill no composer como contexto (badge/chip ou texto, conforme `SelectPrompt` skill atual).
- Teclado completo: ↑↓ navega, Enter/Tab seleciona, Esc fecha — espelhando acessibilidade do dropdown `@` existente.
- i18n + testes bUnit.

**Out of scope:**
- Mudar o comportamento de `/` (segue mesclando tudo) e de `@` (segue arquivos/refs).
- Expansão de comandos com `$ARGUMENTS` (`!` é só skills — comandos ficam no `/`).
- Trigger `#` client-side para coleções RAG (hoje server-side apenas) — candidato a SPEC futura.
- Execução de skill como sub-agente.

## 3. Technical Context

**Where the change happens:**
`src/OpenWebUI.Client/Components/ChatView.razor` — espelhar o caminho `@`: `MentionTokenRegex` (`:914`) → novo `BangTokenRegex` `(?:^|\s)!(?<q>[\w-]*)$`; `OnComposerInputAsync` (`:2004`) → `QueueBangLookup` + `RefreshBangSuggestionsAsync` (debounce 250ms, top-20) reutilizando o markup do dropdown de mentions; seleção → reutilizar caminho `skill` de `SelectPrompt` (`:1770-1782`) que injeta o corpo (`GetRepoSkillAsync` para repo / campo `Content` para DB).
Fonte de dados: `ApiService.GetCustomizeSkillsAsync()` (da SPEC hub) — fallback para `_repoSkills` + DB skills se a SPEC hub ainda não estiver merged (flag de implementação: consumir o endpoint novo quando existir).

**Files to read before implementing:**
- `ChatView.razor` — blocos `:914-915`, `:1933-1945` (Esc/Backspace), `:2004-2126` (mention lookup/debounce/select), `:1759-1840` (SelectPrompt dispatch skill), `:1585-1598` (load `_repoSkills`)
- `tests/OpenWebUI.Client.Tests/ChatMentionTests.cs` — harness bUnit do dropdown
- `src/OpenWebUI.Client/Services/RepoSlashCommands.cs` — shape `SlashItem`

**Files to create or modify:**
```text
src/OpenWebUI.Client/Components/ChatView.razor      # bang regex, lookup, dropdown, select
src/OpenWebUI.Client/Services/ApiService.cs         # GetCustomizeSkillsAsync (se SPEC hub merged)
src/OpenWebUI.Client/wwwroot/i18n/{8 locales}.json  # chat.bang_* (aria/empty)
tests/OpenWebUI.Client.Tests/ChatBangTests.cs       # NOVO
```

## 4. Requirements

### RF-001: Dropdown `!` de skills
- **Description:** `(?:^|\s)!(?<q>[\w-]*)$` dispara dropdown com skills efetivas: label `name`, hint `description`, badge `org|pessoal|repo`. Filtro substring case-insensitive em nome+descrição, ordenadas por `source` (repo → global → pessoal) e nome; top-20; debounce 250ms.
- **Rules:** `!` seguido de espaço/fecha quando query casa item exato já selecionado; Esc fecha sem limpar input; clique fora fecha; `aria-expanded`/`role="listbox"`/`option` como o dropdown `@`; skill `disabledByAdmin`/`enabledForMe=false` **não** lista.
- **Input → Output:** `!wri` → `write-specs` selecionável; Enter insere a skill.

### RF-002: Inserção da skill selecionada
- **Description:** Ao selecionar: token `!query` é removido do input e o **conteúdo da skill é injetado no composer** (mesmo comportamento de `SelectPrompt`→`skill`: repo skill via `GetRepoSkillAsync`; DB skill via `Content` já carregado), ficando editável antes do envio.
- **Rules:** DB-skill usa `Content` do item do catálogo (evita fetch extra); repo-skill busca corpo pelo endpoint existente; falha de fetch → toast + input restaurado.

### RF-003: Sem regressão em `/`, `@` e texto literal
- **Rules:** `!` dentro de palavra (`oi!`) não dispara (regex exige boundary); `@` e `/` inalterados; texto "!" sozinho enviado normalmente se dropdown fechado.

### RF-004: Testes bUnit + i18n
- `ChatBangTests.cs`: abrir dropdown, filtrar, selecionar injeta, Esc fecha, item desabilitado não lista. Chaves `chat.bang_*` nos 8 locales; `check-form-a11y.py` verde.

## 5. API Contract

Nenhum endpoint novo **nesta** SPEC — consome `GET /api/v1/customize/skills` (SPEC hub). Se implementada antes do hub, lê `_repoSkills` + `GET /api/v1/skills` (DB próprias) — catálogo org passa a existir quando o hub mergear.

## 6. Acceptance Criteria

- [ ] **Dado** composer vazio **quando** digito `!` **então** dropdown lista minhas skills efetivas com badge de origem.
- [ ] **Dado** dropdown aberto **quando** digito `!wri` **então** filtra `write-specs`; Enter injeta o corpo no input.
- [ ] **Dado** dropdown aberto **quando** pressiono Esc **então** fecha e o texto `!wri` permanece.
- [ ] **Dado** skill com deny global **quando** abro `!` **então** ela não aparece.
- [ ] **Dado** texto "oi!" **quando** digito **então** nenhum dropdown abre; `/` e `@` seguem funcionando.

## 7. Task Plan

| # | Task | Validation |
| - | - | - |
| T1 | Regex + estado + lookup (debounce) — RED: `ChatBangTests` | bUnit falha→verde |
| T2 | Dropdown markup/keyboard/a11y | bUnit navegação |
| T3 | Select→inject (repo fetch + DB content) | bUnit conteúdo |
| T4 | i18n ×8 + tailwind + guards | `check-*.py` |
| T5 | Build/test + DoD | `dotnet build/test` |

## 8. Organization Guardrails

- Mesma branch do hub (`feature/Devin-20261104-customize-hub`) ou branch própria se implementada antes — decidir na execução.
- `ChatView.razor` já tem 4394 linhas — preferir extrair o dropdown para `Components/Chat/{Mention,Bang}Dropdown.razor` se o diff local ficar grande (registrar débito, não bloquear).

## 9. Definition of Done

- [ ] `!` lista skills efetivas, filtra, injeta no composer.
- [ ] Navegação por teclado e a11y equivalentes ao `@`.
- [ ] Zero regressão nos testes de `/` e `@` existentes.
- [ ] i18n ×8 + drift guards; bUnit novos verdes; SPEC `Done` + Issue linkada.
