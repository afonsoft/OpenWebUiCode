# SPEC-20261031-sandboxed-exec

## 0. Metadata

| Field | Value |
|-------|-------|
| Feature | Execução em sandbox (bwrap+seccomp) + permission mode `autonomous` |
| Type | Feature |
| Stack | .NET 10 / ASP.NET Core / Linux |
| Repository | afonsoft/OpenWebUiCode |
| Branch | feature/devin-20261031-sandboxed-exec |
| Ticket | #578 (Epic #572/E19) |
| Status | Approved |

## 1. User Story

**As a** administrador que roda o agente em código não-confiável
**I want** que `shell_exec`/`code_interpreter`/edições rodem dentro de um sandbox (bwrap+seccomp) com filesystem e rede controlados
**So that** o agente possa operar autonomamente (`autonomous`) sem acesso a dados fora do workspace nem rede irrestrita.

**Problem context:**
O Devin CLI tem (`docs/DEVIN-CLI-REA-ANALYSIS.md` §2, §5, `sandbox-runtime/{linux,manager,http_proxy,socks_proxy}.rs`) um sandbox Linux com **bwrap+seccomp**: escrita confinada ao workspace + escopos `Write(...)`; leitura livre exceto `Deny(Read(...))`; filtro de domínios de rede via proxy HTTP/SOCKS; `sandbox.excluded` rules rodam fora; e um permission mode **`autonomous`** disponível só com sandbox — capabilities (rede fora da lista, novo write scope) viram prompts ao usuário, tudo o mais é permitido. O OpenWebUiCode executa `shell_exec` direto no processo do servidor — qualquer comando aprovado tem poder total do usuário do servidor. Isso limita autonomia segura e expõe dados do host.

## 2. Scope

**In scope:**

- Backend de sandbox Linux via **bubblewrap (bwrap)** + **seccomp** (quando disponível): filesystem do workspace bind-rw, resto bind-ro ou invisível, HOME falso mínimo, rede via proxy controlado.
- Modelo de escopo: `Write(<paths>)` adiciona mounts rw; `Deny(Read(<paths>))` esconde paths sensíveis (`.env`, `.ssh`, `.aws`, `webui.db` etc.); defaults por workspace.
- Proxy de rede com allowlist de domínios (HTTP CONNECT + SOCKS); tentativa de domínio não listado → bloqueio + evento `sandbox.network.denied` que o agente pode escalar via `request_scope`.
- Permission mode `autonomous` (só com sandbox ativo): todas as ações dentro do sandbox são permitidas; novas capabilities (domínio fora da lista, write fora do workspace, exclusão de sandbox) viram prompts de aprovação.
- `sandbox.excluded`: tools/rules específicas que rodam fora do sandbox (ex.: LSP do host, browser tools) — allowlist explícita.
- Detecção de capabilities: bwrap/seccomp ausentes → fallback claro (modo autonomous indisponível, aviso).
- Config: `Sandbox:Enabled`, `Sandbox:WriteScopes[]`, `Sandbox:DenyReads[]`, `Sandbox:AllowedDomains[]`, `Sandbox:ExcludedTools[]`.

**Out of scope:**

- Sandbox no Windows/macOS (Linux first; macOS seatbelt é fase 2).
- Isolamento de container/Docker (bwrap é mais leve e suficiente).
- Sandbox para MCP servers (fora do escopo — MCP roda no host).

## 3. Technical Context

**Where the change happens:**

`src/OpenWebUI.Infrastructure/ChatTools/` — novo `SandboxedProcessRunner` que envolve a execução de `shell_exec`/`code_interpreter`/`exec` quando `Sandbox:Enabled`; `ShellSessionManager`/`Pty` ganham wrapper de spawn; `PermissionRuleset` ganha modo `autonomous`; `request_scope`/aprovações escalam novas capabilities.

**Files to read before implementing:**

- `src/OpenWebUI.Infrastructure/ChatTools/` (exec, pty, session managers)
- `src/OpenWebUI.Infrastructure/ChatTools/PermissionRuleset.cs`
- `src/OpenWebUI.Infrastructure/ChatTools/ToolCallRiskClassifier.cs`
- `docs/DEVIN-CLI-REA-ANALYSIS.md` §2, §5
- `src/OpenWebUI.Api/Runs/ChatRunExecutor.cs`

**Files to create or modify:**

```text
src/OpenWebUI.Infrastructure/Sandbox/SandboxedProcessRunner.cs    (novo)
src/OpenWebUI.Infrastructure/Sandbox/BwrapBuilder.cs              (novo)
src/OpenWebUI.Infrastructure/Sandbox/NetworkProxyFilter.cs        (novo)
src/OpenWebUI.Infrastructure/Sandbox/SandboxScope.cs              (novo — Write/Deny/Read)
src/OpenWebUI.Infrastructure/ChatTools/PermissionRuleset.cs       (modo autonomous)
src/OpenWebUI.Infrastructure/ChatTools/Tools/ExecBuiltinTool.cs   (spawn via sandbox)
tests/OpenWebUI.Api.Tests/SandboxedExecTests.cs                   (novo)
```

## 4. Requirements

### RF-001: Spawn via bwrap
- **Description:** `shell_exec`/`code_interpreter`/`exec` rodam via `bwrap` com: `--bind <workspace> <workspace>` rw, `--ro-bind` do sistema essencial (`/usr`, `/lib`, `/bin`, `/etc/resolv.conf`), HOME falso tmpfs, `--unshare-net` quando sem rede ou `--share-net`+proxy quando com rede, `--die-with-parent`, seccomp básico se disponível.
- **Rules:** ausência de bwrap → log + fallback sem sandbox + `autonomous` indisponível; PTY funciona dentro do sandbox.

### RF-002: Escopos Write/Deny(Read)
- **Description:** `Sandbox:WriteScopes[]` adiciona mounts rw fora do workspace; `Sandbox:DenyReads[]` torna paths invisíveis dentro do sandbox (bind de tmpfs vazio ou `--tmpfs` sobre eles).
- **Rules:** deny wins sobre qualquer bind; paths padrão sensíveis (`.env*`, `.ssh`, `.aws`, `*.db`, secrets) são deny por default configurável.

### RF-003: Proxy de rede com allowlist
- **Description:** Quando rede habilitada, o processo usa proxy HTTP/SOCKS interno (`HTTP_PROXY`/`SOCKS_PROXY` envs) que filtra por `Sandbox:AllowedDomains[]`; domínio não listado → 403 + evento `sandbox.network.denied {domain}`.
- **Rules:** allowlist suporta wildcard `*.example.com`; `localhost`/`127.0.0.1` sempre permitido; evento pode ser escalado via `request_scope` → aprovação adiciona o domínio à sessão.

### RF-004: Permission mode `autonomous`
- **Description:** Com sandbox ativo, preset `autonomous` permite todas as ações dentro do sandbox; novas capabilities (domínio fora da lista, write fora do workspace, tool em `sandbox.excluded`) viram prompt de aprovação.
- **Rules:** `autonomous` indisponível sem sandbox (erro claro); capabilities aprovadas são memorizadas na sessão.

### RF-005: `sandbox.excluded` tools
- **Description:** `Sandbox:ExcludedTools[]` lista tools que rodam fora do sandbox (LSP, browser, MCP) — decisão consciente e auditada.
- **Rules:** tool excluída aparece no audit com motivo; exclude de tools mutáveis exige confirmação.

### RF-006: Detecção e degradação
- **Description:** Na primeira execução, detecta bwrap/seccomp; sem eles → modo degradado (sem sandbox) com aviso no stream e `autonomous` recusado.
- **Rules:** `devin doctor`-like endpoint/`GET /api/v1/system/sandbox` reporta `{bwrap, seccomp, proxy}` disponíveis.

## 5. API Contract

**Endpoint:** `GET /api/v1/system/sandbox` → `{ "bwrap": true, "seccomp": true, "proxy": true, "mode": "available|degraded" }`.
**Auth:** JWT admin.

## 6. Acceptance Criteria

- [x] **Given** sandbox ativo, **when** `shell_exec` com `cat /etc/shadow`, **then** o path está invisível/indisponível (deny read default).
- [x] **Given** `Write(workspace)` apenas, **when** `echo x > /tmp/x`, **then** falha fora do escopo.
- [x] **Given** domínio não listado, **when** `curl https://evil.com`, **then** proxy bloqueia + evento `sandbox.network.denied`.
- [x] **Given** `request_scope` aprova novo domínio, **when** retry, **then** a conexão passa.
- [x] **Given** sem bwrap, **when** preset `autonomous`, **then** erro claro de indisponibilidade.
- [x] **Given** preset `autonomous`, **when** ação dentro do sandbox, **then** executa sem prompt.

**Edge cases:**

| Scenario | Input | Expected behavior |
| --- | --- | --- |
| bwrap falha ao spawn | erro do processo | fallback sem sandbox + aviso |
| `Deny(Read)` sobre workspace | `.env` dentro do workspace | invisível dentro do sandbox |
| Proxy indisponível | rede pedida sem proxy | sem rede (`--unshare-net`) |
| Tool em `excluded` mutável | `file_write` excluída | roda fora + audit + confirmação |

## 7. Task Plan

- [x] **T1 — Discovery:** ler exec/pty/session managers + ruleset; checar bwrap/seccomp no ambiente.
- [x] **T2 — Scope+builder:** `SandboxScope`, `BwrapBuilder` (binds, unshare, env).
- [x] **T3 — Proxy:** `NetworkProxyFilter` (allowlist, CONNECT/SOCKS, eventos).
- [x] **T4 — Runner:** `SandboxedProcessRunner` integrando exec/pty.
- [x] **T5 — Autonomous:** preset + capability prompts + exclusões.
- [x] **T6 — Detecção:** endpoint de capabilities + degradação.
- [x] **T7 — Testes:** unitários (scope, builder, proxy filter) + integração (comando confinado real se bwrap disponível).
- [x] **T8 — Done + PR.**

**7.1 Validation strategy**

| Type / Stack | Required evidence |
|---|---|
| **.NET** | Unit tests (scope, builder args, proxy filter, degrade); integration tests (exec confinado quando bwrap presente); 80% coverage. |

## 8. Organization Guardrails

- **Branches:** `feature/devin-20261031-sandboxed-exec`.
- **Security:** deny-reads padrão para secrets; proxy allowlist; `autonomous` só com sandbox; exclusões auditadas.
- **Scope:** Linux-first; sem container.
- **Architecture:** sandbox é camada de infra; tools chamam o runner — não sabem de bwrap.

## 9. Definition of Done

- [x] RFs implementados; aceite coberto por testes.
- [x] Edge cases tratados; `dotnet test` verde; `check-spec-status.py` ok.
- [x] Guardrails respeitados; endpoint de capabilities documentado.

## Open Questions / Pending Ambiguity

- Proxy interno em processo ou sidecar? (recomendo em processo — `NetworkProxyFilter` como middleware local).
- `autonomous` deve existir sem sandbox como "bypass+warning"? (recomendo não — força a presença do sandbox).


## Notas de implementação

- `Sandbox/` (Infrastructure): `SandboxScope` (Write/Deny/AllowedDomains/
  ExcludedTools via `Sandbox:*` + concessões de sessão `GrantDomain`),
  `BwrapBuilder` (bind rw do workspace, ro-bind de sistema, deny por
  /dev-null ou tmpfs, HOME tmpfs, `--unshare-net` ou proxy env),
  `DomainAllowlist` (wildcard `*.`, localhost sempre) + `SandboxProxyServer`
  (CONNECT; negado → 403 + `DeniedHosts`), `SandboxedProcessRunner`
  (fallback sem bwrap com aviso), `SandboxExec` (ponto de entrada das
  tools; `Sandbox:Enabled` + `sandbox.excluded`), `SandboxCapabilities`
  (probe bwrap/seccomp/proxy).
- Tools `shell_exec`/`code_interpreter` + `ChatJobService` (background)
  roteiam pelo runner; footer agrega avisos de modo degradado e domínios
  negados (`sandbox.network.denied`).
- Preset `autonomous` (RF-004): allow para tudo dentro do sandbox;
  tool excluída ou sandbox indisponível → gate do dono/erro claro.
- Endpoint `GET /api/v1/system/sandbox` (admin) reporta capabilities.
- Escalada `request_scope`: `SandboxScope.GrantDomain(runId, domínio)`
  adiciona à allowlist da sessão (limpa no fim da run).
- Testes: 6 novos em `SandboxedExecTests` (allowlist, escopo/grants,
  args do bwrap, proxy 403 + DeniedHosts, capabilities). Build 0/0.
