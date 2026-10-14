# SPEC-20261014-cli-opencode-analysis

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `cli-opencode-analysis` |
| Type | `Analysis` (artefato de pesquisa — gera SPECs filhas, não código) |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `docs/Devin-20261014-comparative-specs` |
| Ticket | — |
| Status | `Approved` |

## 1. User Story

**As a** mantenedor do Open WebUI .NET
**I want** uma análise sistemática de agentes CLI e do fork `alltomatos/opencode` confrontada com a arquitetura atual
**So that** as melhorias aplicáveis virem SPECs priorizadas com evidência de código, em vez de uma lista genérica de features.

**Problem context:**
A SPEC-20261013 trouxe a primeira rodada de comparação com `oh-my-pi` (can1357) e gerou 5 SPECs implementáveis (#350–#354). O usuário pediu uma segunda rodada cobrindo (a) o ecossistema de CLIs agentic em geral e (b) o fork modificado `alltomatos/opencode` — que já integrou memória centralizada, orquestração de agentes externos, provider nativo OmniRoute e canais de mensageria. Este documento é o resultado; as SPECs filhas referenciadas no §6 carregam o detalhe de implementação.

## 2. Scope

**In scope:**
- Fontes analisadas com evidência de arquivo:
  - `oh-my-pi` (`/home/ubuntu/repos/oh-my-pi`): docs de compaction, memory, task-agent-discovery, advisor-watchdog, config-usage, tools/{debug,context-notes,new-context}.
  - `alltomatos/opencode` (`/home/ubuntu/repos/opencode-fork`, branch `dev`, ~v1.21.87): `DEVELOPER-ROADMAP.md` (Epics E01–E18), `packages/opencode/src/memory/index.ts`, `packages/opencode/src/agentui/index.ts`, `docs/agents/batuta-external-agents.md`, `docs/agents/omniroute-native-provider.md`, `AGENTS.md` (SessionV2).
  - CLIs de referência (conhecimento público + SKILL.md do ecossistema): Claude Code, OpenCode upstream, Gemini CLI, Cursor, Codex CLI.
- Matriz de capacidades × o estado atual deste fork (código real em `src/`).
- Priorização com dependências, risco e critérios de aceite por item.

**Out of scope:**
- Implementação (cada item priorizado ganha SPEC própria).
- Análise linha-a-linha dos 35 packages do opencode — só os diferenciais documentados.
- Benchmarks de performance/latência.

## 3. Technical Context

**Estado atual do fork (base da comparação):**
- Runs desacopladas (`ChatRunExecutor` + SSE), steer/queue, checkpoints git, `delegate_task` com worktree isolado, jobs/rotinas (`RoutineBuiltinTools`), LSP×6, personas via skills, plan/build/chat modes, presets de aprovação + `ToolCallRiskClassifier`.
- Já entregue desde a rodada 1: model roles (#350/PR #356), approval tiers + overrides persistentes (#351/PR #357).
- Compaction: `ContextCompactionService` — resumo via papel `memory`, boundary-safe p/ tool results, resumo sintético persistido. **Sem** poda mecânica, **sem** recovery de histórico, **sem** resumo estruturado.
- Memória: `memory_save`/`memory_search` manuais; sem consolidação automática cross-chat.
- Config: `ConfigService` kv global (admin) + `SettingsJson` por usuário — sem camada intermediária de override para MCP/GitHub/skills/providers.
- Sub-agentes: `delegate_task` procedural — sem definições declarativas, sem agentes externos.

## 4. Requirements

### RF-001: Matriz de capacidades (evidência → gap → destino)

Cada linha cita a fonte externa e o estado interno; "Destino" indica a SPEC filha ou "já coberto".

| Capacidade | Evidência externa | Estado no fork | Destino |
|---|---|---|---|
| Poda mecânica de contexto (elidir tool results superseded/useless antes de resumir) | oh-my-pi `docs/compaction.md` | Só resumo via LLM | SPEC-context-maintenance-v2 |
| Recovery de histórico pós-compaction (`history://current/full` via read/grep) | oh-my-pi `docs/compaction.md` | Texto antigo fica inacessível ao modelo | SPEC-context-maintenance-v2 |
| Resumo estruturado (arquivos tocados, decisões, pendências) | oh-my-pi `compaction.md` (`CompactionEntry`); opencode E11 | Resumo em texto puro | SPEC-context-maintenance-v2 |
| Notebook persistente por ramo (`context_notes`, ≤16KB) | oh-my-pi `tools/context-notes.md` | — | SPEC-20261013-agent-context-management (#353) |
| Rollover de contexto a pedido (`new_context`) | oh-my-pi `tools/new-context.md` | Só automático | SPEC-20261013-agent-context-management (#353) |
| Consolidação background de sessões → memória injetada | opencode `src/memory/index.ts` (opt-out default, `syncIntervalHours` 6h, `maxSessionAgeDays` 30, cadeia `DEFAULT_MODEL_CANDIDATES` de modelos baratos); oh-my-pi `memory.md` | `memory_save`/`memory_search` manuais | SPEC-memory-pipeline |
| Config em camadas com provenance (env>runtime>overlay>project>global>default) | oh-my-pi `config-usage.md` | Global admin + `SettingsJson` por usuário, sem override intermediário | SPEC-config-layers |
| MCP por usuário (servidores próprios + toggle sobre catálogo global) | opencode E03 (catálogo MCP + OAuth persistido); pedido explícito do usuário | MCP só admin-global (`UserId=null`) | SPEC-config-layers |
| BYOK / override de API key por usuário | opencode (auth por provider por usuário); pedido do usuário | Providers só admin | SPEC-config-layers |
| Agentes declarativos `.md` + frontmatter (tools, model, spawns, output schema, autoloadSkills) | oh-my-pi `task-agent-discovery.md`; opencode AgentUI (`src/agentui/index.ts`, 928 linhas) | `delegate_task` procedural | SPEC-agent-definitions |
| Orquestração de agentes **externos** (CLIs detectados no PATH, skill `batuta-cli`, DAG + terminal ao vivo) | opencode `docs/agents/batuta-external-agents.md` + Epic E04 | — | SPEC-agent-definitions (fase 2) |
| Provider dinâmico OmniRoute com capabilities pass-through (`vision/tool_calling/reasoning`, `input_modalities`) | opencode `docs/agents/omniroute-native-provider.md` | `ProviderService` com catálogo estático + heurística de capabilities por regex | SPEC-cli-opencode-analysis → entra como requisito no SPEC-context-maintenance-v2/RF de catálogo; e follow-up de model-roles |
| Combos como pseudo-modelos (`owned_by === "combo"`) | opencode omniroute-native-provider.md | — | Follow-up de model-roles (catalog sync OmniRoute) |
| Fallback de catálogo "unavailable context" (manter último válido) | opencode omniroute-native-provider.md | Lista falha → lista vazia | SPEC-cli-opencode-analysis RF-003 (recomendação direta) |
| Inbox durável de prompts (admit antes de executar; steer em boundary; exact-retry por message-id) | opencode `AGENTS.md` (SessionV2) | Steer/queue existem; admission não é durável como entidade | Backlog (SPEC futura) |
| Dashboard /stats de tokens e custos | opencode Epic E15 | Usage por run, sem agregação central | Backlog |
| Canais de mensageria (WhatsApp/Telegram → sessões) | opencode E10 + commits `whatsapp`/`channels` | — | Fora de escopo do produto |
| Permissões granulares por área (filesystem toggle, bash toggle) | opencode commits v1.21.79–80 | approval-tiers cobre por tool | Já coberto (#351) |
| Advisor/watchdog | oh-my-pi `advisor-watchdog.md` | — | SPEC-20261013-run-advisor (#352) |
| Session tree / branching | oh-my-pi session entries com `parentId` | Linear + `Versions` | SPEC-20261013-chat-branching (#354) |
| Hooks de sessão / magic keywords (`ultrathink`) | oh-my-pi; Claude Code skills/hooks | Slash commands existem | Backlog |
| Debug via DAP | oh-my-pi `tools/debug.md` | — | Backlog |
| Marketplace de skills/plugins | oh-my-pi; opencode E03 | repo-skills + slash commands | Backlog |

### RF-002: Análise específica — contexto sob roteamento omniroute

Cenário do usuário: cada request pode cair em um provider/modelo diferente; o contexto precisa ser portável e reconstruído a cada request.

1. **O que já está certo:** cada run reconstrói contexto do SQLite por request e resolve provider por modelo — a arquitetura é compatível por construção. O resumo em texto puro é o formato correto (portável entre providers).
2. **Gaps:**
   - Sem poda mecânica → paga-se resumo de LLM mesmo para lixo mecânico (tool results obsoletos).
   - Sem resumo estruturado → o "novo" modelo chega frio: não sabe quais arquivos foram tocados nem quais decisões foram tomadas.
   - Sem recovery (`history://`) → após compactar, o modelo não pode revisitar o passado.
   - Capabilities por heurística (regex) enquanto o OmniRoute já emite `capabilities.vision/tool_calling/reasoning` e `input_modalities`/`output_modalities` — pass-through é mais correto e mantém `model-roles`/`media-attachments` honestos.
   - Falha de discovery do gateway → catálogo zera; o padrão "manter último válido" do opencode é mais robusto.

### RF-003: Quick wins identificados (sem SPEC nova necessária)

- **Capabilities pass-through do OmniRoute**: quando a conexão é OmniRoute (`/v1/models` com `capabilities`/`input_modalities`), alimentar `ModelInfo.Capabilities` direto em vez de regex — pequena alteração em `ProviderService`+`ModelCapabilityResolver`.
- **Catálogo sticky**: último catálogo válido em cache kv (`connection:{id}:last-catalog`) quando discovery falha.
- **Prompts em arquivo**: extrair prompts inline (ex.: `LlmContextSummarizer`) para `.md` embedded — editável sem rebuild, padrão oh-my-pi.
- **`wait`/`park` entre sub-agentes**: `delegate_task` hoje não permite esperar resultado de sub-agente assíncrono — endpoint `GET /runs/{id}` já existe; tool `delegate_wait` é uma linha de custo pequena.

### RF-004: Fora de escopo permanente (registrado para não repavimentar)

- Canais WhatsApp/Telegram — o produto é web UI; canais são responsabilidade de outros componentes do ecossistema (N8N/WAHA).
- Runtime de desktop/TUI/PTY — não aplicável a Blazor WASM.
- Vendorização de Python — `code_interpreter`/`PythonToolExecutor` já rodam no host/container.

## 5. Acceptance Criteria

- [ ] Matriz §RF-001 completa com evidência `arquivo:linha` ou doc de cada fonte.
- [ ] Cada gap "Destino" aponta para SPEC existente ou nova SPECs filhas criadas neste PR.
- [ ] Nenhum item recomendado duplica funcionalidade já implementada (verificado contra `src/`).
- [ ] Quick wins §RF-003 são acionáveis sem mudança de arquitetura.
- [ ] SPECs filhas referenciam esta análise no campo "Problem context".

## 6. SPECs filhas geradas

| SPEC | Origem principal | Prioridade |
|---|---|---|
| `SPEC-20261014-context-maintenance-v2` | oh-my-pi compaction + RF-002 | Alta (desbloqueia omniroute) |
| `SPEC-20261014-memory-pipeline` | opencode E11 + oh-my-pi memory | Alta |
| `SPEC-20261014-config-layers` | pedido do usuário + oh-my-pi config-usage + opencode E03 | Alta |
| `SPEC-20261014-agent-definitions` | oh-my-pi task-agent-discovery + opencode AgentUI/Batuta | Média |

## 7. Open Questions

1. `agent-definitions` fase 2 (agentes externos via PATH/skill `batuta-cli`) depende de decisão de produto: nosso fork roda server-side; detectar CLIs no **servidor** faz sentido só se o servidor for o ambiente de execução do usuário — confirmar escopo (workspace repos vs host).
2. OmniRoute pass-through pressupõe que a conexão expõe o formato documentado; confirmar contra a instância real (`:20128`) antes de implementar RF-003.
3. Config-layers BYOK tem implicação de billing/quota — precisa de decisão se usuário pode trazer key própria em deploy multi-tenant.
