# SPEC-20261104-skills-allowed-tools-plugins

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `skills-allowed-tools-plugins` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feature/Devin-20261104-skills-allowed-tools-plugins` |
| Ticket | `GH#698` — https://github.com/afonsoft/OpenWebUiCode/issues/698 |
| Status | `Approved` |

## 1. User Story

**As a** administrador ou usuário que cadastra skills
**I want** declarar `allowed-tools` por skill (frontmatter SKILL.md ou campo no DB) e importar plugins (manifest com skills+commands+MCPs) org-wide
**So that** skills restritas executem só com as tools necessárias (paridade Devin Skills/Plugins) e um pacote inteiro seja instalável de uma vez.

**Problem context:**
Paridade Devin: skills podem declarar `allowed-tools` restringindo as tools do agente quando ativas; Plugins são pacotes org-scoped (skills+commands+MCPs). Hoje `Skill` (Entities.cs:1529) não tem `AllowedTools`; `SkillDiscoveryService` já parseia frontmatter (`name`, `description`, `agent`, `model`, `subtask`, `mode`, `autoinvoke` — :447-540) — `allowed-tools:` é extensão natural. `SkillImportService` já importa SKILL.md do GitHub → skills globais — um `plugin.json` manifest estende isso para pacotes.

## 2. Scope

**In scope:**
- `Skill.AllowedTools` (string? CSV) + migration; `RepoEntry.AllowedTools` parseado de `allowed-tools:`/`allowed_tools:` no frontmatter (CSV ou YAML list inline `[a, b]`).
- Semântica de enforcement: skills **ativas** no turno (injetadas via `!`/autoinvoke/model-skill) com `AllowedTools` não-vazio restringem as tools do agente — **interseção** entre as listas de todas as skills restritivas ativas; lista built-in essencial (`read`, `write`, `edit`, `exec`, `list_dir`, `search`...) sempre permitida; skill sem `AllowedTools` = sem restrição.
- UI: campo `AllowedTools` (textarea, CSV) no editor de skill do `/customize`; badge "restricted" no catálogo; `!` dropdown mostra indicador.
- Plugin: `plugin.json` no root de um repo git — `{skills:[paths], commands:[paths], mcps:[{name,url,transport}]}` — novo modo `POST /api/v1/admin/skills/import-plugin` (admin only): skills→globais no DB, commands→prompts globais, mcps→MCP servers org (sem secrets — headers ficam p/ config manual).
- i18n ×8; NUnit + bUnit.

**Out of scope:**
- Enterprise teams/RBAC multi-org (não existe conceito de org além de admin/global).
- Marketplace/registry de plugins.
- `allowed-tools` como allowlist absoluta **sem** essenciais (essenciais sempre passam — decisão Q7).
- Sandboxing de MCPs importados via plugin.

## 3. Technical Context

**Where the change happens:**
- `Skill` entity + EF migration (`AllowedTools` string?).
- `SkillDiscoveryService.TryParse`/`ParseEntryContent` — novo campo frontmatter.
- `SkillImportService` — preserva `allowed-tools` ao importar (parse já existe — estender dict→entidade).
- Enforcement: onde o turno monta a lista de tools do agente (run/agent pipeline — `Completions/`, `AgentRun`/`RunTools`) — filtrar pela interseção dos `AllowedTools` das skills ativas ∪ essenciais.
- `CustomizeEndpoints` — `SkillUpsertRequest.AllowedTools`; catálogo retorna o campo.
- `Customize.razor` — campo no editor + badge; `ChatView` bang dropdown — ícone restricted.
- `plugin.json` import — estende `SkillImportService` ou novo `PluginImportService` (admin).

**Files to read before implementing:**
- `src/OpenWebUI.Domain/Entities.cs:1529` (`Skill`)
- `src/OpenWebUI.Infrastructure/Services/SkillDiscoveryService.cs` (RepoEntry + frontmatter parse)
- `src/OpenWebUI.Infrastructure/Services/SkillImportService.cs`
- `src/OpenWebUI.Api/Completions/` — onde tools do turno são resolvidas (buscar construção da tool list para o provider)
- `src/OpenWebUI.Api/Endpoints/CustomizeEndpoints.cs` (upsert/catalog contracts)
- `docs/en/ANALYSIS-DEVIN-WEBAPP.md` §D17 (plugins)

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities.cs                              (Skill.AllowedTools)
src/OpenWebUI.Infrastructure/Migrations/*AddSkillAllowedTools*
src/OpenWebUI.Infrastructure/Services/SkillDiscoveryService.cs (RepoEntry.AllowedTools)
src/OpenWebUI.Infrastructure/Services/SkillImportService.cs    (preserve + plugin manifest)
src/OpenWebUI.Infrastructure/Services/PluginImportService.cs   (novo — ou estende SkillImport)
src/OpenWebUI.Api/Endpoints/CustomizeEndpoints.cs             (contract + import-plugin route)
src/OpenWebUI.Api/Completions/**                              (tool filter no turno)
src/OpenWebUI.Client/Pages/Customize.razor                    (campo + badge + import-plugin UI)
src/OpenWebUI.Client/Components/ChatView.razor                (badge restricted no ! dropdown)
src/OpenWebUI.Client/Services/ApiService.cs                   (métodos)
i18n ×8 · tests Api (filter + import) + Client (editor/badge)
```

## 4. Requirements

### RF-001: Campo `AllowedTools` em Skill DB + frontmatter
- **Description:** `Skill.AllowedTools` string? (CSV normalizado, max ~500 chars); frontmatter `allowed-tools:` ou `allowed_tools:` em SKILL.md → `RepoEntry.AllowedTools` (lista). Import preserva.
- **Input → Output:** `allowed-tools: read, search_files` → `["read","search_files"]`; ausente → null (sem restrição).

### RF-002: Enforcement por skills ativas
- **Description:** ao montar a tool list do turno, coletar `AllowedTools` não-vazios das skills ativas (DB global+pessoal habilitadas e injetadas + repo skills ativadas via `!`/autoinvoke). Tool permitida se: `essencial` OU `nenhuma skill restritiva ativa` OU `presente na interseção de todas as listas restritivas`.
- **Rules:** essenciais = `read, write, edit, exec, list_dir, search_files, todo_write` (constante); interseção vazia → só essenciais; log debug quando filtro aplicado (`n skills restritivas, m tools removidas`) — sem vazar nomes de skills de outros usuários (todas as ativas são do próprio usuário/global).

### RF-003: UI — editor, badge, dropdown
- **Description:** editor de skill em `/customize` ganha campo "Allowed tools (CSV, opcional)"; catálogo mostra badge `restricted` quando preenchido; `!` dropdown mostra ícone/tooltip.

### RF-004: `plugin.json` + import-plugin (admin)
- **Description:** `POST /api/v1/admin/skills/import-plugin {repoUrl}` — fetch `plugin.json` (validate: skills[] paths `.md`, commands[] paths `.md`, mcps[] `{name,url}` http-only); skills→`Skill` global (upsert por nome, preserva allowed-tools), commands→`Prompt` global, mcps→`McpServer` org (`UserId=null`) criado **desabilitado** sem headers (admin configura secrets depois). SSRF: só github.com + hosts já allowlistados no `SkillImportService`.
- **Input → Output:** `{repoUrl}` → `{imported:{skills:n,commands:n,mcps:n}, errors:[...]}`; 400 manifest inválido; 403 non-admin.

### RF-005: i18n + testes
- **Description:** `customize.skill_allowed_tools`, `customize.skill_restricted`, `customize.import_plugin*`, `chat.bang_restricted` ×8. NUnit: parse frontmatter, interseção filter, essencial sempre permitido, import-plugin 403/400/happy (mock http). bUnit: badge restricted, campo no editor.

## 5. API Contract

**Endpoint:** `POST /api/v1/admin/skills/import-plugin` — admin JWT.

**Request:** `{"repoUrl":"https://github.com/org/plugin-repo"}`
**Response 200:**
```json
{"imported":{"skills":3,"commands":2,"mcps":1},"errors":[]}
```
`plugin.json` (no repo, root):
```json
{"name":"my-plugin","skills":["skills/foo/SKILL.md"],"commands":["commands/deploy.md"],"mcps":[{"name":"db","url":"https://mcp.example.com/sse","transport":"sse"}]}
```
**Erros:** 400 manifest ausente/inválido/URL não-https; 403 non-admin; 422 repo inacessível.

## 6. Acceptance Criteria

- [ ] **Dado** SKILL.md com `allowed-tools: read, search` **quando** descoberta **então** `RepoEntry.AllowedTools == ["read","search"]`.
- [ ] **Dado** skill restritiva ativa com `[read]` **quando** turno monta tools **então** tools sem essenciais+`read` são removidas; `exec` continua (essencial).
- [ ] **Dado** 2 skills restritivas `[read,exec]` e `[read]` ativas **quando** monta tools **então** não-essenciais permitidas = interseção `{read}`.
- [ ] **Dado** nenhuma skill restritiva ativa **quando** monta tools **então** comportamento atual inalterado.
- [ ] **Dado** `plugin.json` com 2 skills+1 mcp **quando** admin importa **então** 2 skills globais + 1 MCP org desabilitado criados; reimport idempotente.
- [ ] **Dado** non-admin **quando** POST import-plugin **então** 403; manifest malformado → 400.

## 7. Task Plan

- [ ] **T1:** `Skill.AllowedTools` + migration + contracts + catálogo.
- [ ] **T2:** `RepoEntry.AllowedTools` parse (CSV e `[a, b]` inline) + preservação no import.
- [ ] **T3:** Tool filter no turno (interseção + essenciais) — TDD nos cenários BDD.
- [ ] **T4:** `plugin.json` + `POST /admin/skills/import-plugin` + `PluginImportService`.
- [ ] **T5:** Client: editor campo + badges + `!` indicador + botão import-plugin (admin).
- [ ] **T6:** i18n ×8 + tailwind + testes + DoD → PR.

## 8. Organization Guardrails

- **Security:** allowed-tools nunca alarga permissões — só restringe; essenciais documentados e fixos (não configurável por skill — evita bypass); plugin import só admin + hosts allowlistados; MCPs importados desabilitados sem headers (secrets nunca do manifest).
- **Scope:** sem marketplace; sem teams RBAC; sem allowed-tools por usuário-final em skills globais (campo é do dono da skill).
- **Architecture:** filter na camada de completions/agent (Infrastructure/Api), não no client; parse frontmatter reusa `SkillDiscoveryService`.
- **Upstream parity:** `allowed-tools` segue a semântica Devin/Claude skills (lista restritiva quando ativa).

## 9. Definition of Done

- [ ] RF-001..RF-005 + BDD cobertos; migration aplicada.
- [ ] Tool filter testado (interseção, essenciais, nenhuma restritiva → no-op).
- [ ] Build Release + suites verdes; guards; i18n paridade.
- [ ] SPEC → `Done` + arquivada.

**Next action after DoD is complete:** PR `feature/Devin-20261104-skills-allowed-tools-plugins`.

## Open Questions / Pending Ambiguity

- Nenhuma bloqueante. Ponto de atenção na implementação: localizar exatamente onde a tool list do turno é montada (run pipeline) — se o filtro for inviável sem refator grande, fallback documentado: injetar restrição como instrução no system prompt (menos estrito) e marcar RF-002 como "soft enforcement".
