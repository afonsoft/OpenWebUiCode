# SPEC-20261122 — Transições automáticas de modo (chat ⇄ plan ⇄ build)

## 0. Metadata

| Campo | Valor |
|-------|-------|
| Owner | afonsoft |
| Status | `Delivered` |
| Origem | pedido do usuário — "aprovo o plano e ele continua no modo plano"; quer transições automáticas entre chat→plan→build (ou chat→build direto) e preset `smart` como default em plan/build |

## 1. User Story
Como usuário, quero que o modo do agente acompanhe a intenção: peço um plano
em chat → o chat vira plan; aprovo o plano → vira build e executa; peço
implementação direta → vai a build sem passar por plan. Slash commands de
modo (`/plan`, `/build`, `/code`, `/chat`) funcionam digitados ou via picker.

## 2. Problemas diagnosticados (estado atual)
1. `builtin_plan_exit` existe, mas o bloco PLAN do system prompt nunca o
   menciona — o modelo não chama a tool quando o usuário aprova por texto,
   e a conversa fica presa em plan.
2. `PlanExitAsync` e `ResolveEffectiveModeAsync` ainda exigem repo
   vinculado: plano aprovado sem binding vai para `chat` (não `build`) e o
   modo build é silenciosamente rebaixado para `chat` — vestígio da
   exigência removida pela SPEC-20261027 (scratch workspace).
3. `/plan` só existe no picker do composer — `/build`, `/code`, `/chat`
   não existem; texto digitado `/build …` não muda o modo.
4. Não há tool para o modelo trocar de modo — transições dependem 100%
   do usuário.
5. Preset default `approve-mutations` faz cada comando de análise do plan
   exigir aprovação manual.

## 4. Requirements

### RF-001 — plan_exit → sempre build
- `PlanExitAsync`: alvo sempre `build` (binding não é mais exigido —
  SPEC-20261027). Texto de retorno atualizado.

### RF-002 — fim do rebaixamento build→chat sem binding
- `ResolveEffectiveModeAsync` devolve o modo persistido; `EffectiveAgentMode`
  no client e o revert on-repo-change seguem o mesmo.

### RF-003 — tool `mode_switch` (transição pelo modelo)
- `builtin:mode_switch` args `{mode, reason?}`; Tier `Read` (só estado de
  sessão — sem aprovação).
- Permitida em chat/build; negada em plan (não anunciada — `plan_exit` é a
  saída correta).
- plan→build via tool → erro orientando a chamar `builtin_plan_exit`.
- Efeito: `chat.Mode` persistido + preset de entrada (RF-005); o executor
  detecta a mudança no gate da próxima call e publica `event: mode` (chip
  do cliente atualiza ao vivo).

### RF-004 — Slash commands de modo
- Builtins do composer: `/plan` (existe), `/build`, `/code` (alias de
  build), `/chat`.
- Servidor: `Content` iniciando com `/plan|/build|/code|/chat` (word
  boundary) → muda `chat.Mode`, aplica preset de entrada, e o restante
  vira a mensagem (vazio → diretiva mínima de modo). Cobre API/headless/
  texto digitado sem picker.

### RF-005 — Preset `smart` na entrada de plan/build
- Toda transição para plan|build (PATCH, plan_exit, mode_switch, slash):
  se o preset atual é `allow-readonly`/`approve-mutations` e o dono nunca
  escolheu preset manualmente → `smart`.
- PATCH com `ApprovalPreset` explícito marca `chat:{id}:preset.pinned=1`
  (ConfigService) — escolha manual nunca é sobrescrita.

### RF-006 — Prompts do modo
- Bloco PLAN: plano DETALHADO (itens ordenados, arquivos-alvo, critério de
  aceite por item) + instrução: sinal de aprovação do usuário (texto ou
  botão) → `builtin_plan_exit` IMEDIATO — única saída para build.
- Bloco CHAT: pedido de planejamento → `mode_switch("plan")`; implementação
  direta → `mode_switch("build")`; troca é instantânea.
- `AgentModeToggle`: build destravado sem repo (scratch cobre — mesma
  SPEC-20261027).

## 6. Acceptance Criteria
- AC-1: plan_exit aprovado sem binding → `chat.Mode="build"` + evento mode.
- AC-2: enqueue `"/build faça x"` → chat.Mode build, preset smart, mensagem
  persistida "faça x"; `"/plan …"`/`"/chat …"` idem.
- AC-3: `mode_switch` chat→plan persiste modo+preset; plan→build retorna
  erro apontando plan_exit; mesmo alvo → no-op.
- AC-4: modo mudado por tool mid-run emite `event: mode` com o novo valor.
- AC-5: PATCH mode→build bump de preset; preset escolhido manualmente
  (pinned) nunca é sobrescrito.
- AC-6: bloco PLAN menciona builtin_plan_exit; bloco CHAT menciona
  builtin_mode_switch. Builtins incluem plan/build/code/chat.
- AC-7: build + testes NUnit verdes; drift guards do client verdes.
