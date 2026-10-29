# SPEC-20261010-sonarcloud-first-analysis

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonarcloud-first-analysis` |
| Type | `Automation` |
| Stack | `.NET 10` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261010-sonarcloud-first-analysis` |
| Ticket | `GAP-automation-sonarcloud` |
| Status | `Completed` |
| Priority | `high` |
| Depends on | — |

## 1. User Story

**As a** maintainer **I want** the `sonarqube` job in `code-quality.yml` to actually analyze the repo **So that** the quality gate is a real signal, not a false-green placeholder.

**Problem context:** the job runs `dotnet sonarscanner /k:"afonsoft_open-webui" /o:"afonsoft"` gated on `secrets.SONAR_TOKEN`, but the project `afonsoft_open-webui` does NOT exist in the SonarCloud org (`api/projects/search` → 27 projects, none matches) and `SONAR_TOKEN` is absent from repo Actions secrets (`gh api repos/.../actions/secrets` → no `SONAR*`). The job has never produced an analysis — the `📊 Code Quality` check passes green without any Sonar data.

## 2. Scope

**In scope:**
- Provision the project on SonarCloud (manual, user action: https://sonarcloud.io → org `afonsoft` → Analyze new project → `afonsoft/OpenWebUiCode`, key `afonsoft_open-webui`).
- Add `SONAR_TOKEN` to repo Actions secrets (user or admin with repo settings).
- Verify the next `code-quality` run produces an analysis + real quality gate.
- Baseline triage of the first findings (bulk-dismiss boilerplate FPs, file real ones).

**Out of scope:** changing workflow YAML logic (already correct), migrating to self-hosted Sonar.

## 3. Acceptance Criteria

- Given a push to `main`, when `code-quality.yml` runs, then `dotnet sonarscanner end` uploads an analysis visible at sonarcloud.io/project/overview?id=afonsoft_open-webui.
- Given the quality gate, when it fails, then the check reports failure (real signal).

## 4. Task Plan

1. User: create SonarCloud project + token → repo secret `SONAR_TOKEN`.
2. Trigger a run; confirm upload + gate.
3. Triage first findings → issues/SPECs per sonarqube-autofix.

## Delivered

- Provisionamento feito fora da spec em 2026-10-10: projeto afonsoft_open-webui live no SonarCloud e check "SonarQube Analysis" verde no CI.
