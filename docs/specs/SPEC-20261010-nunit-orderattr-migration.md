# SPEC-20261010-nunit-orderattr-migration

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `nunit-orderattr-migration` |
| Type | `Tests` |
| Stack | `.NET 10` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261010-nunit-orderattr-migration` |
| Ticket | `GAP-tests-nunit-order` |
| Status | `Completed` |
| Priority | `low` |
| Depends on | — |

## 1. User Story

**As a** maintainer **I want** the deprecated NUnit `[Order]` attributes replaced **So that** the test build stops emitting ~441 CS0618 warnings and stays compatible with future NUnit versions.

**Problem context:** `dotnet test` prints a wall of `'OrderAttribute' is obsolete: ... use DependsOnTest or DependsOnFixture instead` warnings (~441 call sites across Api.Tests fixtures).

## 2. Scope

**In scope:** replace `[Order(n)]`-ordered test flows with `[DependsOnTest(nameof(OtherTest))]` / `[DependsOnFixture]` per NUnit guidance — only in fixtures where ordering is semantically required; where the order was incidental, drop the attribute entirely (preferred: tests should be independent).

**Out of scope:** changing test logic/assertions.

## 3. Acceptance Criteria

- Given `dotnet build tests/OpenWebUI.Api.Tests`, then zero CS0618 `OrderAttribute` warnings.
- Given the suite, then the same tests pass; ordered sequences still run in dependency order.

## 4. Task Plan

1. List fixtures using `[Order]`; decide per-fixture drop-vs-DependsOnTest.
2. Apply mechanical replacement; full suite run.

## Delivered

- PR #290 (c15ad86) — 0 usos de [Order], 31 DependsOnTest; zero warnings CS0618.
