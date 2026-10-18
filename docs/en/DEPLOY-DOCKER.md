# Docker deployment

Complete guide to running Open WebUI (.NET) — `afonsoft/open-webui` — with Docker: prebuilt image, compose layouts, `.env` reference, volumes, upgrades and troubleshooting.

> 🇧🇷 [Versão em português](../pt/DEPLOY-DOCKER.md)

## Prerequisites

- Docker Engine ≥ 24 (or Docker Desktop) with the Compose plugin (`docker compose version`)
- The host port below free (`3032` by default)

## Quick start

The image is published on [Docker Hub](https://hub.docker.com/r/afonsoft/open-webui)
and [GHCR](https://github.com/afonsoft/open-webui/pkgs/container/open-webui) on
every `v*.*.*` tag — **multi-arch (`linux/amd64` + `linux/arm64`)**, so it runs
unchanged on x86 servers and ARM boards (Raspberry Pi, Apple Silicon VMs).

**1. One command, nothing else needed:**

```bash
docker run -d --name open-webui \
  -p 3032:8080 \
  -v openwebui-data:/data \
  afonsoft/open-webui
```

Open `http://localhost:3032` — the **first registered user becomes admin** (or seed one via `ADMIN_*`, below).

**2. Compose with the published image (no clone, no build):**

```bash
curl -LO https://raw.githubusercontent.com/afonsoft/open-webui/main/docker-compose.hub.yaml
docker compose -f docker-compose.hub.yaml up -d
```

**3. Compose building from source (recommended for development):**

```bash
git clone https://github.com/afonsoft/open-webui.git
cd open-webui
cp .env.exemplo .env                 # edit values — see reference below
docker compose up -d --build
```

> `docker-compose.yaml` runs **only the app** — AI providers (Ollama/OpenAI) come from `.env` or the admin UI (Settings → Connections). For a local lab with Ollama + Whisper bundled, use `docker-compose.full.yaml`.

Pull a specific registry or pin a version:

```bash
docker pull afonsoft/open-webui:latest        # Docker Hub
docker pull ghcr.io/afonsoft/open-webui:latest # GHCR
docker pull afonsoft/open-webui:1.2.3          # pinned version
```

### Production layout

`docker-compose.prod.yaml` runs the published image with a **watchtower**
sidecar that polls for new tags every 5 minutes and rolls the container
forward automatically:

```bash
docker compose -f docker-compose.prod.yaml up -d
```

## Containers & volumes

| Item | Value | Notes |
|---|---|---|
| HTTP port | `8080` (container) | publish with `-p <host>:8080`; compose maps `${WEBUI_PORT:-3032}:8080` |
| Data volume | `/data` | SQLite `openwebui.db` + uploaded files — back this up |
| User | non-root `app` (uid **1654**) | `chown -R 1654:1654` bind-mounted host dirs |
| Healthcheck | `GET /health` → `200 {"status":true}` | already wired as the image `HEALTHCHECK` |
| Base image | `mcr.microsoft.com/dotnet/runtime-deps:10.0` | self-contained publish — no .NET runtime required on the host |

Compose mounts the named volume `openwebui` at `/data`. To use a host directory instead:

```yaml
    volumes:
      - ./data:/data        # chown -R 1654:1654 ./data first
```

## `.env` reference

`docker-compose.yaml` reads `.env` twice: `env_file` injects the variables into the container, and `${VAR}` interpolations resolve from it (`required: false` — a missing `.env` falls back to the defaults below).

### App

| Var | Default | Notes |
|---|---|---|
| `WEBUI_PORT` | `3032` | host port published → container `8080` |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` enables OpenAPI + WASM debugging |
| `ConnectionStrings__Default` | `Data Source=/data/openwebui.db` | EF Core connection — the image default already lands in the volume |
| `WEBUI_MEM_LIMIT` | `4g` | container memory limit (compose `deploy.resources`) |

### First admin (first boot only, empty database)

| Var | Default | Notes |
|---|---|---|
| `ADMIN_EMAIL` | — | login e-mail of the seeded admin |
| `ADMIN_PASSWORD` | — | required with `ADMIN_EMAIL` |
| `ADMIN_NAME` | `Admin` | display name |

Without these, the first user to sign up through the UI becomes admin.

### AI providers (seeded as connections on first boot only)

| Var | Default | Notes |
|---|---|---|
| `OLLAMA_BASE_URL` / `OLLAMA_BASE_URLS` | — | single URL or `;`-separated list; e.g. `http://host.docker.internal:11434` |
| `OPENAI_API_BASE_URL` / `OPENAI_API_BASE_URLS` | — | OpenAI-compatible endpoint(s) (LiteLLM, Azure, vLLM…) |
| `OPENAI_API_KEY` / `OPENAI_API_KEYS` | — | key(s) matching the URLs above |
| `RAG_EMBEDDING_MODEL` | `nomic-embed-text` | embedding model via Ollama (Knowledge/RAG) |
| `RAG_EMBEDDING_MODEL_OPENAI` | `text-embedding-3-small` | embedding model via OpenAI-compatible |
| `WHISPER_URL` | — | faster-whisper-server endpoint (speech-to-text) |

After the first boot these are managed in **Settings → Connections** (admin) — the env vars only seed an empty config.

### Auth providers (optional)

- **OAuth/OIDC:** `GOOGLE_CLIENT_ID/SECRET`, `GITHUB_CLIENT_ID/SECRET`, `MICROSOFT_CLIENT_ID/SECRET/TENANT_ID`, `OPENID_PROVIDER_URL`, `OPENID_CLIENT_ID/SECRET`
- **LDAP:** `LDAP_SERVER`, `LDAP_PORT`, `LDAP_USER_DN_TEMPLATE`, `LDAP_MAIL_ATTRIBUTE`, `LDAP_NAME_ATTRIBUTE`, `LDAP_SEARCH_BASE`

### Code execution / Jupyter (host installs only)

`PYTHON_PATH`, `JUPYTER_LOCAL_COMMAND`, `Jupyter__SpawnTimeoutSeconds` — **the image ships no Python/Jupyter**; Python tools and the local Jupyter terminal only work when the app runs outside the container.

## Full stack (local lab)

`docker-compose.full.yaml` adds **Ollama** and **faster-whisper** next to the app:

```bash
cp .env.exemplo .env
docker compose -f docker-compose.full.yaml up -d --build
docker exec -it ollama ollama pull llama3.2   # pull a model
```

- App: `http://localhost:3032` (`WEBUI_PORT`)
- Ollama: `http://localhost:11434` (`OLLAMA_PORT`) — auto-seeded as a connection (`OLLAMA_BASE_URL=http://ollama:11434`)
- Whisper: `http://localhost:8000` (`WHISPER_PORT`) — auto-seeded (`WHISPER_URL=http://whisper:8000`)
- Memory limits: `OLLAMA_MEM_LIMIT` (default `4g`), `WHISPER_MEM_LIMIT` (default `2g`)

## Upgrade

```bash
# prebuilt image
docker pull afonsoft/open-webui:latest
docker rm -f open-webui && docker run -d --name open-webui -p 3032:8080 -v openwebui-data:/data afonsoft/open-webui

# compose checkout
git pull && docker compose up -d --build
```

EF Core migrations run automatically at startup (legacy `webui.db` bases are baselined) — upgrading is safe as long as the `/data` volume survives.

## Backup

```bash
docker run --rm -v openwebui-data:/v -v "$PWD":/b alpine \
  tar -C /v -czf /b/openwebui-backup.tgz .
```

Stop the container first (or accept a live SQLite snapshot — the DB is in `/data/openwebui.db`).

## Releasing (maintainers)

Images are built and published **only on a version tag** — never on branch pushes:

```bash
git tag v1.2.3 && git push origin v1.2.3
```

`.github/workflows/release.yml` then: validates `X.Y.Z` → builds Linux/Windows archives → builds a **multi-arch image (amd64 + arm64)** and pushes `ghcr.io/afonsoft/open-webui:{X.Y.Z,latest}` → re-tags and pushes `docker.io/afonsoft/open-webui:{X.Y.Z,latest}` when the `DOCKERHUB_USERNAME`/`DOCKERHUB_TOKEN` secrets exist → creates the GitHub Release and a GitHub **deployment record**. The Docker Hub overview page is synced manually via the **🐳 Docker Hub Overview** workflow (`docs/dockerhub/overview.md` → Hub description).

## Troubleshooting

| Symptom | Check |
|---|---|
| Container restarts immediately | `docker logs open-webui` — usually a bind-mounted `/data` not owned by uid 1654 |
| Empty model list | no connection configured — seed `OLLAMA_BASE_URL`/`OPENAI_API_BASE_URL` on first boot or add in Settings → Connections |
| Cannot reach host Ollama | use `http://host.docker.internal:11434` (add `extra_hosts: ["host.docker.internal:host-gateway"]` on Linux) |
| Page stuck loading / old assets | the doc/app shell sends `no-cache` — hard-refresh; behind a proxy keep `X-Forwarded-*` headers |
| Healthcheck `unhealthy` | `curl -v http://localhost:8080/health` inside the container |
