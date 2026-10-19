# SONAR_FIX_REVIEW_NOTES — fix/pr-review-sweep-20261019

Notas de revisão do sweep de achados Sonar/CodeQL/Devin (PRs #432–#466).

## Decisões de implementação

### ProviderService (RF-A1)
Constantes de chaves JSON (`MessageKey`, `ErrorKey`, `ChoicesKey`, `ToolCallsKey`,
`FunctionKey`, `ArgumentsKey`, `ThinkingKey`, `DeltaKey`, `IndexKey`, `CandidatesKey`,
`TextKey`, `PartsKey`) centralizam literais repetidos em parsers Ollama/OpenAI/Gemini.
`MidStreamProviderError` virou if/else explícito — mesma classificação de status.

### Endpoints S107 (RF-A2)
`ListCatalogAsync` e `ExpandCommandAsync` recebem records internos
(`ModelCatalogDeps`, `ExpandCommandDeps`) via `[AsParameters]` — projeto não usa
Request Delegate Generator, então o binding por objeto funciona em minimal APIs.

### SettingsModal (RF-A4 + RF-C8)
`SelectTab` delega por switch expression para `Load*TabAsync` por aba. Todos os
`.Result` pós-`Task.WhenAll` viraram `await` — incluindo o `LoadAsync` (version,
api key, memories, webhook, connections, admin config, capabilities, audio). O
mesmo padrão foi corrigido em `ChatView.OnInitializedAsync` e `Sidebar.LoadAsync`
(mesma classe de violação da regra global).

### Auto-chain (RF-C1/C2/C3/C4)
- Continuação same-chat (`meta.autoChain`) reutiliza o worktree da mãe — sub-runs
  delegadas (chat filho) continuam isoladas.
- `NotifyParentChatAsync` pula quando `parentChatId == run.ChatId` — marcador
  "Subtarefa concluída" só existe em chat pai de subtarefa delegada.
- `r:{id}:agent.steps` copiado para a filha junto ao enqueue.
- Gate: chain só com `PendingTodos > 0` ou (`== -1` e tool calls executadas);
  nudge de pending-todos suprimido no `lastStep` (round seguinte não teria tools).

### ask_user (RF-C7)
Preset autônomo auto-responde perguntas com opções e perguntas livres comuns;
perguntas livres cujo texto pede dado só-do-dono (`RequiresOwnerInput`:
senha/password/token/secret/credential/username/email/sudo/login, pt/en) seguem
para `approvals.WaitAsync` — chegam ao usuário mesmo em always-allow.

### Retry (RF-C5)
`ExecuteCallAsync`: `ToolExecutor.IsMutable` → `maxAttempts = 1`. Exceção de tool
mutável volta ao modelo na 1ª falha — reexecutar duplicaria o efeito.

### Checkpoint (RF-C10)
`RunLoopCheckpoint` carrega `PendingTodos`; o resume seed reidrata a contagem
para o auto-continue não perder o sinal após restart.

## Riscos conhecidos

- `ProviderService`: 128 substituições de literais — cobertura dos parsers via
  testes existentes (ProviderService/ToolStreaming). Revisar diff com cuidado.
- `ChatRunExecutor.ParamStr`/`EffectiveModeAsync`: reescrita de ternários —
  semântica preservada, mas paths de system-prompt dependem delas.
- Testes de timing (`_firstDelayMs`, delays de 1.5–2s) seguem o padrão já
  existente na fixture para evitar flakiness de race.

## Rejeitados (evidência)

- `ChatRunExecutor` S125: o bloco comentado do PR-head não existe no `main`.
- `FakeHandler.SendAsync` `HttpResponseMessage`: ownership passa ao `HttpClient`
  chamador; disposal seria uso-após-dispose — documentado, não corrigido.
- Cobertura de `DisposeAsync` excluída no PR #452: decisão explícita de escopo.
