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
| Status | `Done` |

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
- **Description:** ao montar a tool list do turno (`ChatRunExecutor.LoadRunToolsAsync`, antes do split deferred), coletar `AllowedTools` não-vazios das skills ativas server-side — DB skills via `MetaJson.skill_ids` do modelo custom + repo/global/remote `autoinvoke:true` que passam nos toggles. Tool permitida se presente na interseção; interseção vazia → só essenciais.
- **Rules:** essenciais = `file_list, file_read, file_grep, file_glob, todo_write` (nomes reais das built-ins — só leitura + tracking; mutáveis excluídas propositalmente). Skill sem `AllowedTools` = não-restritiva. Evento `skills.allowed_tools` auditável quando filtrado. **Limitação conhecida:** skill inserida via `!` vira texto no prompt (o servidor não vê a referência) — restrição aplica-se só a skills ativadas server-side (model-bound + autoinvoke).

### RF-003: UI — editor, badge, dropdown
- **Description:** editor de skill em `/customize` ganha campo "Allowed tools (CSV, opcional)"; catálogo mostra badge `restricted` quando preenchido; `!` dropdown mostra ícone/tooltip.

### RF-004: `plugin.json` + import-plugin (admin)
- **Description:** `POST /api/v1/admin/plugins/import {repoUrl}` — fetch `plugin.json` da raiz (validate: skills[] paths `.md`/`SKILL.md` dir, commands[] paths `.md`, mcps[] `{name,transport,url|command}`); skills→`Skill` global (upsert por nome, preserva allowed-tools), commands→`Skill` global (não existe entidade Command no DB — commands de repo vira skill injetável; documentado), mcps→`McpServer` org (`UserId=null`) criado **desabilitado** sem headers (admin configura secrets depois). SSRF: só github.com/raw.githubusercontent.com (mesmo do `SkillImportService`).
- **Input → Output:** `{repoUrl}` → `{importedSkills:[...], importedMcps:[...], skipped:[{name,reason}], warnings:[...]}`; 400 manifest ausente/malformado/URL não-GitHub; 403 non-admin; 502 falha GitHub.

### RF-005: i18n + testes
- **Description:** `customize.skill_allowed_tools`, `customize.skill_restricted`, `customize.import_plugin*`, `chat.bang_restricted` ×8. NUnit: parse frontmatter, interseção filter, essencial sempre permitido, import-plugin 403/400/happy (mock http). bUnit: badge restricted, campo no editor.

## 5. API Contract

**Endpoint:** `POST /api/v1/admin/plugins/import` — admin JWT.

**Request:** `{"repoUrl":"https://github.com/org/plugin-repo"}`
**Response 200:**
```json
{"importedSkills":["foo","deploy"],"importedMcps":["db"],"skipped":[],"warnings":[]}
```
`plugin.json` (no repo, root):
```json
{"name":"my-plugin","skills":["skills/foo"],"commands":["commands/deploy.md"],"mcps":[{"name":"db","transport":"http","url":"https://mcp.example.com/mcp"}]}
```
**Erros:** 400 manifest ausente/malformado/URL não-GitHub; 403 non-admin; 502 repo/árvore/download inacessível.

## 6. Acceptance Criteria

- [x] **Dado** SKILL.md com `allowed-tools: read, search` **quando** descoberta **então** `RepoEntry.AllowedTools == ["read","search"]`.
- [x] **Dado** skill restritiva ativa com `[file_read]` **quando** turno monta tools **então** tools fora do conjunto são removidas antes do anúncio/deferral.
- [x] **Dado** 2 skills restritivas `[file_read,file_write]` e `[file_read]` ativas **quando** monta tools **então** permitidas = interseção `{file_read}`; interseção vazia → só essenciais.
- [x] **Dado** nenhuma skill restritiva ativa **quando** monta tools **então** comportamento atual inalterado.
- [x] **Dado** `plugin.json` com 2 skills+1 mcp **quando** admin importa **então** 2 skills globais + 1 MCP org desabilitado criados; reimport idempotente.
- [x] **Dado** non-admin **quando** POST import-plugin **então** 403; manifest malformado → 400.

## 7. Task Plan

- [x] **T1:** `Skill.AllowedTools` + migration + contracts + catálogo.
- [x] **T2:** `RepoEntry.AllowedTools` parse (CSV e `[a, b]` inline) + preservação no import.
- [x] **T3:** Tool filter no turno (interseção + essenciais) — TDD nos cenários BDD.
- [x] **T4:** `plugin.json` + `POST /admin/skills/import-plugin` + `PluginImportService`.
- [x] **T5:** Client: editor campo + badges + `!` indicador + botão import-plugin (admin).
- [x] **T6:** i18n ×8 + tailwind + testes + DoD → PR.

## 8. Organization Guardrails

- **Security:** allowed-tools nunca alarga permissões — só restringe; essenciais documentados e fixos (não configurável por skill — evita bypass); plugin import só admin + hosts allowlistados; MCPs importados desabilitados sem headers (secrets nunca do manifest).
- **Scope:** sem marketplace; sem teams RBAC; sem allowed-tools por usuário-final em skills globais (campo é do dono da skill).
- **Architecture:** filter na camada de completions/agent (Infrastructure/Api), não no client; parse frontmatter reusa `SkillDiscoveryService`.
- **Upstream parity:** `allowed-tools` segue a semântica Devin/Claude skills (lista restritiva quando ativa).

## 9. Definition of Done

- [x] RF-001..RF-005 + BDD cobertos; migration aplicada.
- [x] Tool filter testado (interseção, essenciais, nenhuma restritiva → no-op).
- [x] Build Release + suites verdes; guards; i18n paridade.
- [x] SPEC → `Done` + arquivada.

**Next action after DoD is complete:** PR `feature/Devin-20261104-skills-allowed-tools-plugins`.

## Open Questions / Pending Ambiguity

- Nenhuma bloqueante. Ponto de atenção na implementação: localizar exatamente onde a tool list do turno é montada (run pipeline) — se o filtro for inviável sem refator grande, fallback documentado: injetar restrição como instrução no system prompt (menos estrito) e marcar RF-002 como "soft enforcement".

## Delivered

- **PR:** https://github.com/afonsoft/OpenWebUiCode/pull/701
- **Commit:** branch `feature/Devin-20261104-skills-allowed-tools-plugins`
- `Skill.AllowedTools` (CSV ≤500c) + migration `AddSkillAllowedTools`; frontmatter `allowed-tools:`/`allowed_tools:` → `RepoEntry.AllowedTools` (CSV e `[a, b]`); preservado no `SkillImportService`.
- `SkillToolRestrictionService`: interseção de allowed sets das skills ativas server-side (model `skill_ids` + autoinvoke c/ toggles); vazia → essenciais read-only; filtro em `LoadRunToolsAsync` antes do deferred split + evento `skills.allowed_tools`.
- `PluginImportService` + `POST /api/v1/admin/plugins/import`: skills/commands → `Skill` global; mcps → `McpServer` global desabilitado sem headers; SSRF só github.com.
- UI: campo no editor, badge catálogo + dropdown `!`; i18n ×8 (1392 chaves).
- **Limitação documentada (RF-002):** `!`-inserted skills injetam como texto — restrição só em skills server-side (model-bound + autoinvoke).
- Testes: 8 NUnit novos (parse/Permits/ResolveAsync) + 2 endpoint; 46/46 fixtures afetadas; client 153/153.
