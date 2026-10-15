# SPEC-20261014-agent-definitions

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `agent-definitions` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261014-agent-definitions` |
| Ticket | #363 |
| Status | `Done` |
| Parent | `SPEC-20261014-cli-opencode-analysis` (RF-001) |

## 1. User Story

**As a** usuário que delega trabalho para sub-agentes
**I want** agentes declarativos (arquivo `.md` com frontmatter) descobertos de múltiplas fontes e invocáveis pelo `delegate_task`
**So that** personas/time de agentes viram dados versionáveis com modelo, tools e permissões próprias — sem código C# por agente.

**Problem context:**
`delegate_task` hoje recebe `{task, persona?, tools?, model?}` inline — cada chamada repete configuração. oh-my-pi (`docs/task-agent-discovery.md`) define agentes em Markdown com frontmatter (`tools`, `spawns`, `model` com fallback, `output` schema, `blocking`, `autoloadSkills`, `prewalk`) descobertos de fontes com precedência (`bundled|user|project|extension`). O fork `alltomatos/opencode` vai além com AgentUI (`packages/opencode/src/agentui/index.ts`) — agentes conversacionais customizados com canais, guardrails, prompts e RAG — e Batuta orquestra até agentes **externos** (CLIs detectados via PATH, `docs/agents/batuta-external-agents.md`).

## 2. Scope

**In scope (fase 1):**
- Formato: `*.agent.md` — frontmatter YAML (`name`, `description`, `model` (id ou `role:`), `tools` allowlist, `spawns` allowlist, `maxDepth`, `outputSchema`, `skills`, `blocking` bool) + corpo = system prompt.
- Discovery com precedência: built-in (embedded) < admin (diretório `data/agents/` ou kv global) < usuário (`data/users/{id}/agents/` ou kv) < workspace (`{workdir}/.openwebui/agents/`).
- `delegate_task` aceita `agent: "code-reviewer"` — resolve a definição, monta o spec (modelo via cadeia de papel/model, tools filtradas pela allowlist, prompt do corpo + task).
- `GET /api/v1/agents` catálogo efetivo por usuário (com provenance — reusa RF-006 de config-layers).
- `agent_catalog` tool read-only para o modelo listar agentes disponíveis (nome + descrição + quando usar).

**In scope (fase 2 — registrada, não implementada aqui):**
- Agentes externos (CLIs no host/servidor via detecção + skill bridge — estilo Batuta).
- `outputSchema` validado com retry.
- Mailbox entre agentes (`agent://`).

**Out of scope:**
- AgentUI completo (canais, RAG por agente) — isso é produto separado, não definição de sub-agente.
- Editor visual de agentes na UI (v1 é arquivo; UI read-only lista catálogo).

## 3. Technical Context

**Where the change happens:**
Novo `AgentDefinitionService` (descoberta + parse frontmatter + merge por precedência); `DelegateTaskBuiltinTool`/`delegate_task` ganha caminho `agent:`; `BuiltinToolRegistry` registra `agent_catalog`; endpoint de catálogo em `AgentEndpoints` ou `UserEndpoints`.

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/DelegateTaskBuiltinTool.cs` (ou onde `delegate_task` está)
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` (como sub-runs são spawnadas)
- `src/OpenWebUI.Infrastructure/Services/SkillDiscoveryService.cs` (padrão de discovery já existente para skills — mesmo shape)
- `src/OpenWebUI.Infrastructure/Services/ConfigService.cs` (kv storage para admin/user agents)
- Referências externas: `/home/ubuntu/repos/oh-my-pi/docs/task-agent-discovery.md`, `/home/ubuntu/repos/opencode-fork/docs/agents/batuta-external-agents.md`

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/AgentDefinitionService.cs     # NOVO
src/OpenWebUI.Infrastructure/ChatTools/Tools/DelegateTaskBuiltinTool.cs  # agent: param
src/OpenWebUI.Infrastructure/ChatTools/Tools/AgentCatalogBuiltinTool.cs  # NOVO read-only
src/OpenWebUI.Api/Endpoints/AgentEndpoints.cs                       # NOVO GET /api/v1/agents
src/OpenWebUI.Application/Contracts/AgentContracts.cs               # AgentDefinition record
tests/OpenWebUI.Api.Tests/AgentDefinitionsTests.cs                  # NOVO
.openwebui/agents/*.agent.md (seed examples: code-reviewer, researcher)
```

## 4. Requirements

### RF-001: Formato `*.agent.md`
- **Description:** frontmatter YAML + corpo markdown. Campos: `name` (id kebab, obrigatório), `description` (obrigatório — é o que `agent_catalog`/`delegate_task` exibe), `model` (id explícito ou `role:advisor|tasks|memory`), `tools` (lista ou `*`; default = tools do caller filtradas a Read), `spawns` (outros agentes que ele pode delegar; default nenhum), `maxDepth` (default 1), `skills` (autoload no system prompt), `blocking` (default true).
- **Rules:** frontmatter inválido → definição ignorada com log; `name` duplicado → precedência decide (maior camada vence).

### RF-002: Discovery com precedência
- **Description:** fontes em ordem crescente: built-in embedded → `data/agents/` (admin) → `data/users/{id}/agents/` (usuário) → `{workdir}/.openwebui/agents/` (workspace do chat). Mesmo `name` = o último vence; provenance registrada.
- **Rules:** sem I/O por call — catálogo cacheado com invalidação por timestamp de diretório; reload manual via endpoint ou restart.

### RF-003: `delegate_task` por referência
- **Description:** `delegate_task({agent:"code-reviewer", task:"…"})` resolve definição → spawn com modelo resolvido (model > role > chat), tools filtradas, system prompt = corpo + `task` como user message.
- **Rules:** agente inexistente → erro estruturado ao modelo com a lista disponível (não inventa); `spawns` do agente respeitado (sub-agente não pode delegar além da allowlist).

### RF-004: Catálogo e `agent_catalog` tool
- **Description:** `GET /api/v1/agents` devolve `[{name, description, model, provenance}]` por usuário; tool `agent_catalog` (Tier=Read) devolve o mesmo em texto compacto para o modelo decidir delegação.
- **Rules:** sem execução — só leitura; o modelo que delega continua sendo o caller atual.

## 5. Acceptance Criteria

- [ ] Teste: `*.agent.md` válido é descoberto e invocável por `delegate_task`.
- [ ] Teste: precedência — definição do usuário sobrescreve built-in de mesmo `name`.
- [ ] Teste: frontmatter inválido não derruba o catálogo.
- [ ] E2E: agente com `tools:["file_read"]` não consegue `file_write` na sub-run (gate aplica).
- [ ] Teste: `agent_catalog` devolve apenas agentes visíveis ao usuário atual.
- [ ] `dotnet test` verde; i18n ×8 para strings novas da UI (se houver catálogo visual); XML docs.

## 6. Open Questions

1. `skills` autoload: resolver do `SkillDiscoveryService` existente (repo-bound) ou skills pessoais do usuário (kv)? — depende da decisão de skills em config-layers (OQ-1 lá).
2. Workspace agents (`{workdir}/.openwebui/agents/`): segurança — um repo clonado pode trazer agentes maliciosos com `tools:*`. Mitigação: agents de workspace só ativam com aprovação explícita do dono do chat (uma vez por chat, lembrado) — mesma filosofia de `sandbox_ask` do oh-my-pi.
3. `outputSchema` na fase 1 ou 2? oh-my-pi tem; validação JSON + 1 retry é barata — deixei em fase 2 para manter v1 enxuto; revisar se a implementação ficar trivial.
