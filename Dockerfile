# syntax=docker/dockerfile:1
# Open WebUI (.NET) — multi-stage build, padrão afonsoft/KnowledgeRAG.
# Stage 1: .NET SDK 10 → publish self-contained para linux-x64/arm64.
# Stage 2: runtime-deps (Debian slim) + git, curl, gh CLI e Chromium do
# Playwright (browser_navigate/act/observe + browser_screenshot), tesseract
# (OCR do visual loop), volume /data com DATA_ROOT=/data — uploads,
# workspaces do IDE, checkpoints e worktrees persistem no volume (o content
# root /app pertence a root e não é gravável pelo processo); healthcheck em
# /health.
#
# NOTA: linux-musl-x64/Alpine não é possível — o cliente Blazor WASM hospedado
# restaura Microsoft.NETCore.App.Runtime.Mono.<rid>, que não é publicado para
# musl. bookworm-slim é a menor base glibc que mantém shell para o HEALTHCHECK.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore primeiro (cache de camadas) — só os projetos que a Api referencia.
COPY OpenWebUI.slnx ./
COPY src/OpenWebUI.Domain/OpenWebUI.Domain.csproj             src/OpenWebUI.Domain/
COPY src/OpenWebUI.Application/OpenWebUI.Application.csproj   src/OpenWebUI.Application/
COPY src/OpenWebUI.Infrastructure/OpenWebUI.Infrastructure.csproj src/OpenWebUI.Infrastructure/
COPY src/OpenWebUI.Api/OpenWebUI.Api.csproj                   src/OpenWebUI.Api/
COPY src/OpenWebUI.Client/OpenWebUI.Client.csproj             src/OpenWebUI.Client/
# NOTA: sem -r aqui — um RID no `dotnet restore` se propaga ao client Blazor
# WASM, que passa a exigir um pacote Mono.<rid> inexistente. O RID fica no
# `publish`, cuja restore implícita o delimita corretamente.
# O cache mount do BuildKit preserva o cache NuGet entre builds — a CI
# publica cache type=gha, então os restores dentro da imagem não baixam de novo.
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore src/OpenWebUI.Api/OpenWebUI.Api.csproj

# wasm-tools: sem ele o publish do client Blazor pula os passes de otimização
# (o SDK avisa "Publishing without optimizations"). O passo nativo roda emcc,
# que chama `python` — ausente na imagem sdk, então instala python3 e faz alias.
RUN dotnet workload install wasm-tools \
 && apt-get update \
 && apt-get install -y --no-install-recommends python3 \
 && ln -sf /usr/bin/python3 /usr/local/bin/python \
 && rm -rf /var/lib/apt/lists/*

COPY src/ src/
# O RID segue a arquitetura da própria imagem (nativa ou --platform/QEMU):
# `docker build` simples não injeta TARGETARCH, então uname é a fonte confiável.
RUN --mount=type=cache,target=/root/.nuget/packages \
    case "$(uname -m)" in \
      x86_64)  RID=linux-x64 ;; \
      aarch64) RID=linux-arm64 ;; \
      *) echo "Unsupported arch: $(uname -m)" >&2; exit 1 ;; \
    esac && \
    dotnet publish src/OpenWebUI.Api/OpenWebUI.Api.csproj \
      -c Release -r "$RID" --self-contained true -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime

# curl para o HEALTHCHECK; git para o workspace IDE; sudo para o agente
# (shell_exec roda com permissão total dentro do container — single-tenant);
# tesseract-ocr para o OCR opcional do browser visual loop.
RUN apt-get update \
 && apt-get upgrade -y \
 && apt-get install -y --no-install-recommends curl git sudo procps tesseract-ocr \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /data/workspaces

# GitHub CLI (binário multi-arch): o agente usa `gh`/`git push` com o
# GITHUB_TOKEN/GIT_ASKPASS injetado pelo AgentSecretsEnvService.
ARG GH_VERSION=2.102.0
RUN arch="$(uname -m)" \
 && case "$arch" in x86_64) GH_ARCH=amd64 ;; aarch64) GH_ARCH=arm64 ;; \
      *) echo "Unsupported arch: $arch" >&2; exit 1 ;; esac \
 && curl -fsSL "https://github.com/cli/cli/releases/download/v${GH_VERSION}/gh_${GH_VERSION}_linux_${GH_ARCH}.tar.gz" \
    | tar -xz -C /usr/local/bin --strip-components=2 "gh_${GH_VERSION}_linux_${GH_ARCH}/bin/gh"

WORKDIR /app
COPY --from=build /app/publish ./

# Chromium + libs de SO via driver do Playwright publicado em .playwright/
# (instala em /root/.cache/ms-playwright — mesmo path que BrowserSessionService
# e BrowserScreenshotService procuram; USER é root). --with-deps roda o
# apt-get das dependências do browser, então fica depois do COPY e antes do
# rm final das listas.
RUN case "$(uname -m)" in x86_64) N=linux-x64 ;; aarch64) N=linux-arm64 ;; \
      *) echo "Unsupported arch: $(uname -m)" >&2; exit 1 ;; esac \
 && /app/.playwright/node/$N/node /app/.playwright/package/cli.js install --with-deps chromium \
 && rm -rf /var/lib/apt/lists/*

# DATA_ROOT alimenta DataPaths.Root: tudo que o app grava em runtime
# (uploads, workspaces, checkpoints, worktrees) pousa no volume — sem
# isso o app tentaria criar /app/data e tomaria 500 (EACCES, /app é root).
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ConnectionStrings__Default="Data Source=/data/openwebui.db" \
    DATA_ROOT=/data

EXPOSE 8080
VOLUME ["/data"]
# root: o agente (shell_exec/jobs) precisa de permissão total no container —
# instalar ferramentas via apt, ler qualquer path, sudo sem senha.
USER root

# Probe /health — retorna 200 quando o processo está de pé.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD curl -fsS http://localhost:8080/health -o /dev/null || exit 1

ENTRYPOINT ["./OpenWebUI.Api"]
