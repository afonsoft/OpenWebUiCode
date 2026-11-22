# SPEC-20261124-denied-agents-hidden — agente negado some da spec do delegate_task

**Status**: Completed

## Motivação

Portado do `anomalyco/opencode` (dev): `permission.task` controla quais
subagentes um agente pode invocar, e **o agente negado é omitido da
descrição da tool Task** — o modelo nunca vê o que não pode usar, logo
não gasta turno chamando um subagente que vai falhar no gate. Aqui o
`delegate_task` recebe `builtin_delegate_task` no catálogo
(`PermissionRuleset.cs:130`) e a lista de agentes/personas vai no
prompt (`SystemPromptContributors.cs:609+`) — sem filtro por regra.

## Design

- Regra por agente: o pattern de recurso do `delegate_task` passa a
  casar o alvo da delegação — `delegate_task` com arg
  `agent:"code-review"` resolve regras contra
  `delegate_task:code-review` (o ruleset já suporta `tool:pattern`
  por `ArgPattern`; a assinatura do call ganha o nome do agente).
- Na montagem do catálogo de agentes (descrição da tool + seção do
  system prompt), cada candidato passa pelo
  `PermissionRuleEvaluator` com a assinatura `agent:<nome>` —
  resultado `deny` → agente omitido da lista; `ask`/`allow` → listado.
- Sem agente no arg (delegação livre/default) → casa `delegate_task:*`.
- Agentes omitidos continuam inacessíveis no gate em tempo de execução
  (a omissão é UX, o deny é enforcement — defesa em profundidade).
- Log/auditoria: hook de regra `deny` por agente emite evento
  `rule_denied` no tool call, como hoje.

## Testes

- `CatalogoAgentes_DenyOmite` — regra `deny delegate_task:danger-*`
  esconde `danger-bot`; `allow`/`ask` listam.
- `CatalogoAgentes_GlobPattern` — `delegate_task:*-sandbox` casa glob.
- `DelegateCall_DenyAgente_Assinatura` — call `agent:"x"` negada quando
  regra `deny` casa `delegate_task:x`, mesmo com a tool listada.
- `SystemPrompt_AgenteNegado_Ausente` — seção de delegação não cita.
- Regressão: sem regras, catálogo idêntico ao atual.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/772
- Branch: feature/agent-20261124-opencode-gaps
