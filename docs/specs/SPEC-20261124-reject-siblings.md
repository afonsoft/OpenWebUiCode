# SPEC-20261124-reject-siblings — reject cancela aprovações irmãs pendentes

**Status**: Completed

## Motivação

Portado do `anomalyco/opencode` (dev): lá o `reject` nega o request **e
todos os outros permission requests pendentes da sessão**. Aqui,
`ChatRunApprovals.Resolve` (`src/OpenWebUI.Api/Runs/ChatRunApprovals.cs:143`)
resolve apenas a call `runId:callId` — com `MaxParallelTools=4` um grupo
paralelo pode deixar 3 prompts órfãos depois do usuário rejeitar 1.

## Design

- `Resolve(runId, chatId, callId, approved:false, …)` continua resolvendo
  a call alvo normalmente (com a `message` de instrução, se houver).
- Após resolver a alvo, quando `approved == false`: nega as demais
  entradas de `_pending` **do mesmo `chatId`** cujo `AskedPayload` seja
  de aprovação de tool — `question_asked` (ask_user) e `plan_exit`
  permanecem vivos (rejeitar um `git push` não deve matar uma pergunta
  aberta ao usuário). O discriminador é o tipo do payload serializado
  no `approval_asked` vs `question_asked`.
- Cada irmã negada resolve `ApprovalResult(false, null)` — o tool loop
  devolve resultado de falha por call, então o modelo vê quais calls
  foram rejeitadas individualmente.
- `remember` nunca se aplica a reject (já é o caso — só grava em
  `approved && remember`).
- Endpoint/GET de pendências não muda: as irmãs simplesmente saem de
  `_pending` e o `PendingAsked` deixa de devolvê-las.

## Testes

- `Resolve_Reject_NegaIrmasDaMesmaChat` — 3 pendings no mesmo chat,
  reject na 1ª → todas resolvem `false`; `question_asked` no mesmo chat
  sobrevive.
- `Resolve_Reject_NaoTocaOutroChat` — pending em chat B intacto.
- `Resolve_Approve_NaoCascata` — approve só resolve a alvo.
- E2E de grupo paralelo: 4 calls ask → reject 1 → as 3 viram falha sem
  estacionar.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/772
- Branch: feature/agent-20261124-opencode-gaps
