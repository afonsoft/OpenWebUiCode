# SPEC-20261104-model-visibility-gate

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `model-visibility-gate` |
| Type | `Feature` |
| Stack | `.NET` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `feature/Devin-20261104-model-visibility-gate` |
| Ticket | `GH#688` — https://github.com/afonsoft/OpenWebUiCode/issues/688 |
| Status | `Done` |

## 1. User Story

**As a** administrador do Open WebUI .NET
**I want** controlar em `/admin/config` → Modelos quais modelos de cada conexão ficam habilitados para todos os usuários (popup com switch + filtro por provider/nome), ter conexões próprias por usuário, e ver nas minhas settings apenas o meu catálogo efetivo
**So that** o catálogo que cada usuário vê (chat, settings, combos) é exatamente `BYOK/conexões próprias ∪ modelos habilitados pelo admin` — nem mais, nem menos.

**Problem context:**
Hoje **não existe gate global de modelos**: `ProviderService.ListModelsAsync` agrega todas as conexões **globais** do admin e o único filtro é o `disabledModels` por usuário (`SettingsJson`). Consequências: (a) o admin não consegue esconder um modelo de todos os usuários; (b) BYOK (`user:{id}:provider-keys`) só sobrescreve a chave das conexões do admin — não produz catálogo próprio; (c) a aba "Conexões" das settings carrega `/api/v1/configs/connections` (admin-only) → usuário comum fica em "Carregando…" infinito e admin **edita a config global por acidente**; (d) "Combos salvos" lista combos globais mesmo desabilitados e o picker oferece o catálogo inteiro; (e) "Modelos padrão" é só pré-seleção do chat, sem gestão de habilitação.

## 2. Scope

**In scope:**
- **Gate global de modelos (admin)**: denylist `disabledModels` persistida em `ui.models` (`ModelsConfig` estendido); ausência = habilitado → modelos de conexões/chaves novas entram habilitados por padrão.
- **Rework da aba Modelos em `/admin/config`**: seção "Modelos habilitados" com resumo (ativos/total) + popup "Gerenciar modelos" listando **todos** os modelos com switch por item, filtro por provider, busca por nome/id e enable/disable-all por provider; área principal exibe **somente os ativos**. Persistência imediata por toggle. A seção "Modelos padrão" (pré-seleção do chat) permanece, mas suas opções passam a ser só os habilitados.
- **Catálogo efetivo por usuário**: `GET /api/v1/models/all` e `GET /api/models` passam a retornar `modelos de conexões/chaves do próprio usuário ∪ modelos globais habilitados pelo admin`. `CatalogModel` ganha `Source` (`"global" | "user" | "byok"`).
- **Conexões por usuário**: novo kv `user:{id}:connections` (shape `ConnectionsConfig`, chaves mascaradas na resposta), endpoints `GET/PUT /api/v1/configs/user/connections`, gated por nova flag `features.userConnections` (admin). A aba "Conexões" das settings passa a exibir/editar **somente** as conexões do próprio usuário — nunca as do admin (vale também para admin vendo as próprias settings; a config global continua só em `/admin/config`).
- **BYOK como fonte de modelos**: chave BYOK de provider sem conexão própria nem conexão admin daquele tipo gera conexão sintética em `ProviderTypes.DefaultBaseUrl(type)` → os modelos da chave aparecem no catálogo do usuário com `Source: "byok"`. Overlay de chave sobre conexão admin continua como hoje.
- **Combos salvos**: `GET /api/v1/combos/` retorna, para não-admin, apenas combos próprios + globais com `Enabled == true`; o picker de itens usa o catálogo efetivo. Validação server-side de itens contra o catálogo efetivo.
- **Enforcement server-side**: modelo desabilitado pelo admin é bloqueado também nas completions (chat, tasks, agent loop) quando a origem for conexão global — não apenas escondido das listas.
- **Toggle por usuário continua**: `disabledModels` pessoal só estreita o catálogo efetivo (nunca alarga).
- i18n nos 8 locales + drift guards + testes NUnit (API) e bUnit (client).

**Out of scope:**
- RBAC/grupos por modelo (já coberto por `PolicyService` `model.use`/`provider.use` — o gate é ortogonal e mais simples).
- Modelos custom do workspace (`ModelEntry`) — seguem ACL própria (`AccessGrantsJson`), fora do gate.
- Overrides de segurança: usuário não desbloqueia o que o admin bloqueou via policies (policies continuam absolutas).
- Camada por-projeto/workspace de visibilidade.
- Migração do flag `features.direct_connections` (hoje exclusivo de code execution) — `features.userConnections` é flag nova dedicada para não acoplar semânticas.
- UI de diff visual de provenance além do badge `Source`.

## 3. Technical Context

**Where the change happens:**
`ModelsConfig` (`Application/Contracts/ConfigContracts.cs`) ganha `DisabledModels`; `ConfigEndpoints` (`/api/v1/configs/models` GET/POST + novos `PUT /models/disabled`, `GET /models/catalog`, `GET/POST /user_connections`) carrega/valida o gate; `ApiEndpoints` (`ListAllModelsAsync`, `BuildModelListAsync`, `GetCachedModelListAsync`, `Get/UpdateConnectionsAsync`) aplica gate e merge por usuário; `ModelEndpoints.ListCatalogAsync` (`/api/v1/models/all`) filtra por gate + marca `Source`; `ProviderService` (`ListModelsAsync`, `FetchAllModelsAsync`, `ApplyUserKeysAsync`, `Complete*/Stream*`, `ResolveProviderAsync`) aceita contexto de usuário para merge de conexões e enforcement; `UserConfigOverlayService` guarda `user:{id}:connections`; `ComboEndpoints.ListAsync` filtra globais desabilitados para não-admin e valida itens; client: `Pages/Admin.razor` (aba models), `Components/SettingsModal.razor` (abas models/connections/combos), `Services/ApiService.cs`, i18n `wwwroot/i18n/*.json`.

**Decisões de design (fechadas com o usuário):**
- Gate = **denylist** `disabledModels: ["{provider}:{id}"]` dentro de `ui.models` — novos modelos entram habilitados por padrão ao cadastrar nova chave/conexão.
- Gate **não** cobre modelos vindos de BYOK/conexão do usuário (ele paga a própria conta); cobre todos os providers das conexões globais (ollama/openai/anthropic/google/pipeline).
- "Modelos padrão" (pré-seleção do chat, `DefaultModels`) e "Modelos habilitados" (gate) são conceitos separados na mesma aba.
- Enforcement também no servidor (completions), não só nas listagens.
- `disabledModels` por usuário permanece e só estreita.

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` — `ListModelsAsync`, `FetchAllModelsAsync`, `ApplyUserKeysAsync`, `ResolveProviderAsync`, `ConnectionsFingerprint`
- `src/OpenWebUI.Api/Endpoints/ApiEndpoints.cs` — `ListAllModelsAsync`, `GetCachedModelListAsync`, `ParseDisabledModels`, `InvalidateModelListCacheAsync`, `Get/UpdateConnectionsAsync`
- `src/OpenWebUI.Api/Endpoints/ModelEndpoints.cs` — `ListCatalogAsync`
- `src/OpenWebUI.Api/Endpoints/ConfigEndpoints.cs` — `Get/UpdateModelsConfigAsync`, padrão dos endpoints `user/*`
- `src/OpenWebUI.Api/Endpoints/ComboEndpoints.cs` — `ListAsync`, `Validate`
- `src/OpenWebUI.Infrastructure/Services/UserConfigOverlayService.cs` — padrão kv por usuário (`user:{id}:*`)
- `src/OpenWebUI.Application/Contracts/ConfigContracts.cs` — `ModelsConfig`, `ConnectionsConfig(Response)`
- `src/OpenWebUI.Application/Contracts/ModelContracts.cs` — `CatalogModel`, `ModelInfo`
- `src/OpenWebUI.Client/Pages/Admin.razor` — aba `models` (`_defaultModels`, `DefaultModelOptions`, `SaveModelsConfigAsync`), seção providers
- `src/OpenWebUI.Client/Components/SettingsModal.razor` — abas `models`/`connections`/`combos`/`admin`, `LoadModelCatalogAsync`, `ModelEnabled`, `SaveConnectionsAsync`, BYOK
- `tests/OpenWebUI.Api.Tests/ConfigLayersTests.cs`, `ComboEndpointsTests.cs`, `ModelEndpointsTests.cs` · `tests/OpenWebUI.Client.Tests/ModelVisibilityTests.cs`

**Files to create or modify:**
```text
src/OpenWebUI.Application/Contracts/ConfigContracts.cs        # ModelsConfig + DisabledModels; UserConnectionsToggle
src/OpenWebUI.Application/Contracts/ModelContracts.cs         # CatalogModel + Source
src/OpenWebUI.Api/Endpoints/ConfigEndpoints.cs                # PUT models/disabled, GET models/catalog, GET/POST user_connections
src/OpenWebUI.Api/Endpoints/ApiEndpoints.cs                   # gate em ListAllModelsAsync/BuildModelListAsync; GET/PUT user/connections
src/OpenWebUI.Api/Endpoints/ModelEndpoints.cs                 # filtro do gate + Source em ListCatalogAsync
src/OpenWebUI.Api/Endpoints/ComboEndpoints.cs                 # globais Enabled p/ não-admin + validação de itens
src/OpenWebUI.Infrastructure/Services/ProviderService.cs      # merge user connections + synthetic BYOK + gate enforcement
src/OpenWebUI.Infrastructure/Services/UserConfigOverlayService.cs # UserConnectionsKey + flag key
src/OpenWebUI.Client/Pages/Admin.razor                        # rework aba models + popup gerenciador
src/OpenWebUI.Client/Components/SettingsModal.razor           # catálogo efetivo, aba conexões do usuário, combos
src/OpenWebUI.Client/Components/ModelManagerDialog.razor      # NOVO (ou seção inline) — popup com switches/filtros
src/OpenWebUI.Client/Services/ApiService.cs                   # novos endpoints
src/OpenWebUI.Client/wwwroot/i18n/{en-US,pt-BR,es-ES,fr-FR,de-DE,it-IT,ja-JP,zh-CN}.json
tests/OpenWebUI.Api.Tests/ModelVisibilityGateTests.cs         # NOVO
tests/OpenWebUI.Api.Tests/UserConnectionsTests.cs             # NOVO
tests/OpenWebUI.Client.Tests/ModelVisibilityTests.cs          # catálogo efetivo
tests/OpenWebUI.Client.Tests/AdminModelGateTests.cs           # NOVO (popup admin)
```

## 4. Requirements

### RF-001: Gate global de modelos (denylist admin)
- **Description:** `ModelsConfig` ganha `List<string> DisabledModels` (chaves `{provider}:{id}`, case-insensitive). Ausência da chave = habilitado; GET/POST `/api/v1/configs/models` passam a carregá-lo (GET segue aberto a qualquer usuário autenticado — a lista de ids não é segredo).
- **Rules:** sanitização remove entradas vazias/sem `:`; persistência via kv `ui.models`; mutação bem-sucedida invalida os tags `models`/`providers` do HybridCache (hoje `POST /configs/models` não invalida — bug a corrigir via `InvalidateModelListCacheAsync`).
- **Input → Output:** `PUT /api/v1/configs/models/disabled { models: [...] }` (admin) → `200 { models: [...] }` normalizado.

### RF-002: Catálogo admin completo + estado do gate
- **Description:** `GET /api/v1/configs/models/catalog` (admin-only) retorna **todos** os modelos descobertos das conexões globais com `Enabled` = estado do gate — alimenta o popup gerenciador. É a única superfície que lista modelos desabilitados.
- **Rules:** reutiliza `ProviderService.ListModelsAsync` global + `CatalogModel(Source: "global")`; inclui pipelines/custom como hoje (custom/workspace marcados `Source: "workspace"` ou excluídos — implementador decide pelo shape atual, documentado).
- **Input → Output:** `GET` → `CatalogModelListResponse`.

### RF-003: Enforcement do gate no servidor
- **Description:** `ListAllModelsAsync` (`/api/models`) e `ListCatalogAsync` (`/api/v1/models/all`) removem modelos globais cujo `{provider}:{id}` esteja no denylist. Em completions, quando o provider+modelo resolvido vem de conexão **global** e está no denylist, a chamada falha — cobre `/api/chat/completions`, tasks e agent loop via ponto único em `ProviderService` (verificação após `ResolveProviderAsync`, antes do dispatch).
- **Rules:** exceção = o usuário possui fonte própria para aquele provider (conexão `user:` ou chave BYOK cobrindo o tipo) — nesse caso a execução prossegue pela fonte dele; erro genérico `"Modelo desabilitado pelo administrador."` (403), sem vazar ids de outros.
- **Input → Output:** modelo global desabilitado → ausente das listas; `POST /api/chat/completions` → `403`.

### RF-004: Conexões por usuário
- **Description:** Novo kv `user:{userId}:connections` com shape `ConnectionsConfig`. Endpoints self-scope `GET/PUT /api/v1/configs/user/connections` retornam `ConnectionsConfigResponse` (chaves mascaradas via `KeyConfigured`/`ProviderConnectionResponse`, mesmas regras de merge de chave vazia=manter do endpoint admin). Gated por `features.userConnections` (novo flag, default `false`, toggle admin em `GET/POST /api/v1/configs/user_connections`).
- **Rules:** `features.userConnections` off → `403` nos endpoints e a aba Conexões das settings exibe estado "desabilitado pelo administrador"; conexões do usuário nunca expõem nem alteram a config global; segredos em kv por usuário, nunca retornados em GET.
- **Input → Output:** `PUT` com linhas de conexão → `200 ConnectionsConfigResponse` mascarado.

### RF-005: Catálogo efetivo por usuário (merge 3 fontes)
- **Description:** A lista efetiva do usuário = (1) modelos das **conexões globais habilitados** (`Source: "global"`), (2) modelos das **conexões do usuário** (`Source: "user"`, quando flag on), (3) modelos **BYOK** (`Source: "byok"`): para cada provider com chave em `user:{id}:provider-keys` sem conexão própria do tipo, fetch em `ProviderTypes.DefaultBaseUrl(type)` com a chave do usuário.
- **Rules:** dedup por `{provider}:{id}` — fonte do usuário (`user`/`byok`) vence sobre `global` (consistente com BYOK sobrescrever a key no completion); falha de fetch de uma fonte do usuário não derruba a lista (log debug + segue); BYOK sobre conexão admin existente mantém o overlay atual de chave **e** não duplica o fetch (o catálogo global já cobre a URL); modelos `user`/`byok` ignoram o gate; o `disabledModels` pessoal do usuário filtra o resultado final (só estreita).
- **Input → Output:** `GET /api/v1/models/all` → catálogo efetivo com `Source` + `Enabled` (toggle pessoal); `GET /api/models` → mesmo conjunto já filtrado.

### RF-006: Resolução de conexão por request
- **Description:** `ProviderService` passa a resolver conexões efetivas por usuário: conexões `user:` primeiro (match por provider), depois globais com overlay BYOK atual (`ApplyUserKeysAsync`), depois conexão sintética BYOK (default URL + key). `ResolveProviderAsync` continua escolhendo provider por id de modelo.
- **Rules:** assinatura pública ganha contexto de usuário onde já existe `userId`; caches passam a particionar por fingerprint de conexões efetivas (global+user+byok) e por usuário na lista agregada — sem misturar catálogo de usuários diferentes; `userId` nulo (warmup/boot) = comportamento global atual.
- **Input → Output:** request de completion → roteada para a conexão efetiva correta (key/url do usuário quando aplicável).

### RF-007: Rework da aba Modelos em `/admin/config`
- **Description:** Seção "Modelos habilitados": resumo `{ativos}/{total}` + botão "Gerenciar" abrindo dialog com — busca por nome/id, filtro por provider (chips/select), switch por modelo (`role="switch"`, persistência imediata via RF-001), enable/disable-all por provider. Área principal mostra **somente os ativos**. A seção "Modelos padrão" mantém o dropdown atual, mas `DefaultModelOptions` passa a listar só habilitados.
- **Rules:** única tela que vê todos os modelos; estado `saving` por toggle com revert on error; a11y — switches com `role="switch"`/`aria-checked`, dialog com foco gerenciado (padrão `_showDefaultModelsMenu`/focustrap existente); sem alterar `PromptSuggestions`/banners da mesma aba.
- **Input → Output:** toggle no dialog → `PUT disabled` → resumo e chat/settings atualizam.

### RF-008: Aba Conexões das settings = só conexões do usuário
- **Description:** A aba carrega `GET /api/v1/configs/user/connections` para **todo** usuário (não só admin) e edita via PUT — mesmas linhas editáveis (Ollama URL+name; OpenAI URL+name+key; providers anthropic/google). Nunca exibe a config global. Remove o early-return admin (`_connections` null → loading infinito para não-admin — bug corrigido). Seção "Modelos Ollama" (global) permanece admin-only.
- **Rules:** flag off → empty-state "desabilitado pelo administrador"; chave em branco = preserva (merge server-side RF-004); nenhuma chamada a `/api/v1/configs/connections` fora de `/admin/config`.
- **Input → Output:** usuário salva conexão própria → catálogo dele ganha os modelos dela (RF-005).

### RF-009: Combos salvos escopados
- **Description:** `GET /api/v1/combos/` retorna para não-admin: combos `UserId == user.Id` (qualquer estado) + globais `UserId == null && Enabled == true`. Para admin: inalterado (todos os seus + globais, para gestão). Picker de itens no editor de combo usa o catálogo efetivo (já é `_modelCatalog` — passa a vir filtrado pelo RF-005). `Validate` ganha verificação de item contra o catálogo efetivo do usuário.
- **Rules:** item inválido (não visível para o chamador) → `400` com detalhe genérico; combo global desabilitado some da lista de não-admin (admin ainda vê/gerencia); badges `global`/`seu` mantidos.
- **Input → Output:** listagem filtrada por role; criação/edição com item invisível → `400`.

### RF-010: i18n + a11y + testes
- **Description:** Novas chaves nos 8 locales (`admin.models_enabled*`, `admin.manage_models`, `settings.connections_user_*`, `settings.disabled_by_admin`, `models.source_*`…); drift guards (`check-i18n-parity.py`, `check-css-classes.py`, `check-form-a11y.py`) verdes; testes NUnit cobrindo gate/merge/combos/connections e bUnit cobrindo popup admin + catálogo filtrado.
- **Rules:** semântica pt-BR nas mensagens; cobertura ≥80% nas regras de negócio novas (tabela §7.1).

**Business rules / invariants:**
- Ausência do denylist ⇒ tudo habilitado (migração zero — nenhuma migration/schema novo, só kv).
- Gate ≠ policy: `PolicyService` (deny por grupo) continua avaliado antes e é absoluto; o gate só remove/adiciona itens do catálogo global.
- Um usuário nunca vê conexão, chave ou catálogo além do seu próprio + o global habilitado.
- `ui.models.DefaultModels` que fique apontando para modelo desabilitado é ignorado na UI (pré-seleção filtra por habilitado) sem precisar de limpeza.

## 5. API Contract

**Endpoint:** `PUT /api/v1/configs/models/disabled`
**Auth:** Bearer JWT, role `admin` (403 non-admin)

**Request:**
```json
{ "models": ["{provider}:{modelId}", "openai:gpt-4o", "google:gemini-3-flash"] }
```

**Response (success):**
```json
{ "models": ["openai:gpt-4o", "google:gemini-3-flash"] }
```

**Expected errors:** `400` (payload inválido — entrada sem `:` ou tipo errado), `401`, `403`.

---

**Endpoint:** `GET /api/v1/configs/models/catalog`
**Auth:** Bearer JWT, role `admin`

**Response (success):**
```json
{ "data": [{ "id": "gpt-4o", "name": "GPT-4o", "provider": "openai",
             "ownedBy": "openai", "enabled": false, "source": "global",
             "capabilities": ["vision","tools"] }] }
```
`enabled` aqui = estado do gate global (único endpoint que expõe desabilitados).

---

**Endpoint:** `GET /api/v1/configs/user/connections` e `PUT /api/v1/configs/user/connections`
**Auth:** Bearer JWT (self); `403` quando `features.userConnections` off ou flag desabilitada.
**Request/Response:** idênticos a `POST /api/v1/configs/connections` / `ConnectionsConfigResponse` — chaves nunca retornadas (`keyConfigured` por posição; `providers[].keyConfigured`). Chave vazia no PUT = preserva a existente na mesma URL.

---

**Endpoint:** `GET /api/v1/configs/user_connections` e `POST /api/v1/configs/user_connections`
**Auth:** GET autenticado (client precisa saber se a aba mostra empty-state); POST admin-only.
**Request/Response:** `FeatureToggle { enabled }` — mesmo padrão de `user_provider_keys`.

---

**Endpoint:** `GET /api/v1/models/all` (alterado)
**Auth:** Bearer JWT
**Response:** `CatalogModel` ganha `source: "global" | "user" | "byok"`; modelos globais no denylist **ausentes**; `enabled` = toggle pessoal do chamador.

**Endpoint:** `GET /api/v1/combos/` (alterado) — não-admin recebe apenas globais com `enabled == true`.

## 6. Acceptance Criteria

- [x] **Dado** admin com conexão OpenAI cadastrada **quando** abre `/admin/config` → Modelos → Gerenciar **então** vê todos os modelos OpenAI habilitados (default) com switch ligado.
- [x] **Dado** admin desabilita `openai:gpt-4o` no popup **quando** usuário comum lista `/api/models` ou o seletor do chat **então** `gpt-4o` não aparece; admin também não o vê no chat (só no popup).
- [x] **Dado** `openai:gpt-4o` desabilitado **quando** usuário sem BYOK chama `POST /api/chat/completions {model:"gpt-4o"}` **então** `403` genérico.
- [x] **Dado** usuário com chave BYOK openai **quando** lista `/api/v1/models/all` **então** vê modelos da sua chave com `source:"byok"` mesmo sem conexão admin openai — inclusive `gpt-4o` desabilitado globalmente.
- [x] **Dado** `features.userConnections` on **quando** usuário salva `PUT /user/connections` com um Ollama próprio **então** os modelos dele aparecem com `source:"user"` e requests roteam para a URL/chave dele.
- [x] **Dado** a aba Conexões **quando** aberta por usuário comum **então** não exibe nem carrega conexões do admin (sem chamada a `/configs/connections`); quando off, exibe empty-state.
- [x] **Dado** combo global `Enabled=false` **quando** usuário comum lista `/api/v1/combos/` **então** não aparece; admin continua vendo.
- [x] **Dado** usuário tenta criar combo com modelo fora do seu catálogo efetivo **então** `400` genérico.
- [x] **Dado** usuário com `disabledModels` pessoal contendo `global:model-x` **quando** lista catálogo **então** `model-x` aparece `enabled:false` (toggle pessoal) e some de `/api/models`.
- [x] **Dado** admin cadastra nova chave Anthropic **quando** reabre o popup **então** todos os modelos novos vêm habilitados (denylist inalterada).

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Provider offline na listagem | conexão fora do ar | item ausente da lista, sem erro; denylist preserva a chave para quando voltar |
| Entrada obsoleta no denylist | `{provider}:{id}` não existe mais | ignorada na filtragem (mesmo padrão `ParseDisabledModels`) |
| Modelo id colide admin×user | `openai:gpt-4o` nas duas fontes | dedup → fonte do usuário vence, `source:"user"`/`"byok"` |
| Chave BYOK inválida | fetch 401 do provider | fonte byok vazia, log debug, lista continua |
| `ui.models.DefaultModels` aponta p/ desabilitado | modelo no denylist | pré-seleção ignora; sem limpeza obrigatória |
| Flag `userConnections` off | GET/PUT user/connections | `403`; aba exibe empty-state |
| Toggle admin falha (rede) | PUT disabled erro | switch reverte + nota de erro |

## 7. Task Plan (agent execution)

- [x] **T1 — Discovery:** ler os arquivos da seção 3 e confirmar os pontos de merge/cache/enforcement.
- [x] **T2 — Contracts + config:** `ModelsConfig.DisabledModels`, `CatalogModel.Source`, endpoints `models/disabled`, `models/catalog`, `user_connections` (ConfigEndpoints), `GET/PUT user/connections` (ApiEndpoints), flag kv em `UserConfigOverlayService`.
- [x] **T3 — ProviderService:** merge de conexões efetivas (user + global + byok sintético), particionamento de cache por usuário, gate check pós-`ResolveProviderAsync` nos 3 caminhos de completion.
- [x] **T4 — Listagens:** `BuildModelListAsync`/`ListCatalogAsync` com merge 3 fontes + gate + `Source`; `ComboEndpoints.ListAsync` + validação de itens.
- [x] **T5 — Client admin:** seção "Modelos habilitados" + dialog gerenciador (switches, filtros, enable/disable-all), `DefaultModelOptions` filtrado.
- [x] **T6 — Client settings:** aba Conexões → user connections (reuso das rows); catálogo efetivo + badges `source`; combos picker/validação; empty-states.
- [x] **T7 — i18n ×8 + tailwind rebuild** se classes novas.
- [x] **T8 — Testes:** NUnit (gate, merge, enforcement 403, combos scope, user connections mask/merge, flag off → 403) + bUnit (popup admin, catálogo filtrado, aba conexões non-admin) + regressão dos existentes (`ModelVisibilityTests`, `ConfigLayersTests`, `ComboEndpointsTests`, `ModelEndpointsTests`).
- [x] **T9 — Validation:** `dotnet build`, `dotnet test`, drift guards, DoD (seção 9) → `Status = Done` + PR `feature/Devin-20261104-model-visibility-gate`.

**7.1 Validation strategy by type/stack**

| Type / Stack | Required evidence |
|---|---|
| **.NET** | Unit tests for business rules; integration tests for API/data flows; minimum **80%** coverage. |
| **Frontend (bUnit)** | Render/interaction tests do popup admin, catálogo efetivo, aba conexões — espelhando `ModelVisibilityTests`. |

## 8. Organization Guardrails

- **Branches:** `feature/Devin-20261104-model-visibility-gate`; nunca commit direto em `main`; merge via PR.
- **Workflows:** não modificar `.github/workflows/`.
- **Security:** chaves (admin e usuário) nunca retornam em GET — sempre `keyConfigured`; nenhum log de tokens/keys; `403` genérico sem vazar ids de modelos de outros usuários; secrets ficam em kv, nunca em commit.
- **Scope:** não expandir para RBAC/grupos de modelos nem camada workspace — policies já cobrem.
- **Architecture:** regra de merge em `ProviderService`/`UserConfigOverlayService` (infrastructure), endpoints finos; client só consome contratos `Application`.
- **Upstream parity:** manter shapes compatíveis (`ConnectionsConfig`, `ModelListResponse`); divergências só onde a feature exige (`Source`, `DisabledModels`, `user/connections`).

## 9. Definition of Done

- [x] Todos os RF-001..RF-010 implementados.
- [x] Critérios de aceite (seção 6) cobertos por testes passando.
- [x] Edge cases tratados (provider offline, denylist obsoleto, colisão de fontes, flag off).
- [x] `dotnet build OpenWebUI.slnx --configuration Release` e `dotnet test` verdes; cobertura ≥80% nas regras novas.
- [x] Drift guards (`check-i18n-parity.py`, `check-css-classes.py`, `check-form-a11y.py`, `check-spec-status.py`) verdes.
- [x] Guardrails da seção 8 respeitados; sem secrets/PII em logs; erros no formato genérico.
- [x] i18n ×8 completo; `tailwind.css` regerado se classes novas.

**Next action after DoD is complete:** `Status = Done` + PR na branch `feature/Devin-20261104-model-visibility-gate` referenciando a issue.

## Open Questions / Pending Ambiguity

- Nenhuma bloqueante. Decisões registradas na seção 3 (todas confirmadas pelo usuário).

## Delivered

PR #692 (merged `f2f04ac`): `ModelsConfig.DisabledModels` (denylist
global); `CatalogModel.Source` (`global|user|byok`); endpoints
`/configs/models/{disabled,catalog}` (admin) e `GET/PUT user/connections`
(gated por `features.userConnections`, chaves mascaradas); merge de
conexões efetivas + cache por usuário + gate 403 pós-resolve nos 3
caminhos de completion em `ProviderService`; `ListCatalogAsync`/`/api/models`
com catálogo efetivo; `ComboEndpoints` escopo user+globais habilitados;
seção "Modelos habilitados" + `ModelManagerDialog` no admin; aba Conexões
do usuário (bug loading infinito corrigido); badges de origem; i18n ×8;
9 NUnit (`ModelVisibilityGateTests`) + 8 bUnit (`UserConnectionsTests`).
