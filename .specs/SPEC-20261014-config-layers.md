# SPEC-20261014-config-layers

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `config-layers` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261014-config-layers` |
| Ticket | #364 |
| Status | `Approved` |
| Parent | `SPEC-20261014-cli-opencode-analysis` (RF-001) |

## 1. User Story

**As a** usuário individual do Open WebUI .NET
**I want** sobrescrever por-usuário o que hoje é global do admin (servidores MCP, GitHub, skills, papéis de modelo, chaves de provider)
**So that** o admin define o padrão da organização e cada usuário adapta o seu ambiente sem precisar de privilégio admin.

**Problem context:**
Pedido explícito do usuário: *"Quero que por usuário seja possível configurar o github, mcp, skills, entre outros. Que tenha a configuração global e cada usuário pode ter uma configuração individual para fazer o override do global."* Hoje: MCP servers são só admin-global (`McpServerConfig.UserId == null`); conexões de provider/API keys são só admin; GitHub PAT já é por usuário mas não tem default global; skills são repo-bound; model roles já são por usuário (#356) mas sem fallback para um default global do admin. oh-my-pi (`docs/config-usage.md`) tem `env > runtime > overlay > project > global > default` com `provenance()` — o equivalente para nós (multi-user web, não CLI) é `default → admin global → usuário`.

## 2. Scope

**In scope:**
- Camada de override por usuário sobre o catálogo admin de **MCP servers**: usuário adiciona servidores próprios (com credenciais próprias, nunca expostas a outros usuários) e pode **desabilitar** um servidor global para si.
- **GitHub**: default de org/admin (ex.: token de org ou app) + override por usuário (PAT atual) — quando ambos existem, o do usuário vence.
- **Skills**: catálogo repo-bound continua; usuário pode desabilitar skills globais e registrar skills próprias (upload/paths a definir — ver OQ-1).
- **Model roles**: default global do admin + override por usuário (o papel já resolve por usuário — falta o fallback global antes do fallback pro modelo do chat).
- **Providers/BYOK (opcional nesta SPEC)**: usuário registra chave própria para um provider existente — requests dele usam a key dele; sem key própria → key global.
- **Provenance**: cada valor efetivo carrega a origem (`default|global|user`); a UI de Settings mostra "herdado de admin" vs "seu override" com botão "redefinir".

**Out of scope:**
- Camada por-projeto/workspace (fase 2 — com `workspace.repo` binding).
- Overrides de segurança: usuário não pode desbloquear o que o admin bloqueou (ex.: tool `deny` admin é absoluto — ver RF-006).
- Permissões/RBAC por grupo.
- UI de comparação visual diff (v1 = badge de provenance por item).

## 3. Technical Context

**Where the change happens:**
`ConfigService` (kv pattern já usado: `user:{id}:tool-approvals`); `McpClientService`/`McpConfigService` (merge admin+user catalog); `GitHubService` (resolução de token); `ProviderService`/`ConnectionsConfig` (resolução de key por request); `ModelRoleResolver` (fallback global); endpoints em `UserEndpoints`/`ConfigEndpoints` espelhando os admin para escopo `user/`; `SettingsModal.razor` badges de provenance.

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/Services/McpClientService.cs` + config de servidores (`UserId` field)
- `src/OpenWebUI.Infrastructure/Services/GitHubService.cs` (resolução de PAT por usuário)
- `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` (`ListModelsAsync`, resolução de key)
- `src/OpenWebUI.Infrastructure/Services/ModelRoleResolver.cs` (#356)
- `src/OpenWebUI.Api/Endpoints/UserEndpoints.cs` (padrão `/user/tool-approvals` como referência de merge por usuário)
- `src/OpenWebUI.Infrastructure/Services/ConfigService.cs` (`GetAsync`/`SetAsync` kv)

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/UserConfigOverlayService.cs   # NOVO: merge user/global + provenance
src/OpenWebUI.Infrastructure/Services/McpConfigService.cs           # catálogo efetivo = global + user − disabled
src/OpenWebUI.Infrastructure/Services/GitHubService.cs              # fallback token user→global
src/OpenWebUI.Infrastructure/Services/ProviderService.cs            # key override por request (BYOK)
src/OpenWebUI.Infrastructure/Services/ModelRoleResolver.cs          # fallback user→global→chat
src/OpenWebUI.Api/Endpoints/UserEndpoints.cs                        # /user/mcp-servers, /user/providers/keys
src/OpenWebUI.Application/Contracts/*Contracts.cs                   # MCP server user record, key record, provenance dto
src/OpenWebUI.Client/Components/SettingsModal.razor                 # provenance badges + seções
src/OpenWebUI.Client/wwwroot/i18n/*.json                            # 8 locales
tests/OpenWebUI.Api.Tests/ConfigLayersTests.cs                      # NOVO
```

## 4. Requirements

### RF-001: Resolvedor de configuração em camadas
- **Description:** `UserConfigOverlayService.GetAsync<T>(key, userId)` resolve `user:{userId}:{key}` → `admin:{key}` → default de código, devolvendo `(value, provenance)`.
- **Rules:** provenance sempre exposta; deletes por usuário = tombstone explícito (não "vazio = herda" para coleções — `user:{id}:mcp-servers:disabled` separa "ausente" de "desligado").

### RF-002: MCP por usuário
- **Description:** catálogo efetivo de MCP do usuário = servidores globais habilitados + servidores do usuário − globais desabilitados por ele. CRUD user-scoped (`/api/v1/users/user/mcp-servers`) espelha o admin (`/api/v1/configs/mcp`); credenciais do usuário ficam em kv por usuário, nunca no blob global.
- **Rules:** nome de servidor do usuário não pode colidir com um global habilitado (override por nome = substitui a config global para aquele usuário — documentado); servidor global desabilitado pelo admin some de todos.

### RF-003: GitHub default global + override
- **Description:** `github.token` ganha escopo admin (org default); resolução por request: token do usuário → token global → erro atual.
- **Rules:** token global nunca é emitido ao client de outro usuário; a UI mostra provenance sem revelar o valor.

### RF-004: Model roles com fallback global
- **Description:** resolução de papel: cadeia do usuário → cadeia global do admin → modelo do chat.
- **Rules:** só entra em vigor quando #356 mergeado; ausência total = comportamento atual.

### RF-005: BYOK por usuário (opcional)
- **Description:** `user:{id}:provider-keys` = `{connectionId|provider: key}`; `ProviderService` resolve a key por request por usuário.
- **Rules:** flag admin `features.userProviderKeys` (default off) — BYOK desligado = key global para todos; quando on, quota/custo passam a ser por usuário (documentar).

### RF-006: Provenance + assimetria de segurança
- **Description:** UI de Settings mostra `Herdado`/`Seu` por item com "redefinir" quando há override; overrides de usuário nunca podem **alargar** permissão sobre negação admin (tool/servers bloqueados pelo admin não aparecem como opcionais).
- **Rules:** deny admin > tudo; allow user < allow admin; overrides só estreitam ou personalizam dentro do permitido.

## 5. Acceptance Criteria

- [ ] Teste: usuário adiciona MCP server próprio → aparece no catálogo dele, não no de outro usuário.
- [ ] Teste: desabilitar servidor global afeta só o usuário que desabilitou.
- [ ] Teste: GitHub sem token de usuário usa o global; com token, usa o do usuário.
- [ ] Teste: provenance `default|global|user` correta em cada caminho.
- [ ] Teste: key de usuário não aparece em resposta a outro usuário; valor nunca retorna em GET (só `hasKey:true`).
- [ ] `dotnet test` verde; i18n ×8; drift guards; XML docs em endpoints públicos.

## 6. Open Questions

1. Skills por usuário: upload de `.md` próprio vs só toggle das repo-bound? Skills são arquivos no workspace/repo — upload isolado por usuário precisa de storage próprio (kv?) ou de skills em `data/users/{id}/skills/`. Proposta: v1 = toggle sobre catálogo + skills pessoais em kv; fase 2 decide storage de arquivo.
2. BYOK liga billing: aceitar que quota/admin reports misturem custos de keys diferentes? Regra documentada.
3. MCP servers do usuário rodam no servidor (stdio) ou remote http? v1: só `http`/`sse` remotos para servidores de usuário — stdio user-provided é superfície de segurança admin-only.
