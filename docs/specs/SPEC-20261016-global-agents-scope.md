# SPEC-20261016-global-agents-scope

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `global-agents-scope` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261016-global-agents-scope` |
| Ticket | [#399](https://github.com/afonsoft/open-webui/issues/399) · Epic [#398](https://github.com/afonsoft/open-webui/issues/398) |
| Status | `Completed` |
| Fonte | análise opencode v2.0.31 (`skill/discovery.ts`, docs `/skills`, `/commands`) — pedido explícito do mantenedor: ler skills/tools/mcp/commands de `~/.agents` + `.agents` do projeto |

## 1. User Story

**As a** usuário do open-webui como agente de codificação
**I want** skills, commands e configurações MCP descobertos de `~/.agents/` (global, todos os projetos) além de `.agents/` do workspace
**So that** minhas skills pessoais e de time funcionem em qualquer repo vinculado sem copiar arquivos para cada workspace.

**Problem context:**
`SkillDiscoveryService` só escaneia o workdir do repositório vinculado (`.claude/skills`, `.agents/skills`, `.devin/skills`, `.opencode/skill[s]`, `skill[s]/`) e commands em `.opencode/command`, `.claude/commands`, `.agents/commands`. Não há escopo global: `~/.agents/skills` e `~/.agents/commands` — onde o usuário já mantém dezenas de skills — são invisíveis. O opencode resolve isso com fontes globais + projeto, com precedência projeto > global por ID.

## 2. Scope

**In scope:**
- RF-001: `SkillDiscoveryService.Scan` aceita escopos adicionais: `~/.agents/skills`, `~/.agents/commands` (global) além dos dirs do workdir.
- RF-002: Precedência por nome: projeto > global (mesmo `Name` no projeto sobrescreve o global), igual à ordem de precedência do opencode.
- RF-003: Endpoints `GET /api/v1/workspace/repo/skills` e `/commands` passam a listar união global+projeto com campo `Scope` (`global|project`) por item.
- RF-004: `SkillBuiltinTool` (builtin:skill) carrega também skills globais; description atualizada para mencionar `~/.agents`.
- RF-005: Slash-command picker do composer (`ChatView`/`RepoSlashCommands`) inclui commands globais; badge/grupo "global" vs "repo".
- RF-006: `~/.agents/mcp.json` (opcional): se existir, servidores MCP declarados são mesclados à lista efetiva da sessão (formato compatível `mcpServers` do `.mcp.json`/opencode). Apenas leitura no boot da run; sem escrita.
- RF-007: Sem repo vinculado (modo chat), skills/commands globais continuam descobertos (não dependem de WorkspacePath).

**Out of scope:**
- Catálogos HTTP de skills (`index.json` remoto) — fase 2.
- Overrides por usuário (hidden) já existem via `UserConfigOverlayService` — manter.
- Execução server-side de commands (frontmatter agent/model) — SPEC commands-v2.

## 3. Technical Context

- `src/OpenWebUI.Infrastructure/Services/SkillDiscoveryService.cs` — scan atual (dirs relativos ao workdir, cache 60s).
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/SkillBuiltinTool.cs` — tool `builtin:skill`.
- `src/OpenWebUI.Api/Endpoints/RepoSkillEndpoints.cs` — endpoints de catálogo.
- `src/OpenWebUI.Client` — `RepoSlashCommands`/`ChatView.razor` (picker, interpolação `$N`).
- `src/OpenWebUI.Infrastructure/Services/McpClientService.cs` — servers MCP em DB (`McpServer`).
- Home do usuário no servidor: `Environment.GetFolderPath(UserProfile)` / `$HOME`.

## 4. Requirements

### RF-001: Scan multi-escopo
- **Description:** O scan produz `(global → projeto)` na ordem de precedência baixa→alta: `~/.agents/skills`, depois dirs do workdir na ordem atual.
- **Rules:** ausência de `~/.agents` não é erro; symlinks fora do workdir são seguidos só se legíveis; mesma dedup por `Name` — última fonte vence.
- **Input → Output:** `Scan(workspacePath?, includeGlobal: true)` → `ScanResult` com `Scope` por skill/command.

### RF-002: Precedência projeto > global
- **Description:** skill `foo` em `~/.agents/skills/foo` + `repo/.agents/skills/foo` → versão do repo carregada, marcada `Scope=project`.

### RF-003: Endpoints com escopo
- **Description:** `GET .../skills` e `.../commands` retornam itens com `scope`; filtro `?scope=global|project` opcional.

### RF-004: builtin:skill global
- **Description:** `ExecuteAsync` usa o mesmo scan unificado; erro de "não encontrada" lista skills visíveis de ambos os escopos.

### RF-005: Composer com comandos globais
- **Description:** picker `/` mostra commands globais agrupados/rotulados; interpolação `$N` idêntica.

### RF-006: `.agents/mcp.json` opcional
- **Description:** lê `~/.agents/mcp.json` + `{workspace}/.agents/mcp.json` no formato `{"mcpServers": {"id": {"command":..., "args":[], "url":...}}}`; entradas são mescladas (projeto > global > DB por id) e materializadas como tools virtuais `mcp://` na run. Inválido → warning, ignora o arquivo.

### RF-007: Sem workspace
- **Description:** modo `chat` sem repo: scan global ainda retorna skills/commands; `builtin:skill` funciona (Tier Read, já anunciado fora de WorkspaceTools — avaliar se `skill` sai de WorkspaceTools ou se context.WorkspacePath vazio usa só o global).

## 5. API Contract

**Endpoint:** `GET /api/v1/workspace/repo/skills?scope=`
**Auth:** Bearer JWT / `sk-*`

**Response (success):**
```json
{ "skills": [ { "name": "git-release", "path": "/home/u/.agents/skills/git-release/SKILL.md", "scope": "global", "description": "..." } ] }
```

**Expected errors:** `401 auth` — demais falhas de FS degradam para lista parcial + warning.

## 6. Acceptance Criteria

- [x] **Given** `~/.agents/skills/minha-skill/SKILL.md` existe e repo sem skill de mesmo nome **when** `GET .../skills` **then** aparece com `scope:"global"` e `builtin:skill` carrega seu corpo.
- [x] **Given** mesmo `name` em `~/.agents` e `.agents` do repo **when** `GET .../skills` **then** lista a do repo (`scope:"project"`).
- [x] **Given** `~/.agents/commands/deploy.md` **when** o usuário digita `/deploy x` no composer **then** o template interpolado entra no input.
- [x] **Given** `~/.agents/mcp.json` com um server stdio válido **when** uma run inicia **then** suas tools aparecem como `mcp_*` anunciadas.
- [x] **Given** chat sem repo vinculado **when** `GET .../skills?scope=global` **then** retorna skills globais.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| `~/.agents` inexistente | — | scan só do projeto, sem erro |
| `mcp.json` malformado | `{broken` | warning no log; server ignorado |
| Skill global hidden pelo usuário | overlay `HiddenSkillNames` | oculta em ambos os escopos |
| Path com `~` literal no Linux | `$HOME` resolvido pelo SO | usar `UserProfile`/`HOME`, nunca expandir `~` manualmente para fora |

## 7. Task Plan

- [x] **T1 —** `SkillDiscoveryService`: scan multi-escopo + `Scope` no resultado + dedup.
- [x] **T2 —** Endpoints `RepoSkillEndpoints`: `scope` no payload + filtro.
- [x] **T3 —** `SkillBuiltinTool` + description; chat-mode sem workspace usa global.
- [x] **T4 —** `.agents/mcp.json` reader + merge em `McpClientService` (refresh tools na primeira run).
- [x] **T5 —** Composer: commands globais no picker + label.
- [x] **T6 —** Testes NUnit: scan dedup/precedência, endpoint scope, mcp.json inválido, skill tool global.
- [x] **T7 —** `dotnet build` + `dotnet test`; DoD; PR.

**7.1 Validation strategy:** .NET — unit tests p/ regras (dedup, precedência, parse mcp.json), integration p/ endpoints; ≥80% cobertura nas novas linhas.

## 8. Organization Guardrails

- Branch `feature/devin-20261016-global-agents-scope`; nunca commitar em `main`.
- Não ler/escrever fora de `~/.agents` e workdir; nunca logar conteúdo de skill ou paths de usuário além do necessário.
- MCP servers do `mcp.json` respeitam a mesma aprovação/tier das tools MCP atuais.
- Não seguir symlink para fora de `~/.agents`/workdir.

## 9. Definition of Done

- [x] RF-001..RF-007 implementados.
- [x] Critérios da seção 6 cobertos por testes verdes.
- [x] `dotnet build` 0 erros/0 warnings; `dotnet test` verde.
- [x] `python3 tools/check-spec-status.py` ok.
- [x] Sem secrets/paths sensíveis em logs.

## Open Questions / Pending Ambiguity

- Nome do arquivo MCP: `mcp.json` (adotado; fallback `.agents/mcp.jsonc` se surgir demanda).

## Delivered

| Item | Link |
| --- | --- |
| PR/commit | #415 + #422 · 2dd6bf8ba |
| Issue | https://github.com/afonsoft/open-webui/issues/399 |
