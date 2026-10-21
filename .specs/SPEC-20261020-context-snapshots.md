# SPEC-20261020-context-snapshots

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | Compactação automática do contexto da run ao se aproximar de 256k **tokens** estimados, com snapshots persistidos por chat (e repo do binding) e 3 builtin tools: `context_save`, `context_recovery`, `context_compact` |
| Type | Feature (Backend: builtin tools + EF Core + config KV) |
| Stack | .NET 10, C# 14, EF Core (**SQLite único** — não há provider Postgres no repo), `ContextCompactionService`, `ContextPruner`, `IContextSummarizer`, `IBuiltinChatTool` |
| Repository | afonsoft/open-webui (SPEC adaptada — não copiada — da SPEC-v3 de afonsoft/KnowledgeRAG) |
| Branch | `feature/OpenWebUI-20261020-context-snapshots` |
| Ticket | [#483](https://github.com/afonsoft/open-webui/issues/483) |
| Status | In implementation |
| Supersedes | Adapta SPEC-v3 (KnowledgeRAG): tools MCP viram builtin tools; parâmetro `repo` vira binding do chat (`WorkspaceRepoService`) |

## 1. User Story

**As a** usuário de um chat de agente com histórico longo
**I want** que a run compacte automaticamente o contexto quando se aproximar de 256k tokens, e poder salvar/recuperar/compactar snapshots do contexto de trabalho pelo próprio agente
**So that** sessões longas não estourem a janela do modelo, sem perder decisões, e o agente possa retomar trabalho com estado compacto.

**Problem context:**

- Hoje o limiar é `compaction:threshold` = **256_000 caracteres** (≈ 64k tokens) em `ContextCompactionService.DefaultThresholdChars` (l.118). A meta de 256k tokens não é atingida: a compactação dispara cedo demais em relação ao que o modelo suporta.
- Não existe snapshot durável de contexto: o resumo vive só na KV `chat:{id}:compaction` (um único estado por chat, sem versões).
- O agente não tem tool para salvar/recuperar/compactar o estado sob demanda.
- Elision (poda mecânica de tool results) e sumarização já existem (`ContextPruner`, `LlmContextSummarizer` via `IContextSummarizer`) — **não reimplementar**.

## 2. Scope

**In scope:**

- Limiar de compactação em **tokens estimados** (`ceil(chars/4)`) com KV configurável `compaction:threshold_tokens`, default **224_000** (decisão OQ-1), em regra OR com a KV de chars existente.
- Entidade `ContextSnapshot` (adicionada em `src/OpenWebUI.Domain/Entities.cs` — convenção do repo: todas as entidades moram nesse arquivo) + `DbSet` e configuração em `AppDbContext` + migration `AddContextSnapshots`.
- `ContextSnapshotService` — único ponto com EF para snapshots (save transacional, latest, get, list, retenção).
- 3 builtin tools `context_save`, `context_recovery`, `context_compact` num único arquivo `ContextSnapshotBuiltinTools.cs` (padrão `AgentMemoryBuiltinTools.cs`/`JobBuiltinTools.cs`).
- `BuiltinToolContext` ganha parâmetro opcional `Model` (decisão aprovada): `ChatRunExecutor` preenche com `run.Model`; demais call sites ficam `null`.
- `SecretScrubber` ganha padrão `aft_` (detecção via `Scrub(x) != x`) — reuso dos regexes já existentes (`sk-`, `gh[pousr]_`, `github_pat_`, AWS, Bearer, PEM).
- Gate admin `contexttools.enabled`: estender `ContextBoundaryService.IsContextTool` com os 3 novos nomes.
- Config KV `compaction:*` novas chaves (padrão já existente de `ThresholdsAsync`).

**Out of scope:**

- Cache (`HybridCache`/`IDistributedCache`) para snapshots — v1 sem (leitura é uma linha por chat; OQ-4).
- Endpoints REST `/api/context/*` e tools MCP — repo não expõe MCP server; REST não pedido.
- Parâmetro `repo` nas tools — substituído pelo binding do chat.
- Salvar em vault/`write_note`; alterar `ConversationMessage`/`ChatMessage` ou o histórico persistido.
- Recall de elisão (`history://` já cobre) e eval de faithfulness (SPEC-20261014).
- `ChatWindow.CharCap` do cliente — **confirmado: sem mudança nesta versão** (OQ-2).
- `.github/workflows/`, `Dockerfile`, `docker-compose*`.
- Postgres — provider não existe neste repo (OQ-7 resolvida por evidência: só `Microsoft.EntityFrameworkCore.Sqlite` nos csprojs).

## 3. Technical Context

**Where the change happens:** backend .NET — camada Infrastructure (serviço EF + tools builtin) e Api (DI + call site do contexto de tool). Nenhuma mudança de UI.

**Files to read before implementing:**

- `src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs` — `MaybeCompactAsync` (l.210), `ApplyAsync` (l.145), `DefaultThresholdChars` (l.118), `Thresholds` record (l.134), `ThresholdsAsync` (l.361), `CompactionState` (l.131), `KvKey = chat:{id}:compaction` (l.136), `SplitIndex` (l.315); `IContextSummarizer` (l.27) e `LlmContextSummarizer` (l.36) vivem neste arquivo.
- `src/OpenWebUI.Infrastructure/Services/ContextPruner.cs` — `Prune` estático (l.40); marcadores de elision (l.69, l.85).
- `src/OpenWebUI.Infrastructure/Services/ContextBoundaryService.cs` — `IsContextTool` (l.44), `EnabledAsync` (l.48), padrão de persistência escopada ao chat via KV.
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/ContextNotesBuiltinTool.cs` e `NewContextBuiltinTool.cs` — padrão `IBuiltinChatTool` a seguir (check de `EnabledAsync` + `context.ChatId` nulo → erro "requer chat persistido").
- `src/OpenWebUI.Infrastructure/ChatTools/IBuiltinChatTool.cs` — `ApprovalTier` (l.29), `BuiltinToolResult` (l.16), `SafeInParallel`.
- `src/OpenWebUI.Infrastructure/ChatTools/BuiltinToolContext.cs` — record de contexto (UserId/ChatId/RunId/WorkspacePath/UploadDir) — **sem Model hoje**.
- `src/OpenWebUI.Infrastructure/ChatTools/BuiltinToolRegistry.cs` — `SpecPrefix = builtin_` (l.23), `NormalizeArgs` (l.174), `ToSyntheticTool` (l.108).
- `src/OpenWebUI.Infrastructure/Services/ToolExecutor.cs` — gate `contexttools.enabled` em `LoadEnabledAsync` (l.51–59) que chama `ContextBoundaryService.IsContextTool` (l.58).
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` — `MaybeCompactAsync` (l.247), fase `compacting` (l.253), overflow com `tailScale` (l.456–466), `new_context` force-compact (l.951), construção de `BuiltinToolContext` (l.1027, `run.Model` disponível).
- `src/OpenWebUI.Infrastructure/Services/WorkspaceRepoService.cs` — `GetChatBindingAsync(chatId)` (l.41, kv `chat:{id}:workspace.repo`, SPEC-20261010-chat-repo-binding); binding efetivo `ResolveBindingAsync` (l.56 — resolução estrita por chat, sem herança do global).
- `src/OpenWebUI.Application/Contracts/GitHubContracts.cs` — `WorkspaceRepoBinding(Repo, Branch, Dir, …)` (l.54); `Repo` é o slug `owner/repo`.
- `src/OpenWebUI.Infrastructure/ChatTools/SecretScrubber.cs` — regexes de segredo existentes e API `Scrub`.
- `src/OpenWebUI.Infrastructure/Services/ModelRoleResolver.cs` — `ResolveAsync("memory", userId, fallback, ct)`.
- `src/OpenWebUI.Domain/Entities.cs` — convenção: entidade nova é adicionada aqui (ex.: `AgentMemory` l.1395); `Chat.ModelsJson` (l.115) e `ChatRun.Model` (l.288) existem.
- `src/OpenWebUI.Infrastructure/Data/AppDbContext.cs` — `DbSet`s (l.10–151) + configuração `modelBuilder.Entity<T>` com `HasIndex` (l.156+); `AppDbContextModelSnapshot.cs` regenera com a migration.
- `src/OpenWebUI.Infrastructure/Migrations/` — última: `20261018153416_AdminPolicies.cs`.
- `src/OpenWebUI.Api/Program.cs` — `ContextBoundaryService` AddScoped (l.83), `IContextSummarizer→LlmContextSummarizer` (l.88), builtins `AddScoped<IBuiltinChatTool, T>` (l.122+; tools de contexto em l.138–141).
- `tests/OpenWebUI.Api.Tests/ContextCompactionTests.cs`, `ContextBoundaryTests.cs`, `ContextPrunerTests.cs` — convenção de testes: NUnit, arquivo flat (sem subpasta), SQLite em `Path.GetTempPath()` + `EnsureCreated()`, nomes/comentários em pt-BR.

**Files to create:**

```text
src/OpenWebUI.Infrastructure/Services/ContextSnapshotService.cs
src/OpenWebUI.Infrastructure/ChatTools/Tools/ContextSnapshotBuiltinTools.cs   # 3 classes IBuiltinChatTool num arquivo
src/OpenWebUI.Infrastructure/Migrations/{ts}_AddContextSnapshots.cs + .Designer.cs
tests/OpenWebUI.Api.Tests/ContextSnapshotServiceTests.cs
tests/OpenWebUI.Api.Tests/ContextTokenThresholdTests.cs
tests/OpenWebUI.Api.Tests/ContextSnapshotBuiltinToolsTests.cs
```

**Files to modify:**

```text
src/OpenWebUI.Domain/Entities.cs                              # + class ContextSnapshot
src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs  # Thresholds + regra OR tokens/chars + helper de estimativa
src/OpenWebUI.Infrastructure/Data/AppDbContext.cs             # DbSet + entity config (+ ModelSnapshot via dotnet ef)
src/OpenWebUI.Infrastructure/Services/ContextBoundaryService.cs    # IsContextTool += context_save/context_recovery/context_compact
src/OpenWebUI.Infrastructure/ChatTools/BuiltinToolContext.cs       # + parâmetro opcional Model
src/OpenWebUI.Infrastructure/ChatTools/SecretScrubber.cs           # + regex aft_
src/OpenWebUI.Api/Runs/ChatRunExecutor.cs                          # passar run.Model no BuiltinToolContext (l.1027)
src/OpenWebUI.Api/Program.cs                                     # AddScoped ContextSnapshotService + 3 IBuiltinChatTool
CLAUDE.md / README.md / docs/                                    # catálogo de tools e config compaction:*
```

> Nota: o draft citava "modificar ToolExecutor" — **não é preciso**: o gate já delega a `IsContextTool` (`ToolExecutor.cs:58`); a mudança correta é em `ContextBoundaryService.cs`.

## 4. Requirements

### RF-001 — Limiar em tokens (núcleo do pedido)

- **Description:** a compactação automática dispara pela estimativa de tokens da janela, não só por caracteres.
- **Rules:**
  - Estimativa: `tokens = ceil(chars / 4)` sobre o mesmo somatório já usado (`Content.Length + ToolCallsJson.Length` por mensagem — `MaybeCompactAsync` l.225). Não existe helper hoje → criar `internal static` de estimativa compartilhado entre `ContextCompactionService` e `ContextSnapshotService` (sugestão: arquivo pequeno `Services/ContextTokens.cs` ou método `internal static` no compaction — decisão do implementador dentro de `Services`).
  - Nova KV `compaction:threshold_tokens`, default **224_000** (≈ 87% de 256k — margem para o turno de resposta). Faixa válida: 32_000–256_000 (clamp na leitura).
  - `Thresholds` record ganha `ThresholdTokens`; `ThresholdsAsync` lê a nova KV.
  - Regra de disparo: compacta se `tokensEstimados >= compaction:threshold_tokens` **OU** `chars >= compaction:threshold` (compatibilidade — a KV de chars existente **não** é removida).
  - **Default de `compaction:threshold` sobe para 1_000_000 chars** (~250k tokens, acima do limiar de tokens): com o default antigo de 256_000 chars a regra OR dispararia em ~64k tokens e a feature viraria no-op — o 2º cenário de aceite exige que 400k chars não compactem. A KV permanece como override para o admin compactar **mais cedo**; deployments que a definiram explicitamente não mudam de comportamento.
  - `force` e `tailScale` inalterados (`tailScale` escala só tail, não o limiar).
  - Sem alteração de comportamento quando abaixo de ambos os limiares.
- **Input → Output:** `(history, model) → bool didCompact` — contrato de `MaybeCompactAsync` inalterado.

### RF-002 — Compactação automática (reuso)

- **Description:** fluxo `MaybeCompactAsync → ApplyAsync` permanece; só muda o critério de disparo (RF-001).
- **Rules:**
  - Elision/poda: `ContextPruner` (sem mudança).
  - Sumarização: `IContextSummarizer` (`LlmContextSummarizer`) com fallback já existente — falha no summarizer devolve `false` sem derrubar a run (manter; cobrir com teste).
  - `SplitIndex` (l.315), `TailMessages`/`TailChars`, `TurnPrefix` (RF-004 existente) e fase `compacting` (`ChatRunExecutor` l.253) inalterados.
  - Invariantes: não altera `ChatMessage` persistida; mantém pares tool_call/tool_result; marcador `Role="compaction"` pendurado no leaf do ramo.

### RF-003 — Tabela `ContextSnapshots`

- **Description:** versões do contexto de trabalho por chat (e repo do binding, quando houver).
- **Schema:**

| Coluna | Tipo (SQLite) | Regra |
| --- | --- | --- |
| `Id` | TEXT (GUID) | PK |
| `ChatId` | TEXT | NOT NULL, FK lógica para `Chats.Id` |
| `RepoKey` | TEXT(201) NULL | slug `owner/repo` **lowercase** de `GetChatBindingAsync(chatId)?.Repo`; NULL sem binding |
| `Version` | INTEGER | monotônico por `ChatId`, começa em 1 (`MAX(Version)+1` dentro do mesmo `SaveChanges`) |
| `Label` | TEXT(200) NULL | rótulo opcional |
| `PayloadJson` | TEXT | itens (RF-004), máx. `compaction:max_payload_bytes` = 512 KB serializado |
| `TokenEstimate` | INTEGER | `ceil(chars/4)` — chars = soma de `content` (+ `title`) dos itens |
| `Compacted` | INTEGER (bool) | true quando gravado por/após compactação |
| `Strategy` | TEXT(32) NULL | `none`, `elision`, `elision+summary` |
| `CreatedAt` | INTEGER (epoch UTC s) | NOT NULL — convenção do repo: timestamps são `long` epoch (não TEXT ISO) |
| `CreatedByUserId` | TEXT NULL | `BuiltinToolContext.UserId` da run |

- **Índices:** único `(ChatId, Version)`; `(ChatId, CreatedAt)` — nomes gerados pelo EF (`IX_ContextSnapshots_*`).
- **Retenção:** `compaction:max_snapshots_per_chat` (default 20): ao exceder após insert, remove os mais antigos por `Version` no mesmo `SaveChanges`.
- **Migration:** `dotnet ef migrations add AddContextSnapshots --project src/OpenWebUI.Infrastructure --startup-project src/OpenWebUI.Api`; revisar o `.cs` gerado (só cria tabela+índices, sem tocar tabelas existentes); `DatabaseMigrator` aplica no startup e faz baseline de bases legadas.
- **Configuração EF:** `DbSet<ContextSnapshot> ContextSnapshots` + `modelBuilder.Entity<ContextSnapshot>` seguindo o padrão das demais entidades.

### RF-004 — Formato e validação do payload

- **Rules:**
  - Lista de itens `{ kind, title?, content, score? }` com `kind ∈ {decision, note, passage, tool_output, summary}`; máx. `compaction:max_items_per_snapshot` = 200 itens; `content` ≤ 16_000 chars por item.
  - `decision`/`note`/`summary` nunca são elididos; `tool_output` é o primeiro a sair (RF-006).
  - Campos desconhecidos no item → `invalid_payload` (deserialização estrita).
  - Rejeitar caracteres de controle exceto `\n` e `\t` → `invalid_payload`.
  - Segredos → `secret_detected` e **nada é gravado**: detecção via `SecretScrubber` — `Scrub(content) != content`. Adicionar a ele o padrão `aft_[0-9a-fA-F]{32,}` (não existe neste repo hoje — cobre o cenário da SPEC-v3 e futuras chaves `aft_*`). Conteúdo com segredo **nunca** é logado.
  - Serialização única (mesmo `JsonSerializerOptions`) para `PayloadJson` e leitura.

### RF-005 — Builtin tools (substituem as tools MCP da SPEC-v3)

Três classes `IBuiltinChatTool` no arquivo `ContextSnapshotBuiltinTools.cs`, registradas em `Program.cs` junto às tools de contexto (l.138–141). Anunciadas como `builtin_context_save` etc. (prefixo `builtin_` do registry — l.23). Todas **exigem `context.ChatId` persistido** (playground → erro no padrão `context_notes`) e **checam `boundaries.EnabledAsync` no início** (gate `contexttools.enabled`).

- **`context_save`** — entrada: `label?`, `items` (obrigatório, RF-004), `compact?` (`none` default | `elision` | `elision+summary`). Grava nova versão do chat com `RepoKey` do binding atual. Saída: `{ id, version, tokenEstimate, compacted, strategy, pruned }`. **Tier: Write.**
- **`context_recovery`** — entrada: `version?` (default: última), `tokenBudget?` (default 16_000; clamp 256–`compaction:threshold_tokens`). Aplica poda/elision sobre **cópia** do payload — o snapshot persistido nunca é alterado. Saída: `{ version, items, stats: { tokensBefore, tokensAfter, droppedCount, elidedCount, summaryUsed, summaryFallback }, withinBudget }`. **Tier: Read.**
- **`context_compact`** — entrada: `items?` XOR `fromVersion?` (**exatamente um**, senão `invalid_payload`), `strategy` (default `elision+summary`), `tokenBudget?` (mesmo clamp), `save?` (default false). Saída: `{ compacted[], stats, savedVersion? }`. **Tier: Write** — decisão aprovada: `IBuiltinChatTool.Tier` é estático, não há tier dinâmico por chamada; declarar Write é fail-closed (desvio consciente do SPEC-v3, que pedia Read quando `save=false`).
- **Escopo:** chat atual obrigatório; **sem parâmetro `repo`** — `RepoKey` vem de `WorkspaceRepoService.GetChatBindingAsync(chatId)` (lowercase; NULL sem binding — OQ-3).
- **Gate:** incluir os 3 nomes em `ContextBoundaryService.IsContextTool` (l.44) — cobre anúncio (`LoadEnabledAsync`) e execução (`EnabledAsync` por tool).
- **Erros:** `invalid_payload`, `secret_detected`, `context_not_found` (versão inexistente ou chat sem snapshots), `payload_too_large` — devolvidos como texto `Erro: <code> — <detalhe>` (resultado de erro para o modelo, sem exceção).
- **Elision sobre itens:** projetar itens → `ChatCompletionMessage` (`tool_output` → role `tool`, demais → role `user` com `kind:` no conteúdo) → `ContextPruner.Prune(...)` → mapear de volta por índice com `elided: true`. Reuso real da poda existente, sem duplicar regras.

### RF-006 — Limites de budget

- `tokenBudget` com clamp 256–`compaction:threshold_tokens` (o valor clampado entra em `stats`).
- Após elision + summarization ainda exceder → remover `passage` de menor `score`, depois `tool_output` mais antigo. `decision`/`note`/`summary` nunca removidos; se só restarem esses e exceder, `withinBudget=false` (sem erro).

### RF-007 — Configuração (KV, padrão do repo)

| Chave | Default | Validação (clamp na leitura) |
| --- | --- | --- |
| `compaction:threshold_tokens` | 224_000 | 32_000–256_000 |
| `compaction:threshold` (existente, chars) | 1_000_000 (era 256_000 — ver RF-001) | mantida |
| `compaction:max_snapshots_per_chat` | 20 | 1–100 |
| `compaction:max_items_per_snapshot` | 200 | 1–1000 |
| `compaction:max_payload_bytes` | 524_288 | 64_000–2_000_000 |
| `compaction:summary_timeout_seconds` | 30 | 1–120 |

Lidas via `ConfigService.GetAsync` (padrão de `ThresholdsAsync`, l.361). Sem appsettings novo (OQ-5).

### RF-008 — Modelo para sumarização via tool

- `BuiltinToolContext` ganha parâmetro posicional opcional `string? Model = null` — call sites existentes (5 args) continuam compilando.
- `ChatRunExecutor` (l.1027) passa `run.Model` (`ChatRun.Model`, Entities.cs l.288).
- `context_save`/`context_compact` com estratégia `elision+summary` resolvem o modelo via `ModelRoleResolver.ResolveAsync("memory", userId, context.Model, ct)`; `context.Model` null e sem papel configurado → degrada para `elision` com `summaryFallback=true` (**nunca** erro).
- Sumarização sempre via `IContextSummarizer` (injetado na tool) — nunca chamada direta a provider.

## 5. API Contract

Sem endpoint REST nesta versão. Contrato das tools (JSON de entrada/saída, RF-005):

```text
context_save:     {"label":"sprint-42","items":[{"kind":"decision","title":"Limiar em tokens","content":"…"}],"compact":"none"}
                → {"id":"…","version":3,"tokenEstimate":1820,"compacted":false,"strategy":"none","pruned":0}
context_recovery: {"version":null,"tokenBudget":16000}
                → {"version":3,"items":[…],"stats":{"tokensBefore":…,"tokensAfter":…,"droppedCount":…,"elidedCount":…,"summaryUsed":false,"summaryFallback":false},"withinBudget":true}
context_compact: {"fromVersion":3,"strategy":"elision+summary","tokenBudget":16000,"save":true}
                → {"compacted":[…],"stats":{…},"savedVersion":4}
```

Erros como texto `Erro: <code> — <detalhe>` para o modelo (ex.: `Erro: secret_detected — item 2 contém padrão de segredo`).

## 6. Acceptance Criteria

```text
Cenário: compactação dispara perto de 256k tokens
  Dado um chat com histórico de 230 000 tokens estimados (≈ 920k chars)
  Quando a run prepara o contexto
  Então MaybeCompactAsync compacta o head
  E a fase "compacting" é publicada

Cenário: histórico abaixo do limiar não compacta
  Dado um chat com 100 000 tokens estimados e chars abaixo de compaction:threshold
  Quando a run prepara o contexto
  Então nenhuma compactação é feita

Cenário: regra OR mantém compat com chars
  Dado compaction:threshold = 256_000 chars e histórico de 70k tokens (280k chars)
  Quando a run prepara o contexto
  Então a compactação dispara pelo limiar de chars (compat)

Cenário: save e recovery no mesmo chat
  Dado um chat sem snapshots
  Quando o agente chama context_save com 2 decisões
  E chama context_recovery sem version
  Então recebe version 1 com as 2 decisões

Cenário: retenção por chat
  Dado compaction:max_snapshots_per_chat = 2 e 2 snapshots
  Quando chamo context_save
  Então o total do chat continua 2 e o mais antigo é removido

Cenário: recovery com elision não altera o snapshot
  Dado um snapshot com tool_output de 3000 tokens fora das últimas posições
  Quando chamo context_recovery com tokenBudget 2048
  Então o tool_output vira stub de elision
  E decision e note permanecem
  E o snapshot persistido não muda

Cenário: summarization falha sem erro
  Dado um IContextSummarizer que lança exceção
  Quando chamo context_compact com strategy elision+summary acima do budget
  Então o resultado vem com summaryFallback=true e sem erro

Cenário: secret bloqueado
  Quando chamo context_save com conteúdo contendo "aft_" + 32 hex
  Então recebo secret_detected e nada é gravado

Cenário: isolamento entre chats
  Dado um snapshot no chat A
  Quando context_recovery é chamado no chat B
  Então recebo context_not_found

Cenário: gate admin
  Dado contexttools.enabled = false
  Quando as tools do chat são carregadas
  Então context_save/context_recovery/context_compact não são anunciadas
  E uma call direta devolve o erro de desabilitado
```

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| chat sem binding | `context_save` | `RepoKey` NULL, salva no chat |
| chat com binding | chat vinculado a `Afonsoft/KnowledgeRAG` | `RepoKey` = `afonsoft/knowledgerag` |
| tokenBudget abaixo do mínimo | `10` | clamp 256 |
| tokenBudget acima do máximo | `999999` | clamp `compaction:threshold_tokens` |
| `context_compact` sem origem | sem `items` nem `fromVersion` | `invalid_payload` |
| `context_compact` com ambos | `items` + `fromVersion` | `invalid_payload` |
| `fromVersion` inexistente | `99` | `context_not_found` |
| payload gigante | serializado > 512 KB | `payload_too_large` |
| campo desconhecido | `{"foo":1}` no item | `invalid_payload` |
| `strategy` none | snapshot grande | só corte por budget; `elidedCount=0` |
| sem `context.Model` + sem papel memory | `context_compact` `elision+summary` | degrada p/ elision, `summaryFallback=true`, sem erro |
| `context.ChatId` null (playground) | qualquer uma das 3 | erro "requer chat persistido" |

## 7. Task Plan

- **T1 — Discovery (residual):** itens do draft já resolvidos por evidência — (a) binding chat→repo = `WorkspaceRepoService.GetChatBindingAsync` (l.41); (b) registro de builtins = `AddScoped<IBuiltinChatTool,T>` em `Program.cs` l.122+/138–141; (c) helper de tokens **não existe** → criar (RF-001); (d) Postgres **não existe** → SQLite único. Resta confirmar na implementação: assinatura exata para projetar itens→`ChatCompletionMessage` no reuso de `ContextPruner`.
- **T2 — Limiar em tokens (RF-001):** `Thresholds` + `ThresholdsAsync` + `MaybeCompactAsync` com regra OR; helper de estimativa. Testes: abaixo/no/Acima do limiar de tokens; OR com `compaction:threshold`.
- **T3 — Entidade e DbContext:** `ContextSnapshot` em `Entities.cs`; `DbSet` + `modelBuilder.Entity` em `AppDbContext`.
- **T4 — Migration:** `dotnet ef migrations add AddContextSnapshots --project src/OpenWebUI.Infrastructure --startup-project src/OpenWebUI.Api`; revisar `.cs` gerado (só tabela+índices) e `AppDbContextModelSnapshot`.
- **T5 — `ContextSnapshotService`:** save transacional (versão + retenção), latest/get/list, validação de payload e segredos (`SecretScrubber` + regex `aft_`). Testes de integração SQLite (temp db, padrão `ContextCompactionTests`).
- **T6 — `BuiltinToolContext.Model`:** parâmetro opcional no record; `ChatRunExecutor` passa `run.Model`; demais call sites inalterados.
- **T7 — Builtin tools:** `ContextSnapshotBuiltinTools.cs` (3 classes); projeção itens→mensagens→`ContextPruner`→itens; `IContextSummarizer` + `ModelRoleResolver` via DI; registro em `Program.cs`; `IsContextTool` += 3 nomes.
- **T8 — Config:** chaves `compaction:*` com clamp (RF-007).
- **T9 — Verificação:** `dotnet build OpenWebUI.slnx --configuration Release`; `dotnet test tests/OpenWebUI.Api.Tests`; `python3 tools/check-spec-status.py` (este arquivo entra em `.specs/`). Sem mudança de UI → drift guards de `.razor`/i18n não se aplicam.
- **T10 — Validação manual:** run longa acima do limiar; conferir fase `compacting`; chamar as 3 tools em chat com e sem binding; conferir `contexttools.enabled=false`.
- **T11 — Docs e PR:** atualizar `CLAUDE.md`/`README.md`/`docs/` (catálogo de tools e config); branch `feature/OpenWebUI-20261020-context-snapshots`; PR para `main` com checks verdes.

**Validação por stack:** .NET → build + NUnit + migration aplicada em SQLite (base limpa e base existente). UI: nenhuma mudança.

## 8. Organization Guardrails

- Nunca commitar em `main`; branch `feature/OpenWebUI-20261020-context-snapshots`.
- Não tocar `.github/workflows/`, `Dockerfile`, `docker-compose*`.
- Não commitar `.env`, chaves (`sk-*`, `ghp_*`, `aft_*`…) nem `webui.db`/bases SQLite.
- Não gravar contexto no vault.
- Não alterar `ChatMessage`/histórico persistido nem tabelas existentes — só **adicionar** `ContextSnapshots`.
- Camadas: EF somente em `ContextSnapshotService`; LLM só via `IContextSummarizer`/`ModelRoleResolver`; tools em `ChatTools/Tools`; segredos só via `SecretScrubber`.
- Conteúdo de snapshot e padrões de segredo **nunca** em log.
- Não inventar requisitos: o que não estiver aqui vai para Open Questions / próxima SPEC.

## 9. Definition of Done

- [x] T1–T9 concluídas (T10–T11 = review/QA, docs e PR — em andamento).
- [x] Compactação dispara por `compaction:threshold_tokens` (regra OR) com teste (`ContextTokenThresholdTests`).
- [x] Migration `AddContextSnapshots` aplica em base limpa e existente (`dotnet ef database update` verificado).
- [x] As 3 tools no catálogo de builtins (`Program.cs`) e no gate `contexttools.enabled` (`IsContextTool`).
- [x] `context_compact` declara `Tier = Write`; `context_save` Write; `context_recovery` Read.
- [x] `BuiltinToolContext.Model` preenchido pelo `ChatRunExecutor`; sumarização via papel `memory` com fallback.
- [x] Cenários da seção 6 cobertos por teste (`ContextSnapshotServiceTests`, `ContextSnapshotBuiltinToolsTests`).
- [x] Nenhum conteúdo de contexto nem chave em log (erros levam só código + metadados).
- [x] `dotnet build` Release verde; `dotnet test` 1981/1990 (0 falhas, 9 skipped); `check-spec-status.py` verde.
- [x] Docs atualizadas (README, docs/features); PR para `main` sem force-push.

## Open Questions / Pending Ambiguity

| OQ | Status | Resolução |
| --- | --- | --- |
| OQ-1 limiar 256k tokens vs chars | **Resolvida** | Tokens; `compaction:threshold_tokens` default **224_000**; regra OR com `compaction:threshold` (chars) — aprovado pelo usuário. |
| OQ-2 `ChatWindow.CharCap` vira tokens? | **Resolvida** | Não — cliente inalterado nesta versão — aprovado pelo usuário. |
| OQ-3 escopo repo entre chats | **Resolvida** | Snapshot é por chat; `RepoKey` é só informativo (binding `GetChatBindingAsync`); sem consulta cross-chat por repo no v1. |
| OQ-4 cache `context_recovery` | **Resolvida** | Sem cache no v1 (uma linha por chat). |
| OQ-5 config KV vs appsettings | **Resolvida** | KV `compaction:*` via `ConfigService` (padrão existente). |
| OQ-6 retenção contagem vs idade | **Resolvida** | Por contagem (20/chat, `compaction:max_snapshots_per_chat`). |
| OQ-7 Postgres existe? | **Resolvida** | Não — só provider SQLite nos csprojs. |
| OQ-8 ticket e branch | **Parcial** | Branch `feature/OpenWebUI-20261020-context-snapshots`; ticket criado via `create-issues` após aprovação da SPEC. |
| OQ-9 tier dinâmico `context_compact` | **Resolvida** | `Tier = Write` sempre — `IBuiltinChatTool` não tem tier por chamada; fail-closed — aprovado pelo usuário. |
| OQ-10 modelo p/ `elision+summary` na tool | **Resolvida** | `BuiltinToolContext` ganha `Model?`; `ChatRunExecutor` passa `run.Model`; fallback papel `memory`; sem modelo → degrada p/ elision + `summaryFallback` — aprovado pelo usuário. |
