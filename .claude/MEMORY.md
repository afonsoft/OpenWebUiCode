# MEMORY.md — Estado Cross-Session

> Nunca armazenar PII, secrets ou credenciais. Verificar fatos contra o código antes de usar.

## Decisões Técnicas

| Data | Decisão | Racional | Alternativas descartadas |
|---|---|---|---|
| 2026-10-01 | Tailwind v4 estático commitado | mesma paleta/tema do upstream sem Node no build | Node no pipeline .NET |
| 2026-10-01 | Clean Architecture em 5 projetos | separação Domain/App/Infra/Api/Client | monolito único |
| 2026-10-01 | `MapStaticAssets` para WASM | resolve `#[.{fingerprint}]` | UseStaticFiles + fallback |
| 2026-10-13 | Binding de repo estrito por chat + worktree por conversa (SPEC-15) | conversa nova sem repo; build só com repo; isola escrita entre chats | fallback ao binding global, checkout compartilhado |
| 2026-10-27 | Scratch workspace lazy por chat sem repo (SPEC-20261027, PR #548) | todo chat/build/plan sem repo trabalha em `workspaces/{u}/scratch/{chatId}` com git init; publish empurra para URL/remoto sem gravar `origin` (token nunca toca `.git/config`) | pasta na criação do chat (descartada: lazy evita lixo em conversa pura); origin persistido (descartado: vaza credencial) |
| 2026-11-25 | 404 do `workspace/repo/tree` mantido server-side; ruído eliminado no cliente (SPEC-20261125, PR #788) | diretório inexistente ≠ vazio — 404 é o contrato correto; o problema era o probe incondicional do `ChatWorkspacePanel` a `.agents/memory` | 200 + `exists:false` (descartada: mudança de contrato sem ganho real) |

## Débito Técnico

| Item | Impacto | Prioridade |
|---|---|---|
| Pendências de paridade (RAG, OAuth, voice, realtime) | ver `docs/MIGRACAO-DOTNET.md` | conforme roadmap |
| `check-form-a11y.py` não detecta `<label>` órfão (sem `for` e sem controle interno) — só `for`→`id` quebrado | warnings a11y passam pelo guard | média |
| Telemetria dedicada de skill (descoberta/seleção/leitura) — hoje só logs genéricos de tool call | auditoria de uso de skill depende de reconstrução manual | baixa |
| Sem E2E real de BFCache (headless não emula `e.persisted`) | reconciliação F4 validada só por revisão | baixa |

## Lições Aprendidas

| Contexto | Erro | Como evitar |
|---|---|---|
| Boot WASM | placeholder não resolvido → app muda | smoke test deve validar boot, não só HTML 200 |
| Tarefas LLM | fallback "New Chat" sobrescrevia título | endpoint retorna erro; cliente preserva estado |
| Modo do agente | default "build" sem repo quebrava o gate de workspace | default "chat" + modo efetivo com downgrade quando não há binding |
| Prompt do agente | `AutonomyPrompt` proibia push sem confirmação → agente parava e devolvia link "crie o PR" | bloco dedicado por modo efetivo (`AgentModeContributor` build) + carve-out: commit/push/PR na branch de trabalho fazem parte do loop de entrega; aprovação humana só p/ merge em branch protegida, deploy e APIs externas |
| Skills do agente | `builtin:skill` estava nos 106 toolIds mas o system prompt não tinha catálogo — LLM nunca a chamava | tool ofertada não basta: catálogo compacto (nome+descrição+como invocar) via `SkillCatalogContributor` (Order 71); auditar os 4 estados — descoberta → ofertada → selecionada → aplicada |
| Probes de diretório no cliente | `ChatWorkspacePanel` sondava `.agents/memory` sem verificar existência → 404 no console a cada load de chat | descobrir dirs via tree da raiz (depth≥2) e só consultar paths existentes; não mudar contrato do endpoint para esconder ruído |
| Stream do client | OCE do stall-watchdog caía em `catch (Exception)` → log "stream da run caiu" como se fosse falha | `catch (OperationCanceledException)` dedicado entre o rethrow filtrado e o catch genérico — cancelamento esperado não é erro |
| BFCache × SignalR | `onclose` não dispara em página congelada → WS morto ao voltar do bfcache | `pageshow(e.persisted)` → `[JSInvokable]` de reconciliação (reconectar + recarregar chat + reatachar run); `ConnectAsync` exige hub vivo (Connected/Connecting/Reconnecting), não só mesmo token |

## Handoff 2026-10-27 (fila do composer + resiliência de stream)

- **Fila de mensagens do composer** (`ChatView.razor`): enquanto `_streaming`, Enter/botão envia para `_messageQueue` (texto + snapshot de anexos/mentions); painel cinza acima do composer com edição inline (lápis), force-send (↵ para a run e prioriza o item via `_forceQueueId`) e remover (×). `DrainMessageQueueAsync` nos finally dos 3 caminhos de stream + `HandleRunCompleted` despacha itens como runs sequenciais; `_drainingQueue` barra reentrância (o finally interno retorna e o while externo continua).
- **Links em nova aba**: `MarkdownService` injeta `target="_blank" rel="noopener noreferrer"` pós-sanitize em `<a href>` não-`#` (raw string com conteúdo terminando em `"` precisa delimitador de 4+ aspas — bug pego por teste).
- **Resiliência**: stall watchdog 60s→35s (2 heartbeats de 15s + margem); `GetActiveRunAsync` na carga do chat ganhou retry único de 1s para falhas transitórias de fetch (proxy CF/nginx em produção); `StopStreaming` do caminho legado (playground) agora cancela `_cts` — antes era no-op.
- **AgentRunEventBus**: eventos terminais (`run.completed/stopped/failed`) usam `observers.timeout_ms` (default 90s) — observers pós-run que chamam LLM (InstinctExtractor) estouravam o timeout de 5s dos interceptors e morriam com TaskCanceledException em todo fim de run.
- Produção (`open-webui.afonsoft.dev`) é Browser → Cloudflare → nginx (aaPanel, `proxy_pass localhost:3032`, timeouts 1200s) → Kestrel; SSE tem flush+heartbeat+`X-Accel-Buffering: no` server-side — travadas residuais vinham de fetch transitório de borda, não de buffering de origem.

## Handoff 2026-10-27 (scratch-workspace entregue)

- **#547 scratch-workspace**: entregue via PR #548 (merge `b2889d6`), issue fechada, SPEC arquivada em `docs/specs/`. Deploy local feito (container `openwebui`, :3032, healthy, HTTP 200, endpoints `/workspace/scratch` + `/workspace/publish` respondendo 401 sem auth).
- Ciclo de CI do #548 ensinou: provider real em modo chat entra em loop de tools se o announce incluir tools de build (`worktree_*`, `lsp_*`, `specs`) ou se o prompt convidar exploração — `BuildOnlyTools` no `PermissionRuleset.ShouldAnnounce` + prompt condicional resolveram.
- Coverage ratchet: baseline linha 92.22% / branch 79.73% — quedas >0.05pp reprovam; cobrir endpoints + services novos desde o primeiro commit.
- `.specs/SPEC-20261027-context-gen-wizard.md` permanece deferida (não implementar).

## Handoff 2026-11-20 (fila de mensagens + loop de entrega do modo build)

- **#752 entrega de mensagens enfileiradas**: client (`ChatView.razor`) — item era removido de `_messageQueue` antes do envio e sumia em falha; `SendAsync`/`StreamDetachedRunAsync` agora retornam `bool` e o drain reinsere na cabeça da fila. Server (`ChatRunExecutor`) — steers `mode=queue` só eram promovidos no caminho idle do tool loop; `DrainQueuedSteersAsync` em `FinishAsync`/`FailAsync` promove pendentes a mensagens `user` + despacha run filha (cobre step-cap, veto de budget e falhas). Run `stopped` marca `cancelled` — halt explícito é absoluto.
- **#753 topo do workspace mobile**: abas do `ChatWorkspacePanel` (Tasks…Info) e chips do header (branch/slug/Publicar) viraram ícone + `title`/`aria-label` — texto longo empurrava os ícones para fora da tela.
- **#754 loop de entrega no build mode**: chat `9ead0a1f` mostrou o agente parando a cada etapa (usuário pediu commit/push/PR 3×) e devolvendo link "crie o PR". Causa: `AgentModeContributor` não emitia bloco para `build` (modo default quando mode é nulo/desconhecido) e `AutonomyPrompt` tratava push como ação que exige confirmação. Fix: bloco build "implemente → testes → revalide (build+testes) → próximo item; ao fim commit + push + abra o PR você mesmo" e autonomy restringe aprovação humana a merge em branch protegida/deploy/software externo/APIs de terceiros. Regressão: `AgentPolishTests.BuildMode_EmiteDiretivaDoLoopDeEntrega`.
- Lição reutilizável: prompt de autonomia genérico pode contradizer o fluxo de entrega do projeto — comportamento esperado ("feche o ciclo sozinho") precisa estar explícito no bloco do modo efetivo injetado em runtime, não só em docs (CLAUDE.md/WORKFLOWS.md) que podem não entrar no contexto da run.

## Handoff 2026-11-22 (dev_pipeline P0 + transições automáticas de modo)

- **dev_pipeline P0** (SPEC-20261121): fase `plan` opcional antes do implement; `worker_model`/`fix_model` por fase; stall detection por `HeartbeatAt` (2 polls > `StallSec` → `TryStop` + run substituta `{phase}-stall{n}` com prompt de retomada, teto `MaxStallRetries`); fase `verify` read-only após validação verde (verdict issues → fix); `<pipeline_result>{files,summary,verdict,issues,plan}` parseado para `PhaseResults`. Defaults: `PhaseTimeoutSec=1200`, `StallSec=240`, `MaxStallRetries=1`.
- **Transições de modo** (SPEC-20261122): `builtin:mode_switch` troca chat→plan/chat→build (plan→build recusa — `plan_exit` é o gate humano); slash digitado `/plan|/build|/code|/chat` interceptado em `ChatRunEndpoints.TryModeSlash` antes da expansão de comandos; `ChatModeTransitions.ApplyEntryPresetAsync` sobe preset para `smart` na entrada de plan|build salvo se o dono fixou (`PinPresetAsync` em kv `chat:{id}:preset.pinned`).
- **Lição**: preset `smart` está em `IsAutoPickPreset` — subi-lo como default de plan/build fez o `plan_exit` AUTO-APROVAR sem perguntar (regressão pega por teste). `plan_exit` agora só auto-aprova em `always-allow`/`auto`; `smart` continua perguntando — a aprovação do plano é o gate humano do modo.
- **Lição**: `Rest` é nome reservado de elemento de tupla em C# (CS8126); usar `Remainder`. Build sem repo não downgradeia mais — `ResolveEffectiveMode` é literal e scratch cobre workdir.
- **dev_pipeline fan-out** (SPEC-20261123): `plan` emite `items[{id,title,files[],independent}]`; ≥2 independentes + binding → um chat irmão por item (`InheritChatBinding` → worktree próprio) + run `implement:{id}` em paralelo (`Task.WhenAll` de `WaitRunAsync`), merge `MergeAsync` sequencial no workdir do filho; dependentes/overflow → run `implement-rest`. Stall retry passou a ser **por runKey** (`StallRetriesByKey`) — worker travado não consome budget das irmãs; `CommitAsync` generalizou o merge p/ última run de cada base key (`-stallN` cobre substitutas; `plan` nunca mergeia). Cap `Agent:DevPipeline:MaxFanout` default 4 (clamp 1-8); arg `fanout` default true; sem binding/items → sequencial.
- **Lição**: chat irmão do worker precisa de `ParentRunId` de OUTRO chat (`PlanRunId`) → `sameChatParent` falso → `force:true` → worktree isolado por run; runs encadeadas no MESMO chat dividem o worktree do pai (é assim que test/fix do sequencial aterrissam no worktree do implement).

## Handoff 2026-11-24 (watchdog + telemetria + event-wake + run_workflow)

- **Watchdog** (SPEC-20261124-run-watchdog): `SweepOrphansAsync` só rodava no boot — `WatchdogLoopAsync` periódico (`ChatRuns:WatchdogSeconds` default 60, <=0 desliga) reusa o sweep com modo periódico: queued NÃO re-enfileira (fila in-memory viva → duplicata), run stale cujo owner é esta instância e segue ativa → `TryStop` em vez de requeue, e após `ChatRuns:WatchdogInterruptAfter` (default 3) ticks consecutivos stale → força `interrupted`.
- **Telemetria por run no report**: `CollectTelemetryAsync` agrega `key → (status, duração, stalls)` das runs da pipeline (`Runs` + sufixos `-stallN`) no `BuildReport`.
- **Event-wake**: `WaitRunAsync` faz race `WaitStreamClosedAsync(broadcaster, runId)` × poll 5s — `Complete` do broadcaster acorda na hora; poll segue como fonte de verdade p/ staleness; re-inscreve no runId da substituta. Falha de subscribe degrada p/ poll.
- **run_workflow** (SPEC-20261124): orquestração declarativa — o autor escreve `steps` em JSON, o servidor executa. Kinds: `llm` (run no chat filho + trailer `<pipeline_result>` automático), `parallel` (workers em chats irmãos+worktree, ondas de `MaxFanout`, merge sequencial, exige binding), `tool` (builtin server-side — o gate aconteceu na call Exec do workflow), `validate` (ProjectValidation — falha para a pipeline; retry é um step `llm` do autor lendo `{results:key}`), `commit` (git determinístico, `push:true` por step). Resume por `State.StepIndex`; `status`/`resume` pelo `dev_pipeline` (mesmo kv `devpipeline:{id}`).
- **Lição**: steps llm precisam encadear `ParentRunId` na run do step anterior (`AnchorRunId`) — sem isso cada run abriria worktree isolado NOVO da run pai e as mudanças entre steps se perderiam. `AnchorRunId` varre `state.Runs` do step i-1 pra trás (inclui workers `key:item` do parallel).
- **Lição**: `BuiltinToolResult` não tem `IsError` — falha de builtin em step `tool` é `Refused` (+`RefuseReason`) ou exceção; ambos viram FailStep.
- **P2 succession**: adiado — `ContextCompactionService` + `AsyncCompactionScheduler` + `ContextPruner` já fazem compactação 3-tier com spawn automático por estimativa de tokens; gatilho por contagem de spawns (agy, 32) é marginal.
- **run_workflow v2** (SPEC-20261124-workflow-v2): `when:"passed:key"/"failed:key"` condiciona step por veredito de step anterior (key ausente → skip em cadeia; inválido falha o workflow); `optional:true` registra veredito `issues` e segue (o `FailStepAsync` virou `IssueAsync` nos runners — o loop decide fatal × optional); `kind:"gate"` estaciona a pipeline (`AwaitingGate` persistido, status running) e posta a pergunta no chat pai — `dev_pipeline op=answer approved=true|false` resolve e re-dispara. Gate é máquina de estado, não `ask_user` — preset auto-pick auto-aprovaria.
- **Lição (watchdog)**: `TryStop` foi blindado contra `ObjectDisposedException` — janela entre o lookup no dicionário e o dispose pelo worker; cancel sobre cts morto propagava exceção que abortava o sweep inteiro. Hooks internos p/ teste no dispatcher: `CurrentInstanceId`, `RegisterRunCancelForTest`/`Unregister...`; fixture desliga o loop real com `ChatRuns__WatchdogSeconds=0` e chama o sweep explicitamente.

## run_workflow v2 (branch feature/agent-20261124-workflow-v2, PR #768)

- Steps: `when` (passed:/failed:key, cascata unmet), `optional`
  (issues→continua), `gate` (AwaitingGate no kv; resposta via
  dev_pipeline op=answer — NUNCA auto-aprova, por design não reusa
  ask_user que é auto-pick em preset smart).
- `timeout_sec` por step: CTS linkado; timeout → issue; run filha
  órfã é varrida pelo watchdog global.
- Named workflows kv `wf:{user}:{name}` (op=list/delete, save_as);
  inline args > defaults salvos.
- Retry por item: `EffectiveRunId` percorre `-stallN`; `RecordRun`
  grava retentativas em sufixo. Resume reusa só run Completed.
- `builtin:pipeline_board` (Tier.Read): board kv `board:{user}:{id}`;
  `BoardTrailer` injeta o id no prompt dos workers; join colhe em
  `PhaseResults[{step}:board]`.
- `sub` workflow: expansão em parse-time com keys `{key}.{subKey}`,
  `when` reescrito p/ keys internas, depth máx 2, ciclo por caminho.
- Routine→pipeline é grátis: builtin:routine agenda prompt→run e o
  run invoca run_workflow.
- Pendente (único item restante do roadmap): provider programável no
  fixture — destrava coverage debt do fan-out + e2e real.

## Fix: always-allow automático + fake-200 de billing (PR #769)
- `always-allow`: após hard-denies (ruleset/user deny), retorna Allow
  ANTES do forcePrompt — regra `ask`, override `prompt` e o default
  de `external_directory` não estacionam mais a run.
- Pollinations devolve HTTP 200 com `usage` POSITIVO até no aviso de
  "not enough credits" (chat 9ead0a1f) — o gate usage≤0 deixava passar.
  `MatchesStrongNotice` (termo de crédito + ação de billing juntos)
  vence o usage quando a resposta é curta (≤640); prosa longa com os
  dois marcadores segue liberando (anti-falso-positivo).
- ATENÇÃO: o check fake-200 buffered existe em 3 lugares —
  `ParseCompletionMessage` + `CompleteOllamaAsync`/`CompleteOpenAiAsync`
  inline. Mudança de contrato tem de tocar os três (o 2º furo da
  correção foi descoberto pelo teste que não lançava).
- E2E `Run_ProviderReal_*` flake: quando o gateway fica sem crédito o
  teste falha — o fix converte o aviso em 402/fallback.

## Coverage gate flake (2026-11-24)

- `.ci/coverage-baseline.txt` fica no fio (89.78%) — variância de medição ±0.04-0.08 entre runs idênticas pode reprovar PRs **sem nenhuma mudança de código** (aconteceu no #784: 89.69% numa branch sem código novo). TOL=0.05 não cobre.
- Ação correta: **rerun do job falho** (`gh run rerun <id> --failed`) — nunca baixar o baseline para destravar (é controle de compliance).
- Branch de trabalho cortada de outra branch de feature carrega o conteúdo dela — o merge da filha esvazia a mãe; fechar a mãe com comentário explicando.

## Paralelismo da suíte NUnit — NÃO viável sem migração (2026-11-24)

- Tentativa de `[assembly: Parallelizable(Fixtures)]+LevelOfParallelism(4)` quebrou 164 testes em 12+ fixtures de endpoint: `WebApplicationFactory` + env vars process-global (`ConnectionStrings__Default`, `OPENAI_API_*`, seeds ADMIN_*) são compartilhados por todo fixture — escritores (E2E) corrompem leitores concorrentes (`no such table`, `UNIQUE Users.Email`, `Forbidden/Unauthorized`).
- Os 10 `[NonParallelizable]` existentes só cobrem static-state *conhecido*; o vetor real é env var — isolamento exige migração (env por worker não existe em .NET; precisaria config-injection ou serialized factory access).
- Alternativa adotada: `timeout-minutes: 35` no job NUnit (suíte serial ~22min estava a ~90% do limite 25). Shard real = matriz + merge de coverage — projeto separado.
- `ci-build-test.yml` disparava em `push` (feature/*…) **e** `pull_request` → gates duplicados (~2× compute e ~2× flake exposure); ref do push ≠ refs/pull/N no concurrency group, então nunca dedupava. Fix: `push` só na main.

## Handoff 2026-11-25 (auditoria forense + fixes agent runtime, PR #788)

- **Auditoria** (SPEC-20261125, `AUDIT.md`, merge `c073ad6`): chat `f666d33c` / run `4c84362c` reconstruídos do SQLite — binding do workspace no kv `chat:{id}:workspace.repo` (ConfigEntries); 5 runs no chat, a "órfã" `850404c5` era falso positivo (o campo lido como heartbeat era `StartedAt` — a run estava `running` saudável). Cancel trace real: `TaskCanceledException` em `ProviderService.ReadLineWithIdleAsync`.
- **F1 a11y**: `Sidebar.razor:417` ganhou `for="share-url"` + `id`; `WorkspaceItemEditor.razor:136` (label sem controle sobre div) virou `div.field-label` (classe já existente em `tailwind.input.css`). Falsos positivos do grep: `data-options-for` e `for="gen-proj-*-@idx"` dinâmicos.
- **F2 tree 404**: `ChatWorkspacePanel.LoadMemoriesAsync` agora faz probe na raiz do worktree (`depth:2`) → HashSet de dirs → só chama tree para memory dirs existentes. 404 do `TreeAsync` mantido (correto para dir inexistente).
- **F3 stream OCE**: `catch (OperationCanceledException)` dedicado no `StreamAttachOnceAsync` (`ChatView`) — entre o rethrow filtrado (ct cancelado pelo usuário) e o `catch (Exception)`; OCE do watchdog não gera mais log de falha.
- **F4 BFCache/WS**: `openwebui.watchBfcache` em `app.js` (`pageshow` com `e.persisted`, guard `window._owBfcacheWatched`) → `ChatView.OnBfcacheRestoreAsync` → `RealtimeService.ConnectAsync` + `ReloadChatAsync` + `GetActiveRunAsync`→`AttachActiveRunAsync`. `ConnectAsync` retorna cedo só se token igual **E** estado vivo; senão dispose+recreate.
- **F5 progressive disclosure de skills**: novo `SkillCatalogContributor` (Order 71, entre RepoBinding 70 e Autoinvoke 72) injeta catálogo `- nome — descrição` + instrução `builtin:skill{name}`; exclui autoinvoke (essas já injetam corpo via Autoinvoke Order 72) e respeita toggles hidden/admin-disabled.
- **F6 contexto do repo** (pedido): `SkillDiscoveryService.LoadProjectInstructions` passa a ler `AGENTS/CLAUDE.md` → `.cursor/rules` + `.claude/rules` → `*.md` top-level de `.agents`/`.claude` → `*/memory` → README; dedup por `LinkTarget` de symlink, dirs reparse-point ignorados, teto 32→48KB.
- **Verificação**: build 0/0; suíte **3177 pass / 0 fail / 9 skip** (1h03 em arm64 — CI tem timeout 35min em x64; progresso da suíte serial verificável via fds `/tmp/openwebui-*.db` do testhost, `minimal` verbosity só imprime no fim); drift guards ×4 OK; secret scan limpo. Deploy: container `openwebui` (:3032) rebuildado da main, healthy, GET / 200.
- **Gate de contexto do agente**: para o primeiro prompt enxergar AGENTS.md/rules/memórias o workdir da run precisa ser o worktree do repo (binding) — scratch sem repo não tem esses arquivos, por design.
