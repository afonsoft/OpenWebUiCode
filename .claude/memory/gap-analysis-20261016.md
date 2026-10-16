# gap-analysis run — 2026-10-16 — auditoria "funciona como agente" + sonar-autofix

Resume state. Run on `main` @ 88e51b453 (clean tree). Previous run: `gap-analysis-20261010.md`.

Pergunta do usuário: o repo funciona como **agent** (codificação + conversação), sem gaps/zonas mortas? Depois: rodar `gap-analysis` + `sonarqube-autofix`.

## Source inventory

- PRESENT: `.specs/` (55 SPECs), `docs/` + `docs/specs/` (63 arquivados) + `docs/architecture/`, `.claude/{CONTEXT,MEMORY,rules,agents,hooks,knowledge}`, `CLAUDE.md`, README×2, `settings.json` (deny workflows), `.devin/blueprint.yaml`, `skills/` vendor.
- ABSENT: `AGENTS.md` (repo usa CLAUDE.md), ORCHESTRATOR-ROADMAP.md.
- GitHub: `gh` OK (afonsoft); **0 open issues**; issue #377 CLOSED.
- Build Release: **0 errors, 0 warnings**. Tests: **1650 pass / 0 fail / 8 skip** (skips = ProviderReal, precisam provider real). Client.Tests existe.
- Drift guards: `check-css-classes` OK · `check-form-a11y` OK (67 razor) · `check-i18n-parity` OK (1030 keys, 8 locales).
- Sonar env vars: todas unset (SONARQUBE_*/SONAR_TOKEN) — download fresco impossível nesta sessão.

## Verificação "funciona como agente" (AS-IS)

| Caminho | Evidência | Estado |
| --- | --- | --- |
| Conversação (send→run→stream→UI) | `ChatView.SendAsync` → chat create → repo bind → mode patch → `StreamAssistantAsync` → `ChatStream.EnqueueRunAsync`; runs detached sobrevivem a aba fechada | OK |
| Loop de tools | `ChatRunExecutor` (providers, ToolExecutor, BuiltinToolRegistry, approvals, compaction, steer queue `_recentTools`, publish callbacks) | OK |
| Tools | 40 impl `IBuiltinChatTool` — **todas registradas** em Program.cs (`LspBuiltinToolBase` é base abstrata) | OK |
| Endpoints | 47 mappers `Map*Endpoints` — **todos chamados** (`MapChatRunEndpoints` aninhado via `ChatEndpoints.cs:69`) | OK |
| Codificação/IDE | workspace binding, worktree/run, file+patch+shell tools, LSP (def/diag/symbols/format), Ide pages (explorer/editor/search/scm/preview/tests/statusbar/quickopen), terminal PTY+proxy | OK |
| Infra de agente | memory (MemorySync + memory_learn + migration AgentMemory), routines, subagents (delegate_task + agent_catalog + worktree_diff/merge), checkpoints/rewind, plan mode + SDD validation (PlanSpecTemplate + testes), context compaction, approval tiers, run advisor, parallel runs, chat branching, media, thinking stream | OK |
| Zona morta | 0 `TODO`/`NotImplementedException` em `src/` prod; todas as páginas `@page` alcançadas por nav (`/ide` via ChatView:90 + Workspace:39); client↔server routes pareadas | OK |

## Verdicts

| Gap key | Verdict | Evidence |
| --- | --- | --- |
| GAP-doc-stale-specs-r2 | CONFIRMADO | 10 SPECs em `.specs/` divergentes do mergeado: 8 `Approved` implementadas (agent-memory, agent-routines, context-compaction, delegate-persona, memory-autosync-retention, steer-queue, subagent-worktree, worktree-review — todas com código+PRs), `ide-bundle` "Em execução" (A–D em #334–#337), `plan-spec-sdd` "In implementation" (#380 + PlanSpecSddTests.cs), `ide-intellisense`/`ide-search-scm` sem header `Status` (viola convenção `.specs/README.md`). Recorrência do gap 20261010 — sweep é manual. |
| GAP-automation-sonar-followups | CONFIRMADO | 3 achados `githubactions:*` (perf.yml:76 S8541+S8544, ci-build-test.yml:211 S6506) + RF-C1 `sonar.exclusions` pendentes de humano, documentados **só** em `.sonar_devin_auto_fix/` (gitignored, commit 981ceae00) com issue #377 fechada e 0 issues abertas → sem tracking durável; ~2150 smells vendor continuam contados no QG. |
| GAP-tests-ide-visual | CONFIRMADO | `.specs/SPEC-20261011-ide-bundle.md` — único item aberto: "Validação visual /ide (screenshots) — pedida pelo usuário"; tudo mais entregue (build, 88/88 LSP, 91/91 Client, guards). |
| Endpoints órfãos de registro | REJEITADO | comm mappers×calls: só `MapChatRunEndpoints` — mapeado por `ChatEndpoints.cs:69`. |
| Tools não registradas | REJEITADO | diff impl×DI: só `LspBuiltinToolBase` (abstrata). |
| Stubs/NotImplemented em prod | REJEITADO | grep src/ → 0 hits fora de catches/testes/bundle minificado. |
| Coverage gate | REJEITADO | SPEC-20261008-tests-coverage-gate Completed; baselines sobem a cada merge ("chore(ci): raise coverage baselines"). |
| Offline-init / SW-register / CSP | REJEITADO | corrigidos pós-run 20261010: 9d4178c1c offline render, index.html:37 `.catch`, Program.cs:326 CSP. |
| Paridade upstream (RAG/OAuth/voice/realtime) | REJEITADO | documentado como decisão/roadmap em `docs/MIGRACAO-DOTNET.md` §Parcial — não é gap não declarado. |

## Draft SPECs written (gate pending)

| SPEC | Priority |
| --- | --- |
| `.specs/SPEC-20261016-spec-status-sweep-r2.md` | medium |
| `.specs/SPEC-20261016-sonar-workflow-followups.md` | high (segurança, mas human-gated) |
| `.specs/SPEC-20261016-ide-visual-validation.md` | low |

## sonarqube-autofix outcome (degraded — sem credenciais nesta sessão)

- Skill Phase 1 requer `SONARQUBE_*` env — **nenhuma setada** → download fresco impossível.
- Estado real: ciclo anterior **completo** — board `.sonar_devin_auto_fix/` 100% triado (121 vuln+bug: FIX ×~85, VENDOR ×16, FP/JUSTIFICADO ×17, HUMANO ×3; 2796 smells agregados), SPEC-20261015-sonar-remediation `Done`, PR #378 mergeado, issue #377 fechada.
- Spot-checks de fix verificados no código: `app.js` postMessage origin explícito (S2819), `ApiService.GetSharedChatAsync` `Uri.EscapeDataString` (S7044), `codeexec.js` `crypto.randomUUID` (S2245), `ExecutableResolver.Resolve("git")` (S4036), `_debounce` dispose (S2930).
- Residual transferido para `SPEC-20261016-sonar-workflow-followups` (3 itens workflow + exclusions — humano).

## Gate → Executed (aprovado "sim" pelo usuário)

| Ação | Resultado |
| --- | --- |
| Issues | Epic #392 `gap-analysis-20261016` + slices #393 (sweep), #394 (sonar followups, label `blocked` — hard rule workflows), #395 (IDE visual) |
| Branch | `docs/Devin-20261016-gap-analysis-r2` |
| #393 sweep | Executado — escopo ampliado de 10→52 SPECs terminais movidos `.specs/`→`docs/specs/` com `Delivered`; guard `tools/check-spec-status.py` criado (ativo sem Status / terminal em .specs/ / não-terminal em docs/specs/ → exit 1); convenção em `.specs/README.md` + `CLAUDE.md` |
| #395 IDE visual | Executado — Playwright/Chromium headless, app :8080, usuário e2e + repo `afonsoft/open-webui@main` bound; 8 screenshots em `docs/screenshots/ide-visual-20261016/`; **0 console/page/request errors**; 5 views activity bar OK; mobile OK; `ide-bundle` → Completed → `docs/specs/` |
| #394 sonar followups | Parcial — tracking durável entregue (a própria issue); itens de workflow ficam `blocked` aguardando humano |
| Guard final | `python3 tools/check-spec-status.py` → `OK: 3 SPECs em voo, 116 arquivados` |
| Estado pós-execução | `.specs/` = 3 SPECs (ide-visual-validation, sonar-workflow-followups, spec-status-sweep-r2 — aguardando merge) |
