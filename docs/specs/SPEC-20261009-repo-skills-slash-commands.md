# SPEC-20261009-repo-skills-slash-commands

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `repo-skills-slash-commands` (série web-ide, fatias S3+S4) |
| Type | `Feature` |
| Stack | `.NET 10 / Blazor WASM` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-{YYYYMMDD}-repo-skills` |
| Ticket | `GAP-impl-repo-skills` + `GAP-impl-project-instructions` |
| Status | `Completed` |
| Priority | `high` — pedido explícito do usuário |
| Depends on | `SPEC-20261009-workspace-file-api` (S1) |

## 1. User Story

**As a** usuário com repo vinculado
**I want** que as skills do repo (`**/SKILL.md`, `.claude/skills`, `.agents/skills`, `.devin/skills`, `.opencode/skill*`) apareçam como slash commands no composer e que o agente leia `AGENTS.md`/`CLAUDE.md` automaticamente
**So that** o chat use os fluxos/convenções do próprio repositório — igual ao opencode (`src/skill/discovery.ts`, `src/command/index.ts`).

**Problem context:** hoje `/` no composer só lista prompts de usuário (`ChatView.razor:130-137`, `GET /api/v1/workspace/prompts/command/{command}`); nada lê o workdir. O catálogo `/workspace/skills` é do app, não do repo. O system prompt ignora `AGENTS.md`/`CLAUDE.md` do projeto (`grep AGENTS` → só `Model.SystemPrompt`).

## 2. Scope

**In scope:**
- `SkillDiscoveryService` (Infrastructure): scan do workdir vinculado nos padrões: `**/SKILL.md` sob `{skill,skills}/`, `.claude/skills/`, `.agents/skills/`, `.devin/skills/`, `.opencode/{skill,skills}/`, `skill/`; parse de frontmatter YAML mínimo (`name`, `description`) + corpo markdown. Cap 50 skills, 64KB por skill, path-jailed.
- `GET /api/v1/workspace/repo/skills` → `[{name, description, source: skill|command, path}]`; `GET .../skills/{name}` → corpo.
- Commands do repo: `.opencode/command/**/*.md`, `.claude/commands/**/*.md`, `.agents/commands/**` — frontmatter `{description, agent, model, subtask}` + template com placeholders `$1`..`$N`/`$ARGUMENTS`.
- Composer: `/` passa a listar fontes mescladas (prompts usuário + skills + commands do repo), com badge da fonte (`skill`/`cmd`/`prompt`) e filtro enquanto digita; selecionar injeta corpo no input (commands interpolam `$N` pelos args após o nome).
- Tool `skill {name}`: injeta conteúdo da skill como instrução da run (contexto extra) — padrão opencode `src/tool/skill.ts` (permission `skill` pede aprovação? default: auto — é leitura; registrar em `ToolCallRiskClassifier` como Low).
- System prompt assembly: quando repo vinculado, concatena `AGENTS.md` → `CLAUDE.md` → `.cursor/rules/*.md` → `README.md` (resumo/32KB cap total, ordem de precedência repo-raiz primeiro); injeção marcada `<project_instructions>`.

**Out of scope:**
- Criação/edição de skills pela UI (somente leitura nesta fatia).
- MCP prompts como commands (opencode tem; aqui entra depois — `[A DEFINIR]`).
- Execução de `subtask` (commands que abrem run filha — depende de `delegate_task`; registrar como RF opcional se simples).

## 3. Technical Context

**AS-IS:** composer `/` já tem fluxo de sugestões (`_promptSuggestions`, `SelectPrompt` em `ChatView.razor:341,1044`); `Tool` sintético + registry pattern (`BuiltinToolRegistry`); assembly do system prompt no pipeline de run (`ChatRunExecutor`/`ProviderService.CompleteWithToolsAsync`).

**TO-BE:** skills/commands do repo como cidadãos de primeira classe do composer e do contexto.

**Constraints:**
- Scan invalida em: rebind de repo, troca de branch (`WorkspaceRepoService.SwitchBranchAsync`), e TTL 60s — sem FileSystemWatcher na v1.
- Yaml frontmatter: parser mínimo próprio (nome/description/agent/model/subtask) — sem dependência nova se possível (`YamlDotNet` aceitável se já referenciado; senão parser linha-a-linha).
- Nunca executar código de skill — skills são só markdown injetado.

## 4. Requirements

### RF-001: Discovery
Scan retorna skills+commands deduplicados por `name` (primeiro na ordem de precedência: `.claude` > `.agents` > `.devin` > `.opencode` > `skills/`). Skill sem `name` no frontmatter usa o nome do diretório-pai.

### RF-002: API
`GET .../skills` + `GET .../skills/{name}` + `GET .../commands` + `GET .../commands/{name}` — auth do dono; `404 bound:false` sem repo.

### RF-003: Composer
`/` abre sugestões mescladas com badge de fonte; `/{skill}` insere o corpo da skill (ou `@` mention-style: injeta como contexto oculto da run — `[A DEFINIR]` qual forma; default: injeta no input, editável). `$N`/`$ARGUMENTS` interpolados com o texto após o comando.

### RF-004: Tool `skill`
`builtin:skill` disponível quando repo vinculado; retorna corpo markdown + `location`; erro NotFound → mensagem amigável com lista de nomes válidos.

### RF-005: Project instructions
Run com repo vinculado recebe `<project_instructions>` contendo os arquivos de regras encontrados (cap 32KB, trunca com marcador); quando ausente, nada muda.

### RF-006: i18n/a11y
Sugestões com `role=listbox/option`, keyboard nav (setas/Tab/Esc); chaves `chat.cmd_*`/`ide.*` nos 8 locales.

## 5. Acceptance Criteria

- [ ] Repo com `.claude/skills/foo/SKILL.md` vinculado → `/foo` aparece no composer e executa.
- [ ] `skill` tool retorna o corpo dentro de uma run.
- [ ] `AGENTS.md` do repo aparece no system prompt da run (verificável via log/inspeção ou teste).
- [ ] Command `.claude/commands/review.md` com `$1` interpola o arg.
- [ ] Sem repo → nada quebra; `/` continua listando prompts de usuário.

## 6. Tests

Api.Tests: discovery (fixture com árvore fake de skills), frontmatter edge cases, caps, dedup. Client.Tests: lista mesclada no composer, badges, interpolação `$N`.

## 7. Rollout

Sem flag. Skills lidas são texto — superfície de ataque limitada (injeção de prompt é *intencional* por definição; documentar em `docs/`).

## 8. Risks

- **Prompt injection via skill maliciosa do repo:** o usuário vinculou o repo — mesmo threat model do opencode; mitigar com indicação visual de que a skill virou instrução (toast/notice no transcript).

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/248 |
