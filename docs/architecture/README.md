# Arquitetura

Documentos que descrevem e decidem a arquitetura do Open WebUI .NET.

## ADRs

| ADR | Decisão |
|-----|---------|
| [AD-0001](AD-0001-clean-architecture.md) | Clean Architecture em 5 projetos (Domain → Application → Infrastructure → Api; Client WASM referencia só Application) |
| [AD-0002](AD-0002-sqlite-ef-migrations.md) | SQLite + EF Core Migrations com baseline de bases legadas |
| [AD-0003](AD-0003-server-held-secrets.md) | Segredos somente no servidor (chaves de provider, URLs de tools) |
| [AD-0004](AD-0004-realtime-signalr.md) | Realtime via SignalR `/ws`; SSE para streaming de chat |
| [AD-0005](AD-0005-client-side-media-exec.md) | Voz e execução de código no cliente (Web Speech, Web Worker, Pyodide WASM) |
| [AD-0006](AD-0006-run-failure-visibility.md) | Falhas de run visíveis: SSE `{error}`, mensagem `⚠️` persistida e detalhe upstream real |

## Diagramas e design

- [architecture.md](architecture.md) — visão geral do sistema, camadas, sequência do chat, deploy, boot do WASM e pipeline de CI/CD (Mermaid)
- Fontes `.mmd` editáveis:
  - [openwebui_flow_system-overview.mmd](openwebui_flow_system-overview.mmd) — visão geral (browser/WASM ↔ API ↔ infra ↔ disco)
  - [openwebui_sequence_chat-rag-tools.mmd](openwebui_sequence_chat-rag-tools.mmd) — sequência do chat com RAG + tool loop
  - [openwebui_flow_deploy.mmd](openwebui_flow_deploy.mmd) — deploy via reverse proxy + container + volume
  - [openwebui_flow_client-boot.mmd](openwebui_flow_client-boot.mmd) — boot do WASM (framework-assets mirror, lazy xterm/Pyodide)
  - [openwebui_flow_ci-pipeline.mmd](openwebui_flow_ci-pipeline.mmd) — workflows do GitHub Actions e gates
- [runtime-architecture.html](runtime-architecture.html) — diagrama interativo standalone (archify)
- [../MIGRACAO-DOTNET.md](../MIGRACAO-DOTNET.md) — mapa de paridade com o upstream
