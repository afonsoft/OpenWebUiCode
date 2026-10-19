# SPEC-20261018-policies-admin

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `policies-admin` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261018-policies-admin` |
| Status | `In implementation` |
| Parent | `docs/analise-opencode-v2.md` (P1.1) |

## 1. User Story

**As a** admin da instância
**I want** regras globais de hard-deny sobre providers, ferramentas e integrações (MCP/skills/plugins)
**So that** eu consiga proibir `shell` com `rm -rf`, bloquear um provider inteiro ou um MCP servidor, sem depender do bom comportamento de cada usuário — complementando o RBAC por grupo (que controla *who*, não *what*). Paridade com opencode v2 `policies` + Console.

## 2. Scope

**In scope:**
- Modelo `PolicyStatement { action, resource, effect }` onde `action ∈ {provider.use, tool.use, integration.use, model.use}`, `resource` é glob (ex.: `shell:rm -rf*`, `anthropic/*`, `mcp:filesystem`), `effect = deny` (allow implícito = ausência de deny).
- Avaliação ordenada, **last-match-wins** dentro do conjunto; qualquer deny casa → operação bloqueada com mensagem nomeando a policy violada.
- Enforcement points: seleção de provider/modelo no catálogo e no run, gate de permissão de tool (antes de ask/allow do usuário), carregamento/execução de MCP server, skill e plugin.
- Admin UI: aba "Policies" em Admin → tabela CRUD de statements + teste "simular" (dry-run mostra o que casaria). Audit log de bloqueios.
- Persistência EF (`AdminPolicyStatement`) + seed via `WEBUI_POLICIES_JSON` para GitOps.

**Out of scope:**
- Console remoto multi-workspace (opencode tem; nosso escopo é por instância). Policy `allow` explícito que sobreponha deny — deny é absoluto em v1.
- Merge de policies vindas de plugins (futuro).

## 3. Technical Context

**Where the change happens:**
Novo `PolicyService` (Infrastructure) injetado em `ProviderCatalogService`/`ChatRunExecutor`, `PermissionService`, `McpClientService`, `SkillDiscoveryService`, `PluginRuntime`. UI em `src/OpenWebUI.Client/Pages/Admin/`.

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/Services/PermissionService.cs` — ponto de gate de tools
- `src/OpenWebUI.Infrastructure/Services/McpClientService.cs` — connect/refresh
- `src/OpenWebUI.Infrastructure/Services/SkillDiscoveryService.cs` — loading de skills
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` — resolução de provider/modelo por run
- `src/OpenWebUI.Domain/Entities.cs` — padrão de entidades/config

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/PolicyService.cs      # NOVO: parse + match ordenado
src/OpenWebUI.Domain/Entities.cs                          # AdminPolicyStatement + audit
src/OpenWebUI.Api/Endpoints/AdminPolicyEndpoints.cs       # NOVO: CRUD + simulate
src/OpenWebUI.Client/Pages/Admin/AdminPolicies.razor      # NOVO
src/OpenWebUI.Infrastructure/Services/PermissionService.cs# check tool.use policy antes do ask
src/OpenWebUI.Infrastructure/Services/McpClientService.cs # check integration.use no connect
src/OpenWebUI.Infrastructure/Services/ProviderCatalog*.cs # check provider.use/model.use
tests/OpenWebUI.Api.Tests/PolicyServiceTests.cs           # NOVO
tests/OpenWebUI.Api.Tests/PolicyEnforcementTests.cs       # NOVO
```

## 4. Requirements

### RF-001: Modelo e avaliação ordenada
- **Description:** Statements ordenados por `Order` (ou ordem do array JSON); avaliação percorre todas as regras, última que casa decide — mas `effect` só pode ser `deny` em v1, então basta "qualquer match → deny". `resource` usa glob (`*`, `**`) sobre o identificador concreto (`toolname`, `toolname:args-prefix`, `providerid/modelid`, `mcp:serverid`, `skill:name`).
- **Rules:** matching case-insensitive para ids; glob sobre argumentos só para `tool.use` (ex.: `shell:rm -rf *`). Regra sem `resource` válido é rejeitada no save com 400.

### RF-002: Enforcement em tool.use
- **Description:** `PermissionService`/gate de tools consulta `PolicyService` ANTES das regras de usuário/ask — policy admin vence tudo. Deny retorna erro `PolicyDenied` para o modelo (não crasha a run; o modelo vê "blocked by admin policy X").
- **Rules:** cobre builtin tools e MCP tools (nome `mcp:{server}:{tool}`).

### RF-003: Enforcement em provider.use/model.use
- **Description:** seleção de modelo (chat, persona, command `model:`, subagent) valida `provider.use`/`model.use`; deny remove o modelo da lista retornada ao usuário e rejeita request explícito com 403+policy id.

### RF-004: Enforcement em integration.use
- **Description:** `McpClientService.ConnectAsync`, `SkillDiscoveryService` load e `PluginRuntime` consultam policy por id/nome; deny = não conecta/não carrega, e loga `policy_denied` com (user, action, resource, statement).

### RF-005: Admin UI + simulate
- **Description:** CRUD com editor de statements (linhas action/resource) + botão "Testar" que recebe `{action, resource}` e mostra qual statement casou. Lista de bloqueios recentes (audit).

## 5. Acceptance Criteria

- `tool.use` + `shell:rm *` deny → run com `shell` `rm -rf /tmp/x` falha com PolicyDenied e nome da policy; `shell` `ls` passa.
- `provider.use` + `openai/*` deny → modelos openai somem do seletor e request direto retorna 403.
- `integration.use` + `mcp:*` deny → nenhum MCP conecta; servidor específico deny deixa outros funcionarem.
- Simulate endpoint retorna o statement que casa (ou "no match") para input arbitrário.
- Audit registra cada deny com timestamp, user, action, resource, statement id.
- Testes NUnit cobrindo match order, glob, enforcement nos 3 pontos.
