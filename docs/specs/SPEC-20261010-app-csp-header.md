# SPEC-20261010-app-csp-header

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `app-csp-header` |
| Type | `Security` |
| Stack | `.NET 10` |
| Repository | `afonsoft/open-webui` |
| Branch | `feature/devin-20261010-app-csp-header` |
| Ticket | `GAP-sec-csp-absent` |
| Status | `Completed` |
| Priority | `medium` |
| Depends on | — |

## 1. User Story

**As a** maintainer **I want** the app to emit its own `Content-Security-Policy` **So that** the policy is intentional (covers `worker-src`, `connect-src` for API/WS/SSE) rather than inherited report-only noise from the hosting proxy.

**Problem context:** production console (user log, 2026-10-09) shows `Content-Security-Policy-Report-Only` violations for `script-src` (service-worker.js), `worker-src` fallback, `connect-src 'none'`. The app emits **no** CSP — grep for `Content-Security` in `src/` returns nothing; the header comes from the afonsoft.dev proxy/Cloudflare. WASM legitimately needs `'unsafe-eval'`/`wasm-unsafe-eval`, workers need `worker-src 'self'`, SSE/API/ws need `connect-src`.

## 2. Scope

**In scope:** middleware (or `UseWhen` on responses) setting a real CSP, e.g. `default-src 'self'; script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval'; worker-src 'self'; connect-src 'self' https: wss: data:; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; font-src 'self' data:`; validated headless via a test asserting the header on `GET /` and that WASM boot assets still load (existing boot smoke test).

**Out of scope:** nonce-based CSP for inline boot script (report-only iteration can come later), CSP-RO reporting endpoint.

## 3. Acceptance Criteria

- Given `GET /`, when the response arrives, then a `Content-Security-Policy` header is present with `worker-src 'self'` and a `connect-src` allowing API/SSE/ws.
- Given the WASM boot, when assets load, then no CSP violation blocks `_framework`/`framework-assets`/boot.js.
- Existing boot/SPA tests stay green (add header assertion to a PwaTests/Program smoke test).

## 4. Task Plan

1. Small middleware before `MapStaticAssets` adding the header.
2. Test: header present + values; boot smoke unchanged.

## Delivered

| Item | Link |
| --- | --- |
| PR | https://github.com/afonsoft/open-webui/pull/288 |
