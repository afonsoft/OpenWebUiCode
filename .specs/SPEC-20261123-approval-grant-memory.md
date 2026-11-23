# SPEC-20261123-approval-grant-memory — grants persistentes de aprovação

**Status**: Proposta

## Motivação

Portado do Devin CLI (`local_tools_approval_mode`, *"Auto-approved tool via
prior grant"*): quando o usuário aprova uma tool call, o grant é **lembrado**
— a próxima call com a mesma assinatura não pergunta de novo. O usuário não
é re-incomodado por operações que já autorizou.

Hoje o `ChatRunApprovals` resolve cada pendência individualmente: aprovar
`exec dotnet test` 5 vezes = 5 prompts. O preset `always-allow` existe, mas
é tudo-ou-nada por run — não há "aprovar esta classe de ação".

## Design

- Novo `ApprovalGrantStore` (scoped, em memória por chat — persiste durante
  a vida do chat; persistência em DB é fase 2):
  `record Grant { string Signature; DateTime GrantedAt; string Scope }`.
- No `Resolve(runId, chatId, callId, approved:true, …)`, grava a assinatura
  da call — o mesmo `CallSignature` do `PermissionRuleset`
  (`name:argPattern`: `exec:dotnet test*`, `write:/src/**`, `delegate_task:agent:x`).
- Antes de criar `approval_asked`, o gate consulta o grant store: assinatura
  que casa (glob igual às regras) com grant ativo → auto-aprova e emite
  evento `approval_auto_granted` no stream (transparência — o usuário vê
  que foi auto-aprovado por grant anterior).
- UI/comando de revogação: listar grants do chat e `revoke` individual ou
  `clear` total. Sem revoke → grant expira ao fim do chat (escopo conservador
  inicial; `remember:true` no prompt de aprovação já existente passa a
  gravar o grant).
- Respeita deny rules: grant nunca vence `deny` do `PermissionRuleset` —
  a consulta é `grant && !denied` (ordem: deny rule > grant > ask).
- Não se aplica a `question_asked`/`plan_exit` — só aprovações de tool.

## Testes

- `Grant_AprovouUmaVez_SegundaAutoAprova` — approve `exec:dotnet*`,
  segunda call `exec dotnet build` → auto-approve sem prompt + evento.
- `Grant_DenyVenceGrant` — deny rule `exec:rm*` + grant `exec:*` → call
  `exec rm x` ainda pergunta.
- `Grant_Revoke` — revoke da assinatura → próxima call volta a perguntar.
- `Grant_OutroChatNaoVaza` — grant do chat A não auto-aprova chat B.
- `Grant_Reject_NaoGrava` — reject não grava grant (espelha `remember`).
