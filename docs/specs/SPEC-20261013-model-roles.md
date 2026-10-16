# SPEC-20261013-model-roles

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `model-roles` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261013-model-roles` |
| Ticket | [#350](https://github.com/afonsoft/open-webui/issues/350) — PR [#356](https://github.com/afonsoft/open-webui/pull/356) |
| Status | `Completed` |

## 1. User Story

**As a** usuário/admin do Open WebUI .NET
**I want** atribuir modelos diferentes a papéis auxiliares (tarefas de UI, memória/compaction, advisor, juiz) e saber as capacidades de cada modelo (visão, tools)
**So that** tarefas baratas não queimem o modelo principal, papéis futuros (advisor) tenham roteamento próprio, e a UI saiba quando o modelo escolhido suporta imagens.

**Problem context:**
Hoje um único modelo por chat resolve tudo: `TaskEndpoints` (título/follow-ups/tags/queries) chamam `ProviderService.CompleteAsync` com `request.Model` = modelo do chat; o `LlmContextSummarizer` da compaction idem. Não existe noção de capacidade — a SPEC-20261013-chat-media-attachments (#349) deixou "modelo sem visão → erro do provider" justamente porque não há capability map. Inspirado no `modelRoles` + `retry.fallbackChains` + catálogo KDL do oh-my-pi (policy como dados, não código).

## 2. Scope

**In scope:**
- Papéis de modelo por usuário: `chat` (default implícito), `tasks` (título/follow-ups/tags/queries), `memory` (summaries de compaction e memória), `advisor` (reservado p/ SPEC run-advisor).
- Cadeia de fallback por papel (`role → [m1, m2, ...]`); papel não configurado → modelo do chat (comportamento atual).
- `ModelCapabilityResolver`: capacidades por modelo (`vision`, `tools`, `thinking`) vindas de (a) catálogo estático `model-capabilities.json` commitado (regras por provider + padrão de id), (b) descoberta do provider quando disponível (Ollama `/api/show` `capabilities`), (c) override manual do admin.
- `ModelInfo` ganha `IReadOnlyList<string> Capabilities`; `GET /api/v1/models` e `/all` emitem.
- Client: composer avisa (não bloqueia) ao anexar imagem com modelo sem `vision` — fecha a pendência da SPEC #349.
- UI de settings: dropdown por papel alimentado pelo catálogo; persistência por usuário.

**Out of scope:**
- Sintaxe KDL (o catálogo é JSON simples — .NET não precisa do toolchain KDL do omp).
- Effort/thinking ladders por modelo (SPEC futura).
- Preço/cost tracking por modelo.
- Roteamento automático por conteúdo (router inteligente) — papéis são fixos por configuração.

## 3. Technical Context

**Where the change happens:**
`ConfigService`/`AdminConfig` (papéis default do admin) e settings por usuário (`db` kv já existente — ver `UserSettings`/settings endpoints); `ModelCapabilityResolver` novo serviço em Infrastructure consultando catálogo + cache de `/api/show`; `TaskEndpoints.RunTaskAsync` e `LlmContextSummarizer.SummarizeAsync` resolvem via `ModelRoleResolver` em vez de usar `request.Model` direto; `ModelEndpoints` emite `Capabilities`; `SettingsModal.razor` nova seção; `ChatView.razor` warning de visão.

**Files to read before implementing:**
- `src/OpenWebUI.Api/Endpoints/TaskEndpoints.cs` (`RunTaskAsync` L99-125)
- `src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs` (`LlmContextSummarizer`)
- `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` (resolução provider/modelo, `CompleteAsync`)
- `src/OpenWebUI.Api/Endpoints/ModelEndpoints.cs`
- `src/OpenWebUI.Infrastructure/Services/ConfigService.cs` + `AdminConfig`
- `src/OpenWebUI.Client/Components/SettingsModal.razor` (seções de settings)
- `src/OpenWebUI.Client/Components/ChatView.razor` (`_model`, attach flow)

**Files to create or modify:**
```text
src/OpenWebUI.Application/Contracts/ModelContracts.cs        # Capabilities em ModelInfo + UserModelRoles contract
src/OpenWebUI.Application/Contracts/ConfigContracts.cs       # campos admin (roles default, capability overrides)
src/OpenWebUI.Infrastructure/Services/ModelRoleResolver.cs   # NOVO
src/OpenWebUI.Infrastructure/Services/ModelCapabilityResolver.cs # NOVO
src/OpenWebUI.Infrastructure/Services/model-capabilities.json    # NOVO catálogo (embedded resource)
src/OpenWebUI.Api/Endpoints/TaskEndpoints.cs                 # resolve papel "tasks"
src/OpenWebUI.Api/Endpoints/ModelEndpoints.cs                # emite Capabilities
src/OpenWebUI.Api/Endpoints/ConfigEndpoints.cs               # GET/PUT user model-roles
src/OpenWebUI.Api/Completions/ChatPipeline.cs                # papel "memory" p/ summarizer
src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs
src/OpenWebUI.Client/Components/SettingsModal.razor          # seção "Papéis de modelo"
src/OpenWebUI.Client/Components/ChatView.razor               # aviso de modelo sem visão ao anexar imagem
src/OpenWebUI.Client/Services/ApiService.cs                  # Get/Put model roles
src/OpenWebUI.Client/wwwroot/i18n/*.json                     # 8 locales
tests/OpenWebUI.Api.Tests/                                   # resolver, fallback, capabilities
```

## 4. Requirements

### RF-001: Papéis de modelo por usuário
- **Description:** O usuário configura `modelRoles` (`tasks`, `memory`, `advisor`) cada qual como seletor de modelo + lista de fallback; `chat` não é configurável (é o modelo do chat em uso).
- **Rules:** papel ausente → modelo do chat; fallback iterado em ordem até um resolver (provider online + id conhecido); nenhum resolve → `null` e o caller usa o modelo do chat (nunca falha por config).
- **Input → Output:** `(role, chatModel)` → modelo efetivo.

### RF-002: Roteamento das tasks auxiliares
- **Description:** `TaskEndpoints` (title/follow_ups/tags/queries) e `LlmContextSummarizer` chamam `CompleteAsync` com o modelo resolvido do papel correspondente — não mais o `request.Model` cego.
- **Rules:** a requisição mantém o modelo do chat como último fallback; resposta e formato atuais inalterados; falha no papel → retry no próximo da cadeia → modelo do chat.

### RF-003: Catálogo de capacidades
- **Description:** `model-capabilities.json` embedded com regras `{provider, pattern-glob, capabilities[]}`; Ollama consulta `/api/show` (`capabilities` do upstream) com cache; admin pode adicionar override por id exato via config.
- **Rules:** matching por provider+glob, mais específico vence; desconhecido → `[]` (sem capacidade assumida — fail-safe conservador para `vision`); sem nova dependência NuGet.
- **Input → Output:** `(provider, modelId)` → `["vision","tools","thinking",...]`.

### RF-004: Aviso de visão no composer
- **Description:** Ao anexar arquivo `image/*` com `Capabilities` do modelo atual sem `vision`, o composer mostra aviso inline ("modelo pode não analisar imagens") — não bloqueia o envio (SPEC #349 manda mesmo assim).
- **Rules:** aviso descartável por sessão; sem capabilities carregadas (offline) → não avisa; vídeo não dispara aviso de visão (fora de escopo de análise).

### RF-005: Settings UI + i18n
- **Description:** Seção "Papéis de modelo" em SettingsModal com 3 dropdowns (catálogo de modelos) + hint de fallback; chaves i18n nos 8 locales.

**Business rules / invariants:**
- Capabilities nunca bloqueiam funcionalidade — são aviso/metadata (a autoridade final é o provider).
- `modelRoles` é por usuário; admin define só defaults/overrides de catálogo.
- `advisor` sem consumidor até a SPEC run-advisor — resolver já existe, UI pode ocultar se `advisor` não estiver habilitado.

## 5. API Contract

**`GET /api/v1/models` / `/all`** — `ModelInfo`/`CatalogModel` ganham `capabilities: string[]`.

**`GET /api/v1/configs/user/model-roles`** (novo)
```json
{ "roles": { "tasks": ["ollama:qwen2.5:0.5b", "openai:gpt-4o-mini"], "memory": [], "advisor": [] } }
```
**`PUT /api/v1/configs/user/model-roles`** — mesmo shape; valida ids contra o catálogo (id inválido → 400 com lista dos válidos).

**Expected errors:** `401` sem auth; `400` id inválido — formato `{detail}` atual.

## 6. Acceptance Criteria

- [ ] **Dado** papel `tasks` configurado **quando** o chat gera título **então** a completion usa o modelo do papel, não o do chat.
- [ ] **Dado** primeiro modelo da cadeia offline **quando** gera follow-ups **então** o próximo modelo é usado; todos falhando → modelo do chat.
- [ ] **Dado** papel vazio **quando** qualquer task roda **então** comportamento idêntico ao atual.
- [ ] **Dado** modelo Ollama com `vision` no `/api/show` **quando** `GET /models` **então** `capabilities` contém `"vision"`.
- [ ] **Dado** modelo OpenAI casando com regra do catálogo **quando** `GET /models` **então** capabilities da regra (override admin > descoberta > catálogo).
- [ ] **Dado** imagem anexada com modelo sem `vision` **quando** o chip renderiza **então** aviso inline aparece; envio continua permitido.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Modelo do papel deletado do provider | role → id inexistente | próximo da cadeia; se nenhum, modelo do chat |
| Catálogo json corrompido | deploy com json inválido | `capabilities=[]` p/ todos + log warn; app não quebra |
| `/api/show` falha | Ollama offline | catálogo/override apenas; sem exception |
| Admin override inválido | id sem match | 400 na escrita; leitura ignora overrides órfãos |

## 7. Task Plan

- [ ] **T1 — Discovery:** ler arquivos da seção 3; mapear onde `request.Model` alimenta tasks/compaction.
- [ ] **T2 — Resolvers:** `ModelRoleResolver` (kv do usuário → cadeia → modelo efetivo) e `ModelCapabilityResolver` (catálogo embedded + `/api/show` cache + override admin).
- [ ] **T3 — Contratos+endpoints:** `Capabilities` em `ModelInfo`, GET/PUT model-roles, `TaskEndpoints` e compaction usando papéis.
- [ ] **T4 — Client:** seção em SettingsModal, aviso de visão em ChatView, métodos ApiService, i18n ×8.
- [ ] **T5 — Testes:** resolução de papel/fallback, precedência override>descoberta>catálogo, tasks com papel configurado, capabilities no GET models.
- [ ] **T6 — Validation:** build Release + test + drift guards; DoD.
- [ ] **T7 — Done + PR.**

**7.1 Validation:** unit + integration tests NUnit; ≥80% no tocado; drift guards do client.

## 8. Organization Guardrails

- Branch `feature/Devin-20261013-model-roles`; nada direto em `main`.
- Catálogo é dado versionado no repo — sem download em runtime.
- Sem PII/token em logs (model ids ok, api keys nunca).
- Não expandir para pricing/effort — fora de escopo.

## 9. Definition of Done

- [ ] RF-001..RF-005 implementados e testados.
- [ ] Critérios da seção 6 cobertos; edge cases tratados.
- [ ] build/test/drift guards verdes.
- [ ] SPEC #349 referenciada: aviso de visão entregue aqui, erro de provider continua na #349.

**Next action after DoD:** `Status = Done` + PR referenciando o ticket.

## Open Questions / Pending Ambiguity

- Papel `advisor` fica visível na UI já ou oculto até SPEC run-advisor? Palpite: ocultar até lá.
- Fonte de capacidades para providers OpenAI-compatíveis sem descoberta: só catálogo+override (palpite: sim).

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/356 |
| Issue | https://github.com/afonsoft/open-webui/issues/350 |
