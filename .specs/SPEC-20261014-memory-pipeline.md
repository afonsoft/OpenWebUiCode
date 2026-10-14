# SPEC-20261014-memory-pipeline

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `memory-pipeline` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261014-memory-pipeline` |
| Ticket | #366 |
| Status | `Approved` |
| Parent | `SPEC-20261014-cli-opencode-analysis` (RF-001) |

## 1. User Story

**As a** usuário recorrente do agente
**I want** que fatos e lições de chats antigos se consolidem automaticamente em memória injetada no system prompt
**So that** um chat novo (ou um provider novo sob omniroute) não começa "frio" — o modelo já sabe preferências, decisões recorrentes e contexto de projeto.

**Problem context:**
Hoje `memory_save`/`memory_search` existem, mas dependem do modelo decidir chamar — nada acontece em background. O fork `alltomatos/opencode` tem exatamente isto implementado (`packages/opencode/src/memory/index.ts`): memória markdown global + por-projeto, **on-by-default** (opt-out), auto-sync a cada `syncIntervalHours` (default 6h) consolidando sessões de até `maxSessionAgeDays` (default 30) atrás, usando uma cadeia de candidatos de modelos baratos (`DEFAULT_MODEL_CANDIDATES` — flash/haiku-lite). oh-my-pi (`docs/memory.md`) adiciona `lessons` duráveis e a ressalva "memória é heurística, não autoritativa".

## 2. Scope

**In scope:**
- `MemoryConsolidationService` (background, hosted service): periodicamente consolida chats antigos do usuário em `MEMORY.md` por usuário (global) + por projeto/workspace quando houver binding.
- Injeção no system prompt: `BuildFileContextAsync`-style — memória do usuário + do workspace entram como bloco `<memory>` compactado, truncado a ~4KB.
- Cadeia de modelos para consolidação: papel `memory` (SPEC-model-roles) com fallback para modelo default do admin — nunca falha a run; falha de consolidação só loga.
- `learn` builtin tool: grava lição durável (estruturada `{context, lesson, tags}`) direto na memória — o modelo pode ensinar sem esperar a consolidação.
- Toggle por usuário (`memory.enabled`, default **on** — opt-out como no opencode) + `memory.autoSync`, `memory.syncIntervalHours`, `memory.maxSessionAgeDays` em Settings.

**Out of scope:**
- Memória gerando skills automaticamente (oh-my-pi faz; separar em fase 2 se quisermos).
- Backends pluggáveis (postgres/vector) — usar o storage atual (kv + markdown file por usuário).
- Consolidação de memória de outros usuários (memória é sempre por usuário).
- Edição de memória pela UI (v1 read-only na UI; edição via `learn` ou arquivo).

## 3. Technical Context

**Where the change happens:**
Novo `MemoryConsolidationService` (IHostedService) em Infrastructure; `ChatPipeline`/`BuildFileContextAsync` injeta bloco `<memory>`; `BuiltinToolRegistry` ganha `memory_learn`; `SettingsModal` aba Memory (toggle + intervalos); `ConfigService` kv `user:{id}:memory` para o markdown consolidado.

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/MemoryBuiltinTools.cs` (`memory_save`/`memory_search` existentes)
- `src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs` (`BuildFileContextAsync` ou equivalente de system-prompt assembly)
- `src/OpenWebUI.Infrastructure/Services/ConfigService.cs` (kv pattern)
- `src/OpenWebUI.Infrastructure/Services/ModelRoleResolver.cs` (papel `memory` — merge de #356)
- Referência externa: `/home/ubuntu/repos/opencode-fork/packages/opencode/src/memory/index.ts` (defaults, candidatos, cadência)

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/MemoryConsolidationService.cs  # NOVO hosted service
src/OpenWebUI.Infrastructure/ChatTools/Tools/MemoryBuiltinTools.cs   # + memory_learn
src/OpenWebUI.Api/Program.cs                                        # AddHostedService
src/OpenWebUI.Api/Endpoints/ChatEndpoints.cs                        # injeção <memory>
src/OpenWebUI.Application/Contracts/UserContracts.cs                # MemorySettings
src/OpenWebUI.Client/Components/SettingsModal.razor                 # aba Memory
src/OpenWebUI.Client/wwwroot/i18n/*.json                            # 8 locales
tests/OpenWebUI.Api.Tests/MemoryPipelineTests.cs                    # NOVO
```

## 4. Requirements

### RF-001: Consolidação background
- **Description:** hosted service com `PeriodicTimer` (`syncIntervalHours`, default 6h); para cada usuário com chats modificados desde a última consolidação e dentro de `maxSessionAgeDays` (default 30), resume cada chat novo para bullets e merge no `MEMORY.md` do usuário (e do workspace quando o chat tem repo binding).
- **Rules:** nunca roda durante a request de chat; falha em um chat não aborta o resto; resultado truncado a 16KB por usuário (sobrescreve o mais antigo primeiro — política LRU de entrada).
- **Input → Output:** chats novos → markdown consolidado persistido em kv.

### RF-002: Injeção no system prompt
- **Description:** se `memory.enabled != false` e existe conteúdo, o system prompt ganha `<memory>\n…markdown…\n</memory>` antes do contexto de arquivos; truncado a 4KB; ausente = sem bloco (não polui).
- **Rules:** memória é heurística — o bloco carrega a ressalva textual "memória pode estar desatualizada" (equivalente ao non-authoritative do oh-my-pi).

### RF-003: `memory_learn` tool
- **Description:** builtin `Tier=Write` (gated por approval-tiers); args `{lesson, context?, tags?}`; grava entrada estruturada na memória do usuário imediatamente.
- **Rules:** dedup por similaridade simples (mesma lesson, mesmo contexto → update); resultado devolve a entrada gravada.

### RF-004: Configuração por usuário
- **Description:** Settings → aba Memory: toggle (default on), `autoSync`, `syncIntervalHours` (1–168), `maxSessionAgeDays` (1–365); admin pode fixar default global via config-layers (SPEC-config-layers) quando esta existir — antes disso, defaults de código.
- **Rules:** off desliga consolidação E injeção; o dado já consolidado não é deletado (toggle é silenciador, não purge).

## 5. Acceptance Criteria

- [ ] Teste: consolidar chat com 2 decisões gera bullets no kv do usuário.
- [ ] Teste: system prompt contém `<memory>` só quando há conteúdo e toggle on.
- [ ] Teste: `memory_learn` escreve entrada e aparece no bloco injetado na request seguinte.
- [ ] Teste: provider offline na consolidação → nenhum crash; o tick seguinte retenta.
- [ ] Consolidação usa papel `memory` (model-roles); sem papel configurado cai para o default.
- [ ] `dotnet test` verde; i18n parity (8 locales); drift guards.

## 6. Open Questions

1. Escopo workspace: memória por repo binding existe no opencode — para nós, keying por `workspace.repo` binding do chat; se o chat não tem binding, vai para a global do usuário. Confirmar granularidade.
2. Rate-limit da consolidação: um batch por tick ou espalhado? Começar com batch único por usuário por tick.
3. A memória injetada deve participar da janela pós-compaction (sim — ela é externa ao histórico, então sobrevive à compactação: é exatamente o ponto).
