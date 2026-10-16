# SPEC-20261015-sonar-remediation

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-remediation` |
| Type | `Security` + `Bugfix` + `Refactor` (consolidado) |
| Stack | `.NET` (ASP.NET Core + Blazor WASM) + JS + Shell + Docker + GH Actions |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `fix/Devin-20261015-sonar-remediation` |
| Ticket | [#377](https://github.com/afonsoft/open-webui/issues/377) |
| Status | `Completed` |
| Fonte | `sonarqube-autofix` skill · `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` · SonarCloud `afonsoft_open-webui` (análise 2026-10-15) |

## 1. User Story

**As a** mantenedor do fork
**I want** corrigir as issues de segurança e confiabilidade reais do SonarCloud + CodeQL, triar falsos positivos com justificativa e ajustar o escopo de análise
**So that** o quality gate volte a A (Security/Reliability em código novo) e o backlog de achados reflita apenas código de produto.

**Problem context:**
Análise SonarCloud 2026-10-15: **80 VULNERABILITY** (1 Critical, 46 Major, 33 Minor), **0 SECURITY_HOTSPOT**, **41 BUG** (6 Blocker, 35 Major), **2796 CODE_SMELL**. QG reprovou nos PRs 361/362/368–372 por *Security Rating B* e *Reliability Rating C* em código novo. `github-advanced-security[bot]` deixou 87 review comments CodeQL nos últimos 30 PRs. ~80% dos smells estão fora do produto (`docs/architecture/*.html` gerado por archify = 1132; skills vendor `.agents/.claude/.devin/skills + skills/` ≈ 1000).

Classificação dos 121 vulns+bugs: **83 em código de produto** (`src/`, `tests/`, `Dockerfile`, `tools/`, `.ci/`), **16 em skills vendor**, **22 em docs/workflows**.

## 2. Scope

**In scope:**
- **WS-A — Segurança (produto):** correção das vulnerabilidades reais em `src/`, `Dockerfile`, `tools/` (ver RF-A).
- **WS-B — Confiabilidade (bugs):** correção dos 41 BUGs em produto/CI-local (ver RF-B).
- **WS-C — Escopo de análise:** exclusões Sonar para artefatos gerados/vendor (requer edit de workflow → **revisão humana**).
- **WS-D — CodeQL backlog:** correção dos review comments acionáveis em código de produto (generic catch, Path.Combine, upcast, LINQ, dispose).
- **WS-E — Won't-fix justificado:** FPs marcados no SonarCloud com comentário (sem mudança de código).
- **WS-F — Smells de produto:** triagem — corrigir CRITICAL+BLOCKER de `src/`; restante fica como backlog.

**Out of scope:**
- Editar `.github/workflows/` (hard rule — itens listados para revisão humana em §8/OQ).
- Reescrever skills vendor — o correto é excluir do scan ou corrigir upstream (`afonsoft/skills`).
- Reduzir cobertura ou mudar o quality gate.
- Refatorar os 2796 smells por completo (backlog contínuo).

## 3. Technical Context

**Evidências-chave (board completo em `.sonar_devin_auto_fix/`):**

| Achado | Regra | Qt | Local |
| --- | --- | --- | --- |
| Regex sem timeout (ReDoS) | csharpsquid:S6444 | 25 | Infrastructure.Services (ContextPruner, ProviderCapabilityService, WebLoaderService, ModelFilterService), ChatTools, Api (ChatPipeline, ChatRunExecutor, PassthroughEndpoints, ChannelEndpoints), Client (ChatView, IdeEditor, ToolArgsPreview) |
| Comando sem path absoluto | csharpsquid:S4036 | 6 | WorktreeService:318, CheckpointService:914,971, WorkspaceRepoService:501, WorkspaceGitService:181, LocalTerminalSpawner:108 (`FileName="git"` etc.) |
| Traversal de API | roslyn:S7044 | 2 | Client ApiService.GetSharedChatAsync:57 (`shareId` sem encoding), AuthService.CreateRequest:158 (uri arbitrária) |
| postMessage `*` | javascript:S2819 | 1 CRITICAL | wwwroot/js/app.js:147 (`tab.postMessage(payloadJson, '*')` — deve ser origin `https://openwebui.com`) |
| CSP permissiva | csharpsquid:S7039 | 1 | Api/Program.cs:293–302 (`'unsafe-inline'` em script/style, `https:`/`data:`/`blob:` amplos em connect/img) |
| http:// | csharpsquid:S5332 | 1 | PreviewEndpoints:298 (prefixo loopback — ver WS-E) |
| Limite de upload | csharpsquid:S5693 | 1 | ChatView.razor:1756 (`OpenReadStream(100MB)`) |
| USER root | docker:S6471 | 1 | Dockerfile:77 (intencional — shell_exec; ver WS-E) |
| "password" em json | json:S2068 | 3 | i18n/pt-BR.json:114,143,158 (labels — FP; WS-E) |
| PRNG | S2245 | 4 | codeexec.js:293-294 (msgId/session — trocar por `crypto.randomUUID()`), tools/perf/loadtest.py:364-365 (load gen — FP) |
| ct não propagado | csharpsquid:S8949 | 22 | Api/Runs/ChatRunExecutor.cs:458 + outros (chamadas async sem CancellationToken) |
| IDisposable não descartado | csharpsquid:S2930 | 6 BLOCKER | IdeSearchView.razor:86 `_debounce` + 5 pontos similares (Client) |
| Promise não awaitada | javascript:S9383/S4822 | 8 | wwwroot/js/ide-layout.js:20 +, .ci/a11y/audit.js:36 |
| Loop 1 iteração | csharpsquid:S1751 | 1 | LdapService.cs:69 |
| CSS scoping | css:S8776 | 1 | Client/tailwind.input.css:552 |
| CancelledError | python:S7497 | 1 | tools/perf/loadtest.py:400 |
| Docs HTML | Web:* | 3 | docs/architecture/*.html (title/lang) — gerado |
| Workflows | githubactions:S8541/S8544/S6506 | 3 | perf.yml:76 (pip sem pin/`--only-binary`), ci-build-test.yml:211 (curl -L) — **revisão humana** |
| CodeQL | várias | 87 | generic catch x36, Path.Combine x10, useless upcast x8, LINQ x9, complex cond x5, Dispose x3, unused x5 |

**Files to modify (WS-A/B/D):** `src/OpenWebUI.Client/wwwroot/js/app.js`, `wwwroot/js/codeexec.js`, `wwwroot/js/ide-layout.js`, `Client/Services/ApiService.cs`, `Client/Services/AuthService.cs`, `Client/Components/{ChatView,IdeSearchView,...}.razor`, `Api/Program.cs`, `Api/Endpoints/{PreviewEndpoints,PassthroughEndpoints,ChannelEndpoints}.cs`, `Api/{Completions/ChatPipeline,Runs/ChatRunExecutor}.cs`, `Infrastructure/Services/{ContextPruner,ProviderCapabilityService,WebLoaderService,ModelFilterService,WorktreeService,CheckpointService,WorkspaceRepoService,WorkspaceGitService,LocalTerminalSpawner,LdapService}.cs`, `Infrastructure/ChatTools/**`, `Client/tailwind.input.css`, `tools/perf/loadtest.py`, `.ci/a11y/audit.js`, `Dockerfile` (comentário), `sonar.exclusions` no workflow (humano).

## 4. Requirements

### WS-A — Segurança (obrigatório para QG)

- **RF-A1 (S2819, CRITICAL):** `app.js` `shareCommunity` deve chamar `tab.postMessage(payloadJson, 'https://openwebui.com')` — nunca `'*'`.
- **RF-A2 (S7044):** `ApiService.GetSharedChatAsync` usa `Uri.EscapeDataString(shareId)`; `AuthService.CreateRequest` valida que `uri` é path relativo (`StartsWith("/")` e não `//`) ou `Uri` absoluto do mesmo origin — rejeitar demais casos.
- **RF-A3 (S6444 ×25):** todo `new Regex(...)`/`Regex.Match|Replace|Split` em código de produto recebe timeout explícito (`TimeSpan.FromMilliseconds(250)` ou `Regex.DefaultMatchTimeout` em `Program.cs`/`AssemblyInfo` — decidir e aplicar consistente; `RegexOptions.NonBacktracking` onde compatível).
- **RF-A4 (S4036 ×6):** comandos externos (`git`, etc.) resolvidos por helper `ProcessRunner.ResolveOnPath("git")` (procura PATH uma vez, cacheia, fallback absoluto conhecido) — `ProcessStartInfo.FileName` passa a receber o path absoluto.
- **RF-A5 (S7039):** revisar CSP: remover `'unsafe-eval'` se não necessário, substituir `https:` amplo em `connect-src`/`img-src`/`frame-src` por allowlist concreta onde possível, adicionar `require-trusted-types-for 'script'` somente se compatível com WASM boot. Documentar por diretiva por que cada token existe. **Não pode quebrar boot WASM/streaming/markdown.**
- **RF-A6 (S5693):** limite de upload (100 MB) vira constante nomeada `MaxUploadBytes` + comentário `// sonar: intentional` e, se factível, config `u:{id}:upload.maxBytes` — manter limite explícito.
- **RF-A7 (S2245):** `codeexec.js` troca `Math.random()` por `crypto.randomUUID()` (ids de mensagem/sessão).

### WS-B — Confiabilidade

- **RF-B1 (S8949 ×22):** propagar `ct` nas chamadas async sinalizadas (principal `ChatRunExecutor.cs`).
- **RF-B2 (S2930 ×6, BLOCKER):** descartar `_debounce`/timers/IDisposable nos componentes Razor sinalizados (`@implements IDisposable`/`IAsyncDisposable` já usado no projeto — seguir padrão).
- **RF-B3 (S9383 ×7 + S4822):** `ide-layout.js` e `audit.js`: `await`, `.catch(handler)` ou `void` explícito com justificativa.
- **RF-B4 (S1751):** `LdapService.cs:69` — remover loop de 1 iteração (desenrolar ou tornar multi-iteração real).
- **RF-B5 (css:S8776):** `tailwind.input.css:552` — adicionar scoping root correto.
- **RF-B6 (S7497):** `loadtest.py` re-raise `asyncio.CancelledError` após cleanup.
- **RF-B7 (Web):** docs/architecture `*.html` ganham `<title>` + `lang` — **ou** resolvido via WS-C (exclusão).

### WS-C — Escopo de análise (requer revisão humana do workflow)

- **RF-C1:** estender `/d:sonar.exclusions` em `code-quality.yml` para cobrir `docs/**`, `skills/**`, `.agents/**`, `.claude/skills/**`, `.devin/**`, `.ci/**` — elimina ~2150 smells vendor/gerados + 16 vulns vendor.
- **RF-C2:** confirmar que `docs/architecture/**/*.html` atual não cobre JS/CSS embutido (issues persistem) — ajustar pattern para `docs/**` ou investigar.

### WS-D — CodeQL backlog

- **RF-D1:** `Path.Combine` x10 — revisar cada ponto (pode dropar args — usar `Path.Join`/path completo conforme intenção).
- **RF-D2:** generic catch x36 — onde o catch existe só para log+swallow, trocar por `catch (SpecificException)` ou filtrar; onde é boundary intencional, manter com comentário.
- **RF-D3:** useless upcast x8, LINQ Where/Select x9, complex condition x5, unused x5, cast x1 — aplicar sugestões triviais.
- **RF-D4:** Missing Dispose x3 (`HttpResponseMessage`) — `using`.

### WS-E — Won't-fix (registrar justificativa no SonarCloud, sem código)

- `docker:S6471` — `USER root` intencional (agente shell_exec instala ferramentas); comentário já existe no Dockerfile.
- `csharpsquid:S5332` — prefixo `http://127.0.0.1|localhost` em PreviewEndpoints é comparação de loopback (redirect local), não fetch externo.
- `json:S2068` — `"password"` em pt-BR.json são labels de UI.
- `python:S2245` — `random` em loadtest.py gera carga, não segredos.
- `shell:S6505/S6506` — scripts de skills vendor (correção vai por WS-C/upstream).
- `githubactions:*` — itens de workflow sinalizados para revisão humana (§8).

## 5. API Contract

N/A — sem novos endpoints. `GetSharedChatAsync` mantém assinatura; validação interna apenas.

## 6. Acceptance Criteria

- [ ] **Given** scan SonarCloud na branch **when** análise conclui **then** 0 issues `VULNERABILITY`/`BUG` em `src/`+`tools/`+`.ci/` exceto marcadas won't-fix justificadas.
- [ ] **Given** `app.js` **when** shareCommunity dispara **then** postMessage usa origin explícito (revisão de código).
- [ ] **Given** shareId malicioso (`../`, `..%2F`) **when** GetSharedChatAsync chamado **then** URL path segment é escapado.
- [ ] **Given** regex de produto **when** input patológico **then** MatchTimeout evita hang (teste de unidade com input malicioso não trava >1s).
- [ ] **Given** componentes Razor com timer **when** Dispose **then** sem ObjectDisposedException em teste.
- [ ] **Given** suite completa **when** `dotnet test` **then** 1596+ pass, 0 fail; cobertura ≥ 90.55%/76.62% (baseline do gate CI).
- [ ] **Given** QG do PR **when** análise **then** Security/Reliability Rating A em código novo.
- [ ] **Given** board **when** fim **then** `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` 100% triado (fix/won't-fix/human-review).

## 7. Task Plan

- [x] **T1 — Analysis:** download issues (121 vuln+bug + facets smells), PR bot comments (30 PRs), classificação → `SONAR_FIX_TODO_BOARD.md`.
- [x] **T2 — WS-A:** RF-A1…A7 aplicados (postMessage origin, traversal, regex timeouts ×25, paths absolutos ×6, CSP justificada, upload const, crypto.randomUUID).
- [x] **T3 — WS-B:** RF-B1…B6 aplicados (ct ×22, dispose ×6, promises ×8, loop LDAP, CancelledError); B7 via WS-C/won't-fix.
- [x] **T4 — WS-D:** mecânicos aplicados (Path.Join, complex-cond, dispose-tests=FP); generic-catch = backlog documentado.
- [ ] **T5 — Validação:** `dotnet build -c Release` ✓ 0 erros + `dotnet test` + drift guards (`check-css-classes`, `check-form-a11y`, `check-i18n-parity`) + tailwind regen se .razor mudar.
- [x] **T6 — WS-C/F + WS-E:** issue #377 aberta; workflow items + won't-fix documentados em `SONAR_FIX_REVIEW_NOTES.md`.
- [ ] **T7 — PR:** commit convencional + PR; aguardar QG.

## 8. Organization Guardrails

- `.github/workflows/` **não** é editado neste PR — RF-C1 e githubactions:* ficam documentados para o usuário aplicar/revisar.
- Sem commit direto em `main`; sem secrets; sem `--force`/`--no-verify`.
- Documentação XML em APIs públicas novas; async/await com `Async`.

## 9. Definition of Done

- [ ] RF-A1…A7, RF-B1…B7 implementados.
- [ ] RF-D1…D4 aplicados onde custo-benefício.
- [ ] Todos os critérios de aceitação verificados.
- [ ] Suite verde + coverage gate CI passa.
- [ ] Board 100% triado; won't-fix com justificativa escrita.
- [ ] Issue(s) de tracking abertas (esta SPEC + workflow pendente).
- [ ] `SONAR_FIX_REVIEW_NOTES.md` + métricas gerados.

## Open Questions / Pending Ambiguity

- **OQ-1 (humano):** aprovar edit em `code-quality.yml` (RF-C1) e fixes `githubactions:*`?
- **OQ-2:** CSP — remover `'unsafe-eval'`/`https:` amplo pode quebrar providers externos configuráveis (Ollama/OpenAI base URL do usuário). Proposta: manter `connect-src` permissivo (necessário para BYOK arbitrário) e endurecer `script-src`/`frame-src`. Validar com o usuário antes de restringir `connect-src`.
- **OQ-3:** Docker `USER root` — manter (design do agente) ou criar usuário dedicado + sudoers? Recomendação: manter + justificar.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/378 |
| Issue | https://github.com/afonsoft/open-webui/issues/377 |
