# SPEC-20261031-reasoning-tag-sanitization

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `reasoning-tag-sanitization` |
| Type | `Bugfix` |
| Stack | `.NET` (ASP.NET Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/reasoning-tag-sanitization-20261031` |
| Ticket | `GH#561` |
| Status | `Completed` |
| Parent | gap-analysis-20261031 · bug reportado pelo usuário (chat `30b15271-12c9-42b6-8fa6-ca2a8968ab5a` em produção) |

## 1. User Story

**As a** usuário do chat
**I want** que tokens de controle do modelo (`<|channel>`, `<|channel|>`, `<|begin_of_thought|>`, `<thought>` etc.) nunca apareçam como texto na resposta
**So that** mensagens renderizam limpas e o raciocínio vai para a seção colapsável de "Thinking", em paridade com o upstream.

**Problem context:**
Em produção (`open-webui.afonsoft.dev`), o chat `30b15271-12c9-42b6-8fa6-ca2a8968ab5a` ("Análise Comparativa do OpenCode", model `Gemini` via OmniRoute) persistiu duas mensagens assistant com markup cru do modelo no `Content`:

- `7ee15ec2` (pos 148, 484 chars): termina com `\n\n<|channel>thought\n<channel|>`
- `fbd17835` (pos 150, 5053 chars): contém `…Stack Tecnológica**.\n\n<|channel>thought\n<channel|>O arquivo foi criado…`

Evidência no banco (`/data/openwebui.db`): `ChatRunEvents` do run `cfeece72-09b5-4379-9116-f0cebe6901e1` mostram o token chegando **dentro de `delta.content`** nos eventos `content_delta` — o provider (OmniRoute → modelo rotulado "Gemini") emite o marcador inline e o pipeline o persiste verbatim em `ChatMessages.Content` (`ChatRunExecutor.cs:2464`).

**Causa raiz (AS-IS):**
- `ProviderService` só saneia dois casos: campos estruturados de reasoning (`reasoning_content`/`reasoning`/`thinking`/`reasoning_details` — `ExtractOpenAiReasoning`, `ProviderService.cs:532-548`) e markup DSML DeepSeek (`DsmlToolCallFilter`, `:1101-1205`, aplicado em `FilterOpenAiDeltaLine` `:2876` e na variante buffered `ExtractDsmlFields` `:686`).
- **Não existe filtro para tags de raciocínio inline nem para special tokens `<|…|>`** — greps por `<|`, `|>`, `special token`, `harmony` retornam zero em `src/`/`tests/`.
- O upstream (`open-webui/open-webui@main backend/open_webui/utils/middleware.py:280-290`) mantém `DEFAULT_REASONING_TAGS` — `('<think>','</think>')`, `('<thinking>',…)`, `('<reason>',…)`, `('<reasoning>',…)`, `('<thought>',…)`, `('<Thought>',…)`, `('<|begin_of_thought|>','<|end_of_thought|>')`, `('◁think▷','◁/think▷')` — e o `tag_output_handler` (`:4198+`, chamado em `:5439`) revarre o output acumulado do stream, move blocos de tag para um item `type:'reasoning'` e re-emite o output limpo ("the raw chunk still carries the tag text: resend the cleaned output instead"). O port .NET não implementa essa detecção — gap de paridade (CLAUDE.md: "Manter paridade visual/funcional com o upstream").

## 2. Scope

**In scope:**
- Filtro de stream/buffered no `ProviderService` que (a) detecta pares de tags de raciocínio inline e (b) remove special tokens `<|…|>` órfãos/malformados de `content` e `reasoning`.
- Roteamento do texto interno de blocos de tag de raciocínio para o canal de reasoning (`delta.reasoning`), em vez de `content`.
- Defesa em profundidade: sanitização final do `content` antes de persistir `ChatMessage` (`ChatRunExecutor`).
- Testes NUnit no padrão da seção DSML de `ProviderServiceTests.cs`.

**Out of scope:**
- Parâmetro por-modelo `reasoning_tags` configurável (upstream suporta; fica para SPEC futura se necessário).
- Reescrita/limpeza retroativa das mensagens já persistidas no banco (data fix separada, se desejada).
- Mudanças no OmniRoute/provider — o filtro é no consumer (este repo).
- Renderização inline de `<think>` no markdown do cliente — o canal `delta.reasoning` + `ThinkingSection.razor` já cobrem a UX.

## 3. Technical Context

**Where the change happens:**
`src/OpenWebUI.Infrastructure/Services/ProviderService.cs` — mesmo seam do `DsmlToolCallFilter`: streamed (`FilterOpenAiDeltaLine` `:2876-2913`, `CompleteWithToolsStreamingAsync` filtros `:846-847`, `:2837-2838`, `EmitDsmlTail` `:2918`), buffered (`ExtractDsmlFields` `:686-707` via `ParseCompletionMessage` `:610`). Defesa extra no ponto de persistência `ChatRunExecutor.FinishAsync` (`:813`, `:2464`).

**Files to read before implementing:**
- `CLAUDE.md` · `.claude/rules/`
- `src/OpenWebUI.Infrastructure/Services/ProviderService.cs` (`DsmlToolCallFilter` `:1101-1205`, `HeldSuffixLength` `:1193`, `FilterOpenAiDeltaLine` `:2876`, `EmitDsmlTail` `:2918`, `ExtractDsmlFields` `:686`)
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs` (`OnDeltaAsync` `:662-688`, `OnReasoningAsync` `:694-711`, `AccumulateDelta` `:2176`, `FinishAsync` `:2427-2486`)
- `src/OpenWebUI.Api/Completions/ChatPipeline.cs` (tool loop — onde `ProviderCompletion.Content`/`Reasoning` entram no histórico)
- `tests/OpenWebUI.Api.Tests/ProviderServiceTests.cs` (seção DSML `:1392+` — padrão de SSE fake)
- Referência upstream: `open-webui/open-webui backend/open_webui/utils/middleware.py:280-320, 4198-4300, 5430-5460`

**Files to create or modify:**
```text
src/OpenWebUI.Infrastructure/Services/ProviderService.cs   # novo filtro inline (ex.: ReasoningMarkupFilter / SpecialTokenFilter)
src/OpenWebUI.Api/Runs/ChatRunExecutor.cs                  # sanitização final antes de persistir (defesa em profundidade)
tests/OpenWebUI.Api.Tests/ProviderServiceTests.cs          # novos casos na seção de markup
tests/OpenWebUI.Api.Tests/ChatRun*Tests.cs                 # regressão E2E do conteúdo persistido (se houver seam)
```

## 4. Requirements

### RF-001: Filtro de tags de raciocínio inline no stream
- **Description:** o pipeline deve detectar pares de tags de raciocínio dentro de `delta.content`/`delta.reasoning` — famílias do upstream `('<think>','</think>')`, `('<thinking>',…)`, `('<reason>',…)`, `('<reasoning>',…)`, `('<thought>',…)`, `('<Thought>',…)`, `('<|begin_of_thought|>','<|end_of_thought|>')`, `('◁think▷','◁/think▷')` — mais a variante observada em produção `<|channel>thought` … `<channel|>`/`<|channel|>`. O conteúdo interno do bloco vai para o canal de reasoning (comportamento upstream), nunca para `content`.
- **Rules:** markup nunca aparece no `content` emitido; texto fora das tags flui normalmente; bloco não fechado no fim do stream é descartado como markup (não vaza).
- **Input → Output:** `delta.content:"texto <|channel>thought\n<channel|> mais texto"` → `content:"texto  mais texto"` (residuo mínimo normalizado).

### RF-002: Strip de special tokens órfãos `<|…|>`
- **Description:** tokens de controle sem par — `<|start|>`, `<|end|>`, `<|message|>`, `<|return|>`, `<|channel|>`, `<|channel>`, `<channel|>`, `<|im_start|>`, `<|im_end|>`, `<|eot_id|>`, `<|begin_of_thought|>`, `<|end_of_thought|>` e variantes com pipe deslocado — são removidos de `content` e `reasoning` mesmo fora de bloco de raciocínio.
- **Rules:** não remover `<`/`>` legítimos (code blocks, HTML do usuário): a allowlist cobre apenas os padrões de token `<|[A-Za-z_]+|?>`/`<[A-Za-z_]+|>` documentados; qualquer outro `<…>` passa intacto.

### RF-003: Robustez a chunk boundary
- **Description:** marcador partido entre dois deltas SSE (ex.: `"<|chan"` + `"nel>thought"`) não pode vazar — reusar o padrão `HeldSuffixLength`/`_pending` do `DsmlToolCallFilter` (segurar sufixo que pode ser prefixo de marcador até o próximo `Feed`/`Flush`).
- **Input → Output:** chunks `["ab<|chan","nel>thought\n<channel|>cd"]` → `["ab","","cd"]` (ou agregação equivalente sem markup).

### RF-004: Cobertura nos quatro caminhos
- **Description:** o filtro aplica em (a) stream sem tools (`FilterOpenAiDeltaLine`), (b) stream com tools (`CompleteWithToolsStreamingAsync` — content e reasoning filters), (c) buffered `ParseCompletionMessage`/`ExtractDsmlFields` (OpenAI e Ollama), e (d) `EmitDsmlTail` no fim de stream.

### RF-005: Sanitização na persistência (defesa em profundidade)
- **Description:** `ChatRunExecutor` aplica um `Strip` one-shot no `content.ToString()` antes de `FinishAsync`/criação do `ChatMessage` — garante que nenhum caminho (steer, retry, continuação, tool loop buffered) persista markup.
- **Rules:** operação pura e idempotente; não altera `PartialContent` já gravado (fica consistente na próxima checkpoint).

### RF-006: Regressão com payload de produção
- **Description:** teste usando o payload real do incidente — `"...e **Stack Tecnológica**.\n\n<|channel>thought\n<channel|>O arquivo foi criado..."` — asserta que o `content` final não contém `channel`/`thought` markup e que `ChatMessage.Content` persistido está limpo.

**Business rules / invariants:**
- Nenhum texto visível ao usuário (SSE broadcast, `PartialContent`, `ChatMessage.Content`) contém marcadores `<|…|>`/tags de raciocínio.
- Raciocínio extraído segue o canal `delta.reasoning` existente — sem mudança de contrato SSE.
- Ollama (`message.thinking`/`message.content`) recebe a mesma sanitização.
- Filtro não quebra `DsmlToolCallFilter` existente — os dois operam em cadeia.

## 5. API Contract

N/A — nenhum endpoint/contrato novo; o shape SSE (`delta.content`, `delta.reasoning`) permanece idêntico, apenas com `content` limpo.

## 6. Acceptance Criteria

- [ ] **Given** um stream SSE cujo `delta.content` contém `"resp\n\n<|channel>thought\n<channel|>segue"`, **when** o pipeline processa, **then** o content acumulado é `"resp\n\nsegue"` (whitespace normalizada) e nenhum marcador vai ao broadcaster.
- [ ] **Given** um bloco `<think>raciocínio secreto</think>resposta` em `delta.content`, **when** processado, **then** `content` = `"resposta"` e `reasoning` recebe `"raciocínio secreto"`.
- [ ] **Given** marcador partido entre chunks (`"x<|chan"` / `"nel|>y"`), **when** processado, **then** saída `"xy"` sem markup.
- [ ] **Given** `<thought>análise</thought>resposta` buffered (non-stream), **when** `ParseCompletionMessage` roda, **then** `Content`=`"resposta"`, `Reasoning`=`"análise"`.
- [ ] **Given** texto com `<div>`/`<code>`/`<custom>` legítimo, **when** processado, **then** passa intacto (sem over-strip).
- [ ] **Given** a run termina, **when** `FinishAsync` persiste, **then** `ChatMessage.Content` não contém `<|`/`|>` markup — regressão usando o payload real do chat `30b15271`.
- [ ] **Given** todos os testes DSML existentes, **when** a suite roda, **then** permanecem verdes.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| Tag não fechada | `"texto <|channel>thought"` (fim de stream) | markup descartado, `content` = `"texto"` |
| Tag vazia | `"a<|channel|>b"` | `"ab"` |
| Token em reasoning | `reasoning_content:"x<|im_end|>y"` | `"xy"` |
| Case variants | `<Thought>` vs `<thought>` | ambos detectados conforme lista upstream |
| DSML + special token juntos | `"a<｜DSML｜tool_calls>…</｜DSML｜tool_calls><|channel|>b"` | DSML → call extraída; token → removido; `"ab"` |

## 7. Task Plan (agent execution)

- [ ] **T1 — Discovery:** ler `ProviderService.cs` (filtros `:686`, `:846`, `:1101`, `:2837`, `:2876`), `ChatRunExecutor.cs` (`:660-720`, `:2176`, `:2427`), testes DSML `ProviderServiceTests.cs:1392+`, e o `tag_output_handler` upstream (`middleware.py:4198-4300`).
- [ ] **T2 — Test first (RED):** testes NUnit novos cobrindo RF-001…RF-006 (padrão SSE fake da seção DSML + um caso buffered Ollama + regressão de persistência).
- [ ] **T3 — Implementation (GREEN):** novo filtro privado (ex.: `ReasoningMarkupFilter`) no `ProviderService` com `Feed/Flush/Extract` espelhando `DsmlToolCallFilter`; encadear nos quatro caminhos (RF-004); `Strip` one-shot em `ChatRunExecutor` antes de persistir (RF-005).
- [ ] **T4 — Verification:** `dotnet test tests/OpenWebUI.Api.Tests --filter "ProviderService|ChatRun"`; suite completa; `dotnet build -c Release`.
- [ ] **T5 — Done + PR:** DoD completo → `Status = Done` + PR na branch `fix/reasoning-tag-sanitization-20261031`.

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **Bugfix / .NET** | Teste de reprodução primeiro (RED), depois fix (GREEN); regressão incluída; suite NUnit completa verde; cobertura ≥80% no código tocado. |

## 8. Organization Guardrails (mandatory)

- **Branches:** nunca commitar em `main` — usar `fix/reasoning-tag-sanitization-20261031`.
- **Workflows:** não modificar `.github/workflows/`.
- **Security:** não logar conteúdo de mensagens; marcadores removidos não devem aparecer em logs (apenas contadores/flags).
- **Scope:** não implementar `reasoning_tags` configurável nem retroativo no banco — SPECs separadas se aprovadas.
- **Paridade upstream:** seguir `DEFAULT_REASONING_TAGS` + semântica do `tag_output_handler` (markup fora do content; inner text → reasoning).

## 9. Definition of Done

- [ ] RF-001…RF-006 implementados.
- [ ] Todos os acceptance criteria cobertos por testes NUnit verdes (RED→GREEN evidenciado).
- [ ] Edge cases da seção 6 tratados.
- [ ] `dotnet build -c Release` + `dotnet test tests/OpenWebUI.Api.Tests` verdes; cobertura não decresce.
- [ ] Guardrails da seção 8 respeitados.
- [ ] Logs sem conteúdo/markup de mensagens.

**Next action after DoD is complete:** `Status = Done` + PR na branch `fix/reasoning-tag-sanitization-20261031`.

## Open Questions / Pending Ambiguity

- `[A DEFINIR]` Reescrita retroativa das ~2 mensagens persistidas com markup (UPDATE sanitizante em `ChatMessages.Content`) — decidir se entra nesta entrega ou em SPEC de data-fix separada. Recomendação: separada.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/580 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/561
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/560
