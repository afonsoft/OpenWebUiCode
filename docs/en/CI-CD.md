# CI/CD & tooling

Map of every GitHub Actions workflow and the repository tooling that keeps the
.NET 10 / Blazor WASM port healthy — what each pipeline runs, which tools it
shells out to, what it gates on, and how to reproduce the checks locally.

> 🇧🇷 [Versão em português](../pt/CI-CD.md)

## Workflow inventory

| Workflow file | Display name | Triggers | Purpose |
|---|---|---|---|
| `ci-build-test.yml` | 🚀 CI Build & Test | push `main` + `feature/*` `fix/*` `bug/*` `hotfix/*` `chore/*` `test/*`, PR → `main`, dispatch | Release build, tests + coverage gate, Blazor WASM drift guards, Tailwind freshness, Docker build, baseline ratchet, admission gate |
| `a11y-audit.yml` | ♿ Accessibility Audit | PR / push `main`, dispatch | axe-core scan + usability/visual smoke (Playwright, mobile 375px + desktop) |
| `chat-e2e.yml` | 💬 Chat E2E (LLM real) | PR / push `main`, dispatch | Chat E2E against a real LLM provider (secrets-gated) |
| `code-quality.yml` | 📊 Code Quality | PR / push `main`, dispatch | Qodana + SonarCloud analysis |
| `security-scan.yml` | 🔒 Security Scan | push / PR `main`, weekly (Mon 02:00 UTC), dispatch | CodeQL, gitleaks, actionlint + zizmor, OSV-Scanner, hadolint, Snyk, Trivy |
| `scorecard.yml` | 🛡️ OpenSSF Scorecard | `branch_protection_rule`, weekly (Mon 07:27 UTC), push `main`, dispatch | OpenSSF Scorecard → SARIF |
| `perf.yml` | ⚡ Performance Load Test | weekly (Sun 03:30 UTC), dispatch | aiohttp load harness (`tools/perf/`), aggregate-rps gate, opens a tracking issue when the scheduled run fails |
| `release.yml` | 🚀 Release & Publish | tag `v*.*.*` | Release artifacts, multi-arch Docker images (GHCR + Docker Hub), deployment registration, GitHub Release |
| `dockerhub-overview.yml` | 🐳 Docker Hub Overview | dispatch | Syncs `docs/dockerhub/overview.md` to the Docker Hub page |
| `cleanup-artifacts.yml` | 🧹 Cleanup Artifacts | daily 03:23 UTC, dispatch | Deletes workflow artifacts older than 30 days |

All actions are pinned to full-length SHAs (with a `# vX.Y.Z` comment) — a
supply-chain hardening choice audited weekly by `zizmor`.

## `ci-build-test.yml` — the main pipeline

The admission pipeline. A `changes` job classifies the diff first
(`scripts/classify-changes.sh` → `code` / `docs` / `docker` outputs) so
docs-only PRs skip the expensive jobs and draft PRs run nothing.

| Job | What it does | Tools |
|---|---|---|
| `changes` | Classifies changed paths into `code`/`docs`/`docker` | `scripts/classify-changes.sh` |
| `build-openwebui` | `dotnet build OpenWebUI.slnx -c Release` on .NET 10 — skipped on draft PRs and docs-only diffs | .NET SDK 10 |
| `test` | All NUnit tests + coverage gate (below); uploads `test-results` (TRX) and `coverage-xml` (Cobertura) artifacts; posts a sticky PR coverage comment; runs `scripts/check-env-drift.sh` | `dotnet test`, Coverlet collector, `coverlet.runsettings`, Python gate script |
| `blazor-client-validation` | Client drift guards — fails if `.razor` uses undefined CSS classes, form controls lack accessible names, or the 8 locales drift apart | `tools/check-css-classes.py`, `tools/check-form-a11y.py`, `tools/check-i18n-parity.py` |
| `tailwind-freshness` | Regenerates `wwwroot/css/tailwind.css` and diffs it — the committed artifact must match the input | `tailwindcss` CLI v4.3.3 (cached binary) |
| `docker-build` | Builds the production `Dockerfile` (PRs: only when `code` or `docker` changed) | Docker |
| `pr-test-policy` | Fails a PR that changes production code without touching `tests/` | `scripts/check-pr-test-policy.sh` |
| `coverage-ratchet` | **main pushes only** — raises `.ci/*baseline*` files to the measured rates and commits `[skip ci]`; never lowers | Python + `git` |
| `admission-verdict` | Single "Gate / CI" required check aggregating all job results | `github-script` |
| `ci-summary` | CI Dashboard summary in the run page | `github-script` |

### Coverage gate mechanics

- `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings`
  emits `coverage.cobertura.xml`.
- `coverlet.runsettings` excludes the Client assembly, test assemblies,
  `*.Migrations.*`, `Microsoft.AspNetCore.OpenApi.*` and compiler-generated
  code (`Obsolete`, `GeneratedCode`, `ExcludeFromCodeCoverage` attributes).
- The gate compares measured **line** and **branch** rates against committed
  baselines: `.ci/coverage-baseline.txt` and `.ci/coverage-branch-baseline.txt`,
  with `TOL = 0.05` for run-to-run measurement noise.
- Baselines are a **ratchet**: `coverage-ratchet` pushes them up whenever main
  coverage improves, so the floor only moves in one direction automatically.
- When a legitimate small dip lands (e.g. noise beyond `TOL`), reset the
  baseline file to the latest measured value on `main` — same procedure as the
  "reset baselines to reality" precedent (`ci(coverage): reset baselines to
  post-E19 reality`). Never bump `TOL` to hide real drops.

## `a11y-audit.yml` — accessibility

Matrix job over the Playwright harness in `.ci/a11y/` (`npm ci` + `playwright
install chromium`), against the published app:

| Check | Script | Artifacts |
|---|---|---|
| axe-core (mobile 375px + desktop) | `.ci/a11y/audit.js` | `a11y-report.json` |
| Usability + visual smoke (mobile 375px + desktop) | `.ci/a11y/usability.js` | `usability-report.json` + `screenshots/usability/` |

## `chat-e2e.yml` — real-provider chat E2E

Secrets-gated: without `LLM_API_KEY` the job exits green with a notice. With
`LLM_PROVIDER`/`LLM_BASE_URL`/`LLM_API_KEY`/`LLM_MODEL` configured it runs the
`E2E_CHAT=1` NUnit suite that exercises `/api/chat/completions` over real SSE
and prints the transcripts as artifacts (`chat-e2e-results`).

## `code-quality.yml` — static analysis

- **Qodana** (`JetBrains/qodana-action`) — full-solution .NET inspection,
  `qodana-report` artifact.
- **SonarCloud** (`dotnet-sonarscanner`, Java 17) — analyzes to
  [`afonsoft_open-webui`](https://sonarcloud.io/project/overview?id=afonsoft_open-webui).
  `sonar-project.properties` keeps docs/skills/vendor directories out of
  analysis and EF migrations out of duplication counting; the same exclusion
  set is passed via `/d:sonar.exclusions` in the workflow.

## `security-scan.yml` — security toolbox

| Tool | Scope | Output |
|---|---|---|
| CodeQL (init/autobuild/analyze) | C# + JavaScript SAST | GitHub code scanning |
| `gitleaks git --redact` | Secret scanning over git history | job log |
| `actionlint` + `zizmor` | Workflow lint + Actions security audit | `zizmor-sarif` → code scanning |
| `osv-scanner scan --recursive` | Dependency vulnerabilities (OSV DB) | job log |
| `hadolint` | `Dockerfile` lint | job log |
| `snyk test --severity-threshold=high --all-projects` | Dependency + code vulns | job log |
| `trivy` (`aquasecurity/trivy-action`) | Built container image scan | SARIF → code scanning |

## `perf.yml` — load test

Publishes the app, boots it against `tools/perf/mock_ollama.py` (fake Ollama on
`127.0.0.1:11434` with scripted triggers — `__delegate__`, `__words__N`,
`__sleep__MS`) and drives `tools/perf/loadtest.py` (aiohttp) through the
scenarios `rps` / `sessions` / `subagents` / `real` (dispatch-selectable or
`all`). A minimum aggregate-rps gate fails the run; scheduled failures open or
update a tracking issue (`issue-on-failure` job). See `tools/perf/README.md`.

## `release.yml` — tag → artifacts → images → release

On `v*.*.*` tags: validates the version tag → builds release artifacts
(`openwebui-<ver>`) → matrix `docker buildx` per platform → multi-arch manifest
→ registers a `production` deployment (URL from the `DEPLOY_URL` repo variable)
→ creates the GitHub Release with artifacts. Images land on both GHCR and
Docker Hub (`linux/amd64` + `linux/arm64`).

## Utility workflows

- **`dockerhub-overview.yml`** — manual; runs `scripts/update-dockerhub-overview.sh`
  to publish `docs/dockerhub/overview.md` as the Docker Hub repo description.
- **`cleanup-artifacts.yml`** — daily; `gh api` loop deleting artifacts > 30 days.
- **`scorecard.yml`** — OpenSSF Scorecard SARIF upload (supply-chain posture).

## Repo tooling catalog

| Path | Used by | What it checks/does |
|---|---|---|
| `tools/check-css-classes.py` | `blazor-client-validation` + PR checklist | every class used in `.razor` exists in the generated Tailwind CSS |
| `tools/check-form-a11y.py` | `blazor-client-validation` + PR checklist | form controls have an accessible name |
| `tools/check-i18n-parity.py` | `blazor-client-validation` + PR checklist | the 8 locales carry the same keys |
| `tools/check-i18n-placeholders.py` | manual / PR checklist | placeholders match across locale values |
| `tools/check-spec-status.py` | manual / PR checklist | `.specs/` bookkeeping vs `docs/specs/` status |
| `tools/perf/` | `perf.yml` | `mock_ollama.py` + `loadtest.py` load harness |
| `tools/ide-bundle/` | IDE surface build | Node bundle for the in-app IDE |
| `scripts/classify-changes.sh` | `changes` job | diff → `code`/`docs`/`docker` outputs |
| `scripts/check-env-drift.sh` | `test` job + PR checklist | env vars used in code are documented in `.env.exemplo` |
| `scripts/check-pr-test-policy.sh` | `pr-test-policy` job | production changes ship tests |
| `scripts/import-locales.py` | manual | imports upstream locale files |
| `scripts/update-dockerhub-overview.sh` | `dockerhub-overview.yml` | pushes `docs/dockerhub/overview.md` to Docker Hub |
| `.ci/a11y/` | `a11y-audit.yml` | Playwright audit/usability/screenshots harness |
| `.ci/coverage-*.txt` | coverage gate + ratchet | committed line/branch coverage baselines |
| `coverlet.runsettings` | `test` job | coverage collection + exclusions |
| `sonar-project.properties` | `code-quality.yml` + manual scans | SonarCloud exclusion sets |

## Reproduce locally

```bash
dotnet restore OpenWebUI.slnx
dotnet build OpenWebUI.slnx --configuration Release
dotnet test OpenWebUI.slnx --collect:"XPlat Code Coverage" --settings coverlet.runsettings

# drift guards (same 3 that run in CI; the last 2 are PR-checklist extras)
python3 tools/check-css-classes.py
python3 tools/check-form-a11y.py
python3 tools/check-i18n-parity.py
python3 tools/check-i18n-placeholders.py
python3 tools/check-spec-status.py

# tailwind artifact freshness
tailwindcss -i src/OpenWebUI.Client/tailwind.input.css \
  -o src/OpenWebUI.Client/wwwroot/css/tailwind.css --minify

# env drift + perf harness
./scripts/check-env-drift.sh
python3 tools/perf/loadtest.py --scenario rps --duration 60 --workers 8
```
