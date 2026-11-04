# SPEC-20261104-customize-hub

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `customize-hub` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feature/Devin-20261104-customize-hub` |
| Ticket | `[A DEFINIR]` — criar Issue após aprovação |
| Status | `Approved` |
| Depends on | `SPEC-20261104-model-visibility-gate` (padrão de escopo user/global consolidado) |

## 1. User Story

**As a** usuário ou administrador do Open WebUI .NET
**I want** uma página `/customize` estilo Devin web com abas **Knowledge**, **Skills** e **MCPs**, onde cadastros feitos pelo administrador ficam disponíveis para todos os usuários e cadastros feitos por mim valem só para a minha conta
**So that** conhecimento, procedimentos reutilizáveis (skills) e servidores MCP compartilhados otimizam o contexto e padronizam o fluxo de trabalho de toda a organização — sem sair do produto para editar arquivos ou configs de agente.

**Problem context:**
Hoje os três conceitos existem mas estão espalhados e sem escopo de organização:
- **Skills**: há 3 tipos — Prompts (DB por usuário), `Skill` (DB por usuário, `/api/v1/skills`) e repo-skills (SKILL.md discovery + toggles por usuário). Nenhum cadastro de skill "da organização" — admin não consegue publicar skill para todos.
- **Knowledge**: existe apenas o RAG de documentos (`KnowledgeCollection`); não há "notas de conhecimento" estilo Devin (instruções/contexto injetados no prompt por trigger) — que é o que otimiza o contexto do agente.
- **MCPs**: superfícies separadas — admin em `Admin.razor→config→mcp`, usuário em `SettingsModal→integrações→MCP`. Não há visão unificada "o que a org disponibiliza vs. o que eu tenho".
- Não há ponto único de entrada estilo Devin **Customize** na navegação.

Referências (lidas): docs.devin.ai `product-guides/skills` (escopo Personal/Org/Enterprise, `@skills:<name>`), `product-guides/memory`, `onboard-devin/knowledge-onboarding` (Knowledge do Devin está **deprecated** → migra para Skills — reforça o modelo abaixo), `work-with-devin/deepwiki-mcp`.

## 2. Scope

**In scope:**
- **Página `/customize` + `/customize/{tab}`** (tabs `knowledge|skills|mcps`) com layout de abas laterais estilo `Workspace.razor`, item "Customize" no bottom-nav da `Sidebar.razor` (visível para todos).
- **Aba Knowledge** — nova entidade `KnowledgeNote` (título, trigger/descrição, conteúdo markdown, `Pinned`, `UserId NULL=global`): CRUD escopado (admin cria globais + próprias; usuário só próprias), listagem mesclada org+pessoal com badge de escopo, injeção no system prompt (pinada = sempre; senão por trigger match), tombstone `u:{id}:knowledge-disabled` para esconder notas globais, e ação **"Sugerir conhecimento"** no chat que cria rascunho de nota a partir do contexto.
- **Aba Skills** — catálogo unificado com badge de origem (`global|pessoal|repo`): (a) skills DB (`Skill` entity, `UserId` vira nullable → `null`=global) com CRUD escopado; (b) repo/plugin skills (discovery existente) com toggle por usuário (já existe) e **deny global admin** (novo endpoint para `skills-disabled` global); toggle por usuário respeita deny global (não reabilita).
- **Aba MCPs** — visão unificada sobre os endpoints já existentes: seção "Organização" (globais, CRUD admin via `/api/v1/mcp/servers`) + seção "Pessoal" (`/api/v1/users/user/mcp-servers`, http-only) + toggle por usuário sobre globais (tombstone existente). Reorganização de UI — sem contrato novo.
- **Importar skills de repositório git** — botão na aba Skills (admin): `POST /api/v1/admin/skills/import {repoUrl, subPath?, names[]?}` → baixa `*/SKILL.md` (default pré-preenchido `https://github.com/afonsoft/skills`, subPath `skills`), faz parse do frontmatter (`name`, `description`) e upserta `Skill` globais por nome (idempotente).
- i18n nos 8 locales + drift guards + testes NUnit/bUnit + migration EF (`KnowledgeNotes` + `Skill.UserId` nullable).

**Out of scope:**
- Escopo "Enterprise" multi-org do Devin (app é single-org).
- Skills de plugin com `allowed-tools`/`permissions`/`triggers` granulares — guardado para SPEC futura de plugins.
- Execução de skills como sub-agente (hoje skill = texto injetado; orquestração já coberta por `delegate_task`/repo-commands).
- Sugestão automática de conhecimento por LLM (o botão "sugerir" cria rascunho editável — sumarização por LLM fica em SPEC-20261104-session-insights-v2).
- Versionamento de SKILL.md (Devin tem history; aqui `UpdatedAt` basta na v1).
- Multi-tenant isolation além do padrão `UserId`/`u:{id}:*` existente.

## 3. Technical Context

**Where the change happens:**
- **Nova página**: `src/OpenWebUI.Client/Pages/Customize.razor` (`@page /customize`, `@page /customize/{Tab}` — padrão de `Workspace.razor:1-7,303-311`) + `Components/Customize/{KnowledgeTab,SkillsTab,McpsTab}.razor`; item de nav em `Components/Sidebar.razor` (bottom-nav block ~`:260-320`).
- **Domain**: `Entities.cs` — novo `KnowledgeNote`; `Skill.UserId` → `string?` (null=global, mesmo padrão `McpServer:1705+`). Migration EF `AddCustomizeHub` (regra do repo: schema novo → `dotnet ef migrations add`).
- **Backend**: novo `Endpoints/CustomizeEndpoints.cs` (grupo `/api/v1/customize`) — knowledge CRUD + effective skills catalog + import; reutiliza `UserConfigOverlayService` para tombstones e `SkillDiscoveryService` para a fatia "repo".
- **Injeção de contexto**: `SystemPromptContributors.cs` — novo contributor `KnowledgeNotesContributor` (order ~73, após autoinvoke skills) injetando notas pinadas + trigger-matched, honrando tombstones; budget cap igual ao de skills (4000 chars total por seção — seguir padrão existente).
- **MCPs**: só UI — consome `/api/v1/mcp/servers` (admin) e `/api/v1/users/user/mcp-servers` (existentes).
- **Config do agente**: `.mcp.json` na raiz do repo declarando o servidor DeepWiki remoto (`https://mcp.deepwiki.com/mcp`) — devolve contexto de repo para sessões Devin/Claude (pedido do usuário).

**Decisões de design (fechadas com o usuário):**
- "Knowledge" da aba = **notas estilo Devin** (instruções/contexto), NÃO o RAG de documentos (que permanece no Workspace inalterado).
- Escopo = `UserId NULL=global` na entidade + tombstone `u:{id}:*-disabled` — padrão `McpServer`/overlay existente.
- Seed de skills default = **import explícito** via "Importar de repositório" (default `afonsoft/skills`), idempotente por nome — não auto-seed no boot.
- Trigger match de KnowledgeNote: match simples case-insensitive do campo `Trigger` contra o conteúdo da mensagem do usuário (substring/palavra-chave) — nota `Pinned` ignora trigger e sempre entra. Sem embeddings nesta v1.

**Files to read before implementing:**
- `src/OpenWebUI.Domain/Entities.cs` — `Skill` (`:1529`), `McpServer` (`:1705`), `KnowledgeCollection` (`:842`), `ConfigEntry`
- `src/OpenWebUI.Api/Endpoints/UserMcpEndpoints.cs` — padrão effective-catalog com `source`/`enabledForMe` (`:46-115`) — **espelhar para skills**
- `src/OpenWebUI.Api/Endpoints/McpEndpoints.cs`, `RepoSkillEndpoints.cs`, `PluginEndpoints.cs` — superfícies existentes
- `src/OpenWebUI.Infrastructure/Services/UserConfigOverlayService.cs` — tombstones `mcp-disabled`/`skills-disabled` (`:37-55,254-287`)
- `src/OpenWebUI.Infrastructure/Services/SkillDiscoveryService.cs` — frontmatter parse + roots (`:313-336,487-498`)
- `src/OpenWebUI.Infrastructure/Services/AutoinvokeSkillsContributor.cs` + `SystemPromptContributors.cs` — ponto de injeção
- `src/OpenWebUI.Client/Pages/Workspace.razor` — padrão de página multi-tab; `Components/Sidebar.razor` — bottom-nav
- `src/OpenWebUI.Client/Services/ApiService.cs` — métodos MCP/skills existentes

**Files to create or modify:**
```text
src/OpenWebUI.Domain/Entities.cs                        # + KnowledgeNote; Skill.UserId -> string?
src/OpenWebUI.Application/Contracts/CustomizeContracts.cs # NOVO — DTOs KnowledgeNote/SkillCatalogItem/ImportRequest
src/OpenWebUI.Api/Endpoints/CustomizeEndpoints.cs        # NOVO — /api/v1/customize/*
src/OpenWebUI.Api/Program.cs                             # mapear grupo
src/OpenWebUI.Infrastructure/Services/UserConfigOverlayService.cs # knowledge-disabled tombstone
src/OpenWebUI.Infrastructure/Services/SystemPromptContributors.cs # KnowledgeNotesContributor
src/OpenWebUI.Infrastructure/Services/SkillImportService.cs # NOVO — fetch repo + parse SKILL.md + upsert
src/OpenWebUI.Client/Pages/Customize.razor               # NOVO
src/OpenWebUI.Client/Components/Customize/{KnowledgeTab,SkillsTab,McpsTab}.razor # NOVO
src/OpenWebUI.Client/Components/Sidebar.razor            # item "Customize"
src/OpenWebUI.Client/Components/ChatView.razor           # ação "Sugerir conhecimento" (menu ⋯ do chat)
src/OpenWebUI.Client/Services/ApiService.cs              # métodos novos
src/OpenWebUI.Client/wwwroot/i18n/{8 locales}.json
.mcp.json                                                # NOVO — deepwiki remoto p/ agentes
tests/OpenWebUI.Api.Tests/CustomizeEndpointsTests.cs     # NOVO
tests/OpenWebUI.Client.Tests/CustomizePageTests.cs       # NOVO
```

## 4. Requirements

### RF-001: Página `/customize` e navegação
- **Description:** Rotas `/customize` e `/customize/{tab}` (`knowledge` default, `skills`, `mcps`), autenticadas. Layout: rail de abas à esquerda + conteúdo — espelha `Workspace.razor`. Item "Customize" na bottom-nav da sidebar, entre Workspace e Playground, para todos os papéis.
- **Rules:** tab inválida → redirect `/customize`; deep-link por tab; badge de escopo em todo item listado (`Organização`/`Pessoal`/`Repo`); seções "Organização" editáveis só para `Role=="admin"` (usuário comum vê read-only + toggle).
- **Input → Output:** clique na sidebar → `/customize/knowledge` renderiza listas org+pessoal.

### RF-002: Entidade `KnowledgeNote` + migration
- **Description:** Nova tabela `KnowledgeNotes`: `Id` (GUID), `UserId` (`string?`, **null=global/admin**), `Title` (único por owner), `Trigger` (descrição/palavras-chave de quando aplicar — obrigatório se `!Pinned`), `Content` (markdown), `Pinned` (bool), `CreatedAt`, `UpdatedAt`. `Skill.UserId` vira `string?` no mesmo migration.
- **Rules:** uniqueness `(UserId, Title)` (nulls distintos); conteúdo ≤ 8000 chars; soft-delete não — delete físico; migration via `dotnet ef migrations add AddCustomizeHub` no projeto Infrastructure.

### RF-003: API de Knowledge (`/api/v1/customize/knowledge`)
- **Description:** `GET` → notas globais (enabled) + próprias do usuário, cada uma com `scope: "org"|"personal"`, `enabledForMe`. `POST` → cria no escopo do papel (admin escolhe `global:true| false`; usuário sempre pessoal). `PUT/{id}`/`DELETE/{id}` → owner ou admin (admin não edita nota pessoal de outro). `PUT /{id}/enabled` → tombstone `u:{id}:knowledge-disabled` (só afeta globais, para o chamador).
- **Rules:** `Trigger` obrigatório quando `Pinned=false`; `Title` duplicado no mesmo owner → `409`; payload sanitizado (trim, limites); resposta nunca inclui conteúdo de nota desabilitada para o chamador? — inclui, com `enabledForMe:false` (permite reativar); notas globais desabilitadas pelo tombstone não entram na injeção daquele usuário.
- **Input → Output:** `POST {title, trigger, content, pinned, global}` → `201 KnowledgeNote`.

### RF-004: Injeção de notas no system prompt
- **Description:** `KnowledgeNotesContributor` injeta no system prompt: todas as notas **pinadas** efetivas (globais não-tombstoned + próprias) + notas cujo `Trigger` case-matcha a última mensagem do usuário. Formato: `## Knowledge\n### {Title}\n{Content}`.
- **Rules:** cap total 4000 chars (truncate com aviso); ordem determinística (globais pin → pessoais pin → matches por UpdatedAt desc); tombstone e `IsActive=false` excluem; sem vazar nota de outro usuário.

### RF-005: "Sugerir conhecimento" a partir do chat
- **Description:** Ação no menu do chat (⋯) "Sugerir conhecimento" abre dialog pré-preenchido com título sugerido = título do chat e conteúdo = texto livre editável (v1 sem LLM); salva via RF-003 no escopo pessoal (ou org se admin e `global:true`).
- **Rules:** rascunho descartável; pós-save link "Abrir em Customize".

### RF-006: Aba Skills — catálogo unificado
- **Description:** `GET /api/v1/customize/skills` retorna lista mesclada: skills DB globais (`UserId null`), DB próprias, e repo/plugin skills do discovery — cada item `{name, description, source:"global"|"personal"|"repo", enabled, enabledForMe, editable, origin}`.
- **Rules:** dedup por `name` — pessoal sombreia global, repo sombreia DB? **Não**: DB e repo são namespaces diferentes — repo skills têm `source:"repo"` e `editable:false` (corpo vem do FS); toggle por usuário via `u:{id}:skills-disabled` existente; **novo** `PUT /api/v1/admin/skills/disabled {names[]}` escreve denylist global `skills-disabled` (admin) — global-off é absoluto (item aparece `enabled:false, disabledByAdmin:true` e toggle do usuário não reabilita — mesmo comportamento de `RepoSkillEndpoints`).
- **Input → Output:** catálogo com 3 origens + estado efetivo por usuário.

### RF-007: Skills DB com escopo org
- **Description:** `Skill.UserId` nullable → `null` = global. `POST/PUT/DELETE /api/v1/customize/skills` — admin cria/edita globais e próprias; usuário só próprias. Skills globais entram no catálogo de todos (salvo tombstone/deny).
- **Rules:** `Name` único por owner (`(UserId,Name)`); mesmas validações de `PluginEndpoints`; conteúdo injetável = `Content` (sem frontmatter — frontmatter só existe na fatia repo/import).

### RF-008: Importar skills de repositório git (admin)
- **Description:** `POST /api/v1/admin/skills/import {repoUrl, subPath="skills", names[]?}` → resolve GitHub repo público via API de contents/trees (ou `git ls-remote`+shallow fetch via `shell_exec` interno — implementador escolhe; sem credencial — repos privados fora de escopo), varre `*/SKILL.md`, faz parse do frontmatter YAML (`name`, `description`) e upserta `Skill` global por `Name` (create ou update de `Description`/`Content`).
- **Rules:** só admin; `names` vazio = todos; limite 100 arquivos/2MB por arquivo; timeout 60s; resposta `{imported:[...], skipped:[{name,reason}]}`; SKILL.md sem `name` → usa nome do diretório; falha de rede → `502` com mensagem; SSRF — só hosts `github.com`/`raw.githubusercontent.com` na v1.
- **Input → Output:** `{repoUrl:"https://github.com/afonsoft/skills"}` → 27 skills globais cadastradas/atualizadas.

### RF-009: Aba MCPs — visão unificada
- **Description:** Duas seções: **"Servidores da organização"** — lista `/api/v1/mcp/servers` (admin: CRUD completo http+stdio; usuário: read-only + toggle `enabledForMe` via endpoint existente); **"Meus servidores"** — `/api/v1/users/user/mcp-servers` (CRUD http-only, toggle). Reusa componentes visuais existentes (status dot, tools badge).
- **Rules:** nenhum endpoint novo; usuário não-admin nunca recebe payload com headers/segredos (já mascarados); seção org escondida inteira? — não, mostrada read-only para transparência.

### RF-010: i18n, a11y e testes
- Novas chaves `customize.*`, `knowledge.*`, `skills.import_*` nos 8 locales; drift guards verdes; testes NUnit (escopo, tombstone, injeção, import com mock HTTP) + bUnit (abas, badges, gated sections).

**Business rules / invariants:**
- Global (`UserId null`) visível para todos; pessoal só para o dono; admin não edita recurso pessoal de outro usuário (só desliga global).
- Toggle do usuário nunca reabilita item com deny global (mesma regra de repo-skills).
- Conteúdo injetado honra budget — conhecimento nunca derruba a montagem do system prompt.

## 5. API Contract

**Endpoint:** `GET /api/v1/customize/knowledge`
**Auth:** Bearer JWT
**Response:**
```json
{ "data": [{ "id": "…", "title": "Convenções de PR", "trigger": "pull request",
             "content": "…", "pinned": false, "scope": "org",
             "enabledForMe": true, "updatedAt": 1762214400 }] }
```

**Endpoint:** `POST /api/v1/customize/knowledge` / `PUT /{id}` / `DELETE /{id}` / `PUT /{id}/enabled`
**Auth:** Bearer JWT; `global:true` só admin (`403` caso contrário); `404/403` ao tocar recurso de outro dono.
**Request (POST/PUT):** `{ "title": "…", "trigger": "…", "content": "…", "pinned": false, "global": false }`

---

**Endpoint:** `GET /api/v1/customize/skills`
**Response:**
```json
{ "data": [{ "name": "write-specs", "description": "…", "source": "global",
             "enabled": true, "enabledForMe": true, "disabledByAdmin": false,
             "editable": true }] }
```

**Endpoint:** `POST /api/v1/customize/skills` / `PUT /{id}` / `DELETE /{id}`
**Request:** `{ "name": "…", "description": "…", "content": "…", "global": false }`

**Endpoint:** `PUT /api/v1/admin/skills/disabled` — admin; `{ "names": ["a","b"] }` → escreve kv `skills-disabled` global.
**Endpoint:** `POST /api/v1/admin/skills/import` — admin; `{ "repoUrl": "…", "subPath": "skills", "names": [] }` → `{ "imported": ["…"], "skipped": [{"name":"…","reason":"…"}] }`.

**Expected errors:** `400` payload inválido · `401` · `403` escopo/role · `404` recurso · `409` título/nome duplicado · `502` falha no fetch do repo.

## 6. Acceptance Criteria

- [ ] **Dado** usuário comum **quando** abre `/customize` **então** vê abas Knowledge/Skills/MCPs e item na sidebar; seções "Organização" aparecem read-only.
- [ ] **Dado** admin que cria nota global pinada **quando** usuário envia mensagem **então** o system prompt contém a nota; usuário pode desativá-la para si (tombstone) sem afetar outros.
- [ ] **Dado** nota com trigger "deploy" **quando** mensagem contém "deploy" **então** nota é injetada; quando não contém, não é.
- [ ] **Dado** admin que clica "Importar de repositório" com `afonsoft/skills` **então** as 27 skills viram globais e aparecem para todos; reimport não duplica (upsert por nome).
- [ ] **Dado** skill global com deny admin **quando** usuário tenta reativar **então** recebe erro e o toggle não liga.
- [ ] **Dado** admin cadastra MCP stdio na seção org **quando** usuário abre aba MCPs **então** vê o servidor com toggle próprio; cadastro http-only na seção pessoal funciona.
- [ ] **Dado** usuário em um chat **quando** escolhe "Sugerir conhecimento" e salva **então** a nota aparece na sua aba Knowledge.
- [ ] Edge: flag/permissões off, conteúdo >8000 chars, repo inacessível, nome duplicado → erros amigáveis sem quebrar a página.

## 7. Task Plan

| # | Task | Validation |
| - | - | - |
| T1 | Entidades + migration (`KnowledgeNote`, `Skill.UserId` nullable) | `dotnet ef migrations add` + build |
| T2 | `CustomizeEndpoints` knowledge CRUD + tombstone + effective skills catalog + admin deny | NUnit: escopo/403/409/tombstone |
| T3 | `KnowledgeNotesContributor` (pin+trigger, budget, tombstone) | NUnit: injeção/exclusão/cap |
| T4 | `SkillImportService` + `POST import` | NUnit com HTTP mock do GitHub |
| T5 | Página `Customize.razor` + 3 tabs + sidebar + ApiService | bUnit: abas/badges/gating |
| T6 | "Sugerir conhecimento" no chat + `.mcp.json` | bUnit + arquivo commitado |
| T7 | i18n ×8 + tailwind rebuild + drift guards | `tools/check-*.py` |
| T8 | Build/test completos + DoD | `dotnet build/test` |

## 8. Organization Guardrails

- Branch `feature/Devin-20261104-customize-hub` a partir de `main` atualizado (após merge do model-visibility-gate ou rebase).
- Sem push/commit em `main`; sem `--no-verify`; secrets nunca commitados; SSRF do import restrito a github.com na v1.
- Schema novo → migration obrigatória (regra do CLAUDE.md).
- Segredos MCP continuam mascarados; nenhum endpoint novo retorna `HeadersJson`/keys.

## 9. Definition of Done

- [ ] `/customize` + 3 abas funcionais, item na sidebar.
- [ ] Knowledge: CRUD escopado, pin/trigger, injeção no prompt, tombstone, "sugerir".
- [ ] Skills: catálogo 3-fontes, CRUD escopado, deny global admin, import de repo (afonsoft/skills testado).
- [ ] MCPs: seções org+pessoal unificadas sobre endpoints existentes.
- [ ] Migration aplicada e testada; i18n 8 locales; drift guards verdes.
- [ ] `.mcp.json` com DeepWiki commitado.
- [ ] Testes NUnit+bUnit novos verdes; build limpo; SPEC marcada `Done`; Issue criada e linkada.
