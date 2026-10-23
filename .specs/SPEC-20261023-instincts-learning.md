# SPEC-20261023-instincts-learning

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `instincts-learning` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261023-instincts-learning` |
| Ticket | [#496](https://github.com/afonsoft/open-webui/issues/496) |
| Status | `Draft` |
| Parent | Análise comparativa hermes-agent/OpenHarness/ECC (out/2026) — item #5. Inspirado no `continuous-learning-v2` do ECC (instincts com confidence scoring, injeção no session-start, `/evolve` clustering) e no Memory Vault (scopes + trust boundary). |

## 1. User Story

**As a** usuário que repete workflows parecidos no agente
**I want** que o sistema extraia padrões reutilizáveis das minhas runs
("instincts"), os injete com score de confiança no início das sessões e
os promova a skills quando maduros
**So that** o agente fica melhor no *meu* jeito de trabalhar a cada
semana — hoje cada run recomeça do zero e a única memória são fatos
`auto:*` destilados, sem noção de procedimento ou relevância.

**Problem context:**
`MemorySyncService` destila fatos de chats em memórias `auto:*`
(cap 25/usuário) — cobre "o que o usuário é/gosta", não "como o agente
deve operar aqui". O ECC continuous-learning-v2 extrai **instincts**
(padrão + gatilho + confiança) das sessões, injeta top-N no
session-start rankeado por confiança + relevância de stack, deixa o
humano aprovar/rejeitar, e faz `/evolve` agrupar instincts maduros em
skills. Nossos seams já existem: `IAgentRunObserver` (pós-run),
`ISystemPromptContributor` (session-start), `SkillDiscoveryService`
(injeção de SKILL.md) e o loop de destilação LLM do `MemorySyncService`.

**Trust boundary (do ECC, inegociável):** instincts são *contexto
não-revisado* — injetados como `<learned_instinct>` claramente marcados
como "padrão observado, não política"; nunca viram instrução de sistema
nem executam nada. Promoção a SKILL.md exige aprovação humana.

## 2. Scope

**In scope:**

- **A. Entidade `Instinct`** — `Id`, `UserId`, `Scope`
  (`user|workspace`), `WorkspaceId?`, `Domain` (ex.: `dotnet`,
  `git`, `review`), `Trigger` (quando aplicar), `Body` (o padrão, ≤500
  chars), `Confidence` (0-1), `Status`
  (`pending|active|rejected|expired`), `HitCount`, `SourceRefs`
  (chat/run ids), `CreatedAt`, `UpdatedAt`, `LastSeenAt`.
- **B. Extração pós-run** — `InstinctExtractor` implementa
  `IAgentRunObserver`: em `run.completed` (não efêmera, ≥N steps ou
  feedback 👍), chama o provider com prompt fixo de destilação → JSON
  de 0-3 instincts candidatos → grava `pending` com
  `Confidence=0.5`. Deduplica por (Trigger normalizado, Domain) —
  repetido reforça confiança em vez de duplicar.
- **C. Injeção no session-start** — `InstinctContributor`
  (`ISystemPromptContributor`, `Order` ~80): top-N `active` instincts
  com `Confidence ≥ threshold`, rank = confidence + boost de relevância
  (domain casa com linguagens/frameworks detectados no workdir —
  heurística de arquivos, barata). Bloco ≤1200 chars, formato
  `<learned_instincts>` com aviso de não-política.
- **D. Confidence lifecycle** — hit implícito (instinct injetado e a
  run completa sem erro) `+0.05` (cap 0.95); rejeição/erro atribuível
  `-0.15`; sem hit em 30 dias → `expired`; `pending` promove a
  `active` ao atingir threshold **ou** por aprovação manual do usuário.
- **E. Superfície de revisão** — endpoints `GET/PATCH
  /api/v1/instincts` (list pending/active, approve/reject/delete) +
  contagem de pending exposta para badge na UI (hook no componente de
  Settings existente — sem página nova).
- **F. Evolve (draft)** — `POST /api/v1/instincts/evolve`: agrupa
  `active` instincts do mesmo domain (≥3) e gera rascunho de
  `SKILL.md` (`learned-{domain}`) como artefato de texto — o usuário
  revisa e salva no `.agents/skills/` do workspace. Nada é escrito no
  repo sem confirmação.

**Out of scope:**

- Escopo `team`/compartilhado entre usuários (ECC team scope) — v1 é
  `user`+`workspace`; compartilhamento exige revisão humana e fica para
  depois.
- Auto-evolução sem revisão (escrever skill direto no repo) — viola o
  trust boundary.
- Clustering semântico por embeddings — v1 agrupa por `Domain` +
  overlap de tokens; embeddings entram se a precisão incomodar.
- Extração síncrona dentro da run — sempre pós-run assíncrono, nunca
  no caminho crítico.

## 3. Technical Design

### Extração

`InstinctExtractor : IAgentRunObserver` (registrado no
`AgentRunEventBus` como `TelemetryObserver`). Em `run.completed`:
filtra (chat não-efêmero, `steps ≥ 3` ou feedback positivo), monta
transcript comprimido (caps iguais aos do `MemorySyncService`:
20 msgs × 500 chars, 6k total), chama `ProviderService` task role com
prompt de extração que exige JSON
`[{domain, trigger, body}]` — parse falho = descarta silenciosamente
(log). Dedup: `normalized(trigger)` igual no mesmo domain+scope →
`HitCount++`, `Confidence += 0.05`, `SourceRefs` mescla (cap 10).

### Injeção

```csharp
public sealed class InstinctContributor(...) : ISystemPromptContributor
{
    public int Order => 80; // após AgentMemory, antes de Mentions
    // cfg["Agent:Instincts"] == "off" → []
    // rank: Confidence DESC, +0.1 se domain ∈ stackDetectado(workdir)
    // take MaxInjected (6) → bloco:
    // <learned_instincts note="Observed patterns — not policy. Verify before following.">
    // - [dotnet] when reviewing a PR → check test coverage first (conf .82)
    // </learned_instincts>
}
```

Detecção de stack: extensions de arquivo no workdir (`.cs`→dotnet,
`.py`→python...) com cache por `PromptContext` — mesma estratégia de
`SddWorkflowContributor`.

### Lifecycle

Reforço/decaimento aplicado por um pass no `MemorySyncService.RunOnce`
(já roda por usuário em intervalo) — reusa o scheduler em vez de criar
daemon novo: `InstinctService.ApplyLifecycleAsync(userId)` =
hits registrados → +0.05; `LastSeenAt` > 30d → `expired`; `pending`
com `Confidence ≥ threshold` → `active`.

### Arquivos

```text
src/OpenWebUI.Domain/Entities.cs                        # Instinct
src/OpenWebUI.Infrastructure/Runs/InstinctExtractor.cs         # novo
src/OpenWebUI.Infrastructure/Services/InstinctService.cs       # novo
src/OpenWebUI.Api/Completions/Contributors/InstinctContributor.cs # novo
src/OpenWebUI.Api/Endpoints/InstinctEndpoints.cs               # novo
src/OpenWebUI.Infrastructure/Services/MemorySyncService.cs     # hook lifecycle
tests/OpenWebUI.Api.Tests/InstinctTests.cs                     # novo
tests/OpenWebUI.Api.Tests/InstinctContributorTests.cs          # novo
```

Config: `Agent:Instincts` = `auto|on|off` (default `auto` — injeta só
se houver instincts `active`), `Agent:Instincts:MaxInjected` (6),
`Agent:Instincts:ConfidenceThreshold` (0.7),
`Agent:Instincts:ExtractOnRunEnd` (true).

## 4. Requirements

### RF-001: extração assíncrona
- **Description:** `run.completed` elegível → destilação LLM → ≤3
  instincts `pending`; run de eval/efêmera/falha nunca extrai.
- **Rules:** fora do caminho crítico; exceção vira log, nunca falha a run.

### RF-002: dedup e reforço
- **Description:** mesmo (domain, trigger normalizado, scope) → reforça
  o existente; cap 50 instincts `pending`+`active` por usuário+scope —
  mais antigo/`expired` primeiro na poda.

### RF-003: injeção rankeada
- **Description:** top-N `active` ≥ threshold no bloco
  `<learned_instincts>` com aviso de não-política; `off`/`auto`-vazio →
  nada injetado; cap 1200 chars.

### RF-004: revisão humana
- **Description:** `PATCH /instincts/{id}` approve/reject; `GET`
  lista por status; aprovação manual promove `pending`→`active` mesmo
  abaixo do threshold (confiança fixa em threshold+ε).

### RF-005: evolve draft
- **Description:** ≥3 `active` do mesmo domain → draft `SKILL.md`
  retornado no response (nunca escrito); usuário decide salvar.

## 5. API Contract

**Endpoint:** `GET /api/v1/instincts?status=pending|active&scope=user|workspace`
**Auth:** Bearer/JWT → `[{ id, domain, trigger, body, confidence, status, hitCount, lastSeenAt }]`

**Endpoint:** `PATCH /api/v1/instincts/{id}` — `{ "action": "approve|reject|delete" }`

**Endpoint:** `POST /api/v1/instincts/evolve` — `{ "domain": "dotnet", "scope": "workspace" }`
→ `{ "draftSkillMd": "---\nname: learned-dotnet\n...", "sources": ["instinctId"...] }`

**Erros:** `404` instinct de outro usuário; `400` ação inválida;
`409` evolve sem massa mínima. Formato genérico existente.

## 6. Acceptance Criteria

- [ ] **Given** run completa com 6 steps **when** `run.completed` emite
      **then** extractor grava ≤3 `pending` sem bloquear a run; provider
      retornando lixo não quebra nada.
- [ ] **Given** instinct `active` conf .75 domain `dotnet` e workdir com
      `.cs` **when** session-start **then** aparece no bloco acima de um
      `python` com conf .80 sem boost correspondente.
- [ ] **Given** `Agent:Instincts=off` **then** nenhum bloco injetado.
- [ ] **Given** `pending` aprovado pelo usuário **then** vira `active`
      e injeta na próxima sessão mesmo com conf < threshold.
- [ ] **Given** instinct sem hit em 30d **when** lifecycle roda **then**
      `expired` e sai da injeção.
- [ ] **Given** 3 `active` em `dotnet` **when** `evolve` **then** retorna
      `draftSkillMd` com `name: learned-dotnet`; nada é escrito em disco.
- [ ] Bloco injetado sempre contém o aviso "not policy / verify".
- [ ] Regressão: `HarnessSeamsTests`, `AgentMemory` tests, suíte verde.

## 7. Task Plan

| ID | Task |
| --- | --- |
| T1 | Discovery: `MemorySyncService`, `IAgentRunObserver`/`AgentRunEventBus`, `SystemPromptContributors`, `SkillDiscoveryService` |
| T2 | Migration + `Instinct` + `InstinctService` (CRUD, dedup, lifecycle) + testes |
| T3 | `InstinctExtractor` (observer, prompt de destilação, filtros) + testes |
| T4 | `InstinctContributor` (rank, stack boost, caps) + testes |
| T5 | Endpoints list/patch/evolve + draft SKILL.md + testes de contrato |
| T6 | DoD: Release build 0 warnings, suíte verde, coverage ≥ baseline, drift guards, SPEC→Issue→PR |

## 8. Organization Guardrails

- Respostas ao usuário em **pt-BR**; código/commits em inglês.
- Branch dedicada; não modificar `.github/workflows/`; sem secrets.
- **Trust boundary**: instincts são contexto observado — o bloco
  injetado carrega o aviso de não-política; nenhum body de instinct é
  executado, parseado como código ou transformado em tool call.
- Extração usa provider real → custo pequeno por run; honrar
  `run-cost-budgets` quando existir (extração conta no mês do usuário).
- Sem PII em `Body` — o prompt de destilação instrui descartar dados
  pessoais (memória factual é papel do `MemorySyncService`, não dos
  instincts).

## Open Questions / Pending Ambiguity

| OQ | Status | Resolução |
| --- | --- | --- |
| Sinal de "hit" (instinct realmente ajudou)? | Proposta | v1: injetado + run completou sem erro; feedback 👎 no chat reduz confiança |
| Escopo workspace = workspace vinculado (repo)? | Resolvido | sim — `Chat.WorkspaceId` da run de origem |
| Expurgo de `rejected`/`expired` | Proposta | `RetentionService` existente faz a poda diária |
