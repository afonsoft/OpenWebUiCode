# SPEC-20261010-offline-init-resilience

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `offline-init-resilience` |
| Type | `Bugfix` |
| Stack | `.NET 10` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261010-offline-init-resilience` |
| Ticket | `GAP-impl-offline-init` |
| Status | `Completed` |
| Priority | `medium` |
| Depends on | SPEC-20261009-notification-feed (delivered) |

## 1. User Story

**As a** user on flaky/offline network **I want** the app to keep rendering the cached shell when API calls fail during component init **So that** I see the app (maybe degraded) instead of the Blazor unhandled-error bar.

**Problem context:** E2E (PR #269 session) showed that with the service worker active and the shell cached, going offline still lands on the unhandled-error screen: `NotificationBell`, `Sidebar` and `Ide` call the API in `OnInitializedAsync`/`OnAfterRenderAsync` and let `HttpRequestException` (`Failed to fetch`) propagate to the Blazor error UI.

## 2. Scope

**In scope:**
- Wrap init-time API calls in `NotificationBell.razor`, `Sidebar.razor`, `Ide.razor` (and any other component fetching during first render — audit `@code OnInitializedAsync`/`OnAfterRenderAsync` bodies calling `ApiService`) with try/catch that logs and renders a degraded state (empty badge / cached data / retry button).
- Client.Tests asserting degraded render on injected fetch failure (stub `IJSRuntime`/fake HttpMessageHandler already used elsewhere).

**Out of scope:** full offline CRUD / queueing mutations; background sync.

## 3. Acceptance Criteria

- Given the SW-active app offline, when a component init fetch throws `HttpRequestException`, then the page keeps rendering (no `blazor-error-ui` bar) and shows degraded content.
- Given connectivity restored, when the component retries (poll/manual), then normal data loads without reload.

## 4. Task Plan

1. Add a small `SafeInitAsync(Func<Task>)` helper (or try/catch inline where simpler) in each component.
2. Tests per component: handler throws → assert no exception surfaces + fallback markup.

## Delivered

- PR #289 (9d4178c) — componentes sobrevivem a fetch offline com render degradado + retry.
