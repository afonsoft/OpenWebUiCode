# SPEC-20261128-ux-polish — P2.24–P2.26

**Status**: Approved — plano de melhorias aprovado 2026-11-28 (UX polish comparativo opencode/oh-my-pi → Devin web).

## Objetivo

Fechar três lacunas de UX do chat agente: atividade sempre visível no drawer
mobile, saídas de tool com preview inteligente e estatísticas de tarefas por
run conectadas ao painel de insights.

## Requisitos

- RF-001 (P2.24): no drawer mobile, o `RunActivityFeed` aparece no topo do
  `ChatWorkspacePanel`, acima das tabs, quando há calls da run. Não remover o
  feed inline da conversa nem duplicar no drawer quando não houver calls.
- RF-002 (P2.25): `ToolCallCard` resume a saída por tipo em um cabeçalho
  compacto (linhas/bytes; diff com +/−; JSON como JSON; erro com primeira
  linha) e mantém o corpo colapsável/expansível. Saídas grandes não explodem
  a altura da conversa.
- RF-003 (P2.26): `InsightsPanel` recebe o histórico de tarefas por run já
  carregado no `ChatWorkspacePanel` e mostra um bloco compacto com runs
  recentes e progresso `done/total`, relacionando as estatísticas da sessão
  ao histórico persistido.
- RF-004: chaves novas nos 8 locales; regenerar Tailwind por causa das
  classes `.razor`; drift guards verdes.
- RF-005: testes bUnit cobrem ordenação/visibilidade do feed no painel,
  resumo inteligente de outputs e bloco de task stats no InsightsPanel.

## Não-escopo

- Backend novo: usa os endpoints/contratos já entregues em
  SPEC-20261128-task-history e SPEC-20261104-session-insights-v2.
- Redesign visual completo do painel.
