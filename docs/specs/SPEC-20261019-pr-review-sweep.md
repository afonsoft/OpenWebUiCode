# SPEC-20261019-pr-review-sweep

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `pr-review-sweep` |
| Type | `Fix` |
| Stack | `.NET` (ASP.NET Core + EF Core + Blazor WASM) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `fix/pr-review-sweep-20261019` |
| Status | `Draft` |
| Parent | auditoria dos últimos 30 PRs (#432–#466) — comentários SonarCloud, Devin Review e GitHub Advanced Security/CodeQL |

## 1. User Story

**As a** mantenedor do fork
**I want** corrigir os achados dos bots de review que ainda persistem no `main` (após dedupe com fixes posteriores)
**So that** o repo zera pendências de qualidade/segurança reportadas sem duplicar trabalho já feito.

## 2. Escopo — GAPs CONFIRMADOS no `main`

### A. SonarCloud (code smells — 32 issues coletadas; todas re-verificadas por arquivo/linha)

**RF-A1 `ProviderService.cs` — literais repetidos (S1192 ×11) + ternário aninhado (S3358):**
extrair constantes para `"message"`, `"error"`, `"choices"`, `"tool_calls"`, `"function"`, `"arguments"`, `"thinking"`, `"delta"`, `"index"`, `"candidates"` e reutilizar constante existente p/ `"content"`; desaninhar o ternário em `MidStreamProviderError` (~:565-580).

**RF-A2 S107 — métodos com 8 parâmetros:**
`ModelEndpoints.ListCatalogAsync` (:49) e `RepoSkillEndpoints.ExpandCommandAsync` (:152). Agrupar em record/DTO de contexto ou reduzir via serviço.

**RF-A3 S1135 — comentários com "TODO" (falso positivo semântico, mas o scanner conta):**
`PolicyService.cs:13` e `ChatPipeline.cs` (~:492, literal `builtin_todo_write`/comentário). Reformular comentário sem a palavra-gatilho.

**RF-A4 `SettingsModal.razor`:**
S3776 `SelectTab` complexidade 16 (:1411); S3358 ternário aninhado ext-skills (:926-928); S3267 `foreach` → `.Select(m => m.Id)` (:983-986).

**RF-A5 `ChatView.razor`** — S3776 `SelectPrompt` complexidade 18 (~:1501). Extrair branches builtin/prompt/skill/command.

**RF-A6 `ChatRunDispatcher.cs`** — S3776 `SweepOrphansAsync` 18 (:94); S108 catch vazio (:259).

**RF-A7 `ChatRunExecutor.cs`** — S1192 `"system"` ×5 (:268 região); S3358 ×2 (~:1026-1027 `EffectiveModeAsync`); S1172 parâmetro `user` não usado em `SpawnCommandSubagentAsync` (:1058, callsite :141). S125 (código comentado :409 no PR-head) — não encontrado no main atual → REJEITADO como já resolvido.

**RF-A8 `RunRecoveryPlanner.cs`** — S3776 `Replay` complexidade 17 (:52).

**RF-A9 `CommandExpansion.cs`** — S3776 `Interpolate` 23 (:78); S127 ×2 modificação de contador de loop (:90, :108) → reescrever como `while`/indexação explícita.

### B. GitHub Advanced Security / CodeQL (15 alerts)

**RF-B1 `AgentLoopAutoChainTests.cs`** — assignments inúteis `var run`/`var runs` nunca lidos (:306/:519/:570 no PR-head; equiv. ~:312/:394/:465/:519/:569 atuais — corrigir todos os não-lidos).

**RF-B2 `PolicyService.cs:58`** — `foreach` com filtro implícito → `.Where(...)`.

**RF-B3 `BrowserSessionService.cs:87`** — `ActionTimeoutMs * 2` (int×int→float) → `ActionTimeoutMs * 2f` (overflow/precision).

**RF-B4 `TesseractOcr.cs`** — `matched` HashSet nunca lido (:72) → remover; `foreach` → `.Select(line => line.Split('\t'))` (:96 região).

**RF-B5 `MessageBubbleSourceViewTests.cs:47`** — `HttpClient` → `using var`.

**RF-B6 `ChatRunDispatcher.cs`** — catch vazio :260 → log/swallow justificado; catch genérico :305 → filtro `when (ex is not OperationCanceledException)`.

**RF-B7 `RunEventWriter.cs:73`** — catch genérico → filtro OCE.

**RF-B8 `RunRecoveryPlanner.cs:128`** — `foreach` → `.Select(...)`.

**RF-B9 `RepoCommandServiceTests.cs`** — catches vazios :40-41 → log via `TestContext`; `HttpResponseMessage` :307 — criado e retornado por `FakeHandler.SendAsync` (ownership do caller) → falso positivo, documentar.

### C. Devin Review (bugs confirmados ainda no `main`)

**RF-C1 Auto-chain perde worktree (PR#459 BUG-1):** `BuildToolContextAsync` força worktree p/ qualquer `ParentRunId`; filhas same-chat devem reutilizar workspace/worktree da mãe (distinguir via `meta.autoChain`).

**RF-C2 Notificação contaminada (PR#459 BUG-2):** `NotifyParentChatAsync` grava "subtarefa concluída" no próprio chat quando parent está no mesmo chat → pular quando `parentRun.ChatId == run.ChatId`.

**RF-C3 `agent.steps` não herdado (PR#459 BUG-3):** copiar `r:{runId}:agent.steps` (e demais chaves `agent.*` por run) para a filha em `TryChainContinuationAsync`.

**RF-C4 Continuações espúrias (PR#459 BUG-4 + PR#458 BUG-4):** `HitStepCap=true` no último round sem trabalho real gera chains desnecessárias; e nudge de pendingTodos injetado no `lastStep` é inexequível. Gatear: só chain quando `PendingTodos > 0` ou (`PendingTodos == -1` e houve tool calls — `Messages.Count > 0`); não injetar nudge em `lastStep`.

**RF-C5 Retry de tool mutável (PR#458 BUG-1):** retries automáticos no executor só para tools idempotentes (`ToolExecutor.IsMutable`/`IsSafeInParallel` já existem) — mutável falha → devolve erro ao modelo na 1ª exceção.

**RF-C6 Conteúdo intermediário perdido (PR#458 BUG-2):** antes do nudge, anexar `step.Content` como mensagem assistant no histórico do loop e no checkpoint; decidir agregação no retorno final.

**RF-C7 `ask_user` livre respondida pelo agente (PR#458 BUG-3):** sob presets autônomos, auto-responder só perguntas com opções; perguntas sem opções que pedem input só-do-usuário (classificação por keywords: senha/password/token/credential/username/email/sudo...) continuam indo ao dono via `WaitAsync`.

**RF-C8 `.Result` em `SettingsModal.razor`** — viola regra async; `:1448-1451` (e bloco ~:2188-2264) → `await`/`Task.WhenAll` results.

**RF-C9 `CoverageRound2Tests.cs`** — `WaitForExit` síncrono → `WaitForExitAsync`; `RequireGit` deve `Assert.Ignore` quando `git` ausente (Devin PR#452 BUG-1 + ANALYSIS-2).

**RF-C10 Checkpoint sem `PendingTodos` (PR#458 ANALYSIS-2):** persistir contagem em `RunLoopCheckpoint`/snapshot e restaurar no resume.

**RF-C11 Prompt do system vs overrides `prompt` (PR#458 SEC-1):** ajustar redação da diretiva always-allow para reconhecer tools marcadas sempre-perguntar pelo dono.

## 3. Fora de escopo / rejeitados / inconclusivos

- **Rejeitados (já resolvidos ou FP):** S125 ChatRunExecutor:409 (código comentado ausente no main); `HttpResponseMessage` em `FakeHandler.SendAsync` (ownership transferida ao caller); `DisposeAsync` coverage exclusion (análise, decisão de escopo do PR#452); avisos "subtarefa" de processo dos PRs #453/#454 (workflows já mergeados).
- **Análises Devin sem ação (documentar):** UI não anexa stream da filha (#459 A-1, enhancement); settings freeze herança (#459 A-2, decisão de produto); nudge em chats não-autônomos (#458 A-1, comportamento intencional — registrar justificativa); RF-004 admin UI adiado (#458 A-3).
- **Devin automation comments** (#460/#456/#455): controle de monitoramento, não-defeito.

## 4. Plano de testes

- Novos testes NUnit para: chain sem-trabalho não dispara; `agent.steps` herdado; mesmo-chat não força worktree; `NotifyParent` pulado p/ same-chat; ask_user livre→usuário em always-allow; retry só-idempotente; pendingTodos persistido no checkpoint.
- Atualizar testes existentes afetados por refactor S3776/S107/S127.
- `dotnet build` + `dotnet test` + drift guards (`check-css-classes`, `check-form-a11y`, `check-i18n-parity`, `check-spec-status`) + regenerar tailwind.css se classes .razor mudarem.

## 5. Artefatos do fluxo sonarqube-autofix

- `SONAR_FIX_TODO_BOARD.md` — 32 issues mapeadas → RF.
- `SONAR_FIX_REVIEW_NOTES.md` — classificação confirmado/rejeitado/duplicado/FP com evidência (arquivo:linha, regra, PR origem).
- `.gitignore` → `.sonar_devin_auto_fix/**`.
