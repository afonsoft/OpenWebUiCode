# Deploy com Docker

Guia completo para rodar o Open WebUI (.NET) — `afonsoft/open-webui` — com Docker: imagem pronta, layouts de compose, referência do `.env`, volumes, upgrade e troubleshooting.

> 🇺🇸 [English version](../en/DEPLOY-DOCKER.md)

## Pré-requisitos

- Docker Engine ≥ 24 (ou Docker Desktop) com o plugin Compose (`docker compose version`)
- Porta do host livre (`3032` por padrão)

## Início rápido

A imagem é publicada no [Docker Hub](https://hub.docker.com/r/afonsoft/open-webui)
e no [GHCR](https://github.com/afonsoft/open-webui/pkgs/container/open-webui) a
cada tag `v*.*.*` — **multi-arch (`linux/amd64` + `linux/arm64`)**, rodando
igual em servidores x86 e placas ARM (Raspberry Pi, VMs Apple Silicon).

**1. Um comando, sem mais nada:**

```bash
docker run -d --name open-webui \
  -p 3032:8080 \
  -v openwebui-data:/data \
  afonsoft/open-webui
```

Abra `http://localhost:3032` — o **primeiro usuário registrado vira admin** (ou semeie um via `ADMIN_*`, abaixo).

**2. Compose com a imagem publicada (sem clone, sem build):**

```bash
curl -LO https://raw.githubusercontent.com/afonsoft/open-webui/main/docker-compose.hub.yaml
docker compose -f docker-compose.hub.yaml up -d
```

**3. Compose buildando do fonte (recomendado para desenvolvimento):**

```bash
git clone https://github.com/afonsoft/open-webui.git
cd open-webui
cp .env.exemplo .env                 # ajuste os valores — referência abaixo
docker compose up -d --build
```

> O `docker-compose.yaml` sobe **só o app** — os providers de IA (Ollama/OpenAI) vêm do `.env` ou da UI admin (Configurações → Conexões). Para um lab local com Ollama + Whisper junto, use o `docker-compose.full.yaml`.

Baixar de um registry específico ou fixar versão:

```bash
docker pull afonsoft/open-webui:latest        # Docker Hub
docker pull ghcr.io/afonsoft/open-webui:latest # GHCR
docker pull afonsoft/open-webui:1.2.3          # versão fixada
```

### Layout de produção

O `docker-compose.prod.yaml` sobe a imagem publicada com um sidecar
**watchtower** que verifica novas tags a cada 5 minutos e atualiza o
container automaticamente:

```bash
docker compose -f docker-compose.prod.yaml up -d
```

## Containers & volumes

| Item | Valor | Notas |
|---|---|---|
| Porta HTTP | `8080` (container) | publique com `-p <host>:8080`; o compose mapeia `${WEBUI_PORT:-3032}:8080` |
| Volume de dados | `/data` | SQLite `openwebui.db` + arquivos enviados — é isso que você faz backup |
| Usuário | non-root `app` (uid **1654**) | `chown -R 1654:1654` em diretórios bind-mounted |
| Healthcheck | `GET /health` → `200 {"status":true}` | já configurado como `HEALTHCHECK` da imagem |
| Imagem base | `mcr.microsoft.com/dotnet/runtime-deps:10.0` | publish self-contained — sem runtime .NET no host |

O compose monta o volume nomeado `openwebui` em `/data`. Para usar um diretório do host:

```yaml
    volumes:
      - ./data:/data        # rode chown -R 1654:1654 ./data antes
```

## Referência do `.env`

O `docker-compose.yaml` lê o `.env` duas vezes: `env_file` injeta as variáveis no container e interpolações `${VAR}` resolvem dele (`required: false` — sem `.env` caem os defaults abaixo).

### App

| Var | Default | Notas |
|---|---|---|
| `WEBUI_PORT` | `3032` | porta do host publicada → container `8080` |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` habilita OpenAPI + debug do WASM |
| `ConnectionStrings__Default` | `Data Source=/data/openwebui.db` | conexão EF Core — o default da imagem já cai no volume |
| `WEBUI_MEM_LIMIT` | `4g` | limite de memória do container (`deploy.resources` do compose) |

### Primeiro admin (só no primeiro boot, base vazia)

| Var | Default | Notas |
|---|---|---|
| `ADMIN_EMAIL` | — | e-mail de login do admin semeado |
| `ADMIN_PASSWORD` | — | obrigatória junto com `ADMIN_EMAIL` |
| `ADMIN_NAME` | `Admin` | nome de exibição |

Sem essas vars, o primeiro usuário que se cadastrar pela UI vira admin.

### Providers de IA (semeados como conexões só no primeiro boot)

| Var | Default | Notas |
|---|---|---|
| `OLLAMA_BASE_URL` / `OLLAMA_BASE_URLS` | — | URL única ou lista separada por `;`; ex.: `http://host.docker.internal:11434` |
| `OPENAI_API_BASE_URL` / `OPENAI_API_BASE_URLS` | — | endpoint(s) compatíveis com OpenAI (LiteLLM, Azure, vLLM…) |
| `OPENAI_API_KEY` / `OPENAI_API_KEYS` | — | chave(s) correspondentes às URLs acima |
| `RAG_EMBEDDING_MODEL` | `nomic-embed-text` | modelo de embeddings via Ollama (Knowledge/RAG) |
| `RAG_EMBEDDING_MODEL_OPENAI` | `text-embedding-3-small` | modelo de embeddings via OpenAI-compatível |
| `WHISPER_URL` | — | endpoint faster-whisper-server (speech-to-text) |

Depois do primeiro boot elas são gerenciadas em **Configurações → Conexões** (admin) — as env só semeiam uma config vazia.

### Providers de auth (opcionais)

- **OAuth/OIDC:** `GOOGLE_CLIENT_ID/SECRET`, `GITHUB_CLIENT_ID/SECRET`, `MICROSOFT_CLIENT_ID/SECRET/TENANT_ID`, `OPENID_PROVIDER_URL`, `OPENID_CLIENT_ID/SECRET`
- **LDAP:** `LDAP_SERVER`, `LDAP_PORT`, `LDAP_USER_DN_TEMPLATE`, `LDAP_MAIL_ATTRIBUTE`, `LDAP_NAME_ATTRIBUTE`, `LDAP_SEARCH_BASE`

### Execução de código / Jupyter (só instalação no host)

`PYTHON_PATH`, `JUPYTER_LOCAL_COMMAND`, `Jupyter__SpawnTimeoutSeconds` — **a imagem não traz Python/Jupyter**; tools Python e o terminal Jupyter local só funcionam quando o app roda fora do container.

## Stack completa (lab local)

O `docker-compose.full.yaml` sobe **Ollama** e **faster-whisper** junto ao app:

```bash
cp .env.exemplo .env
docker compose -f docker-compose.full.yaml up -d --build
docker exec -it ollama ollama pull llama3.2   # baixar um modelo
```

- App: `http://localhost:3032` (`WEBUI_PORT`)
- Ollama: `http://localhost:11434` (`OLLAMA_PORT`) — semeado como conexão (`OLLAMA_BASE_URL=http://ollama:11434`)
- Whisper: `http://localhost:8000` (`WHISPER_PORT`) — semeado (`WHISPER_URL=http://whisper:8000`)
- Limites de memória: `OLLAMA_MEM_LIMIT` (default `4g`), `WHISPER_MEM_LIMIT` (default `2g`)

## Upgrade

```bash
# imagem pronta
docker pull afonsoft/open-webui:latest
docker rm -f open-webui && docker run -d --name open-webui -p 3032:8080 -v openwebui-data:/data afonsoft/open-webui

# checkout com compose
git pull && docker compose up -d --build
```

As migrations EF Core rodam automaticamente no startup (bases legadas `webui.db` ganham baseline) — o upgrade é seguro desde que o volume `/data` sobreviva.

## Backup

```bash
docker run --rm -v openwebui-data:/v -v "$PWD":/b alpine \
  tar -C /v -czf /b/openwebui-backup.tgz .
```

Pare o container antes (ou aceite um snapshot quente do SQLite — o banco está em `/data/openwebui.db`).

## Release (mantenedores)

As imagens são buildadas e publicadas **somente quando uma tag de versão é criada** — nunca em push de branch:

```bash
git tag v1.2.3 && git push origin v1.2.3
```

O `.github/workflows/release.yml` então: valida `X.Y.Z` → builda os arquivos Linux/Windows → builda uma **imagem multi-arch (amd64 + arm64)** e publica `ghcr.io/afonsoft/open-webui:{X.Y.Z,latest}` → retagueia e publica `docker.io/afonsoft/open-webui:{X.Y.Z,latest}` quando os secrets `DOCKERHUB_USERNAME`/`DOCKERHUB_TOKEN` existem → cria a GitHub Release e o registro de **deployment** do GitHub. A página de overview do Docker Hub é sincronizada manualmente pelo workflow **🐳 Docker Hub Overview** (`docs/dockerhub/overview.md` → descrição do Hub).

## Troubleshooting

| Sintoma | Verificar |
|---|---|
| Container reinicia em loop | `docker logs open-webui` — geralmente `/data` bind-mounted sem dono uid 1654 |
| Lista de modelos vazia | nenhuma conexão configurada — semeie `OLLAMA_BASE_URL`/`OPENAI_API_BASE_URL` no primeiro boot ou adicione em Configurações → Conexões |
| Não alcança o Ollama do host | use `http://host.docker.internal:11434` (adicione `extra_hosts: ["host.docker.internal:host-gateway"]` no Linux) |
| Página presa carregando / assets velhos | o shell do app sai com `no-cache` — hard-refresh; atrás de proxy preserve os headers `X-Forwarded-*` |
| Healthcheck `unhealthy` | `curl -v http://localhost:8080/health` dentro do container |
