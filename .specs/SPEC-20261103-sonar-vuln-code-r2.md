# SPEC-20261103-sonar-vuln-code-r2

## 0. Metadata

| Field | Value |
| --- | --- |
| Feature | `sonar-vuln-code-r2` |
| Type | `Security` |
| Stack | `.NET` (C#/Blazor) + `Docker` |
| Repository | `/home/ubuntu/repos/OpenWebUiCode` |
| Branch | `fix/sonar-vuln-code-r2-20261103` |
| Ticket | `GH#650` |
| Status | `Done` |
| Parent | sonarqube-autofix 2026-11-03 · board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md` |

## 1. User Story

**As a** mantenedor do fork
**I want** corrigir as 5 issues de segurança abertas no escopo do produto (`src/`, `Dockerfile`)
**So that** o board do SonarCloud zera as VULNERABILITY acionáveis e o pipeline publica sem findings reais.

**Problem context:** segunda rodada do sweep sonar (a primeira, `SPEC-20261031-sonar-vuln-code`, limpou o lote de out/31). 4 vulns + 1 smell docker no mesmo arquivo.

| Issue | Regra/Sev | Arquivo:linha | Problema |
| --- | --- | --- | --- |
| `AaGj…` | `roslyn.sonaranalyzer.security.cs:S7044` MAJOR | `src/OpenWebUI.Client/Services/AuthService.cs:186` | API traversal via input não sanitizado em `AuthService.CreateRequest()` — path/URL construído com dado do usuário |
| `AaGj…` | `csharpsquid:S7039` MAJOR | `src/OpenWebUI.Api/Program.cs:491` | CSP permissiva — endurecer `Content-Security-Policy` |
| `AaGj…` | `csharpsquid:S5693` MAJOR | `src/OpenWebUI.Client/Components/ChatView.razor:2297` | Upload sem limite de tamanho (`InputFile`/request content length) |
| `AaGj…` | `docker:S6471` MINOR | `Dockerfile:140` | `USER root` como default da imagem final |
| `AaGj…` | `docker:S7031` MINOR | `Dockerfile:64` | RUN consecutivas que podem ser mescladas (smell, mesmo arquivo) |

## 2. Scope

**In scope:** as 5 issues acima — fix cirúrgico por arquivo, sem mudança de comportamento exceto a mitigação pedida pela regra.

**Out of scope:**
- As 3 `json:S2068` (pt-BR.json) — tratadas como FP-triage em `SPEC-20261103-sonar-vuln-i18n-fp`.
- 32 vulns stale de vendor (`shell:S6505`/`S6506` em `skills/`/`.agents/`/`.claude/`/`.devin/` — dirs já excluídos do scan; resolução é ação manual no SonarCloud, ver board).
- Redesign da CSP além do mínimo para a regra (hardening progressivo fica para SPEC própria se o usuário quiser).

## 3. Technical Context

**Where the change happens:** client WASM (`AuthService`, `ChatView`), pipeline HTTP da API (`Program.cs`, registro de headers) e imagem final (`Dockerfile`).

**Files to read before implementing:**
- `src/OpenWebUI.Client/Services/AuthService.cs` — `CreateRequest()` ~L186
- `src/OpenWebUI.Api/Program.cs` — bloco de headers/CSP ~L491 + SPEC entregue `docs/specs/SPEC-20261010-app-csp-header.md`
- `src/OpenWebUI.Client/Components/ChatView.razor` ~L2297 — handler de upload
- `Dockerfile` — L64 (RUNs), L140 (USER) + `docs/specs/` docker precedents
- `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md`

**Files to create or modify:**
```text
src/OpenWebUI.Client/Services/AuthService.cs
src/OpenWebUI.Api/Program.cs
src/OpenWebUI.Client/Components/ChatView.razor
Dockerfile
tests/OpenWebUI.Api.Tests/** (testes novos/ajustados)
```

## 4. Requirements

### RF-001: Sanitizar path/URL em `AuthService.CreateRequest`
- **Description:** validar/normalizar o segmento de URL derivado de input (allowlist de caracteres ou `Uri.EscapeDataString`/validação de path relativo), rejeitando `..`, `//`, hosts externos.
- **Rules:** comportamento compatível — chamadas legítimas continuam; testes cobrindo input malicioso.

### RF-002: Restringir CSP em `Program.cs`
- **Description:** remover diretivas permissivas sinalizadas (ex.: `unsafe-eval`/`unsafe-inline` desnecessários, `*` em `connect-src`/`img-src` onde não exigido) mantendo o boot WASM funcional — checar o que a CSP atual precisa (Blazor WASM precisa de `wasm-unsafe-eval` ou hashes; media/blob para attachments).
- **Rules:** app continua bootando; `a11y`/e2e não quebram; documentar o que ficou de fora.

### RF-003: Limitar tamanho de upload no `ChatView.razor`
- **Description:** aplicar `maximumReceiveMessageSize`/`InputFile` max + validação server-side correspondente (endpoint já deve ter limite — alinhar client ao mesmo valor).
- **Rules:** limite coerente com o backend; UX mostra erro amigável.

### RF-004: Usuário não-root na imagem
- **Description:** criar/usar usuário não-privilegiado no stage final do `Dockerfile` (`USER <uid>` ou usuário nomeado), garantindo permissões de escrita em `/data`/dirs necessários.
- **Rules:** `docker compose up` funciona; volume `/data` writable; merge das RUNs L64 conforme `docker:S7031`.

## 5. API Contract

N/A — sem mudança de payload; endpoints e headers funcionais preservados (CSP é a única mudança de wire).

## 6. Acceptance Criteria

- [ ] **Given** `AuthService.CreateRequest()` com input `../admin` ou host absoluto, **then** request rejeitado/normalizado (teste verde).
- [ ] **Given** boot da app, **then** CSP emitida sem as permissões sinalizadas e app funcional.
- [ ] **Given** upload acima do limite, **then** rejeitado com erro tratado (sem crash/sem envio).
- [ ] **Given** `docker build` + `docker run`, **then** processo roda como não-root e `/data` persiste.
- [ ] SonarCloud não reporta mais as 5 issues após próximo scan.
- [ ] `dotnet test` verde; `hadolint Dockerfile` limpo na mudança.

## 7. Task Plan

1. Ler os 4 arquivos + testes existentes de auth/headers/upload.
2. RF-001..RF-004 em commits atômicos por arquivo.
3. Testes: unitários de sanitização (AuthService), limite de upload, e validação do Dockerfile via `hadolint` + build local.
4. `dotnet build -c Release` + `dotnet test` + docker build.
5. Checklist do board `.sonar_devin_auto_fix/SONAR_FIX_TODO_BOARD.md`.

## 8. Effort Estimate

`~3-4h` (4 fixes independentes + testes + validação docker).

## Delivered

Verificação 2026-11-03: todos os findings já resolvidos em SPECs
anteriores (o scan r2 reportou instâncias stale das mesmas regras):

| Regra | Finding | Estado atual |
| --- | --- | --- |
| `csharpsquid:S7044` | path traversal em `AuthService.CreateRequest` | **Já fixado** — decode antes de validar (`%2e%2e`), rejeita `..`/`//`/backslash/host; testes em `ClientServiceEdgeTests` |
| `csharpsquid:S7039` | CSP permissiva em `Program.cs` | **Já fixado** — `unsafe-eval` removido; `wasm-unsafe-eval`+`unsafe-inline` com justificativa documentada |
| `csharpsquid:S5693` | upload sem limite em `ChatView`/`FileEndpoints` | **Já fixado** — `maxAllowedSize: MaxUploadBytes` client + `file.Length > MaxUploadBytes` server (100MB alinhado) |
| `docker:S6471` | `USER root` na imagem final | **Won't-fix documentado** — SPEC-20261031-sonar-vuln-code: processo é o agente (shell_exec/apt), decisão de design single-tenant |
| `docker:S7031` | RUNs consecutivas | **Já fixado** — apt+gh num único layer (mesmo RUN, comentário no Dockerfile) |

Nenhuma mudança de código necessária — triage com evidência linha-a-linha.
