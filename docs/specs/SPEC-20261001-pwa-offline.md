# SPEC-20261001-pwa-offline

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `pwa-offline` |
| Type | `Feature` |
| Stack | `.NET / Blazor` |
| Repository | `afonsoft/OpenWebUiCode` |
| Branch | `feature/devin-20261001-pwa-offline` |
| Ticket | `[pwa-offline] Issue #23` |
| Status | `Completed` |

## 1. User Story

**As a** usuário do Open WebUI
**I want** instalar a app como PWA com cache do shell
**So that** ela abra como app nativo e o carregamento inicial seja instantâneo, como no upstream.

**Problem context:**
Sem manifest nem service worker (`docs/MIGRACAO-DOTNET.md:70` — ⬜). `index.html` atual não registra PWA. Atenção: issue upstream conhecida — servir `index.html` sem `Cache-Control` causa loop de reload após upgrade; adotar `no-cache` no documento desde o início.

## 2. Scope

**In scope:**
- `manifest.webmanifest` (nome, ícones 192/512, `display: standalone`, theme-color `#171717`).
- Service worker: cache do shell Blazor (framework + css + assets) estratégia cache-first para fingerprinted assets e network-first para o documento/API.
- `Cache-Control: no-cache` em `index.html`/documentos (previne o bug de reload infinito do upstream).
- Prompt de instalação nativo do navegador funcionando.

**Out of scope:**
- Offline de dados (chat offline com sync) — só shell estático.
- Push notifications.
- Background sync.

## 3. Technical Context

**Where the change happens:**
`Client/wwwroot` (manifest, `service-worker.js`, registro no `index.html`), `Api` (headers de cache no pipeline de static files).

**Files to read before implementing:**
- `src/OpenWebUI.Client/wwwroot/index.html`, `src/OpenWebUI.Api/Program.cs` (`MapStaticAssets` config)

**Files to create or modify:**
```text
src/OpenWebUI.Client/wwwroot/{manifest.webmanifest,service-worker.js}
src/OpenWebUI.Client/wwwroot/index.html, assets/icons/*
src/OpenWebUI.Api/Program.cs (cache headers)
```

## 4. Requirements

### RF-001: Manifest
- **Description:** Manifest válido linkado no `index.html` com ícones e tema dark.
- **Rules:** `start_url` `/`; `display: standalone`.
- **Input → Output:** instalação pelo navegador habilitada

### RF-002: Service worker
- **Description:** Registra SW que cacheia assets fingerprinted e revalida documento/API.
- **Rules:** update do SW aplica na próxima carga sem loop; falha de rede de API propaga erro real (sem cache de respostas de API).
- **Input → Output:** reload → shell servido do cache

### RF-003: Cache headers
- **Description:** `index.html` com `Cache-Control: no-cache`; assets fingerprinted `immutable`.
- **Rules:** não quebrar `MapStaticAssets`.
- **Input → Output:** headers corretos no response

## 5. API Contract

N/A (infra de hosting). Feature flag `pwa` em `/api/config` se quisermos opt-out `[A DEFINIR]` — recomendado: sempre on.

## 6. Critérios de Aceite

- [ ] Lighthouse reporta "instalável".
- [ ] Segunda carga carrega shell do cache (DevTools → SW ativo).
- [ ] `index.html` sai com `Cache-Control: no-cache`.
- [ ] Upgrade de versão não prende usuário em reload loop.

## Delivered

- PR: https://github.com/afonsoft/OpenWebUiCode/pull/39 (merged)
- Issue: https://github.com/afonsoft/OpenWebUiCode/issues/25
- Epic: https://github.com/afonsoft/OpenWebUiCode/issues/14
