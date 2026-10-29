# SPEC-20261010-delegate-persona — Persona overrides no delegate_task

Status: Completed

## Contexto
`delegate_task` herda modelo/tools/system do pai — o filho é um clone. Referência: opencode delega "por persona" (`subagent_type` = skill/agent com prompt próprio, ex.: reviewer, tester).

## Objetivo
`delegate_task` aceita `persona` — slug (Name ou Id) de uma Skill ativa do usuário; o `Content` da skill vira mensagem `system` no request e no transcript do chat filho (o EnrichRequestAsync mescla antes, preservando o conteúdo).

## Escopo
1. Param `persona` no ParametersJson do `DelegateTaskBuiltinTool`.
2. Resolução: `db.Skills` `UserId==context.UserId && IsActive && (Name==persona || Id==persona)`. Não encontrada → `BuiltinToolResult` de erro com a lista de personas disponíveis (não delega).
3. Persona válida → mensagem `system` na posição 0 do chat filho + primeira mensagem do `childRequest`.

## Fora de escopo
Tools por persona (filho herda tools do pai); model override por persona; catálogo de personas built-in.

## Acceptance Criteria
- `persona` válida → `RequestJson` do filho começa com `system` = Content da skill; chat filho tem `ChatMessage` system na posição 0.
- `persona` inválida → erro com lista de skills ativas, sem criar chat/run.
- Sem `persona` → comportamento atual.
- Tests: persona injeta system no filho; persona inexistente → erro com lista.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/303 |
