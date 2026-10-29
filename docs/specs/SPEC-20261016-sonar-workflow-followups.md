# SPEC-20261016-sonar-workflow-followups

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-workflow-followups` |
| Type | `Security` + `Automation` |
| Repository | `/home/ubuntu/repos/open-webui` |
| Status | `Delivered` |
| Ticket | [#394](https://github.com/afonsoft/OpenWebUiCode/issues/394) · Epic [#392](https://github.com/afonsoft/OpenWebUiCode/issues/392) |
| Fonte | `gap-analysis`/`sonarqube-autofix` run 2026-10-16 — gap `GAP-automation-sonar-followups` |

## 1. User Story

**As a** mantenedor do fork
**I want** as pendências de workflow do SonarCloud rastreadas de forma durável e aplicadas com revisão humana
**So that** as 3 vulnerabilidades `githubactions:*` não se percam e o gate de qualidade pare de contar ~2150 smells de código vendor/gerado.

**Problem context:**
SPEC-20261015-sonar-remediation (Done, PR #378, issue #377 fechada) deixou 3 itens marcados `HUMANO` + RF-C1 por exigirem edição de `.github/workflows/` (hard rule — revisão humana obrigatória). Hoje essas pendências existem **somente** em `.sonar_devin_auto_fix/SONAR_FIX_REVIEW_NOTES.md`, que é **gitignored** — num clone limpo a informação some; não há issue aberta rastreando.

## 2. Scope

**In scope:**
- **RF-001 (tracking):** abrir GitHub Issue de follow-up listando os 3 achados + RF-C1 (ação deste run, após gate).
- **RF-002 (humano):** `.github/workflows/perf.yml:76` — `pip install` com `--only-binary :all:` + versões pinadas (S8541 + S8544).
- **RF-003 (humano):** `.github/workflows/ci-build-test.yml:211` — `curl -L` → avaliar `--proto-redir -https` ou equivalente (S6506).
- **RF-004 (humano):** `.github/workflows/code-quality.yml` — estender `sonar.exclusions` para `docs/**, skills/**, .agents/**, .claude/skills/**, .devin/**, .ci/**` (tira ~2150 smells vendor/gerados + 16 vulns vendor do radar).

**Out of scope:**
- Novos achados de produto (zerados na remediação); smells de produto restantes (backlog contínuo).

## 3. Technical Context

- Evidência: `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` (itens HUMANO) + `SONAR_FIX_REVIEW_NOTES.md` §"Pendente — revisão humana".
- `.sonar_devin_auto_fix/` está em `.gitignore` (commit 981ceae00).
- Hard rule repo: `.github/workflows/` não é editado sem revisão humana → execução é manual/assistida.

## 4. Requirements

- **RF-001:** issue de tracking aberta e linkada nos notes.
- **RF-002/003/004:** aplicados por humano ou com aprovação explícita; verificados pelo próximo scan SonarCloud.

## 5. Acceptance Criteria

- [ ] Issue de tracking existe e referencia este SPEC + o board.
- [ ] Após aplicação humana: próximo scan SonarCloud mostra 0 `githubactions:*` e queda de ~2150 smells.
- [ ] QG dos PRs deixa de reprovar por ruído vendor.

## 6. Task Plan

- [x] T1 — criar issue de tracking (gate do gap-analysis).
- [x] T2 — humano aplica RF-002/003/004.
- [x] T3 — validar no próximo scan + fechar issue.
