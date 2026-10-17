# gap-analysis — 2026-10-17

Re-run pós-merge da cadeia E18 (#415–#421) e dos slices E17 #402/#406/#405 (#423–#425).
Escopo: issues abertas no GitHub + SPECs `.specs/` vs `origin/main` (`e7941a329`) + SonarCloud.
Read-only: nenhum branch/commit/issue criado neste run; para no approval gate.

## Fontes (inventário)

- `gh issue list` autoritativo (repo `afonsoft/open-webui`); `origin/main` em `e7941a329`, worktree limpo.
- `.specs/` 17 SPECs em voo; `docs/specs/` 116 arquivados; `tools/check-spec-status.py` OK.
- SonarCloud público (sem token local — `SONAR_TOKEN` é secret do CI): 2900 issues abertas, coletadas via `api/issues/search` paginado em `/tmp/sonar_issues.json`.
- Deploy local `docker compose` healthy em `:3032` (`e7941a329`).

## Issues abertas no GitHub (veredicto)

| Issue | Veredicto | Evidência |
|---|---|---|
| #400 `[E17] provider-resilience` | **PENDENTE — não implementado** | `ProviderService` sem retry/backoff: `git grep Retry-After origin/main` só casa `ComboService/RateLimitService/WebSearchService`; sem `ContextOverflow`/length-continuation. SPEC `.specs/SPEC-20261016-provider-resilience.md` ACs 77–82 todos `[ ]`. |
| #401 `[E17] parallel-tool-calls` | **PENDENTE — não implementado** | `RunToolLoopAsync` executa calls sequencialmente (`ChatPipeline.cs`); `SemaphoreSlim` existente é de run-scheduling (`ChatRunDispatcher/Pauses`), não de tool-concurrency. SPEC ACs `[ ]`. |
| #403 `[E17] commands-v2` | **PENDENTE — parcial (baseline client)** | `RepoSlashCommands.cs` faz interpolação client-side `$ARGUMENTS`/`$N` (SPEC T6 prevê como fallback). Faltam: `CommandDefinition` server, endpoint `/expand`, payload `command:`, `subagent:true`, blocos `!\`shell\``, validação de `model`. |
| #404 `[E17] run-recovery` | **PENDENTE — não implementado** | Sem entidade `ChatRunEvent`/`RunEvent` (`git grep` vazio); `Snapshot` existente = git checkpoint (`CheckpointService`); `Resume` = pause-gate de usuário (`ChatRunPauses`); `Heartbeat` = SSE keepalive client, não ownership de run. SPEC T1–T5 `[ ]`. |
| #398 `[E17] Epic opencode-parity` | **PARCIAL — 3/7 slices** | Entregues+fechadas: #402 (`d3ffa5cd3`), #406 (`4c457fc54`), #405 (`e7941a329`). Restantes: #400/#401/#403/#404. |
| #392 `[Epic] gap-analysis-20261016` | **PARCIAL** | Ver detalhe abaixo. |

## Reconciliação SPECs × implementação (`origin/main e7941a329`)

### Entregues e verificadas (símbolos confirmados em `origin/main`)

| SPEC | Entrega | Evidência |
|---|---|---|
| tool-output-bounding | #423 `d3ffa5cd3` | `ToolOutputService.cs`; wire em `ToolExecutor`; allowlist `file_read`; cleanup no `RetentionService` |
| websearch-failover | #424 `4c457fc54` | `SearchDetailedAsync` em `WebSearchService`/`WebSearchBuiltinTool`; `engine=auto` validado em `RetrievalEndpoints` |
| agent-steps-usage | #425 `e7941a329` | `AgentDefinitionService.cs:38,328` (`Steps`+parse); `MetaJson`; `ApplyUsageMetaAsync`; `stream_options`; `TotalSteps`/`OnStepAsync`; `agent.steps` kv; `MaxSteps` em `ChatRunContracts.cs:106`; badge `chat.status_step` nos 8 locales |
| global-agents-scope | #415 + #422 `2dd6bf8ba` | descoberta `~/.agents` + compat dirs; `.agents/mcp.json` |
| E18 (6 slices) | #416–#421 `7a2bb4265` | automation/combos/maintenance/external-agents/personas/question-forms |

### Gap confirmado — bookkeeping de SPECs

`GAP-bookkeeping-stale-spec-status` **CONFIRMADO** — 10 SPECs entregues+mergeadas seguem em `.specs/` com Status `Approved`/`In implementation` em vez de `Completed`+arquivo em `docs/specs/` (convenção: `docs/specs/SPEC-*` → `| Status | Completed |`):

| SPEC | Status atual | Merge |
|---|---|---|
| tool-output-bounding | `In implementation` | `d3ffa5cd3` |
| agent-steps-usage | `In implementation` | `e7941a329` |
| automation-actions | `In implementation` | `7a2bb4265` |
| model-combos | `In implementation` | `7a2bb4265` |
| session-maintenance | `In implementation` | `7a2bb4265` |
| websearch-failover | `Approved` | `4c457fc54` |
| global-agents-scope | `Approved` | `2dd6bf8ba` |
| external-agent-registry | `Approved` | `7a2bb4265` |
| agent-personas-channels | `Approved` | `7a2bb4265` |
| question-forms | `Approved` | `7a2bb4265` |

Impacto: drift de bookkeeping (mesmo defeito que `spec-status-sweep-r2` resolveu para SPECs anteriores — recorrente, o sweep não cobre SPECs que estavam em voo). Não quebra CI (guard só checa presença de Status). Correção: mesmo procedimento do sweep-r2 (Status→`Completed`, checkboxes, mover para `docs/specs/`).

### Epic #392 — detalhe

| Slice | Veredicto | Evidência |
|---|---|---|
| spec-status-sweep-r2 | **FEITO** | todos ACs `[x]` no SPEC |
| ide-visual-validation | **FEITO** | ACs `[x]`; screenshots em `docs/screenshots/ide-visual-20261016/` |
| sonar-workflow-followups | **PARCIAL** | RF-002/003/004 aplicados (`.github/workflows/perf.yml:76` `aiohttp==3.14.4 --only-binary`; `ci-build-test.yml:211` `--proto-redir =https`; `code-quality.yml:123` exclusões). **Pendentes**: AC "próximo scan mostra 0 `githubactions:*` e queda de ~2150 smells" — `githubactions:S6506` continua aberto (FP do analyzer p/ `--proto-redir`) e ~2260 issues vendor/docs seguem OPEN no board. |

## SonarCloud (sonarqube-autofix) — classificação

Fonte: `api/issues/search` público, 2900 issues, snapshot `/tmp/sonar_issues.json` (produto: `/tmp/sonar_prod.json`).

### Ruído vendor/stale (~2260 = 78%) — NÃO acionável por código

`docs/` 1134 + `skills/` 273×4 cópias (`.agents`, `.claude/skills`, `.devin/skills`, `skills`) + `.ci/a11y` 34. **Exclusões funcionam**: issues vendor têm `creationDate` 08–10/out e `updateDate` ~10/out — não são re-detectadas; permanecem OPEN porque SonarCloud não auto-fecha issues de arquivos excluídos. Remediação: bulk-resolve ("Won't Fix") no UI/API com token — `SONAR_TOKEN` é secret de CI, requer ação humana ou credencial dedicada. **Não gerar SPEC de código.**

### Produto real = 639 issues (`src` 603, `tools` 33, `tests` 2, `Dockerfile` 1, `.github` 1)

#### BUGs (3)

| Regra | Local | Nota |
|---|---|---|
| `S8949` | `ApiEndpoints.cs:288` | passar `http.RequestAborted` — real, quick fix |
| `S8949` | `ApiEndpoints.cs:601` | idem |
| `css:S8776` | `tailwind.input.css:552` | missing scoping root — quick fix |

#### BLOCKER code smells (2)

| Regra | Local | Nota |
|---|---|---|
| `S2699` | `ServiceCoverageTests.cs:471` `PtySession_ResizeInvalido_Noop` | teste no-throw sem `Assert` — trivial |
| `S2699` | `ToolStreamingTests.cs:1010` `Pausa_Idempotente_ForgetLimpaGate` | idem |

#### VULNERABILITYs (10) — triagem

| Regra | Local | Veredicto |
|---|---|---|
| `S7044` | `Client/Services/AuthService.cs:170` | **FP provável** — `CreateRequest` já sanitiza: rejeita `//`, segmentos `..`, exige path relativo (`AuthService.cs:160-170`). WASM client-side, sem SSRF. Recomendado resolve-as-false-positive; opcional: bloquear `%2e`. |
| `S5693` | `ChatView.razor:1818` | content-length de request — revisar |
| `S7039` | `Program.cs:333` | CSP restritiva — revisar headers |
| `S5332` | `PreviewEndpoints.cs:298` | http p/ preview de sites do usuário — provável won't-fix (intencional) |
| `S6471` | `Dockerfile:77` | root user — decisão de design, won't-fix provável |
| `S2068` | `pt-BR.json:116,145,160` | **FP** — chaves i18n "password" (labels), não credenciais |
| `S2245` | `tools/perf/loadtest.py:364,365` | **benigno** — pseudoaleatório em ferramenta de perf |
| `S6506` | `ci-build-test.yml:211` | **FP** — `--proto-redir =https` já aplicado (#397); analyzer não reconhece a flag |

#### CODE_SMELLs (~624) — débito técnico, não bloqueante

Top regras: `S3358` nested-ternary ×91 · `S3776` cognitive-complexity ×89 · `S1192` dup-strings ×85 · `S2325` ×36 · `S107` too-many-params ×30 · `S6582`/`S3504`/`S7773`/`S2486` (JS wwwroot) ~60 · `S4487` unread-fields ×14. Refatoração ampla — recomendar SPEC separado se quiser atacar.

## Achados consolidados (ordem: categoria → prioridade → chave)

| Gap key | Prioridade | Veredicto |
|---|---|---|
| GAP-implementation-provider-resilience | Alta | CONFIRMADO (#400 aberta, SPEC aprovado, sem código) |
| GAP-implementation-parallel-tool-calls | Alta | CONFIRMADO (#401 aberta) |
| GAP-implementation-commands-v2 | Alta | CONFIRMADO (#403; baseline client `$ARGUMENTS` existe) |
| GAP-implementation-run-recovery | Alta | CONFIRMADO (#404; sem `ChatRunEvent`/ownership heartbeat) |
| GAP-bookkeeping-stale-spec-status | Média | CONFIRMADO (10 SPECs entregues com status obsoleto — recorrente ao sweep-r2) |
| GAP-sonar-stale-vendor-issues | Média | CONFIRMADO (~2260 OPEN no board; requer bulk-resolve c/ token — ação humana) |
| GAP-sonar-quickfixes | Baixa | CONFIRMADO (S8949×2, S2699×2, css:S8776 — ~5 fixes triviais) |
| GAP-sonar-vuln-triage | Baixa | CONFIRMADO (S5693, S7039 revisar; S7044/S2068/S6506/S5332/S6471/S2245 → FP/won't-fix) |
| GAP-sonar-codesmell-debt | Baixa | CONFIRMADO (~624 smells; S3776/S3358/S1192 dominam — SPEC de refatoração opcional) |
| — e17-specs-bookkeeping-epic392 | — | `sonar-workflow-followups` parcial: código aplicado, validação de scan pendente (mesma causa do gap vendor-stale) |

## Ações propostas (aguardando aprovação)

1. **Bookkeeping** (sem código, mesma operação do sweep-r2): marcar 10 SPECs `Completed` + mover p/ `docs/specs/`; atualizar checkboxes do Epic #392 (2/3 feitos) e #398 (3/7).
2. **Sonar quick-fixes** (1 PR pequeno): `RequestAborted` ×2, asserts nos 2 testes no-throw, css:S8776.
3. **Implementar E17 restante**: #400 → #401 → #403 → #404 (SPECs já aprovados — só executar).
4. **Sonar vendor cleanup**: sem ação de código — orientar resolve em massa no SonarCloud (humano com token) ou via `api/issues/bulk_change` com credencial.

Approval gate — não criar issues/branches/commits até confirmação.
