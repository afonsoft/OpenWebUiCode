# SPEC-20261128-persisted-tool-names — nomes reais de tools no histórico

**Status**: Approved — plano de melhorias aprovado 2026-11-28 (correção da validação E2E do item P2.25).

## Objetivo

Fazer o histórico reidratar o nome real da tool a partir do `ToolCallsJson`
da mensagem assistant que originou a chamada, em vez de usar o `ToolCallId`
(`call_N...`) como se fosse nome. Isso restaura a classificação por tipo do
preview de output (por exemplo, `file_write`/`file_edit` → diff).

## Requisitos

- RF-001: extrair um mapa `id → function.name` dos blocos `ToolCallsJson`
  persistidos, tolerando JSON malformado.
- RF-002: `ToolCallView.FromPersisted` aceita nome resolvido e mantém o id
  original como identificador estável do evento.
- RF-003: quando não houver mapeamento, preservar o comportamento anterior de
  fallback para o `ToolCallId`.
- RF-004: testes bUnit/unitários cobrem nome resolvido, fallback e parsing
  tolerante a JSON inválido.
- RF-005: regenerar Tailwind se classes mudarem; drift guards verdes.

## Não-escopo

- Alterar o formato persistido no banco ou o protocolo enviado aos providers.
- Reclassificar outputs que não dependem do nome da tool.
