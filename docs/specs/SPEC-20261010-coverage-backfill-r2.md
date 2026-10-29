# SPEC-20261010-coverage-backfill-r2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `coverage-backfill-r2` |
| Type | `Tests` |
| Stack | `.NET 10` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261010-coverage-backfill-r2` |
| Ticket | `GAP-tests-coverage-r2` |
| Status | `Completed` |
| Priority | `medium` |
| Depends on | SPEC-20261008-tests-coverage-gate |

## 1. User Story

**As a** maintainer **I want** coverage backfill on the remaining uncovered service blocks **So that** line/branch coverage recovers to the legacy baseline (~90.6/76.4) instead of sitting at 89.56/76.01.

**Problem context:** after PR #278 the measured gate is 89.56% line / 76.01% branch (floor 89.50/75.95). Largest still-uncovered blocks (cobertura XML, uncovered line counts): `CheckpointService` ~108, `PtySession` ~72, `LdapService` ~72, `ChatRunDispatcher` ~60, `McpClientService` ~54, `VideoEndpoints` ~58, `ChatJobService` ~48, `PythonToolExecutor` ~46.

## 2. Scope

**In scope:** new Api.Tests fixtures for `CheckpointService` (snapshot/revert via `git commit-tree` on temp repo), `ChatRunDispatcher` (queue serialization per ChatId, `bogus` connection failure path), `VideoEndpoints` + `ChatJobService` (endpoint guards/happy path via WebApplicationFactory), `PythonToolExecutor` (process spawn with temp script), `LdapService`/`McpClientService`/`PtySession` only where deterministic (no real LDAP/PTY flakiness).

**Out of scope:** raising baselines beyond honest measurement; e2e.

## 3. Acceptance Criteria

- Given `dotnet test ... --collect:"XPlat Code Coverage" --settings coverlet.runsettings --configuration Release`, then measured ≥90.0 line / ≥76.3 branch.
- Given new fixtures, then suite stays 0 failed; no new env-var leaks (clear env in `OneTimeTearDown`).

## 4. Task Plan

1. Fixtures per block, following `LspEndpointsTests`/`LspBuiltinToolsTests` recipes (binding via `config.SetAsync`, admin `DefaultUserRole="user"`, temp `DATA_ROOT` + cleanup).
2. Full Release suite + measure; bump `.ci/coverage-*.txt` to measured-0.05.

## Delivered

- PR #291 (abdc743) — fixtures determinísticas (CoverageBackfillR2Tests.cs); baselines 90.26/76.99.
