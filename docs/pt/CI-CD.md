# CI/CD e ferramentas

Mapa de todos os workflows do GitHub Actions e das ferramentas do repositório
que mantêm o port .NET 10 / Blazor WASM saudável — o que cada pipeline roda,
quais tools ela invoca, no que ela faz gate e como reproduzir as checagens
localmente.

> 🇺🇸 [English version](../en/CI-CD.md)

## Inventário de workflows

| Arquivo | Nome exibido | Gatilhos | Propósito |
|---|---|---|---|
| `ci-build-test.yml` | 🚀 CI Build & Test | push `main` + `feature/*` `fix/*` `bug/*` `hotfix/*` `chore/*` `test/*`, PR → `main`, dispatch | Build Release, testes + gate de cobertura, drift guards do Blazor WASM, freshness do Tailwind, build Docker, ratchet do baseline, gate de admissão |
| `a11y-audit.yml` | ♿ Accessibility Audit | PR / push `main`, dispatch | Scan axe-core + smoke de usabilidade/visual (Playwright, mobile 375px + desktop) |
| `chat-e2e.yml` | 💬 Chat E2E (LLM real) | PR / push `main`, dispatch | E2E de chat contra provider de LLM real (gate por secrets) |
| `code-quality.yml` | 📊 Code Quality | PR / push `main`, dispatch | Análise Qodana + SonarCloud |
| `security-scan.yml` | 🔒 Security Scan | push / PR `main`, semanal (seg 02:00 UTC), dispatch | CodeQL, gitleaks, actionlint + zizmor, OSV-Scanner, hadolint, Snyk, Trivy |
| `scorecard.yml` | 🛡️ OpenSSF Scorecard | `branch_protection_rule`, semanal (seg 07:27 UTC), push `main`, dispatch | OpenSSF Scorecard → SARIF |
| `perf.yml` | ⚡ Performance Load Test | semanal (dom 03:30 UTC), dispatch | Harness de carga aiohttp (`tools/perf/`), gate de rps agregado, abre issue de tracking quando a run agendada falha |
| `release.yml` | 🚀 Release & Publish | tag `v*.*.*` | Artefatos de release, imagens Docker multi-arch (GHCR + Docker Hub), registro de deployment, GitHub Release |
| `dockerhub-overview.yml` | 🐳 Docker Hub Overview | dispatch | Sincroniza `docs/dockerhub/overview.md` para a página do Docker Hub |
| `cleanup-artifacts.yml` | 🧹 Cleanup Artifacts | diário 03:23 UTC, dispatch | Apaga artifacts de workflow com mais de 30 dias |

Todas as actions são pinadas por SHA completo (com comentário `# vX.Y.Z`) —
decisão de hardening da cadeia de suprimentos auditada semanalmente pelo
`zizmor`.

## `ci-build-test.yml` — pipeline principal

O pipeline de admissão. O job `changes` classifica o diff primeiro
(`scripts/classify-changes.sh` → outputs `code` / `docs` / `docker`), então
PRs só de docs pulam os jobs caros e PRs em draft não rodam nada.

| Job | O que faz | Ferramentas |
|---|---|---|
| `changes` | Classifica paths alterados em `code`/`docs`/`docker` | `scripts/classify-changes.sh` |
| `build-openwebui` | `dotnet build OpenWebUI.slnx -c Release` no .NET 10 — pulado em PR draft e diff só de docs | .NET SDK 10 |
| `test` | Todos os testes NUnit + gate de cobertura (abaixo); sobe artifacts `test-results` (TRX) e `coverage-xml` (Cobertura); comentário sticky de cobertura no PR; roda `scripts/check-env-drift.sh` | `dotnet test`, Coverlet collector, `coverlet.runsettings`, script Python do gate |
| `blazor-client-validation` | Drift guards do client — falha se `.razor` usar classe CSS indefinida, controle de form sem nome acessível ou os 8 locales divergirem | `tools/check-css-classes.py`, `tools/check-form-a11y.py`, `tools/check-i18n-parity.py` |
| `tailwind-freshness` | Regenera `wwwroot/css/tailwind.css` e compara — o artefato commitado precisa bater com o input | CLI `tailwindcss` v4.3.3 (binário em cache) |
| `docker-build` | Build do `Dockerfile` de produção (em PR: só quando `code` ou `docker` mudou) | Docker |
| `pr-test-policy` | Falha PR que muda código de produção sem tocar `tests/` | `scripts/check-pr-test-policy.sh` |
| `coverage-ratchet` | **só push na main** — sobe `.ci/*baseline*` para as taxas medidas e commita `[skip ci]`; nunca baixa | Python + `git` |
| `admission-verdict` | Check required único "Gate / CI" agregando o resultado de todos os jobs | `github-script` |
| `ci-summary` | Resumo "CI Dashboard" na página da run | `github-script` |

### Mecânica do gate de cobertura

- `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings`
  emite `coverage.cobertura.xml`.
- `coverlet.runsettings` exclui o assembly Client, assemblies de teste,
  `*.Migrations.*`, `Microsoft.AspNetCore.OpenApi.*` e código gerado
  (`Obsolete`, `GeneratedCode`, `ExcludeFromCodeCoverage`).
- O gate compara as taxas medidas de **linha** e **branch** com baselines
  commitadas: `.ci/coverage-baseline.txt` e
  `.ci/coverage-branch-baseline.txt`, com `TOL = 0.05` para ruído de medição
  entre runs.
- Os baselines funcionam como **ratchet**: o `coverage-ratchet` os sobe sempre
  que a cobertura da main melhora — o piso só se move para cima
  automaticamente.
- Quando uma queda pequena e legítima entra (ex.: ruído acima do `TOL`),
  redefina o baseline para o último valor medido na `main` — mesmo
  procedimento do precedente "reset baselines to reality" (`ci(coverage):
  reset baselines to post-E19 reality`). Nunca aumente o `TOL` para esconder
  quedas reais.

## `a11y-audit.yml` — acessibilidade

Job em matrix sobre o harness Playwright em `.ci/a11y/` (`npm ci` +
`playwright install chromium`), contra o app publicado:

| Checagem | Script | Artifacts |
|---|---|---|
| axe-core (mobile 375px + desktop) | `.ci/a11y/audit.js` | `a11y-report.json` |
| Usabilidade + smoke visual (mobile 375px + desktop) | `.ci/a11y/usability.js` | `usability-report.json` + `screenshots/usability/` |

## `chat-e2e.yml` — E2E de chat com provider real

Gate por secrets: sem `LLM_API_KEY` o job sai verde com notice. Com
`LLM_PROVIDER`/`LLM_BASE_URL`/`LLM_API_KEY`/`LLM_MODEL` configurados roda a
suíte NUnit `E2E_CHAT=1`, que exercita `/api/chat/completions` em SSE real e
publica as transcrições como artifact (`chat-e2e-results`).

## `code-quality.yml` — análise estática

- **Qodana** (`JetBrains/qodana-action`) — inspeção .NET da solução inteira,
  artifact `qodana-report`.
- **SonarCloud** (`dotnet-sonarscanner`, Java 17) — analisa para
  [`afonsoft_open-webui`](https://sonarcloud.io/project/overview?id=afonsoft_open-webui).
  `sonar-project.properties` mantém diretórios de docs/skills/vendor fora da
  análise e migrations do EF fora da contagem de duplicação; o mesmo conjunto
  de exclusões vai via `/d:sonar.exclusions` no workflow.

## `security-scan.yml` — caixa de ferramentas de segurança

| Ferramenta | Escopo | Saída |
|---|---|---|
| CodeQL (init/autobuild/analyze) | SAST C# + JavaScript | GitHub code scanning |
| `gitleaks git --redact` | Scan de segredos no histórico git | log do job |
| `actionlint` + `zizmor` | Lint de workflows + auditoria de segurança de Actions | `zizmor-sarif` → code scanning |
| `osv-scanner scan --recursive` | Vulnerabilidades de dependências (DB OSV) | log do job |
| `hadolint` | Lint do `Dockerfile` | log do job |
| `snyk test --severity-threshold=high --all-projects` | Vulns de dependências + código | log do job |
| `trivy` (`aquasecurity/trivy-action`) | Scan da imagem de container buildada | SARIF → code scanning |

## `perf.yml` — teste de carga

Publica o app, sobe contra `tools/perf/mock_ollama.py` (Ollama falso em
`127.0.0.1:11434` com gatilhos scriptados — `__delegate__`, `__words__N`,
`__sleep__MS`) e dirige `tools/perf/loadtest.py` (aiohttp) pelos cenários
`rps` / `sessions` / `subagents` / `real` (selecionáveis no dispatch ou `all`).
Um gate de rps agregado mínimo falha a run; falhas agendadas abrem ou
atualizam uma issue de tracking (job `issue-on-failure`). Ver
`tools/perf/README.md`.

## `release.yml` — tag → artefatos → imagens → release

Em tags `v*.*.*`: valida a tag de versão → builda artefatos de release
(`openwebui-<ver>`) → matrix `docker buildx` por plataforma → manifest
multi-arch → registra deployment `production` (URL da repo variable
`DEPLOY_URL`) → cria o GitHub Release com os artefatos. Imagens vão para GHCR
e Docker Hub (`linux/amd64` + `linux/arm64`).

## Workflows utilitários

- **`dockerhub-overview.yml`** — manual; roda `scripts/update-dockerhub-overview.sh`
  para publicar `docs/dockerhub/overview.md` como descrição do repo no Docker Hub.
- **`cleanup-artifacts.yml`** — diário; loop `gh api` apagando artifacts > 30 dias.
- **`scorecard.yml`** — upload SARIF do OpenSSF Scorecard (postura de supply-chain).

## Catálogo de ferramentas do repo

| Path | Usado por | O que verifica/faz |
|---|---|---|
| `tools/check-css-classes.py` | `blazor-client-validation` + checklist do PR | toda classe usada em `.razor` existe no Tailwind CSS gerado |
| `tools/check-form-a11y.py` | `blazor-client-validation` + checklist do PR | controles de form têm nome acessível |
| `tools/check-i18n-parity.py` | `blazor-client-validation` + checklist do PR | os 8 locales têm as mesmas chaves |
| `tools/check-i18n-placeholders.py` | manual / checklist do PR | placeholders batem entre os valores dos locales |
| `tools/check-spec-status.py` | manual / checklist do PR | bookkeeping de `.specs/` vs status em `docs/specs/` |
| `tools/perf/` | `perf.yml` | harness de carga `mock_ollama.py` + `loadtest.py` |
| `tools/ide-bundle/` | build da superfície IDE | bundle Node do IDE embutido |
| `scripts/classify-changes.sh` | job `changes` | diff → outputs `code`/`docs`/`docker` |
| `scripts/check-env-drift.sh` | job `test` + checklist do PR | env vars usadas no código documentadas em `.env.exemplo` |
| `scripts/check-pr-test-policy.sh` | job `pr-test-policy` | mudanças de produção vêm com testes |
| `scripts/import-locales.py` | manual | importa arquivos de locale do upstream |
| `scripts/update-dockerhub-overview.sh` | `dockerhub-overview.yml` | publica `docs/dockerhub/overview.md` no Docker Hub |
| `.ci/a11y/` | `a11y-audit.yml` | harness Playwright de audit/usability/screenshots |
| `.ci/coverage-*.txt` | gate de cobertura + ratchet | baselines commitadas de linha/branch |
| `coverlet.runsettings` | job `test` | coleta e exclusões de cobertura |
| `sonar-project.properties` | `code-quality.yml` + scans manuais | conjuntos de exclusão do SonarCloud |

## Reproduzir localmente

```bash
dotnet restore OpenWebUI.slnx
dotnet build OpenWebUI.slnx --configuration Release
dotnet test OpenWebUI.slnx --collect:"XPlat Code Coverage" --settings coverlet.runsettings

# drift guards (os mesmos 3 do CI; os 2 últimos são extras do checklist de PR)
python3 tools/check-css-classes.py
python3 tools/check-form-a11y.py
python3 tools/check-i18n-parity.py
python3 tools/check-i18n-placeholders.py
python3 tools/check-spec-status.py

# freshness do artefato tailwind
tailwindcss -i src/OpenWebUI.Client/tailwind.input.css \
  -o src/OpenWebUI.Client/wwwroot/css/tailwind.css --minify

# env drift + harness de perf
./scripts/check-env-drift.sh
python3 tools/perf/loadtest.py --scenario rps --duration 60 --workers 8
```
