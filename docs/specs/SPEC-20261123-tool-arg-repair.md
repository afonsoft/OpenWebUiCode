# SPEC-20261123-tool-arg-repair — reparo de argumentos de tool_call malformados

**Status**: Completed

## Motivação

Portado do Devin CLI (`agent-ext/src/tool_arg_leak_recovery.rs`, evento
`repaired_args`): quando o modelo emite `tool_call` com argumentos malformados
(JSON truncado, vírgula sobrando, aspas não fechadas — comum em outputs longos
ou streaming instável), o Devin **repara** em vez de falhar a call.

Hoje o `ToolExecutor`/`ChatPipeline` falha a call ou faz nudge genérico de
"resposta vazia" — perdendo a intent do modelo que estava 95% correta.

## Design

- Estágio `ToolArgRepair` no caminho de parse de tool calls
  (`ChatPipeline` onde `function.arguments` é desserializado).
- Escada determinística primeiro, sem LLM:
  1. `JsonDocument.TryParse` — passa direto quando válido.
  2. Reparo sintático: trim de trailing commas, fechamento de aspas/chaves/
     colchetes pendentes, escape de control chars, unwrap de markdown fence.
  3. Reparo por schema: para cada property `required` ausente que tenha
     `default` no schema da tool, preencher; remover campos não-declarados
     (`additionalProperties` violadores) quando a tool é estrita.
- Se o reparo determinístico falhar → falha normal da call (sem mudança de
  comportamento); **não** introduzir round-trip de LLM no hot path.
- Quando o reparo alterar o documento: executa a call com os args reparados,
  anexa nota no resultado da tool (`"arguments were auto-repaired"`) e emite
  evento de telemetria `tool_args_repaired` (tool, tipo de reparo aplicado) —
  igual ao `repaired_args` do Devin.
- Limite: payload de args > 256KB ou erro que mude a semântica
  (campo required faltando sem default) → não repara, falha normal.
- Gate de segurança: reparo nunca inventa valor para campo `required`
  sensível (paths, comandos) — melhor falhar do que executar arg errado.

## Testes

- `Repair_JsonTruncado_FechaEstrutura` — `{"path":"/x","content":"abc` →
  parseia e executa.
- `Repair_TrailingComma` — JSON com vírgula antes de `}`/`]` parseia.
- `Repair_DefaultPreenchido` — property opcional ausente recebe o default.
- `Repair_RequiredAusente_Falha` — `required` sem default → erro normal da
  call (não inventa valor).
- `Repair_Telemetria` — reparo bem-sucedido emite `tool_args_repaired` e a
  nota aparece no resultado da tool.

## Delivered

Implementado em feature/agent-20261123-devin-parity — ver commit da
branch e testes dedicados em tests/OpenWebUI.Api.Tests.
