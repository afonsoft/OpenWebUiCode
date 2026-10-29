# SPEC-20261009-test-stabilization

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `test-stabilization` (Api.Tests flaky infra) |
| Type | `BugFix` (infra de testes) |
| Stack | `.NET 10` / NUnit |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `devin/{ts}-flake-stabilization` |
| Status | `Completed` |

## 1. User Story

**As a** maintainer
**I want** que o job "Tests + Coverage Gate (NUnit)" fique verde na `main`
**So that** as ~15-16 falhas determinísticas de infra compartilhada deixem de mascarar regressões reais.

## 2. Root causes found

Cada falha foi mapeada para um bug de código de teste (não probabilístico):

| Causa raiz | Testes afetados | Fix |
| --- | --- | --- |
| Vazamento de env vars entre fixtures + órfãos de WAL SQLite (`-wal`/`-shm` persistem após delete do `.db`, e um novo arquivo no mesmo path recupera frames antigos → `'table "ApiKeys" already exists'`) | `Endpoints_SemAuth_401` e fixtures dependentes | `[IsolateEnvironment]` (ITestAction `Targets=Suite`) em todos os fixtures via `TestInfra`; `TestInfra.DeleteDb` deleta `.db` + `-wal` + `-shm` em ~85 sites |
| `static` `HttpClientHandler` compartilhado no `DelegatingHandler` do mock OAuth — primeira `HttpClient` disposed matava o handler para as demais | `Github_OAuth_EmailFallback` ×2, `ExchangeCode` | inner handler por instância (`RewriteToMockHandler`) |
| `MemoryCache` criado com `using var`/`using` escapando do escopo (cache disposed retornado junto com o service) | `AutoConfigure_*` ×5, `PrimeiroUsuario_ViraAdmin` | cache vira campo do fixture, disposed no `OneTimeTearDown`; `ExecuteDeleteAsync` bypassa cache → `_cache.Clear()`/`mc1.Clear()` no `SetUp` |
| Cache de 60s de `ListModelsAsync` (fingerprint de URLs/keys) compartilhado entre testes | `ListModels` ×3 | `mc1.Clear()` por teste |
| Mesma RSA key para IdP e "rogue" cert → assinatura SAML validava de verdade | `T04_Acs` | `IdpRsaKey` vs `RogueRsaKey` separadas |
| `await using var app = WebApplication.Create()` no echo WS — dispose no return encerrava o upstream | `WebSocket_Tunnel` | instância não-disposed (vive até o fim do processo do host de teste) |
| `BrowserTools:Path` não pinado → dependia de não haver Chrome no ambiente | `Browser_SemBinario_ScreenshotFalhou` (só local) | pin `/bin/false` — hermético com ou sem Chrome |
| 6 testes de endpoint sem `ConnectionStrings__Default` pinado → caíam no `data/openwebui.db` do CWD | `WorkspaceFileEndpointsTests` ×3, `WorkspaceTestRunTests`, `GitHubWorkspaceTests`, `RepoSkillEndpointsTests` | `TestInfra.UseDb()` pinando DB temp por teste |

## 3. Verification

- `dotnet test tests/OpenWebUI.Api.Tests` (Release) **3× consecutivas: 0 falhas, 1050 passed, 3 skipped** cada (runs `verify1/2/3.trx`).
- `tests/OpenWebUI.Client.Tests`: **67/67** (intocado).
- Drift guards: `check-css-classes.py`, `check-form-a11y.py`, `check-i18n-parity.py` — OK.
- 0 `<<<<<<<` markers em `src/`/`tests/`.

## 4. Out of scope

- App code (`src/`) — nenhuma mudança; todos os fixes são em `tests/OpenWebUI.Api.Tests`.
- Os 3 skipped são testes `ProviderReal` que exigem gateway LLM (comportamento existente).

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/OpenWebUiCode/pull/267 |
