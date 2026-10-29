# Open WebUI (.NET)

![Open WebUI (.NET)](https://raw.githubusercontent.com/afonsoft/OpenWebUiCode/main/src/OpenWebUI.Client/wwwroot/assets/icons/icon-512.png)

**The Open WebUI experience rewritten in .NET 10 — a single self-contained container**: Blazor WebAssembly UI + ASP.NET Core backend + SQLite, with chat, RAG, tools, channels, notes, admin **and a full coding-agent platform** — faithful to the upstream `open-webui/open-webui` layout and features, extended with parallel sub-agents, durable agent memory and a workspace/IDE, no Python or Node required at runtime.

## Quick start

```bash
docker pull afonsoft/open-webui

docker run -d --name open-webui \
  -p 3032:8080 \
  -v openwebui-data:/data \
  afonsoft/open-webui
```

- App: `http://localhost:3032` — the first registered user becomes admin (or seed one via `ADMIN_EMAIL` / `ADMIN_PASSWORD` / `ADMIN_NAME`).
- Health probe: `GET /health` (already wired as the image `HEALTHCHECK`).

The same image is also on GHCR: `docker pull ghcr.io/afonsoft/openwebuicode`.

**Multi-arch:** `linux/amd64` + `linux/arm64` — the same tag runs on x86 servers and ARM boards (Raspberry Pi, Apple Silicon).

## What you get

- **Chat** — streaming completions (OpenAI-compatible `/api/chat/completions`), Ollama + OpenAI-compatible providers, multi-model, arena mode, regenerate/edit/versions.
- **Coding-agent platform** — background agent runs with mid-run steering, `delegate_task` sub-agents in parallel worktrees (depth ≤ 3), checkpoints & revert, Plan/Build modes, durable `memory_save`/`memory_search` with 6h auto-distill.
- **Workspace / IDE** — repo binding (GitHub), file explorer, editor, integrated terminal (PTY) and browser tool, LSP-driven code intelligence.
- **Tools & MCP** — workspace tools (HTTP or Python code), MCP tool servers with discovery + `tools/call`, model filters (inlet/outlet), pipelines proxying.
- **RAG / Knowledge** — files and collections, `#` references, embeddings (Ollama or OpenAI-compatible), web search integration.
- **Realtime collab** — channels and collaborative notes over SignalR; approvals/notifications pushed live.
- **Admin** — users/groups/RBAC, connections, models, functions, skills, evals, analytics, automations, i18n (en/pt-BR and more), PWA installable.
- **Performance** — `HybridCache` (stampede-safe L1) on hot paths: provider model lists, config reads, API-key auth resolution, MCP server/tool lookups.
- **Ops built-in** — EF Core migrations at startup, non-root image, healthcheck, SQLite in a volume.

## Ports & volumes

| Item | Value |
|---|---|
| HTTP port | `8080` (container) — map with `-p <host>:8080` |
| Data volume | `/data` — SQLite `openwebui.db` + uploads |
| User | non-root `app` (uid **1654**) — `chown` bind-mounted host dirs accordingly |

## Common environment variables

All ASP.NET Core settings accept `Section__Key` env vars. Frequently used:

| Variable | Default | Purpose |
|---|---|---|
| `ConnectionStrings__Default` | `Data Source=/data/openwebui.db` | EF Core connection (SQLite file lives in the volume) |
| `OLLAMA_BASE_URL` / `OLLAMA_BASE_URLS` | — | Seed Ollama connection(s) on first boot (`;`-separated) |
| `OPENAI_API_BASE_URL` / `OPENAI_API_BASE_URLS` | — | Seed OpenAI-compatible connection(s) on first boot |
| `OPENAI_API_KEY` / `OPENAI_API_KEYS` | — | API keys matching the URLs above |
| `RAG_EMBEDDING_MODEL` | `nomic-embed-text` | Embedding model via Ollama (RAG) |
| `RAG_EMBEDDING_MODEL_OPENAI` | `text-embedding-3-small` | Embedding model via OpenAI-compatible |
| `WHISPER_URL` | — | faster-whisper-server endpoint (speech-to-text) |
| `ADMIN_NAME` / `ADMIN_EMAIL` / `ADMIN_PASSWORD` | — | Seed the first admin user on an empty database |
| `GOOGLE_*` / `GITHUB_*` / `MICROSOFT_*` / `OPENID_*` | — | OAuth/OIDC social login |
| `LDAP_*` | — | Enterprise LDAP bind |
| `WEBUI_PORT` | `3032` | Host port (docker compose only — not read by the app) |

## Docker Compose

```yaml
services:
  openwebui:
    image: afonsoft/open-webui:latest
    ports:
      - "3032:8080"
    volumes:
      - openwebui:/data
    restart: unless-stopped

volumes:
  openwebui:
```

Full stack with Ollama + Whisper for local testing lives in `docker-compose.full.yaml` on GitHub. For production, `docker-compose.prod.yaml` adds a watchtower sidecar that auto-updates the container on new tags (5-minute poll).

## Docs

Deploy guide (EN/PT-BR): `docs/en/DEPLOY-DOCKER.md` / `docs/pt/DEPLOY-DOCKER.md` in the repository. Source: https://github.com/afonsoft/OpenWebUiCode
