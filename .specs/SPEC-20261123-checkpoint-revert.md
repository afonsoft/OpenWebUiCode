# SPEC-20261123-checkpoint-revert — checkpoints de conversa por step com revert

**Status**: Proposta

## Motivação

Portado do Devin CLI (*"Partial revert to step"*, *"only expanded-chain IDs
are valid revert targets"*, *"Compaction cancelled by revert"*): a sessão
mantém checkpoints por step; revert restaura **conversa + arquivos** a um
ponto anterior — a forma mais forte de "desfazer" num loop agêntico.

Hoje uma run que segue direção errada só tem `cancel`: não dá para voltar a
"antes da edição X" sem perder tudo. Temos `ChatRunEvents` persistidos e
branching de chat — mas nenhum revert atômico conversa+arquivos.

## Design

- **Checkpoint por step**: ao fim de cada `step_finished`, o run grava um
  checkpoint `{ chatId, runId, stepIndex, messageChainHead, gitShadowRef }`
  — `gitShadowRef` é um commit leve num ref shadow
  (`refs/openwebui/checkpoints/<chatId>/<stepIndex>`) criado via
  `git stash create`/`commit-tree` **só quando o workdir é git**; workdir
  não-git → checkpoint só de conversa.
- Nova tool `revert_step { stepIndex | checkpointId }` + evento de UI:
  - valida que o alvo está na cadeia atual do chat (equivale ao
    "expanded-chain IDs" do Devin);
  - conversa: marca mensagens > stepIndex como `reverted=true` (soft-delete,
    preserva audit — nunca deleta histórico);
  - arquivos: `git checkout <shadowRef> -- .` (restaura arquivos tracked;
    untracked criados depois ficam — igual ao "arquivos novos não somem" do
    stash); registra `revert_applied` no stream com diff-stat.
- Interações: cancela compaction/approval pendente do trecho revertido
  (*"Compaction cancelled by revert"*); runs em andamento rejeitam revert
  com mensagem clara (só revert em run finalizada/pausada).
- Limite: mantém os últimos N=20 checkpoints por chat; mais antigos expiram
  (GC no sweep de órfãos).
- Escopo consciente: **não** reverte DB, efeitos externos (emails, pushes)
  nem runs paralelas — o evento deixa isso explícito ao modelo.

## Testes

- `Checkpoint_StepGravado_RevertRestaura` — edit em step 2, revert pro
  checkpoint do step 1 → arquivo volta, mensagens do step 2 viram reverted.
- `Revert_AlvoForaDaCadeia_Rejeita` — step de outro branch → erro claro.
- `Revert_CancelaAprovacaoPendente` — approval asked no trecho revertido
  sai de `_pending`.
- `Revert_NaoGit_SoConversa` — workdir sem git: mensagens revertem, arquivos
  ficam (evento reporta o modo parcial).
- `Checkpoint_GC_Limita20` — 25 steps → só os 20 mais recentes revertíveis.
