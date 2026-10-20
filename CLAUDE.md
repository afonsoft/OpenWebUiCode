# CLAUDE.md — Open WebUI (.NET)

## Missão

Fork do [Open WebUI](https://github.com/open-webui/open-webui) migrado para **.NET 10 / C# 14 / Blazor WebAssembly**, mantendo layout, funcionalidade e configuração fiéis ao upstream (backend FastAPI + frontend Svelte originais). Este arquivo é o ponto de entrada de agentes: carregado automaticamente antes de cada sessão.

## Tech Stack

| Camada | Tecnologia | Versão |
|--------|-----------|--------|
| Runtime | .NET | 10.0 (SDK 10.0.401, C# 14) |
| Frontend | Blazor WebAssembly | — |
| Backend | ASP.NET Core Minimal APIs | — |
| ORM | Entity Framework Core + SQLite | 10.0 |
| Auth | JWT + `Bearer sk-*` (chaves de API) | — |
| CSS | Tailwind CSS v4 (saída estática commitada) | — |
| Testes | NUnit | — |
| Container | Docker multi-stage (`:3032 → :8080`) | — |
| CI | GitHub Actions (`ubuntu-latest`) | — |

## Estrutura do Projeto

```
src/OpenWebUI.Domain         # entidades, sem dependências
src/OpenWebUI.Application    # contratos/DTOs e interfaces
src/OpenWebUI.Infrastructure # EF Core + serviços (JWT, providers, config)
src/OpenWebUI.Api            # Minimal APIs + hosting do WASM
src/OpenWebUI.Client         # Blazor WASM (UI fiel ao upstream)
tests/OpenWebUI.Api.Tests    # NUnit
OpenWebUI.slnx               # solução
Dockerfile, docker-compose.yaml (só app, lê .env), docker-compose.full.yaml (infra de testes), .env.exemplo
```

Dependências apontam para dentro: `Api → Infrastructure → Application → Domain`. O `Client` referencia apenas `Application`.

## Caminhos por Plataforma

| Plataforma | Config | Skills | Rules | Knowledge |
|-----------|--------|--------|-------|-----------|
| Claude Code / Devin CLI | `CLAUDE.md` | `.claude/skills/` | `.claude/rules/` | `.claude/knowledge/` |
| Devin | `.devin/config.json` | `.devin/skills/` | via `read_config_from` | — |

## Comandos

```bash
dotnet restore OpenWebUI.slnx
dotnet build OpenWebUI.slnx --configuration Release
dotnet test tests/OpenWebUI.Api.Tests
dotnet run --project src/OpenWebUI.Api   # app em http://localhost:8080

# Docker
cp .env.exemplo .env                     # variáveis de ambiente (opcional)
docker compose up -d --build             # app em http://localhost:3032 (WEBUI_PORT)
docker compose -f docker-compose.full.yaml up -d --build   # app + Ollama + Whisper (testes)

# Regenerar CSS Tailwind (após editar classes em .razor)
tailwindcss -i src/OpenWebUI.Client/tailwind.input.css \
  -o src/OpenWebUI.Client/wwwroot/css/tailwind.css --minify

# Drift guards do client (rodar antes de PRs que tocam .razor/i18n —
# os mesmos 3 executam no CI, job "Blazor WASM Client Validation")
python3 tools/check-css-classes.py   # classes usadas vs definidas
python3 tools/check-form-a11y.py     # controles sem nome acessível
python3 tools/check-i18n-parity.py   # paridade de chaves nos 8 locales
python3 tools/check-spec-status.py   # drift de bookkeeping .specs/ vs docs/specs/
```

## Code Standards

### DO

- Manter paridade visual/funcional com o upstream `open-webui/open-webui` (referência: `git fetch upstream --depth 1 && git show FETCH_HEAD:<path>`).
- Documentação, comentários e nomes de testes em português (pt-BR); código e commits em inglês.
- Documentação XML (`///`) em APIs públicas.
- `async/await` com sufixo `Async`; nunca `.Result`/`.Wait()`.
- Regenerar `wwwroot/css/tailwind.css` após alterar classes em `.razor` e commitar o resultado.
- Endpoints de tarefas/LLM retornam erro (ex.: `NotFound`) quando o provider falha — nunca valor enlatado que o cliente possa persistir sobre dados do usuário.

### DON'T

- Não fazer push/commit direto em `main`.
- Não modificar `.github/workflows/` sem revisão humana.
- Não commitar secrets, `.env`, `webui.db` ou bases SQLite.
- Não editar `wwwroot/css/tailwind.css` manualmente (é gerado).
- Não usar `UseStaticFiles`/`MapFallbackToFile` para o WASM — usar `app.MapStaticAssets()` (placeholders `#[.{fingerprint}]` só são resolvidos por ele).
- O boot WASM passa por `js/boot.js` (`autostart="false"`): assets de `_framework` que não sejam `.js` são buscados no espelho sem extensão `GET /framework-assets/{stem}/{ext}` (com `?enc=b64` como fallback anti content-sniffing) para sobreviver a proxies corporativos que bloqueiam `.dat`/`.wasm`.

## Hard Rules

1. `main` é protegida — merge apenas via PR.
2. Todos os testes devem passar antes do merge.
3. APIs públicas exigem documentação XML.
4. Nunca commitar secrets, tokens ou connection strings.
5. Não modificar `.github/workflows/` sem revisão humana.
6. Não usar `--no-verify` ou `--force` sem aprovação explícita.

## Soft Rules

1. Modificar `Dockerfile`/`docker-compose.yaml` → confirmar com o usuário.
2. Mudança de schema do SQLite → criar migration: `dotnet ef migrations add <Nome> --project src/OpenWebUI.Infrastructure --startup-project src/OpenWebUI.Api`. `DatabaseMigrator` aplica no startup e faz baseline de bases legadas (`webui.db` antigas sem `__EFMigrationsHistory`).
3. Adicionar dependência NuGet → verificar breaking changes.
4. Deletar arquivos → exigir justificativa.

## Agent Loop

Padrão: **Plan-and-Execute**.

1. Receber tarefa → carregar `CLAUDE.md` + `.claude/rules/`.
2. Ler `.claude/memory/orchestrator_stats.md` e `.claude/MEMORY.md`.
3. Apresentar plano de execução quando multi-arquivo.
4. Executar em branch dedicada.
5. Loop de verificação: `build → test → CI`.
6. Atualizar memória e abrir PR.

## Response Style

- Português (pt-BR) para o usuário; commits em inglês (Conventional Commits).
- Conciso e direto, sem preâmbulos.
- Referências de arquivo como `path:linha`.

## Workflows

- Feature: `feature/{Agent}-{YYYYMMDD}-{descricao}` → implementar → testes → PR para `main`.
- Bug fix: `fix/{descricao}` → reproduzir → corrigir → regressão → PR.
- Seed de conexões por env: `OLLAMA_BASE_URL(S)`, `OPENAI_API_BASE_URL(S)`, `OPENAI_API_KEY(S)` (`;`-separados, primeiro boot). Lista completa em `.env.exemplo`.
- Primeiro usuário registrado vira admin — ou semeado por `ADMIN_NAME`/`ADMIN_EMAIL`/`ADMIN_PASSWORD` quando a base está vazia.

## Referências

- `.claude/rules/` — guardrails (global + por domínio)
- `.claude/agents/` — sub-agentes (review, plan, test)
- `.claude/CONTEXT.md` — engenharia de contexto
- `.claude/RULES.md` — hard/soft rules detalhadas
- `.claude/TOOLS.md` — ferramentas e integrações
- `.claude/WORKFLOWS.md` — automação e pipelines
- `.claude/MEMORY.md` — decisões e lições cross-session
- `.devin/blueprint.yaml` — setup do ambiente Devin
- `.specs/` — SPECs SDD em andamento (execução no modelo ADD — `docs/AI-DRIVEN-DEVELOPMENT.md`)
- `docs/` — documentação do sistema (overview, tecnologias, pacotes, features, API)
- `docs/MIGRACAO-DOTNET.md` — mapa de paridade com o upstream
