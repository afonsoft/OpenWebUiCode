# Changelog

Todas as mudanças notáveis deste projeto são documentadas aqui, seguindo
[Keep a Changelog](https://keepachangelog.com/pt-BR/1.0.0/) e
[SemVer](https://semver.org/lang/pt-BR/).

## [Unreleased]

### Added

- **Plataforma de agente** (SPEC-15 e leva anterior): binding de repo por chat
  com **worktree git exclusiva** (`worktrees/{owner}__{repo}/{chatId}`, branch
  `chat/{chatId}`) sobre checkout base compartilhado — resolução estrita por
  chat, sem vazamento do binding global; seletor de três modos
  **chat/plan/build** no composer com `build` barrado sem repo vinculado;
  escolha autônoma de opções nos presets `smart`/`auto`/`always-allow`
  (evento auditável `decision_auto`); workspace acessível em telas mobile.
- `UseForwardedHeaders` (`X-Forwarded-For/Proto/Host`) — `redirect_uri` do
  OAuth e cookies corretos atrás de reverse proxy (padrão do agent-harness).
- Documentação de arquitetura: 5 ADRs (`docs/architecture/AD-0001`–`AD-0005`),
  diagramas de deploy/sequência, índice `docs/architecture/README.md`.
- Auditoria de paridade atualizada em `docs/MIGRACAO-DOTNET.md`
  (33 routers upstream × estado real: ~20 migrados, ~12 parciais, ~5 pendentes).
- Cobertura de testes elevada para **97,4% de linhas / 88,5% de branches**
  (311 testes de API): suites novas para chats, canais, auth/usuários,
  workspace, modelos/arquivos/avaliações, tasks, superfície da API, OAuth,
  automações, permissões e serviços de infraestrutura — incluindo caminhos
  de erro e borda. `coverlet.runsettings` exclui Client WASM, código gerado
  OpenAPI e assemblies de teste da medição.

### Fixed

- Checkout base divergente não trava mais o bind de repos: com tree limpa o
  HEAD divergente vira `backup/{branch}-{ts}` e o base é resetado para o ref
  do `origin` (#346).
- `AutomationScheduler` não derruba mais o host no shutdown
  (`OperationCanceledException` tratada como parada graciosa) — PR #43.
- Warning CS8602 em `AutomationEndpointsTests`.

## [0.1.0] - 2026-10-01

Primeira entrega completa da migração **Open WebUI → .NET 10 / Blazor WebAssembly**.

### Added

- **Base** (PRs #3–#9): solução Clean Architecture (`Domain` → `Application` →
  `Infrastructure` → `Api` + `Client` WASM), auth JWT + chaves `sk-*`, chat com
  streaming SSE, sidebar/chats/folders/pins/share, workspace (models, prompts,
  memories, knowledge básico), arquivos/anexos, avaliações 👍/👎, tasks LLM
  (título/follow-ups/tags), layout fiel ao upstream (Tailwind v4 + paleta oklch),
  Dockerfile + docker-compose.
- **Epic gap-analysis-20261001** — 13 slices de paridade (PRs #29–#41):
  - `ef-migrations` (#29): EF Core Migrations com baseline de `webui.db` legadas.
  - `auth-sso-rbac` (#30): OAuth/OIDC (Google, GitHub, Microsoft), LDAP, grupos/RBAC.
  - `realtime-channels` (#31): canais em grupo via SignalR `/ws` + `@modelo`.
  - `rag-knowledge` (#32): Knowledge com embeddings + retrieval vetorial.
  - `tools-functions` (#33): tools HTTP com function calling e seleção por chat.
  - `analytics` (#34): dashboard admin-only de métricas.
  - `code-execution` (#35): execução JS (Web Worker) e Python (Pyodide) no chat.
  - `i18n` (#36): pt-BR/en-US, troca de idioma sem reload.
  - `image-generation` (#37): OpenAI Images + botão por mensagem + config admin.
  - `missing-pages` (#38): `/playground`, abas Avaliações e Configurações no admin.
  - `pwa-offline` (#39): manifest, service worker do shell, `no-cache` no documento.
  - `automations-calendar` (#40): automações agendadas + calendário mensal.
  - `voice` (#41): ditado STT, TTS por mensagem e modo Call (Web Speech API).
- **Harness de agentes** (PRs #10–#13, #28): catálogo afonsoft/skills, `.claude/`,
  `.devin/`, 13 SPECs de paridade e relatório de gap-analysis.

### Removed

- Todo o código legado do fork: backend FastAPI, frontend SvelteKit, Docker/K8s
  upstream, configs Node/Python e workflows não-.NET (PR #5). Histórico git
  reescrito (commit único) para zerar Releases/Packages/Contributors herdados.
