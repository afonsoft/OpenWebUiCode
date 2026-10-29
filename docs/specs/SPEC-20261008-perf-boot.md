# SPEC-20261008-perf-boot

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `perf-boot` |
| Type | `Refactor` |
| Stack | `.NET` (Blazor WASM hosting + JS) |
| Repository | `/home/ubuntu/repos/open-webui` |
| Branch | `feature/devin-20261008-perf-boot` |
| Ticket | [#189](https://github.com/afonsoft/OpenWebUiCode/issues/189) (Epic [#182](https://github.com/afonsoft/OpenWebUiCode/issues/182)) |
| Status | `Completed` |

## 1. User Story

**As a** user on a mobile or throttled connection
**I want** the app shell to paint fast and the WASM boot to cost less main-thread time
**So that** the app feels instant instead of ~6 s FCP / ~2–4.6 s TBT (indicative local Lighthouse).

**Problem context:**
Findings G1–G5. Local Lighthouse (indicative, localhost): **Perf 34 mobile / 58 desktop** (3-run medians). Evidence:
- `assets/fonts/Inter-Variable.ttf` = **805 KiB — 86 % of page weight**; loaded as TTF via `@font-face` (`tailwind.input.css:34-39`).
- 6 classic `<script>` (no `defer`) in `<head>` (`index.html:23-29`): `app.js`, `audio.js`, `codeexec.js`, `collab.js`, `xterm.js` (56 KiB, **85 % unused** on boot), `xterm-addon-fit.js`, `terminal.js` + render-blocking `xterm.css` — Lighthouse est. ~4.6 s savings (mobile).
- `dotnet.native.*.js` evaluates ~4.0 s on main thread; 18 long tasks (worst 1,926 ms); serial fetch chain document→boot.js→dotnet.js→dotnet.native with no preload hints; b64 mirror path adds decode+verify cost.
- `meta description` absent (SEO 91); `splash-dark.png` 500×500 for 224 px; no `fetchpriority` on LCP.

## 2. Scope

**In scope:**
- Font: convert Inter to WOFF2 (+ subset if feasible), preload, keep `font-display: swap`.
- Defer all `<head>` scripts; lazy-load xterm JS+CSS on first `TerminalView` mount (component-level dynamic import).
- Boot chain: preload/`fetchpriority` hints for `dotnet.js`, `dotnet.native.*.js`, `.wasm`; evaluate parallelizing `loadBootResource` fetches.
- `index.html`: `<meta name="description">`, splash `width`/`height`, `fetchpriority="high"` on LCP image; right-size `splash-dark.png`.
- Re-run Lighthouse (3× mobile + 3× desktop) for before/after numbers.

**Out of scope:**
- Blazor runtime/WASM internals rewrite; AOT pipeline changes beyond measuring feasibility.
- The b64 mirror fallback mechanism (keep — corporate-proxy requirement) — only measure/optimize its cost if trivial.
- Server-side compression/caching config (deploy-layer; note as follow-up if found).

## 3. Technical Context

**Where the change happens:**
`src/OpenWebUI.Client/wwwroot/index.html`, `tailwind.input.css` `@font-face`, `wwwroot/assets/fonts/`, `Components/TerminalView.razor` + `wwwroot/js/terminal.js` (lazy load), `wwwroot/js/boot.js` (`loadBootResource` chain), `src/OpenWebUI.Api` static-asset mapping if headers/hints needed there.

**Files to read before implementing:**
- `design-review-20261008.md` findings G + Lighthouse tables
- `index.html`, `boot.js`, `terminal.js`, `tailwind.input.css:34-39`
- `CLAUDE.md` — WASM boot mirror constraint (`framework-assets` b64) is a hard requirement

**Files to create or modify:**
```text
src/OpenWebUI.Client/wwwroot/index.html
src/OpenWebUI.Client/tailwind.input.css
src/OpenWebUI.Client/wwwroot/css/tailwind.css        # regenerated
src/OpenWebUI.Client/wwwroot/assets/fonts/           # + .woff2, retire .ttf
src/OpenWebUI.Client/wwwroot/assets/splash-dark.png  # right-sized
src/OpenWebUI.Client/wwwroot/js/terminal.js          # dynamic xterm loader
src/OpenWebUI.Client/Components/TerminalView.razor   # lazy-load trigger
src/OpenWebUI.Client/wwwroot/js/boot.js              # preload/parallel hints if viable
```

## 4. Requirements

### RF-001: WOFF2 font + preload
- **Description:** Replace `Inter-Variable.ttf` with WOFF2 (variable or static subset); keep `font-display: swap`; add `<link rel="preload" as="font" type="font/woff2" crossorigin>`.
- **Rules:** identical visual metrics; license intact; `.ttf` removed from deploy payload.
- **Input → Output:** 805 KiB TTF → ≤ ~300 KiB WOFF2 (target −60 %+).

### RF-002: Defer head scripts
- **Description:** `index.html:23-29` scripts get `defer` (order preserved); verify `window.openwebui` globals still initialize before `boot.js` runs and before first Blazor interop.
- **Input → Output:** parser-blocking scripts → deferred.

### RF-003: Lazy-load xterm
- **Description:** `xterm.js`, `xterm-addon-fit.js`, `terminal.js`, `xterm.css` load on demand (first `TerminalView` open / `/terminal` route) via dynamic script/link injection in `terminal.js` — not in `index.html`.
- **Input → Output:** eager 57+ KiB terminal payload → on-demand.

### RF-004: Boot-chain resource hints
- **Description:** Add `<link rel="preload" as="script">`/modulepreload for `js/boot.js`, `_framework/dotnet.js`, `dotnet.native.*.js` (fingerprinted name — resolve via build placeholder or runtime manifest) and `.wasm`; evaluate parallel fetch inside `loadBootResource`.
- **Input → Output:** serial chain → overlapping fetches; reduced boot critical path.

### RF-005: HTML polish
- **Description:** `<meta name="description">`; `splash-dark.png` served at display size (or `srcset`); `width`/`height` on splash/auth/chat `<img>`s; `fetchpriority="high"` on the LCP image.
- **Input → Output:** SEO audit green; zero CLS from images; right-sized assets.

### RF-006: Post-change measurement
- **Description:** Lighthouse mobile + desktop, 3 runs each, same Chrome/lh versions; record metric deltas in the SPEC before `Done`.
- **Input → Output:** before/after table → merged into review doc.

## 5. API Contract

N/A — client/hosting only. (If `MapStaticAssets` precompression turns out to matter, document as follow-up; endpoint shape unchanged.)

## 6. Acceptance Criteria

- [ ] **Given** a cold load **when** Lighthouse mobile runs 3× **then** median Perf improves vs. 34 baseline and FCP < 4 s (indicative local target; production numbers may differ).
- [ ] **Given** the network log **when** booting **then** font is `.woff2`, xterm assets absent until TerminalView/terminal route opens.
- [ ] **Given** `index.html` **when** parsed **then** every `<script>` in `<head>` is `defer`, meta description exists, splash has dimensions.
- [ ] **Given** `/terminal` or panel Terminal tab **when** first opened **then** xterm loads on demand and works.
- [ ] **Given** SEO audit **when** re-run **then** `meta-description` passes (→ ~100 SEO).

| Scenario | Input | Expected |
| --- | --- | --- |
| xterm lazy import fails | offline/edge | Terminal shows error state, rest of app unaffected |
| `defer` order | scripts that depend on each other | preserved order; no `openwebui` undefined |
| b64 mirror path | boot | still works (integrity check intact) |
| Font swap | FOUT | acceptable (swap already), metrics identical |

## 7. Task Plan

- [ ] **T1 — Baseline:** archive current `/tmp/lh/*.json` numbers into the SPEC.
- [ ] **T2 — Font:** RF-001 (convert, preload, update `@font-face`, remove ttf).
- [ ] **T3 — Scripts:** RF-002 + RF-003 (defer; lazy xterm loader).
- [ ] **T4 — Hints + HTML:** RF-004, RF-005.
- [ ] **T5 — Measure:** RF-006 3+3 runs; record deltas; fill DoD.
- [ ] **T6 — Done + PR.**

**7.1 Validation strategy:** Refactor — suite green, zero behavioral change; perf evidence = Lighthouse 3-run medians before/after (same env); xterm lazy-load manually verified.

## 8. Organization Guardrails

- **Branches:** `feature/devin-20261008-perf-boot`; never `main`.
- **Boot contract:** keep `autostart="false"` + the 3-layer `framework-assets` mirror intact — corporate-proxy requirement.
- **CSS:** regen `tailwind.css`, commit.
- **Assets:** Inter license retained in `assets/fonts/`.
- **Scope:** boot-path perf only — no feature changes.

## 9. Definition of Done

- [ ] RF-001…RF-006 implemented; before/after Lighthouse table recorded.
- [ ] Mobile perf median improved vs. baseline; no metric regressed > noise.
- [ ] Terminal lazy-load verified; font renders identically; SEO meta present.
- [ ] Build + tests green; guardrails respected.

## Open Questions / Pending Ambiguity

- Whether `dotnet.native` filename is stable enough for a static preload (else resolve via blazor.boot manifest at runtime).

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/197 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/189
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/182
