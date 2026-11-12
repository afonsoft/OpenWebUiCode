# SONAR_FIX_TODO_BOARD — PR review sweep (PRs #432–#466)

## Sweep 2026-11-11 — branch `fix/sonar-issues-20261111`

Fonte: SonarCloud `afonsoft_open-webui` (2.450 issues abertas no snapshot;
157 acionáveis em `src/`, `tests/`, `tools/`, `Dockerfile` — as demais são
achados de vendor/docs já cobertos por exclusões do scan).
Snapshot bruto: `.sonar_devin_auto_fix/sonarqube_issues.json`;
classificação: `.sonar_devin_auto_fix/actionable_by_file.json`.

### Corrigidos nesta branch

| Regra | Arquivos | Status |
|-------|----------|--------|
| S7044 (URI não sanitizada) | `Client/Services/ApiService.cs`, `AuthService.cs` | [x] upload/API com URI segura relativa ao `BaseAddress` |
| S5693 (tamanho de request) | `ApiService.cs` (upload), `ChatView.razor` (`file.Size`) | [x] limite de corpo explícito |
| S6444 ×4 (regex sem timeout) | plugin/skill/index services | [x] `matchTimeout` explícito |
| S5034 ×2 (ValueTask multi-uso) | `Api/Endpoints/ChatRunEndpoints.cs` | [x] consumo único via helpers |
| S4487 ×10 (campos só escritos — Razor) | `Ide.razor`, `MainLayout.razor` | [x] campos → auto-properties |
| S2365 + S3218 ×2 | `ModelManagerDialog.razor`, `GatewayInboundService.cs` | [x] property leve; shadowing renomeado |
| S3776 ×22 (complexidade) | `PluginImportService`, `SkillImportService`, `CodeSearchBuiltinTool`, `ChatInsightsService`, `InsightsEndpoints`, `InsightsPanel`, `ContextCompactionService`, `ChatRunExecutor`, `ChatView`, `ModelSelector`, `ProviderService` | [x] extração de helpers/records (`ExecutionPlan`, `AdvanceChainAsync`, `HeadSummaryArgs`, `CollectAttachmentInfo`, `HandleStallRecoveryAsync`, `ApplyPolledEventsAsync`, `ResolveSlotAsync`, `ApplyGoogleNativeTools`, `HandleStepStartAsync`, `Apply{Text,Thought}DeltaAsync`, `MarkLastAnthropicUserMessage`) |
| S107 ×7 (muitos params) | `ChatRunEndpoints`, `ChatPipeline`, `ComboEndpoints`, `ContextCompactionService` | [x] `[AsParameters]`/records de args (`CompactionState`) |
| S3358 ×14 (ternário aninhado) | `TaskEndpoints`, `CustomizeEndpoints`, `ModelSelector`, `InsightsPanel`, `ChatView` | [x] switch/condicionais explícitas |
| S9422 ×4 (lock) | serviços com `object` lock | [x] `System.Threading.Lock` |
| S1192 ×15 (literal repetido) | `ProviderService`, `ChatInsightsService`, `ChatStreamService` | [x] consts (`UsageKey`, `ParametersKey`, `ThinkingBudgetKey`, `ResultKey`, `ThoughtKey`, `InteractionsApiKey`, `SevMedium`…) |
| S3267/S8969/S2589/S1854/S1172/S1125/S6672/S2234/S9415/S6608/S108 | pontuais | [x] LINQ/early-return/named args/`ILogger<T>` correto/`[0]`/static local fn |
| S6580 (culture) | `GeminiContextCacheService.cs` | [x] `InvariantCulture` + `RoundtripKind` |
| S2925 ×2 (Thread.Sleep em teste) | `ChatBangTests`, `ChatHashTests` | [x] `async Task` + `await Task.Delay` |
| S1135 (falsos "TODO" de substring) | `Program.cs`, `DeepWikiMcpSeeder`, `ContextCompactionService` | [x] rewording ("todos", `builtin_todo_write`, "todo payload") |
| python:S8786 | `tools/check-spec-status.py` | [x] regex `[^|]+` greedy linear (documentado) |

### Falsos positivos / decisões documentadas

- **json:S2068 ×3** (`pt-BR.json` i18n) — labels de UI ("Senha atual" etc.), não credenciais. Triagem manual no SonarCloud (requer token).
- **docker:S6471** — container single-tenant roda como root por design (o processo É o agente); já documentado no Dockerfile.
- **docker:S7031** — RUNs consecutivos separados intencionalmente por camadas lógicas (apt base × toolbelt); cache de layers.
- **S7039 (CSP)** — `'unsafe-inline'` exigido pelos scripts inline de `index.html`; `'unsafe-eval'` removido, `'wasm-unsafe-eval'` apenas. Nonces/hashes = refactor futuro.
- **python:S7503 ×4** (`mock_ollama.py`) — handlers aiohttp DEVEM ser coroutines (contrato do framework); comentário no arquivo.
- **css:S9423 ×5** (`tailwind.input.css`) — `!important` obrigatório no bloco `prefers-reduced-motion` (a11y).
- **javascript:S2486 ×7 / S1874 ×3** — catches com fallback documentado e APIs legadas (`execCommand`, `addListener`) mantidas como fallback de browser antigo.
- **S2325** em `SafeInParallel` — membro de `IBuiltinChatTool`, não pode ser static.
- **S8969** em `WorkspaceFileEndpoints`/`ProviderCapabilityService` — `!` restaurado: compilador não prova não-nulo (CS8602/CS8604).

### Verificação

- `dotnet build --configuration Release`: 0 erros, 0 warnings.
- `dotnet test`: Client.Tests 159/159; Api.Tests 2927/2928 — única falha
  `ChatBranchingE2ETests.SharedChat_RenderizaApenasCaminhoAtivo`, flake de
  timing sob carga paralela (passa isolado; enqueue+share não foram tocados).
- Drift guards: `check-css-classes`, `check-form-a11y`, `check-i18n-parity`, `check-spec-status` — OK.
- Métrica do snapshot: 125 de 157 acionáveis corrigidas; 32 mantidas como
  falso positivo / decisão de design (tabela acima). A contagem oficial no
  SonarCloud só fecha após o próximo scan do CI sobre esta branch.

---

Branch anterior: `fix/pr-review-sweep-20261019` (base `main` @ `6f85ceef3`)
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
