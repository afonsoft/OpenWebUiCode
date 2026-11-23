# SPEC-20261123-context-usage-report — relatório de uso de contexto + extend

**Status**: Proposta

## Motivação

Portado do Devin CLI (`ContextUsage{modelId,totalTokens,maxTokens}` por
`ContextSectionKind` — systemPrompt/tools/messages —, comando *"Show context
window usage"* e tool `extend_context` — *"Only the lead agent can extend
its context window"*).

Hoje o agente não tem visibilidade do próprio orçamento: a compactação
dispara por threshold fixo sem o modelo saber quão perto está — e não existe
comando para o usuário inspecionar onde os tokens estão gastos.

## Design

- `ContextUsageService` (Infrastructure): calcula a contagem por seção do
  prompt montado em `ChatPipeline` — `system_prompt`, `tools`, `rules`,
  `history`, `pending_tool_results` — reutilizando o tokenizer já usado
  pela contagem de overflow (`tokens_used` que alimenta o threshold).
- Novo evento/endpoint `context_usage` por chat: `{ model, totalTokens,
  maxTokens, sections[] , percentUsed, compactionThresholdTokens }` —
  exposto como builtin tool `context_usage` (o agente consulta antes de
  decisões caras) e no stream para a UI mostrar um indicador.
- `extend_context` builtin tool: o agente eleva o
  `compaction:threshold_tokens` da run até o teto do modelo (registrado como
  evento; só o run "lead" — não vale em sub-workflow steps, espelhando o
  "only the lead agent" do Devin).
- Heurística de threshold automático: quando `percentUsed > 80%`, o
  `context_usage` devolve aviso embutido sugerindo diet/compactação —
  dá ao modelo a informação para se auto-regular (hoje o primeiro sinal é
  o overflow estourar).
- Custo zero quando não consultado: o cálculo roda por demanda e reusa o
  prompt já materializado do último step.

## Testes

- `Usage_RetornaSeçõesCorretas` — soma das seções == total do prompt
  montado; percentual correto vs `maxTokens`.
- `Usage_Threshold80_Avisa` — prompt a 85% → campo `warning` presente.
- `Extend_LeadAumentaThreshold` — `extend_context` sobe o threshold da run;
  evento `context_extended` no stream.
- `Extend_SubStep_Rejeita` — chamada dentro de sub-workflow step → erro
  "only lead run can extend".
- `Usage_SemPromptAinda_Fallback` — chat sem run → zeros + `estimating`.
