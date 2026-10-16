# SPEC-20261010-context-compaction — Compactação de contexto em runs longas

Status: Completed

## Contexto
O executor reconstrói o histórico inteiro do chat do DB e envia a cada turno (`ChatRunEndpoints` monta `completionRequest` com todas as mensagens). Em runs longas de agente (tools + saídas), o histórico estoura a janela de contexto do modelo — hoje não existe compactação. Referência: opencode compacta a sessão em resumo.

## Objetivo
Quando o histórico ultrapassa um limiar (~60k chars), resumir o trecho antigo via LLM, persistir resumo + cutoff, e injetar o resumo como contexto no lugar das mensagens compactadas. Mensagens originais ficam no DB (UI mostra tudo); só a janela enviada ao modelo é compactada.

## Escopo
1. `ContextCompactionService` (Infrastructure): `ApplyAsync(chatId, history)` devolve `[ctx-summary] + tail` quando existe kv `chat:{id}:compaction` `{ cutoff, summary }`; `MaybeCompactAsync(chat, history, model, ct)` corta head/tail (tail = últimas ~30 msgs / ~25k chars, sem quebrar grupo tool_call/tool), resume o head via `providers.CompleteAsync` e persiste kv + mensagem marcador `Role="compaction"` no chat.
2. Sumário injetado como mensagem `user` sintética prefixada `[Contexto compactado de mensagens anteriores]` — compatível com qualquer provider (system mid-history não é).
3. Executor aplica antes de `EnrichRequestAsync`: `ApplyAsync` → `MaybeCompactAsync` → `ApplyAsync` (recém-compactado já sai no formato).
4. `IContextSummarizer` seam pro LLM call (impl `LlmContextSummarizer` via ProviderService) — testável sem provider real.
5. Thresholds configuráveis via kv `compaction:threshold` (default 60_000) e `compaction:tail` (default 30 msgs / 25_000 chars).

## Fora de escopo
Compactação manual por comando; UI dedicada do marcador; compactação de tool output individual; arena/pipeline models (skip).

## Acceptance Criteria
- Histórico > limiar → primeira run persiste kv + marcador e envia ao modelo `[resumo] + tail`, não o head inteiro.
- Runs seguintes usam o resumo sem recompactar (cutoff cobre).
- Tail nunca corta um `assistant(tool_calls)` das respostas `tool` correspondentes.
- Modelo arena/pipeline não compacta.
- Falha no summarizer → run segue com histórico normal (best-effort, log warning).
- Tests: split sem quebrar tool group, Apply com kv, MaybeCompact com summarizer fake, executor integra.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/300 |
