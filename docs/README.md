# Documentação do Sistema

## Overview

Open WebUI portado para **.NET 10 / C# 14 / Blazor WebAssembly** — mesma interface, funcionalidades e configuração do upstream `open-webui/open-webui`, com backend ASP.NET Core Minimal APIs substituindo o FastAPI e frontend Blazor WASM substituindo o Svelte.

## Arquitetura

Clean Architecture em 5 projetos:

```
Api ──▶ Infrastructure ──▶ Application ──▶ Domain
Client ──▶ Application
```

- **Domain**: entidades (usuários, chats, mensagens, pastas, arquivos, modelos custom, memórias, notas, avaliações, conexões).
- **Application**: contratos/DTOs e interfaces de serviços.
- **Infrastructure**: EF Core + SQLite (`webui.db`, schema evoluído por EF Migrations com baseline de bases legadas), JWT, providers (Ollama, OpenAI-compatível), seed de conexões por env.
- **Api**: Minimal APIs (`/api/v1/*`, `/api/config`) + hosting do WASM via `MapStaticAssets` + espelho `/framework-assets/{stem}/{ext}` (boot resiliente a proxies que bloqueiam `.dat`/`.wasm`).
- **Client**: Blazor WASM com Tailwind v4 (tema upstream, `.dark` class, Inter).

## Estrutura de diretórios

| Dir | Conteúdo |
|---|---|
| `src/` | 5 projetos .NET |
| `tests/` | `OpenWebUI.Api.Tests` (NUnit) |
| `docs/` | documentação do sistema + `MIGRACAO-DOTNET.md` |
| `.claude/` | harness de agentes (rules, agents, skills, memory) |
| `.devin/` | `blueprint.yaml`, `config.json`, `skills/` (catálogo afonsoft/skills) |
| `.specs/` | SPECs SDD em andamento — executadas no modelo ADD |

## Quick Start

```bash
dotnet run --project src/OpenWebUI.Api   # http://localhost:8080
# ou
cp .env.exemplo .env && docker compose up -d   # http://localhost:3032
```

Primeiro usuário registrado vira admin — ou semeado no primeiro boot via `ADMIN_NAME`/`ADMIN_EMAIL`/`ADMIN_PASSWORD` (ver `.env.exemplo`). Conexões de providers podem ser semeadas por env (`OLLAMA_BASE_URL`, `OPENAI_API_KEY`, ...) — ver `.env.exemplo`. Para testes com infra completa (Ollama + Whisper): `docker compose -f docker-compose.full.yaml up -d`.

## Referências

- [en/DEPLOY-DOCKER.md](./en/DEPLOY-DOCKER.md) / [pt/DEPLOY-DOCKER.md](./pt/DEPLOY-DOCKER.md) — deploy com Docker (imagem publicada, compose, `.env`, volumes)
- [dockerhub/overview.md](./dockerhub/overview.md) — overview publicado na página do Docker Hub
- [technologies.md](./technologies.md) — tecnologias e versões
- [packages.md](./packages.md) — dependências NuGet
- [plugins.md](./plugins.md) — integrações e extensões
- [features.md](./features.md) — funcionalidades implementadas
- [api.md](./api.md) — endpoints da API
- [MIGRACAO-DOTNET.md](./MIGRACAO-DOTNET.md) — paridade com o upstream
- [AI-DRIVEN-DEVELOPMENT.md](./AI-DRIVEN-DEVELOPMENT.md) — modelo ADD: SDD na especificação, agentes na execução
