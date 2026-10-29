# SPEC-20261014-context-maintenance-v2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `context-maintenance-v2` |
| Type | `Feature` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/Devin-20261014-context-maintenance-v2` |
| Ticket | #365 |
| Status | `Completed` |
| Parent | `SPEC-20261014-cli-opencode-analysis` (RF-001/RF-002) |

## 1. User Story

**As a** usuário com runs longas de agente (ou sob roteamento omniroute, onde cada request pode ir a outro provider)
**I want** poda mecânica de contexto, resumo estruturado e capacidade de o modelo revisitar o histórico original
**So that** o contexto enviado a cada request é denso e portável, sem pagar tokens de lixo mecânico nem perder acesso ao passado após compactar.

**Problem context:**
`ContextCompactionService` hoje só resume via LLM (papel `memory`, SPEC-model-roles). oh-my-pi (`docs/compaction.md`) faz três coisas que não fazemos: (1) **poda mecânica** antes do resumo — `dropUseless`/`pruneSuperseded` substituem tool results obsoletos por `[Uneventful result elided]`/`artifact://` sem custo de LLM; (2) **resumo estruturado** (arquivos tocados, decisões, pendências) em vez de prosa; (3) **`history://current/full`** — o modelo recupera mensagens originais via `read`/`grep` mesmo depois da compactação. Sem (3), compactar é irreversível para o agente.

## 2. Scope

**In scope:**
- Passo de **poda mecânica** antes de qualquer resumo: tool results superseded (mesmo `path` reescrito depois), saídas de diagnóstico obsoletas, `ok:true` sem conteúdo, resultados >N bytes substituídos por referência `artifact://runs/{runId}/calls/{callId}`.
- **Resumo estruturado**: o summarizer emite JSON `{files, decisions, pending, notes}` + prosa; persistido como `Role="compaction"` estruturado (schema no `ChatMessage.Meta` ou campo novo).
- **`history://{chatId}/{range|all}`** — pseudo-recurso acessível via `file_read`/`grep` sobre o histórico pré-compactação persistido.
- Split-turn handling: se o cut point cair dentro de um turno de assistant, gerar resumo do prefixo do turno separado (como oh-my-pi) — hoje já evitamos cortar `Role=="tool"`; falta o prefixo separado.

**Out of scope:**
- Compaction provider-native (Responses API `/compact`, Anthropic beta) — nossa superfície é chat-completions; resumo texto é o correto para omniroute.
- `context_notes`/`new_context`/`checkpoint`/`rewind` — coberto por SPEC-20261013-agent-context-management (#353).
- Mudança de schema do histórico visível na UI (a compaction entry já renderiza).

## 3. Technical Context

**Where the change happens:**
`ContextCompactionService` (poda antes do resumo + cut-point de turno), `LlmContextSummarizer` (prompt estruturado → JSON), novo `HistoryResourceService` resolvendo `history://` sobre `ChatMessage` persistido, `BuiltinToolRegistry`/`file_read`+`grep` aceitando o esquema `history://`.

**Files to read before implementing:**
- `src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs`
- `src/OpenWebUI.Infrastructure/ChatTools/Tools/FileBuiltinTools.cs` (`file_read`)
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` (montagem da janela, onde `MaybeCompactAsync`/`ApplyAsync` são chamados — L117-122)
- `src/OpenWebUI.Domain/Entities.cs` (`ChatMessage`, `Role`, `Meta`)

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/ContextPruner.cs            # NOVO: poda mecânica
src/OpenWebUI.Infrastructure/Services/HistoryResourceService.cs   # NOVO: history:// resolver
src/OpenWebUI.Infrastructure/Services/ContextCompactionService.cs # cut-point + resumo estruturado
src/OpenWebUI.Infrastructure/ChatTools/Tools/FileBuiltinTools.cs  # file_read aceita history://
src/OpenWebUI.Application/Contracts/ChatContracts.cs              # CompactionMeta contract
tests/OpenWebUI.Api.Tests/ContextPrunerTests.cs                   # NOVO
tests/OpenWebUI.Api.Tests/HistoryResourceTests.cs                 # NOVO
```

## 4. Requirements

### RF-001: Poda mecânica antes do resumo
- **Description:** Antes de chamar o summarizer, `ContextPruner` marca mensagens tool-elidíveis: (a) tool result cujo `path`/alvo foi reescrito por tool result posterior do mesmo tipo; (b) resultados `ok` sem payload; (c) resultados >4KB substituídos por `artifact://runs/{runId}/calls/{callId}` + contagem de bytes.
- **Rules:** nunca toca em user/assistant text; elisão gera marcador textual estável; a mensagem original NÃO é deletada do DB — só da janela enviada ao modelo.
- **Input → Output:** `List<ChatMessage>` → mesmas mensagens com `Elided` marcado ou conteúdo substituído na projeção da janela.

### RF-002: Resumo estruturado
- **Description:** `LlmContextSummarizer` passa a pedir JSON `{files:[], decisions:[], pending:[], summary:string}`; falha de parse → cai na prosa atual (nunca falha a run).
- **Rules:** persistido com `Role="compaction"`; a UI pode renderizar `files`/`decisions` como chips — prosa pura continua válida (retrocompat com resumos antigos).

### RF-003: `history://` recovery
- **Description:** `file_read` e `grep` aceitam `history://{chatId}` (inteiro), `history://{chatId}/tail:{n}` (últimas n mensagens), `history://{chatId}/msg:{id}` — resolvem sobre o histórico persistido, ignorando elisões.
- **Rules:** read-only; RBAC = dono do chat (o tool context já carrega userId); resultado truncado a 32KB com indicação de continuação.
- **Input → Output:** `file_read("history://abc/tail:40")` → transcript textual das últimas 40 mensagens originais.

### RF-004: Cut-point com prefixo de turno
- **Description:** quando o corte ideal cai dentro de um turno (assistant com tool_calls seguidos de tool results), a compactação gera duas partes: resumo do histórico + prefixo textual do turno corrente, em vez de empurrar o corte para depois do turno inteiro (o turno pode ser grande demais).
- **Rules:** nunca parte um par tool_call/tool_result; se o turno inteiro exceder a janela mesmo assim, corta por mensagem com marcador `[…n mensagens anteriores do turno omitidas — ver history://{chatId}]`.

## 5. Acceptance Criteria

- [ ] Teste: tool result reescrito depois é elidido; o marcador aparece na janela enviada ao mock provider.
- [ ] Teste: `file_read("history://…")` devolve mensagem removida da janela por compactação.
- [ ] Teste: resumo estruturado persiste e parse falho degrada para prosa.
- [ ] E2E (mock provider): run longa compacta → nova request ao provider contém resumo + marcadores de elisão, sem estourar limiar.
- [ ] Coverage gate do CI ≥ baseline; `dotnet test` verde; drift guards ok (sem mudança de razor exceto render opcional de chips — se houver, Tailwind regen + commit).

## 6. Open Questions

1. `artifact://` como storage real (escrever resultado completo em disco `data/artifacts/`) vs só marcador apontando para `tool_result` persistido no DB (mais simples — artifact é lookup no DB, sem FS).
2. Limiar de elisão configurável por chat ou constante (começar com constante 4KB).
3. `history://` deve também alimentar o `file_list`/search de knowledge? Provavelmente não — manter escopo em read/grep.

## Delivered

| Item | Link |
| --- | --- |
| Issue | https://github.com/afonsoft/OpenWebUiCode/issues/365 |
