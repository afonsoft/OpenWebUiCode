# SPEC-20261010-spec-status-sweep

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `spec-status-sweep` |
| Type | `Documentation` |
| Stack | `.NET 10` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261010-spec-status-sweep` |
| Ticket | `GAP-doc-stale-specs` |
| Status | `Completed` |
| Priority | `low` |
| Depends on | SPEC-20261008-spec-status-reconciliation (delivered) |

## 1. User Story

**As a** maintainer **I want** delivered SPECs in `.specs/` marked `Completed` and moved to `docs/specs/` **So that** the backlog view only shows genuinely open work.

**Problem context:** 7 SPECs sit in `.specs/` with `Status: Approved` although their features shipped: `a11y-form-semantics`, `a11y-keyboard-focus`, `a11y-motion-status`, `a11y-nav-landmarks` (a11y guards + audit pass in CI — `check-form-a11y.py`, axe gate), `fix-undefined-css-classes` (`check-css-classes.py` runs in CI), `i18n-parity` (`check-i18n-parity.py` runs in CI). `perf-boot` and `ui-polish` need per-SPEC verification before flipping (no guard found — verify acceptance criteria against code or leave open with a note).

## 2. Scope

**In scope:** for each of the 8 `Approved` SPECs, verify delivery evidence, flip `Status` to `Completed` + archive under `docs/specs/`, or annotate remaining work if not delivered.

**Out of scope:** implementing missing acceptance criteria (that becomes its own SPEC).

## 3. Acceptance Criteria

- Given `.specs/`, then no file claims `Approved` for a feature already merged and guarded in CI.
- Given `docs/specs/`, then each archived SPEC shows `Completed` + delivery evidence (PR/commit).

## 4. Task Plan

1. Per SPEC: read acceptance criteria → map to merged PRs/CI guards.
2. Flip + move delivered ones; list the rest with pending items in the sweep commit message.

## Delivered

- PR: this sweep PR (issue #285: https://github.com/afonsoft/OpenWebUiCode/issues/285)
- 8 SPECs verified with merged delivery PRs (#191–#198): all flipped to `Completed` and archived under `docs/specs/`.
