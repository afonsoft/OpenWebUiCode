# SONAR_FIX_TODO_BOARD — PR review sweep (PRs #432–#466)

Branch: `fix/pr-review-sweep-20261019` (base `main` @ `6f85ceef3`)
SPEC: `.specs/SPEC-20261019-pr-review-sweep.md`
Fontes: SonarCloud (32 issues) + GitHub Advanced Security/CodeQL (15 alerts) + Devin Review (9 bugs + 8 analysis + 3 automation).

## Status legend

- `[x]` corrigido nesta branch
- `[~]` corrigido parcialmente / verificado como já resolvido
- `[r]` rejeitado (falso positivo / já resolvido upstream / fora de escopo)

## RF-A — SonarCloud

| ID | Regra | Arquivo | Status |
|----|-------|---------|--------|
| RF-A1 | S1192 ×11 + S3358 | `Infrastructure/Services/ProviderService.cs` | [x] constantes `MessageKey`…`PartsKey`; `MidStreamProviderError` em if/else |
| RF-A2 | S107 ×2 | `Api/Endpoints/ModelEndpoints.cs`, `RepoSkillEndpoints.cs` | [x] `[AsParameters]` records `ModelCatalogDeps`/`ExpandCommandDeps` |
| RF-A3 | S1135 ×2 | `Infrastructure/Services/PolicyService.cs`, `Api/Completions/ChatPipeline.cs` | [x] rewording sem "TODO" |
| RF-A4 | S3776 (16) + S3358 + S3267 | `Client/Components/SettingsModal.razor` | [x] `SelectTab` → switch + métodos por aba; `ExtSkillStatus`; `Select(m => m.Id)` |
| RF-A5 | S3776 (18) | `Client/Components/ChatView.razor` | [x] `SelectPrompt` dividido em builtin/prompt/skill/command |
| RF-A6 | S3776 (18) + S108 | `Api/Runs/ChatRunDispatcher.cs` | [x] `ClassifyOrphan` extraído; heartbeat OCE com log trace |
| RF-A7 | S1192 `"system"` ×5 + S3358 ×2 + S1172 + S125 | `Api/Runs/ChatRunExecutor.cs` | [x] const `RoleSystem`; `ParamStr`/`EffectiveModeAsync` sem ternário aninhado; param `user` removido; S125 rejeitado (comentário já não existe) |
| RF-A8 | S3776 (17) | `Infrastructure/Services/RunRecoveryPlanner.cs` | [x] `Replay` dividido (`ApplyEvent`) |
| RF-A9 | S3776 (23) + S127 ×2 | `Infrastructure/Services/CommandExpansion.cs` | [x] `Interpolate` `for`→`while` + handler `$` extraído |

## RF-B — CodeQL / GitHub Advanced Security

| ID | Achado | Arquivo | Status |
|----|--------|---------|--------|
| RF-B1 | Useless assignment | `tests/.../AgentLoopAutoChainTests.cs` | [x] assignments não lidos removidos |
| RF-B2 | foreach → Where | `Infrastructure/Services/PolicyService.cs:58` | [x] LINQ `LastOrDefault` (último deny vence) |
| RF-B3 | int×int→float | `ChatTools/Browser/BrowserSessionService.cs:87` | [x] `* 2f` |
| RF-B4 | HashSet morto + loop→Select | `ChatTools/Browser/TesseractOcr.cs` | [x] `matched` removido; `Select(line => line.Split('\t'))` |
| RF-B5 | Dispose ausente | `Client.Tests/MessageBubbleSourceViewTests.cs:47` | [x] `_http` em campo + `[TearDown]` (ownership com bUnit) |
| RF-B6 | empty catch + generic catch | `Api/Runs/ChatRunDispatcher.cs:259/305` | [x] log trace + filtro `is not OperationCanceledException` |
| RF-B7 | generic catch | `Infrastructure/Services/RunEventWriter.cs:73` | [x] filtro OCE |
| RF-B8 | foreach → Select | `RunRecoveryPlanner.cs:128` | [x] `.Select(...)` |
| RF-B9 | empty catches | `tests/.../RepoCommandServiceTests.cs:40-41` | [x] diagnóstico via `TestContext.Out`; `HttpResponseMessage:307` documentado como falso positivo (ownership do caller) |

## RF-C — Devin Review

| ID | Achado | Status |
|----|--------|--------|
| RF-C1 | Same-chat auto-chain perdia worktree/edições não commitadas | [x] filhas same-chat reutilizam worktree da mãe (`BuildToolContextAsync`) |
| RF-C2 | `NotifyParentChatAsync` gravava no próprio chat | [x] early return quando `parentChatId == run.ChatId` |
| RF-C3 | `agent.steps` não herdado pela filha | [x] cópia da chave kv em `TryChainContinuationAsync` |
| RF-C4 | Chain espúria / nudge inexequível no `lastStep` | [x] gate `PendingTodos>0` ou (== -1 e ToolMessages>0); nudge exige `!lastStep` |
| RF-C5 | Retry repetia tools mutáveis (até 3×) | [x] `IsMutable` → `maxAttempts = 1` |
| RF-C6 | Conteúdo intermediário sumia na continuação | [x] `step.Content` persistido antes do nudge |
| RF-C7 | `ask_user` livre auto-respondida em always-allow | [x] `RequiresOwnerInput` (keywords senha/token/…) mantém `WaitAsync` |
| RF-C8 | `.Result` após `Task.WhenAll` | [x] `SettingsModal`, `ChatView`, `Sidebar` → `await` |
| RF-C9 | `WaitForExit` síncrono / `RequireGit` falhando | [x] `WaitForExitAsync` + `Assert.Ignore` em `CoverageRound2Tests` |
| RF-C10 | Checkpoint sem `PendingTodos` | [x] `RunLoopCheckpoint`/resume seed propagam a contagem |
| RF-C11 | Prompt always-allow ambíguo | [x] distingue permissões automáticas de inputs só-do-dono |

## Rejeitados / documentados

- S125 `ChatRunExecutor:409` — código comentado não existe mais no `main`.
- `HttpResponseMessage` em `FakeHandler.SendAsync` — ownership transferida ao caller (design de fake em teste).
- `DisposeAsync` fora de cobertura — decisão de escopo do PR #452.
- 5 análises Devin (UI da filha, herança de settings, nudge fora de build, RF-004 adiado) — observações de design/process, não bugs confirmados.
- 3 comentários de automação + 2 notas de workflow (#453/#454) — sem ação de código.

## Testes de regressão adicionados

`tests/OpenWebUI.Api.Tests/AgentLoopAutoChainTests.cs`:

- `AutoChain_CapSemTrabalho_NaoEncadeia` (RF-C4)
- `AutoChain_NaoGravaMarcadorNoProprioChat` (RF-C2)
- `AutoChain_FilhaHerdaAgentSteps` (RF-C3)
- `AskUser_SenhaEmAlwaysAllow_VaiAoUsuario` (RF-C7)
- `AskUser_PerguntaComum_RespostaAutonoma` (RF-C7 contrapartida)
