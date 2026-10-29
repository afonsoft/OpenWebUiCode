# SPEC-20261010-sw-register-observability

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sw-register-observability` |
| Type | `Observability` |
| Stack | `.NET 10` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261010-sw-register-observability` |
| Ticket | `GAP-obs-sw-register` |
| Status | `Completed` |
| Priority | `low` |
| Depends on | — |

## 1. User Story

**As a** maintainer **I want** service-worker registration failures logged **So that** a silently failed first-load registration is diagnosable instead of invisible.

**Problem context:** `index.html:34` calls `navigator.serviceWorker.register('service-worker.js')` fire-and-forget — no `.catch`, no `.then` logging. In E2E the first registration silently failed (registration only appeared after a manual `register()` in DevTools); nothing in console told us why.

## 2. Scope

**In scope:** add `.then(reg => console.debug('SW registered', reg.scope))` + `.catch(err => console.warn('SW registration failed', err))` in `index.html`; optionally surface `navigator.serviceWorker.controller` state in the existing app log/console for support.

**Out of scope:** registration retry logic, push subscribe.

## 3. Acceptance Criteria

- Given a fresh load, when registration succeeds or fails, then a console line states the outcome (debug on success, warn on failure).
- `PwaTests` (Api.Tests) asserts the index.html snippet contains a `.catch(` on the register call.

## 4. Task Plan

1. Edit the inline register call in `index.html`.
2. Extend `PwaTests` string guard.

## Delivered

- PR #287 (df3f5ae) — index.html loga sucesso/falha do SW register; PwaTests cobre o AC.
